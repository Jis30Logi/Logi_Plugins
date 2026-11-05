namespace Loupedeck.RTSSPerformancePlugin.Actions
{
    using System;
    using System.Globalization;
    using System.Linq;

    using Loupedeck.RTSSPerformancePlugin.Helpers;
    using Loupedeck.RTSSPerformancePlugin.Services;

    /// <summary>
    /// The FPS Cap presets as standalone actions, one per preset.
    ///
    /// <see cref="FpsCapFolder"/> is a PluginDynamicFolder, and a host whose device has no folder
    /// surface for it (G HUB, on the device this was tested against) never lists it. A plain
    /// command is listed everywhere, so the same cap service is exposed here too.
    /// </summary>
    public sealed class FpsCapCommand : PluginDynamicCommand
    {
        private IFpsCapStateService _capState;
        private IRTSSAvailabilityMonitorService _availabilityMonitor;

        public FpsCapCommand()
            : base()
        {
            this.GroupName = "3. Control";

            foreach (var preset in FpsCapFolder.Presets)
            {
                this.AddParameter(PresetParameter(preset), $"FPS Cap {preset}", this.GroupName);
            }

            this.AddParameter(UnlimitedParameter, "FPS Cap Unlimited", this.GroupName);
            this.AddParameter(ResetParameter, "Reset FPS Cap", this.GroupName);
        }

        protected override Boolean OnLoad()
        {
            this.EnsureServicesBound();
            ActionReadiness.SubscribeAndCatchUp(this.OnServicesReady);
            return true;
        }

        protected override Boolean OnUnload()
        {
            ActionReadiness.Unsubscribe(this.OnServicesReady);

            if (this._capState != null)
            {
                this._capState.StateChanged -= this.OnCapStateChanged;
                this._capState = null;
            }

            if (this._availabilityMonitor != null)
            {
                this._availabilityMonitor.StatusChanged -= this.OnAvailabilityChanged;
                this._availabilityMonitor = null;
            }

            return true;
        }

        protected override void RunCommand(String actionParameter)
        {
            try
            {
                this.EnsureServicesBound();

                if (this._capState == null || !this.IsRtssAvailable())
                {
                    PluginLog.Warning("[FPS CAP CMD] Rejected: cap service or RTSS not available.");
                    return;
                }

                var parameter = Normalize(actionParameter);

                PluginLog.Info(
                    $"[FPS CAP CMD] Pressed raw='{actionParameter ?? "<null>"}' routed='{parameter}' " +
                    $"effectiveCap={this._capState.EffectiveCap}");

                if (IsReset(parameter))
                {
                    // Same target as the FPS Cap dial's reset.
                    _ = this._capState.RequestSetExactCapAsync(FpsCapAdjustment.ResetCap);
                }
                else if (IsUnlimited(parameter))
                {
                    _ = this._capState.RequestUnlimitedAsync();
                }
                else if (TryParsePreset(parameter, out var preset))
                {
                    _ = this._capState.RequestSetCapAsync(preset);
                }
                else
                {
                    PluginLog.Warning($"[FPS CAP CMD] Unknown parameter '{actionParameter ?? "<null>"}'.");
                }
            }
            catch (Exception exception)
            {
                PluginLog.Error(exception, "[FPS CAP CMD] RunCommand failed.");
            }
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            System.Threading.Interlocked.Increment(ref this._imageRequests);

            try
            {
                this.EnsureServicesBound();

                var isAvailable = this.IsRtssAvailable();
                var parameter = Normalize(actionParameter);

                // The solid white border always follows the cap the user last chose, so it moves the
                // moment a preset is pressed and never goes missing while RTSS applies it.
                var current = this.ReadCurrentCap();

                if (IsReset(parameter))
                {
                    // A momentary action, not a mode: no active border.
                    return FpsCapRenderer.RenderArtworkPage(
                        imageSize,
                        "icon-reset-fps-cap.svg",
                        isActive: false,
                        isRtssAvailable: isAvailable,
                        isPending: false);
                }

                if (IsUnlimited(parameter))
                {
                    this.TraceImage(parameter, current, current.Unlimited);

                    return FpsCapRenderer.RenderArtworkPage(
                        imageSize,
                        "icon-fps-unlimited.svg",
                        isActive: current.Unlimited,
                        isRtssAvailable: isAvailable,
                        isPending: false);
                }

                if (TryParsePreset(parameter, out var preset))
                {
                    var isActive = !current.Unlimited && current.Cap == preset;

                    this.TraceImage(parameter, current, isActive);

                    return FpsCapRenderer.RenderArtworkPage(
                        imageSize,
                        $"icon-fps-{preset.ToString(CultureInfo.InvariantCulture)}.svg",
                        isActive: isActive,
                        isRtssAvailable: isAvailable,
                        isPending: false);
                }

                return FpsCapRenderer.RenderMainButton(imageSize, isAvailable);
            }
            catch (Exception exception)
            {
                PluginLog.Error(exception, "[FPS CAP CMD] GetCommandImage failed.");
                return FpsCapRenderer.RenderMainButton(imageSize, this.IsRtssAvailable());
            }
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) =>
            String.Empty;

        private void EnsureServicesBound()
        {
            var plugin = RTSSPerformancePlugin.Instance;
            if (plugin == null)
            {
                return;
            }

            if (this._capState == null && plugin.CapStateService != null)
            {
                this._capState = plugin.CapStateService;
                this._capState.StateChanged += this.OnCapStateChanged;
            }

            if (this._availabilityMonitor == null && plugin.AvailabilityMonitor != null)
            {
                this._availabilityMonitor = plugin.AvailabilityMonitor;
                this._availabilityMonitor.StatusChanged += this.OnAvailabilityChanged;
            }
        }

        private Boolean IsRtssAvailable() => this._availabilityMonitor?.IsAvailable ?? false;

        /// <summary>
        /// The cap to highlight. Read-only: a pending request counts as current, so the border does
        /// not lag behind the press; before anything has been requested the service holds 0, so the
        /// profile on disk is consulted instead.
        /// </summary>
        private (Boolean Unlimited, Int32 Cap) ReadCurrentCap()
        {
            var state = this._capState;
            if (state == null)
            {
                return (false, 0);
            }

            if (state.PendingCap == 0 || state.IsUnlimited)
            {
                return (true, 0);
            }

            var cap = state.EffectiveCap;
            if (cap <= 0)
            {
                try
                {
                    var writer = RTSSPerformancePlugin.Instance?.ProfileWriter;
                    if (writer != null && writer.TryReadEffectiveFpsCeiling(null, out var fromProfile) && fromProfile > 0)
                    {
                        cap = fromProfile;
                    }
                }
                catch (Exception exception)
                {
                    PluginLog.Warning(exception, "[FPS CAP CMD] Could not read the cap from the RTSS profile.");
                }
            }

            return (false, cap);
        }

        private void TraceImage(String parameter, (Boolean Unlimited, Int32 Cap) current, Boolean isActive) =>
            PluginLog.InfoThrottled(
                "FPS CAP CMD IMAGE " + parameter,
                $"{current.Unlimited}|{current.Cap}|{isActive}",
                $"[FPS CAP CMD] Image requested for '{parameter}': currentCap={(current.Unlimited ? "unlimited" : current.Cap.ToString(CultureInfo.InvariantCulture))} border={isActive}");

        private void OnServicesReady(Object sender, EventArgs e) => this.Refresh();

        private void OnCapStateChanged(Object sender, FpsCapStateChangedEventArgs e) => this.Refresh();

        private void OnAvailabilityChanged(Object sender, RTSSAvailability status) => this.Refresh();

        private void Refresh()
        {
            try
            {
                this.EnsureServicesBound();

                PluginLog.Info(
                    $"[FPS CAP CMD] Refreshing tiles. confirmed={this._capState?.ConfirmedCap} " +
                    $"pending={this._capState?.PendingCap?.ToString() ?? "-"}");

                var requestsBefore = System.Threading.Interlocked.Read(ref this._imageRequests);

                // A command with many parameters is not repainted by the argument-less call;
                // each key is told by name, as FpsCapFolder does.
                foreach (var parameter in AllParameters)
                {
                    this.ActionImageChanged(parameter);
                }

                this.ActionImageChanged();

                this.VerifyRefreshDelivered(requestsBefore);
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, "[FPS CAP CMD] ActionImageChanged failed.");
            }
        }

        /// <summary>Times the host asked this command for an image; the evidence that a refresh was acted on.</summary>
        private Int64 _imageRequests;

        /// <summary>1 while a delivery check is in flight, so a burst of refreshes shares one check.</summary>
        private Int32 _verifyPending;

        private Int64 _lastEscalationTicks;

        /// <summary>
        /// G HUB acts on ActionImageChanged only some of the time: the same refresh re-rendered every
        /// tile within ~20 ms in one session and was ignored for a whole session in others, with the
        /// tiles subscribed and no error on either side. So a refresh is not assumed to have worked.
        ///
        /// If the host has asked for no image 400 ms later, the stronger SDK signal —
        /// <c>ParametersChanged</c>, which tells the service to re-read this action and update the UI —
        /// is sent (at most once every 3 s) and the outcome of each step is logged, so the log states
        /// plainly whether the host honoured it.
        /// </summary>
        private void VerifyRefreshDelivered(Int64 requestsBefore)
        {
            if (System.Threading.Interlocked.Exchange(ref this._verifyPending, 1) == 1)
            {
                return;
            }

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(400).ConfigureAwait(false);

                    if (System.Threading.Interlocked.Read(ref this._imageRequests) != requestsBefore)
                    {
                        PluginLog.InfoThrottled(
                            "FPS CAP CMD DELIVERED",
                            "[FPS CAP CMD] Refresh delivered: the host re-rendered the tiles.");
                        return;
                    }

                    var now = Environment.TickCount64;
                    if (now - System.Threading.Interlocked.Read(ref this._lastEscalationTicks) < 3000)
                    {
                        return;
                    }

                    System.Threading.Interlocked.Exchange(ref this._lastEscalationTicks, now);

                    PluginLog.Info("[FPS CAP CMD] Refresh not delivered after 400 ms (the host asked for no image); sending ParametersChanged().");

                    var beforeEscalation = System.Threading.Interlocked.Read(ref this._imageRequests);
                    this.ParametersChanged();

                    await System.Threading.Tasks.Task.Delay(600).ConfigureAwait(false);

                    PluginLog.Info(
                        System.Threading.Interlocked.Read(ref this._imageRequests) != beforeEscalation
                            ? "[FPS CAP CMD] ParametersChanged() delivered: the host re-rendered the tiles."
                            : "[FPS CAP CMD] ParametersChanged() NOT delivered: the host asked for no image (tiles not on screen, or not subscribed).");
                }
                catch (Exception exception)
                {
                    PluginLog.Warning(exception, "[FPS CAP CMD] Refresh delivery check failed.");
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref this._verifyPending, 0);
                }
            });
        }

        private static readonly String[] AllParameters =
            FpsCapFolder.Presets
                .Select(PresetParameter)
                .Concat(new[] { UnlimitedParameter, ResetParameter })
                .ToArray();

        private const String UnlimitedParameter = "fps:unlimited";

        private const String ResetParameter = "fps:reset";

        private static Boolean IsReset(String parameter) =>
            String.Equals(parameter, "reset", StringComparison.OrdinalIgnoreCase);

        private static Boolean IsUnlimited(String parameter) =>
            String.Equals(parameter, "unlimited", StringComparison.OrdinalIgnoreCase);

        private static String PresetParameter(Int32 preset) =>
            "fps:" + preset.ToString(CultureInfo.InvariantCulture);

        private static Boolean TryParsePreset(String parameter, out Int32 preset) =>
            Int32.TryParse(parameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out preset) &&
            FpsCapFolder.Presets.Contains(preset);

        /// <summary>The host may hand back a qualified name; only the last segment identifies the preset.</summary>
        private static String Normalize(String actionParameter)
        {
            if (String.IsNullOrWhiteSpace(actionParameter))
            {
                return actionParameter;
            }

            var parameter = actionParameter.Trim();
            var separator = parameter.LastIndexOfAny(new[] { '.', ':', '/', '\\' });

            return separator >= 0 && separator + 1 < parameter.Length
                ? parameter.Substring(separator + 1)
                : parameter;
        }
    }
}
