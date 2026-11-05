namespace Loupedeck.RTSSPerformancePlugin.Actions
{
    using System;
    using System.Globalization;
    using System.Linq;
    using System.Threading.Tasks;

    using Loupedeck.RTSSPerformancePlugin.Helpers;
    using Loupedeck.RTSSPerformancePlugin.Interop;
    using Loupedeck.RTSSPerformancePlugin.Services;

    /// <summary>
    /// The FPS Advisor key: shows performance recommendation based on current metrics.
    /// Reuses the existing FPS Cap action directly when recommending a target cap.
    /// </summary>
    public class FpsAdvisorCommand : ThrottledDynamicCommand
    {
        private FpsAdvisorService _advisor;
        private BottleneckAnalysisService _bottleneckAnalysis;
        private IFrameTimeTrackerService _frameTimeTracker;
        private ZeroPointOnePercentLowTrackerService _zeroPointOnePercentLowTracker;
        private IRTSSAvailabilityMonitorService _availabilityMonitor;
        private IPollTickSchedulerService _pollScheduler;
        private IRTSSReaderService _rtssReader;
        private RTSSProfileWriterService _profileWriter;
        private IRTSSProfileHelperRunner _helperRunner;
        private FpsHealthStateService _healthState;
        private ITelemetryCoordinatorService _coordinator;
        private IEffectiveFpsCapService _effectiveCapService;
        private IFpsCapStateService _capStateService;
        private FpsCapFolder _fpsCapFolder;

        public Task<FpsCapApplyResult> LastOperationTask { get; internal set; }

        public FpsAdvisorCommand()
            : base("FPS Advisor", "FPS performance recommendation based on current metrics.", null)
        {
            this.GroupName = "2. Performance";
        }

        internal FpsAdvisorCommand(
            FpsAdvisorService advisor,
            BottleneckAnalysisService bottleneckAnalysis,
            IFrameTimeTrackerService frameTimeTracker,
            ZeroPointOnePercentLowTrackerService zeroPointOnePercentLowTracker,
            IRTSSAvailabilityMonitorService availabilityMonitor,
            IPollTickSchedulerService pollScheduler,
            IRTSSReaderService rtssReader,
            RTSSProfileWriterService profileWriter,
            FpsHealthStateService healthState,
            IRTSSProfileHelperRunner helperRunner = null,
            IEffectiveFpsCapService effectiveCapService = null,
            FpsCapFolder fpsCapFolder = null,
            IFpsCapStateService capStateService = null)
            : this()
        {
            this._advisor = advisor;
            this._bottleneckAnalysis = bottleneckAnalysis;
            this._frameTimeTracker = frameTimeTracker;
            this._zeroPointOnePercentLowTracker = zeroPointOnePercentLowTracker;
            this._availabilityMonitor = availabilityMonitor;
            this._pollScheduler = pollScheduler;
            this._rtssReader = rtssReader;
            this._profileWriter = profileWriter;
            this._healthState = healthState;
            this._helperRunner = helperRunner;
            this._capStateService = capStateService ?? effectiveCapService as IFpsCapStateService;
            this._effectiveCapService = effectiveCapService ?? this._capStateService;
            this._fpsCapFolder = fpsCapFolder;

            if (this._effectiveCapService != null)
            {
                this._effectiveCapService.CapChanged += this.OnCapChanged;
            }
        }

        internal void ExecuteCommand(String actionParameter = null)
        {
            this.RunCommand(actionParameter);
            try
            {
                var task = this.LastOperationTask ?? (this._fpsCapFolder ?? FpsCapFolder.Instance)?.LastOperationTask;
                task?.Wait(2000);
            }
            catch
            {
                // Ignored
            }
        }

        protected override Boolean OnLoad()
        {
            this.EnsureServicesBound();

            // Binds again the moment services are up, in case the host called this before
            // RTSSPerformancePlugin.Load — see ActionReadiness's remarks.
            ActionReadiness.SubscribeAndCatchUp(this.OnServicesReady);

            return true;
        }

        protected override Boolean OnUnload()
        {
            ActionReadiness.Unsubscribe(this.OnServicesReady);

            if (this._coordinator != null)
            {
                this._coordinator.TelemetryUpdated -= this.OnTelemetryUpdated;
                this._coordinator = null;
            }

            if (this._effectiveCapService != null)
            {
                this._effectiveCapService.CapChanged -= this.OnCapChanged;
                this._effectiveCapService = null;
            }

            if (this._availabilityMonitor != null)
            {
                this._availabilityMonitor.StatusChanged -= this.OnAvailabilityChanged;
                this._availabilityMonitor = null;
            }

            if (this._pollScheduler != null)
            {
                this._pollScheduler.Tick -= this.OnPollTick;
                this._pollScheduler = null;
            }

            this._advisor = null;
            this._bottleneckAnalysis = null;
            this._frameTimeTracker = null;
            this._zeroPointOnePercentLowTracker = null;
            this._rtssReader = null;
            this._profileWriter = null;
            this._helperRunner = null;
            this._healthState = null;
            this._fpsCapFolder = null;
            return true;
        }

        protected override void EnsureServicesBound()
        {
            var plugin = RTSSPerformancePlugin.Instance;

            if (plugin == null)
            {
                return;
            }

            if (this._coordinator == null && plugin.TelemetryCoordinator != null)
            {
                this._coordinator = plugin.TelemetryCoordinator;
                this._coordinator.TelemetryUpdated += this.OnTelemetryUpdated;
            }

            if (this._capStateService == null && plugin.CapStateService != null)
            {
                this._capStateService = plugin.CapStateService;
            }

            if (this._effectiveCapService == null)
            {
                this._effectiveCapService = this._capStateService ?? (IEffectiveFpsCapService)plugin.EffectiveFpsCap;
                if (this._effectiveCapService != null)
                {
                    this._effectiveCapService.CapChanged += this.OnCapChanged;
                }
            }

            this._advisor = this._advisor ?? plugin.FpsAdvisor ?? new FpsAdvisorService();
            this._bottleneckAnalysis = this._bottleneckAnalysis ?? plugin.BottleneckAnalysis;
            this._frameTimeTracker = this._frameTimeTracker ?? plugin.FrameTimeTracker;
            this._zeroPointOnePercentLowTracker = this._zeroPointOnePercentLowTracker ?? plugin.ZeroPointOnePercentLowTracker;
            this._rtssReader = this._rtssReader ?? plugin.Reader;
            this._profileWriter = this._profileWriter ?? plugin.ProfileWriter;
            this._helperRunner = this._helperRunner ?? plugin.ProfileHelperRunner;
            this._healthState = this._healthState ?? plugin.HealthState;
            this._fpsCapFolder = this._fpsCapFolder ?? FpsCapFolder.Instance;

            if (this._availabilityMonitor == null && plugin.AvailabilityMonitor != null)
            {
                this._availabilityMonitor = plugin.AvailabilityMonitor;
                this._availabilityMonitor.StatusChanged += this.OnAvailabilityChanged;
            }

            if (this._pollScheduler == null && plugin.PollScheduler != null)
            {
                this._pollScheduler = plugin.PollScheduler;
                this._pollScheduler.Tick += this.OnPollTick;
            }
        }

        private void OnTelemetryUpdated(Object sender, TelemetrySnapshot snapshot) => this.NotifyIfChanged();

        private void OnCapChanged(Object sender, Int32 newCap)
        {
            this._advisor?.NotifyCapApplied(newCap);
            this.ForceNotify();
        }

        private void OnPollTick(Object sender, PollTickEventArgs e) => this.NotifyIfChanged();

        private void OnAvailabilityChanged(Object sender, RTSSAvailability status) => this.NotifyIfChanged();

        private void OnServicesReady(Object sender, EventArgs e) => this.NotifyIfChanged();

        private FpsAdvice ReadRecommendation()
        {
            this.EnsureServicesBound();

            if (this._coordinator?.CurrentSnapshot?.AdvisorAdvice != null &&
                this._coordinator.CurrentSnapshot.AdvisorAdvice.State != FpsAdvisoryState.Unknown)
            {
                return this._coordinator.CurrentSnapshot.AdvisorAdvice;
            }

            var plugin = RTSSPerformancePlugin.Instance;
            var currentCap = this._effectiveCapService?.CurrentCap ??
                (this._profileWriter != null && this._profileWriter.TryReadFpsCeiling(this._profileWriter.ActiveProfileName, out var c) ? c : 0);
            var cpuUsage = plugin?.CpuSource?.HasCpuUsageReading == true ? (Double?)plugin.CpuSource.CpuUsagePercent : null;
            var gpuUsage = plugin?.GpuSource?.HasGpuUsageReading == true ? (Double?)plugin.GpuSource.GpuUsagePercent : null;
            var processName = this._coordinator?.CurrentSnapshot?.Rtss?.ProcessFileName ??
                this._rtssReader?.LastSnapshot?.ProcessFileName;
            var history = plugin?.PerformanceHistory?.GetSnapshots();

            return this._advisor?.Recommend(
                this._frameTimeTracker?.Current,
                this._zeroPointOnePercentLowTracker?.Current,
                this._bottleneckAnalysis?.Current,
                currentCap,
                this._availabilityMonitor?.IsAvailable ?? false,
                this._healthState,
                cpuUsage,
                gpuUsage,
                processName,
                history) ??
                new FpsAdvice(FpsAdvisoryState.Unknown, null);
        }

        private void NotifyIfChanged() => this.CoreNotifyIfChanged();

        /// <summary>How long a recommendation stays on the key before a different one may replace it.</summary>
        internal static Int64 MinimumDisplayHoldMs = 3000;

        private readonly Object _holdSync = new Object();
        private String _heldDisplay;
        private ColorTier _heldTier = ColorTier.Neutral;
        private Int64 _heldSinceTicks;
        private Boolean _hasHeld;

        /// <summary>
        /// The recommendation to show, held for at least <see cref="MinimumDisplayHoldMs"/> once
        /// displayed so a fast-changing advisor cannot flicker the key. The hold is bypassed when
        /// there is nothing meaningful to hold (RTSS gone, no game) so stale advice never lingers.
        /// </summary>
        private (String Display, ColorTier Tier) ResolveDisplay()
        {
            var freshAdvice = this.ReadRecommendation();
            var fresh = this.ReadDisplayRecommendation();
            var freshTier = freshAdvice?.Tier ?? ColorTier.Neutral;
            var now = Environment.TickCount64;

            lock (this._holdSync)
            {
                var live = (this._availabilityMonitor?.IsAvailable ?? false) && this.HasForegroundGame();

                if (!live || fresh == null)
                {
                    // Nothing to show: drop the hold only if the minimum time has passed or the
                    // source is gone entirely.
                    if (!live || !this._hasHeld || now - this._heldSinceTicks >= MinimumDisplayHoldMs)
                    {
                        this._hasHeld = false;
                        this._heldDisplay = null;
                        return (fresh, freshTier);
                    }

                    return (this._heldDisplay, this._heldTier);
                }

                if (!this._hasHeld || String.Equals(this._heldDisplay, fresh, StringComparison.Ordinal))
                {
                    if (!this._hasHeld)
                    {
                        this._heldSinceTicks = now;
                    }

                    this._hasHeld = true;
                    this._heldDisplay = fresh;
                    this._heldTier = freshTier;
                    return (fresh, freshTier);
                }

                if (now - this._heldSinceTicks >= MinimumDisplayHoldMs)
                {
                    this._heldDisplay = fresh;
                    this._heldTier = freshTier;
                    this._heldSinceTicks = now;
                    return (fresh, freshTier);
                }

                return (this._heldDisplay, this._heldTier);
            }
        }

        private void ClearDisplayHold()
        {
            lock (this._holdSync)
            {
                this._hasHeld = false;
                this._heldDisplay = null;
            }
        }

        internal String ReadDisplayRecommendation()
        {
            var recommendation = this.ReadRecommendation()?.Label;
            if (String.IsNullOrWhiteSpace(recommendation))
            {
                return null;
            }

            if (String.Equals(recommendation, "FPS STABLE", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(recommendation, "STABLE FPS", StringComparison.OrdinalIgnoreCase))
            {
                return "STABLE FPS";
            }

            if (String.Equals(recommendation, "FPS RECOVERY", StringComparison.OrdinalIgnoreCase))
            {
                return "FPS RECOVERY";
            }

            if (FpsAdvisorService.TryDecodeRecommendedFps(recommendation, out var recommendedFps) && recommendedFps > 0)
            {
                return $"TRY {recommendedFps} FPS?";
            }

            return null;
        }

        private Boolean HasForegroundGame()
        {
            var rtssUp = this._availabilityMonitor?.IsAvailable ?? false;
            if (!rtssUp)
            {
                return false;
            }

            var snapshot = this._coordinator?.CurrentSnapshot?.Rtss ?? this._rtssReader?.LastSnapshot;
            if (snapshot == null)
            {
                return false;
            }

            return snapshot.SelectionReason != ActiveAppSelection.None && snapshot.InstantaneousFps >= 1.0;
        }

        protected override String BuildVisualSignature()
        {
            var rtssUp = this._availabilityMonitor?.IsAvailable ?? false;
            var (heldDisplay, heldTier) = this.ResolveDisplay();
            var disp = heldDisplay ?? "none";
            var tier = heldTier.ToString();
            return this.HasForegroundGame() + "|" + disp + "|" + tier + "|" + (rtssUp ? "up" : "down");
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            this.EnsureServicesBound();

            var rtssUp = this._availabilityMonitor?.IsAvailable ?? false;
            var (display, tier) = this.ResolveDisplay();

            return KeyRenderer.RenderFpsAdvisorKey(
                imageSize,
                display,
                this.HasForegroundGame(),
                tier,
                isRtssAvailable: rtssUp);
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "FPS Advisor";

        protected override void RunCommand(String actionParameter)
        {
            this.EnsureServicesBound();

            var recommendation = !String.IsNullOrWhiteSpace(actionParameter)
                ? actionParameter
                : this.ReadRecommendation().Label;
            PluginLog.Info($"[FPS ADVISOR ACTION] clicked recommendation='{recommendation}'");

            if (!TryDecodeRecommendedFps(recommendation, out var recommendedFps) || recommendedFps <= 0)
            {
                PluginLog.Warning($"[FPS ADVISOR ACTION] no actionable target in recommendation='{recommendation ?? "<null>"}'");
                this.NotifyIfChanged();
                return;
            }

            // CRITICAL: Reuse the exact centralized FPS Cap state service
            var capService = this._capStateService ?? RTSSPerformancePlugin.Instance?.CapStateService;
            if (capService != null)
            {
                PluginLog.Info($"[FPS ADVISOR ACTION] requesting preset {recommendedFps} via central FpsCapStateService.");
                this.LastOperationTask = capService.RequestSetCapAsync(recommendedFps);
            }
            else
            {
                var fpsCap = this._fpsCapFolder ?? FpsCapFolder.Instance;
                if (fpsCap != null)
                {
                    PluginLog.Info($"[FPS ADVISOR ACTION] delegating preset {recommendedFps} directly to FpsCapFolder.");
                    fpsCap.RunCommand("fps:" + recommendedFps.ToString(CultureInfo.InvariantCulture));
                    this.LastOperationTask = fpsCap.LastOperationTask;
                }
                else
                {
                    PluginLog.Warning($"[FPS ADVISOR ACTION] No FPS cap service available to apply preset {recommendedFps}.");
                }
            }

            // Reset advisor evaluation state so the new cap is evaluated against incoming telemetry
            this._advisor?.NotifyCapApplied(recommendedFps);

            // The applied recommendation is spent; let the next one show immediately.
            this.ClearDisplayHold();

            // Re-acquire cycle to update all consumers
            this._coordinator?.AcquireCycle();
            this.NotifyIfChanged();
        }

        private static Boolean TryDecodeRecommendedFps(String recommendation, out Int32 recommendedFps) =>
            FpsAdvisorService.TryDecodeRecommendedFps(recommendation, out recommendedFps);

        internal String DoBuildVisualSignature() => this.BuildVisualSignature();
        internal BitmapImage DoGetCommandImage(PluginImageSize size) => this.GetCommandImage(null, size);
        internal Boolean DoHasForegroundGame() => this.HasForegroundGame();
    }
}