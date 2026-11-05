namespace Loupedeck.RTSSPerformancePlugin.Actions
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading.Tasks;

    using Loupedeck.RTSSPerformancePlugin.Helpers;
    using Loupedeck.RTSSPerformancePlugin.Interop;
    using Loupedeck.RTSSPerformancePlugin.Services;

    /// <summary>Top-level FPS Cap action with an internal preset page.</summary>
    public sealed class FpsCapFolder : PluginDynamicFolder
    {
        public static readonly Int32[] Presets = { 30, 60, 90, 120, 144, 180 };

        private const String UnlimitedAction = "unlimited";
        private const String ResetAction = "reset";

        public static FpsCapFolder Instance { get; internal set; }

        private IRTSSProfileWriterService _profileWriter;
        private IRTSSReaderService _reader;
        private IRTSSAvailabilityMonitorService _availabilityMonitor;
        private IRTSSProfileHelperRunner _helperRunner;
        private IPollTickSchedulerService _scheduler;
        private IEffectiveFpsCapService _effectiveCapService;
        private IFpsCapStateService _capStateService;
        private Int32 _lastNotifiedFpsCap = -1;
        private Boolean _isActive;

        /// <summary>
        /// Exposes the background task of the last requested FPS cap operation.
        /// Allows deterministic waiting in integration tests while keeping action handlers non-blocking.
        /// </summary>
        public Task<FpsCapApplyResult> LastOperationTask { get; internal set; }

        /// <summary>
        /// Raised when the main keypad LCD button refreshes its state.
        /// Used by tests and hosts to track dynamic LCD updates deterministically.
        /// </summary>
        public event EventHandler<Int32> MainButtonRefreshed;

        public FpsCapFolder()
            : this(null, null, null, null, null, null, null)
        {
        }

        public FpsCapFolder(
            IRTSSProfileWriterService profileWriter,
            IRTSSReaderService reader,
            IRTSSAvailabilityMonitorService availabilityMonitor,
            IPollTickSchedulerService scheduler,
            IRTSSProfileHelperRunner helperRunner = null,
            IEffectiveFpsCapService effectiveCapService = null,
            IFpsCapStateService capStateService = null)
            : base()
        {
            this.DisplayName = "FPS Cap";
            this.Description = "Set the active RTSS frame limit.";
            this.GroupName = "3. Control";
#pragma warning disable CS0618
            this.Navigation = PluginDynamicFolderNavigation.ButtonArea;
#pragma warning restore CS0618

            this._profileWriter = profileWriter;
            this._reader = reader;
            this._availabilityMonitor = availabilityMonitor;
            this._helperRunner = helperRunner;
            this._scheduler = scheduler;
            this._capStateService = capStateService ?? effectiveCapService as IFpsCapStateService;
            this._effectiveCapService = effectiveCapService ?? this._capStateService;

            if (this._capStateService == null && (profileWriter != null || effectiveCapService != null))
            {
                this._capStateService = new FpsCapStateService(
                    profileWriter,
                    reader,
                    availabilityMonitor,
                    helperRunner,
                    effectiveCapService?.CurrentCap ?? 0);
            }

            if (this._capStateService != null)
            {
                this._capStateService.StateChanged += this.OnCapStateChanged;
                if (effectiveCapService != null && !ReferenceEquals(effectiveCapService, this._capStateService))
                {
                    this._capStateService.CapChanged += (s, c) => effectiveCapService.SetCap(c);
                }
            }
            else if (this._effectiveCapService != null)
            {
                this._effectiveCapService.CapChanged += this.OnEffectiveCapChanged;
            }

            if (this._scheduler != null)
            {
                this._scheduler.Tick += this.OnPollTick;
            }

            if (this._profileWriter != null)
            {
                this._profileWriter.FpsCeilingChanged += this.OnFpsCeilingChanged;
                this._profileWriter.ActiveProfileCeilingChanged += this.OnActiveProfileCeilingChanged;
            }

            Instance = this;
        }

        public Boolean ApplyPreset(Int32 preset)
        {
            this.RunCommand("fps:" + preset.ToString(CultureInfo.InvariantCulture));
            try
            {
                this.LastOperationTask?.Wait(2000);
            }
            catch
            {
                // Ignored
            }
            return true;
        }

        public override PluginDynamicFolderNavigation GetNavigationArea(DeviceType deviceType) =>
            PluginDynamicFolderNavigation.ButtonArea;

        public override String[] GetButtonPressActionNames(DeviceType deviceType) =>
            Presets.Select(preset => this.CreateSafeCommandName("fps:" + preset.ToString(CultureInfo.InvariantCulture)))
                .Concat(new[] { this.CreateSafeCommandName(UnlimitedAction), this.CreateSafeCommandName(ResetAction) })
                .ToArray();

        private String CreateSafeCommandName(String parameter)
        {
            try
            {
                return this.CreateCommandName(parameter);
            }
            catch
            {
                return parameter;
            }
        }

#pragma warning disable CS0618
        [Obsolete]
        public override String[] GetButtonPressActionNames() =>
            this.GetButtonPressActionNames(DeviceType.None);
#pragma warning restore CS0618

        public override String GetButtonDisplayName(PluginImageSize imageSize) => "FPS Cap";

        public override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) =>
            String.IsNullOrWhiteSpace(actionParameter)
                ? null
                : GetActionLabel(NormalizeActionParameter(actionParameter));

        /// <summary>Same default labels the draggable FPS Cap actions use.</summary>
        private static String GetActionLabel(String routedParameter)
        {
            if (String.Equals(routedParameter, ResetAction, StringComparison.OrdinalIgnoreCase))
            {
                return "Reset FPS Cap";
            }

            if (String.Equals(routedParameter, UnlimitedAction, StringComparison.OrdinalIgnoreCase))
            {
                return "FPS Cap Unlimited";
            }

            return Int32.TryParse(routedParameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var preset) &&
                   Presets.Contains(preset)
                ? $"FPS Cap {preset.ToString(CultureInfo.InvariantCulture)}"
                : String.Empty;
        }

        public override LibraryImage GetButtonLibraryImage()
        {
            try
            {
                var icon = PluginResources.GetActionIcon(nameof(FpsCapFolder));
                return String.IsNullOrEmpty(icon) ? null : new LibraryImage(icon);
            }
            catch
            {
                return null;
            }
        }

        public override BitmapImage GetButtonImage(PluginImageSize imageSize)
        {
            try
            {
                this.EnsureServicesBound();
                return FpsCapRenderer.RenderMainButton(imageSize, this.IsRtssAvailable());
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "[FPS CAP] GetButtonImage failed.");
                return FpsCapRenderer.RenderMainButton(imageSize, this.IsRtssAvailable());
            }
        }

        public override LibraryImage GetCommandLibraryImage(String actionParameter)
        {
            try
            {
                var icon = PluginResources.GetActionIcon(nameof(FpsCapFolder));
                return String.IsNullOrEmpty(icon) ? null : new LibraryImage(icon);
            }
            catch
            {
                return null;
            }
        }

        public override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            try
            {
                var routedParameter = NormalizeActionParameter(actionParameter);
                var isAvailable = this.IsRtssAvailable();

                if (String.Equals(routedParameter, "BACK", StringComparison.OrdinalIgnoreCase))
                {
                    return FpsCapRenderer.RenderArtworkPage(imageSize, "back.svg", isRtssAvailable: true);
                }

                if (String.Equals(routedParameter, ResetAction, StringComparison.OrdinalIgnoreCase))
                {
                    return FpsCapRenderer.RenderArtworkPage(imageSize, "icon-reset-fps-cap.svg", isRtssAvailable: isAvailable);
                }

                if (String.Equals(routedParameter, UnlimitedAction, StringComparison.OrdinalIgnoreCase))
                {
                    return FpsCapRenderer.RenderArtworkPage(
                        imageSize,
                        "icon-fps-unlimited.svg",
                        isActive: this.IsUnlimitedActive(isAvailable),
                        isRtssAvailable: isAvailable);
                }

                if (Int32.TryParse(routedParameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var preset) &&
                    Presets.Contains(preset))
                {
                    var presetIconName = $"icon-fps-{preset}.svg";

                    this.GetPresetVisualState(preset, isAvailable, out var isActive, out var isPending);

                    return FpsCapRenderer.RenderArtworkPage(imageSize, presetIconName, isActive: isActive, isRtssAvailable: isAvailable, isPending: isPending);
                }

                return FpsCapRenderer.RenderMainButton(imageSize, isRtssAvailable: isAvailable);
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "[FPS CAP] GetCommandImage failed.");
                return FpsCapRenderer.RenderMainButton(imageSize, this.IsRtssAvailable());
            }
        }

        public override void RunCommand(String actionParameter)
        {
            try
            {
                var routedParameter = NormalizeActionParameter(actionParameter);

                if (String.Equals(routedParameter, "BACK", StringComparison.OrdinalIgnoreCase) ||
                    (actionParameter != null && (actionParameter.EndsWith("BACK", StringComparison.OrdinalIgnoreCase) || actionParameter.EndsWith(":BACK", StringComparison.OrdinalIgnoreCase))))
                {
                    PluginLog.Info("[FPS CAP] Back command triggered; closing FPS Cap folder.");
                    this._isActive = false;
                    this.Close();
                    return;
                }

                if (String.IsNullOrEmpty(actionParameter) || String.IsNullOrEmpty(routedParameter))
                {
                    return;
                }

                this.EnsureServicesBound();

                if (!this.IsRtssAvailable())
                {
                    PluginLog.Warning("[FPS CAP] RunCommand rejected: RTSS is not available.");
                    return;
                }

                if (String.Equals(routedParameter, ResetAction, StringComparison.OrdinalIgnoreCase))
                {
                    if (this._capStateService != null)
                    {
                        this.LastOperationTask = this._capStateService.RequestSetExactCapAsync(FpsCapAdjustment.ResetCap);
                    }
                    else
                    {
                        this.FallbackSetCap(FpsCapAdjustment.ResetCap);
                    }
                    return;
                }

                if (String.Equals(routedParameter, UnlimitedAction, StringComparison.OrdinalIgnoreCase))
                {
                    if (this._capStateService != null)
                    {
                        this.LastOperationTask = this._capStateService.RequestUnlimitedAsync();
                    }
                    else
                    {
                        PluginLog.Warning("[FPS CAP] Unlimited requested but the cap service is not available.");
                    }
                    return;
                }

                if (!Int32.TryParse(routedParameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var preset) ||
                    !Presets.Contains(preset))
                {
                    PluginLog.Warning($"[FPS CAP] Rejected unknown preset action raw='{actionParameter ?? "<null>"}'.");
                    return;
                }

                if (this._capStateService != null)
                {
                    this.LastOperationTask = this._capStateService.RequestSetCapAsync(preset);
                }
                else
                {
                    this.FallbackSetCap(preset);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "[FPS CAP] RunCommand failed.");
            }
        }

        public override Boolean Load()
        {
            try
            {
                Instance = this;
                var result = base.Load();
                this.EnsureServicesBound();
                ActionReadiness.SubscribeAndCatchUp(this.OnServicesReady);
                PluginLog.Info("[FPS CAP] Folder loaded and bound to services.");
                return result;
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "[FPS CAP] Load failed.");
                return false;
            }
        }

        public override Boolean Activate()
        {
            try
            {
                base.Activate();
                this._isActive = true;
                this.EnsureServicesBound();

                var actual = this._capStateService?.ConfirmedCap ?? this.ReadEffectiveFpsCapFromRtss();
                if (actual > 0)
                {
                    this._effectiveCapService?.SetCap(actual);
                    this._lastNotifiedFpsCap = actual;
                }

                this.RefreshPresetImages(force: true);
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "[FPS CAP] Activate failed.");
                return false;
            }
        }

        public override Boolean Deactivate()
        {
            try
            {
                this._isActive = false;
                base.Deactivate();
                this.RefreshMainButtonIfChanged(force: true);
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "[FPS CAP] Deactivate failed.");
                return false;
            }
        }

        public override Boolean Unload()
        {
            try
            {
                ActionReadiness.Unsubscribe(this.OnServicesReady);

                if (this._capStateService != null)
                {
                    this._capStateService.StateChanged -= this.OnCapStateChanged;
                    this._capStateService = null;
                }

                if (this._effectiveCapService != null)
                {
                    this._effectiveCapService.CapChanged -= this.OnEffectiveCapChanged;
                    this._effectiveCapService = null;
                }

                if (this._scheduler != null)
                {
                    this._scheduler.Tick -= this.OnPollTick;
                    this._scheduler = null;
                }

                if (this._profileWriter != null)
                {
                    this._profileWriter.FpsCeilingChanged -= this.OnFpsCeilingChanged;
                    this._profileWriter.ActiveProfileCeilingChanged -= this.OnActiveProfileCeilingChanged;
                    this._profileWriter = null;
                }

                if (this._availabilityMonitor != null)
                {
                    this._availabilityMonitor.StatusChanged -= this.OnAvailabilityChanged;
                    this._availabilityMonitor = null;
                }

                this._reader = null;
                this._helperRunner = null;
                this._lastNotifiedFpsCap = -1;

                if (ReferenceEquals(Instance, this))
                {
                    Instance = null;
                }

                return base.Unload();
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "[FPS CAP] Unload failed.");
                return false;
            }
        }

        private void OnServicesReady(Object sender, EventArgs e)
        {
            try
            {
                this.EnsureServicesBound();
                if (this._isActive)
                {
                    this.RefreshPresetImages(force: true);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[FPS CAP] OnServicesReady failed.");
            }
        }

        private void EnsureServicesBound()
        {
            try
            {
                var plugin = RTSSPerformancePlugin.Instance;
                if (plugin == null)
                {
                    return;
                }

                if (this._capStateService == null && plugin.CapStateService != null)
                {
                    this._capStateService = plugin.CapStateService;
                    this._capStateService.StateChanged += this.OnCapStateChanged;
                }

                if (this._effectiveCapService == null)
                {
                    this._effectiveCapService = this._capStateService ?? (IEffectiveFpsCapService)plugin.EffectiveFpsCap;
                }

                if (this._profileWriter == null && plugin.ProfileWriter != null)
                {
                    this._profileWriter = plugin.ProfileWriter;
                    this._profileWriter.FpsCeilingChanged += this.OnFpsCeilingChanged;
                    this._profileWriter.ActiveProfileCeilingChanged += this.OnActiveProfileCeilingChanged;
                }

                this._reader ??= plugin.Reader;
                this._helperRunner ??= plugin.ProfileHelperRunner;

                if (this._availabilityMonitor == null && plugin.AvailabilityMonitor != null)
                {
                    this._availabilityMonitor = plugin.AvailabilityMonitor;
                    this._availabilityMonitor.StatusChanged += this.OnAvailabilityChanged;
                }

                if (this._scheduler == null && plugin.PollScheduler != null)
                {
                    this._scheduler = plugin.PollScheduler;
                    this._scheduler.Tick += this.OnPollTick;
                }

                if (this._capStateService is FpsCapStateService fcs && this._profileWriter != null)
                {
                    fcs.BindServices(this._profileWriter, this._reader, this._availabilityMonitor, this._helperRunner);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[FPS CAP] EnsureServicesBound failed.");
            }
        }

        private void OnCapStateChanged(Object sender, FpsCapStateChangedEventArgs e)
        {
            try
            {
                this.RefreshMainButtonIfChanged(force: false);
                if (this._isActive)
                {
                    this.RefreshPresetImages();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[FPS CAP] OnCapStateChanged failed.");
            }
        }

        private void OnEffectiveCapChanged(Object sender, Int32 newCap)
        {
            try
            {
                this._lastNotifiedFpsCap = newCap;
                this.RefreshMainButtonIfChanged(force: false);
                if (this._isActive)
                {
                    this.RefreshPresetImages();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[FPS CAP] OnEffectiveCapChanged failed.");
            }
        }

        private void OnAvailabilityChanged(Object sender, RTSSAvailability status)
        {
            try
            {
                this.RefreshMainButtonIfChanged(force: true);
                this.CommandImageChanged(null);
                if (this._isActive)
                {
                    this.RefreshPresetImages(force: true);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[FPS CAP] OnAvailabilityChanged failed.");
            }
        }

        private void OnFpsCeilingChanged(Object sender, Int32 e)
        {
            try
            {
                if (e > 0 && this._capStateService == null)
                {
                    this._effectiveCapService?.SetCap(e);
                }
                this.RefreshMainButtonIfChanged(force: false);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[FPS CAP] OnFpsCeilingChanged failed.");
            }
        }

        private void OnActiveProfileCeilingChanged(Object sender, Int32 e)
        {
            try
            {
                if (e > 0 && this._capStateService == null)
                {
                    this._effectiveCapService?.SetCap(e);
                }
                this.RefreshMainButtonIfChanged(force: false);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[FPS CAP] OnActiveProfileCeilingChanged failed.");
            }
        }

        private void OnPollTick(Object sender, PollTickEventArgs e)
        {
            try
            {
                this.RefreshMainButtonIfChanged(force: false);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[FPS CAP] OnPollTick failed.");
            }
        }

        public void RefreshMainButtonIfChanged(Boolean force = false)
        {
            try
            {
                var currentFps = this._capStateService?.ConfirmedCap ?? this._effectiveCapService?.CurrentCap ?? 0;
                if (!force && this._lastNotifiedFpsCap != -1 && currentFps == this._lastNotifiedFpsCap)
                {
                    return;
                }

                this._lastNotifiedFpsCap = currentFps;

                try
                {
                    this.CommandImageChanged(null);
                    this.CommandImageChanged(String.Empty);
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, "[FPS CAP] CommandImageChanged failed.");
                }

                try
                {
                    this.MainButtonRefreshed?.Invoke(this, currentFps);
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, "[FPS CAP] MainButtonRefreshed event handler threw.");
                }

                PluginLog.Info($"[FPS CAP] Main button refreshed. Effective cap={currentFps}");
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[FPS CAP] RefreshMainButtonIfChanged failed.");
            }
        }

        private void FallbackSetCap(Int32 preset)
        {
            if (!this.IsRtssAvailable() || this._profileWriter == null)
            {
                PluginLog.Warning($"[FPS CAP] requested={preset} write=false result=rtss-not-available.");
                return;
            }

            if (!this.TryGetEffectiveTargetProfile(out var targetProfile, out var isAppProfile))
            {
                PluginLog.Warning($"[FPS CAP] requested={preset} write=false result=target-profile-resolution-failed.");
                return;
            }

            if (isAppProfile)
            {
                this._profileWriter.UpdateActiveApplicationProfile(targetProfile);
            }
            else
            {
                this._profileWriter.UpdateActiveProfile(RTSSProfileWriterService.GlobalProfileName);
            }

            var writeSucceeded = this._profileWriter.TryWriteFpsCeiling(targetProfile, preset);
            if (!writeSucceeded)
            {
                return;
            }

            this._profileWriter.ReloadProfiles();
            if (this._profileWriter.TryReadFpsCeiling(targetProfile, out var actual))
            {
                this._effectiveCapService?.SetCap(actual);
                this._lastNotifiedFpsCap = actual;
                this.RefreshMainButtonIfChanged(force: true);
            }
        }

        public Int32 ReadEffectiveFpsCap()
        {
            if (this._capStateService != null)
            {
                return this._capStateService.EffectiveCap;
            }

            if (this._effectiveCapService != null && this._effectiveCapService.CurrentCap > 0)
            {
                return this._effectiveCapService.CurrentCap;
            }

            return this.ReadEffectiveFpsCapFromRtss();
        }

        public Int32 ReadEffectiveFpsCapFromRtss()
        {
            try
            {
                if (!this.IsRtssAvailable() || this._profileWriter == null)
                {
                    return 0;
                }

                String processName = null;
                try
                {
                    var snapshot = this._reader?.LastSnapshot;
                    if (IsValidTarget(snapshot?.ProcessFileName, snapshot))
                    {
                        processName = snapshot.ProcessFileName;
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, "[FPS CAP] Failed to read application snapshot.");
                }

                if (String.IsNullOrEmpty(processName) &&
                    !String.IsNullOrWhiteSpace(this._profileWriter.ActiveProfileName) &&
                    !RTSSProfileWriterService.IsGlobalProfile(this._profileWriter.ActiveProfileName) &&
                    this._profileWriter.ProfileExists(this._profileWriter.ActiveProfileName))
                {
                    processName = this._profileWriter.ActiveProfileName;
                }

                if (this._profileWriter.TryReadEffectiveFpsCeiling(processName, out var effectiveCap) && effectiveCap > 0)
                {
                    return effectiveCap;
                }

                return 0;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[FPS CAP] ReadEffectiveFpsCapFromRtss failed.");
                return 0;
            }
        }

        private Boolean IsRtssAvailable()
        {
            try
            {
                var monitor = this._availabilityMonitor ?? RTSSPerformancePlugin.Instance?.AvailabilityMonitor;
                return monitor?.IsAvailable == true;
            }
            catch
            {
                return false;
            }
        }

        private void GetPresetVisualState(Int32 preset, Boolean isAvailable, out Boolean isActive, out Boolean isPending)
        {
            var confirmedCap = this._capStateService?.ConfirmedCap ?? this._effectiveCapService?.CurrentCap ?? 0;
            var pendingCap = this._capStateService?.PendingCap;

            isPending = isAvailable && pendingCap.HasValue && pendingCap.Value == preset;
            isActive = isAvailable && confirmedCap == preset && (!this._capStateService?.IsPending ?? true);
        }

        private Boolean IsUnlimitedActive(Boolean isAvailable) =>
            isAvailable && this._capStateService != null &&
            (this._capStateService.IsUnlimited || this._capStateService.PendingCap == 0);

        /// <summary>What each key last told the host it looks like, so only keys that changed are invalidated.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<String, String> _notifiedKeyState =
            new System.Collections.Concurrent.ConcurrentDictionary<String, String>();

        private void NotifyKeyIfChanged(String commandName, String visualState, Boolean force)
        {
            if (!force && this._notifiedKeyState.TryGetValue(commandName, out var previous) &&
                String.Equals(previous, visualState, StringComparison.Ordinal))
            {
                return;
            }

            this._notifiedKeyState[commandName] = visualState;
            this.CommandImageChanged(commandName);
        }

        private void RefreshPresetImages(Boolean force = false)
        {
            try
            {
                var isAvailable = this.IsRtssAvailable();

                foreach (var preset in Presets)
                {
                    this.GetPresetVisualState(preset, isAvailable, out var isActive, out var isPending);

                    var parameter = "fps:" + preset.ToString(CultureInfo.InvariantCulture);
                    this.NotifyKeyIfChanged(
                        this.CreateSafeCommandName(parameter),
                        $"{isAvailable}|{isActive}|{isPending}",
                        force);
                }

                this.NotifyKeyIfChanged(
                    this.CreateSafeCommandName(UnlimitedAction),
                    $"{isAvailable}|{this.IsUnlimitedActive(isAvailable)}",
                    force);
                this.NotifyKeyIfChanged(this.CreateSafeCommandName(ResetAction), $"{isAvailable}", force);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[FPS CAP] RefreshPresetImages failed.");
            }
        }

        private Boolean TryGetEffectiveTargetProfile(out String targetProfile, out Boolean isAppProfile)
        {
            targetProfile = RTSSProfileWriterService.GlobalProfileName;
            isAppProfile = false;

            if (this._profileWriter == null)
            {
                return false;
            }

            try
            {
                var snapshot = this._reader?.LastSnapshot;
                var processName = snapshot?.ProcessFileName;

                if (IsValidTarget(processName, snapshot) && this._profileWriter.ProfileExists(processName))
                {
                    targetProfile = processName;
                    isAppProfile = true;
                    return true;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[FPS CAP] Failed to read application target.");
            }

            if (!String.IsNullOrWhiteSpace(this._profileWriter.ActiveProfileName) &&
                !RTSSProfileWriterService.IsGlobalProfile(this._profileWriter.ActiveProfileName) &&
                this._profileWriter.ProfileExists(this._profileWriter.ActiveProfileName))
            {
                targetProfile = this._profileWriter.ActiveProfileName;
                isAppProfile = true;
                return true;
            }

            targetProfile = RTSSProfileWriterService.GlobalProfileName;
            isAppProfile = false;
            return true;
        }

        private static String NormalizeActionParameter(String actionParameter)
        {
            if (String.IsNullOrWhiteSpace(actionParameter))
            {
                return actionParameter;
            }

            var parameter = actionParameter.Trim();
            var separator = parameter.LastIndexOfAny(new[] { '.', ':', '/', '\\' });

            if (separator >= 0 && separator + 1 < parameter.Length)
            {
                parameter = parameter.Substring(separator + 1);
            }

            return parameter;
        }

        private static Boolean IsValidTarget(String processName, RTSSFrameSnapshot snapshot)
        {
            if (snapshot == null || String.IsNullOrWhiteSpace(processName))
            {
                return false;
            }

            if (snapshot.SelectionReason != ActiveAppSelection.ForegroundWindow &&
                snapshot.SelectionReason != ActiveAppSelection.HighestFramerate)
            {
                return false;
            }

            var rejected = new[] { "LogiPluginService.exe", "LogiOptionsPlus.exe", "explorer.exe", "notepad.exe", "RTSSPerformancePlugin.dll" };
            return !rejected.Contains(processName, StringComparer.OrdinalIgnoreCase);
        }
    }
}