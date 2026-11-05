namespace Loupedeck.RTSSPerformancePlugin.Actions
{
    using System;

    using Loupedeck.RTSSPerformancePlugin.Helpers;
    using Loupedeck.RTSSPerformancePlugin.Services;

    /// <summary>
    /// The Performance Status key: synthesizes overall system performance condition from live telemetry metrics.
    /// Communicates semantic interpretation (Balanced, Performance Watch, GPU Load High, CPU Load High, FPS Dropping, Memory Pressure).
    /// </summary>
    public class PerformanceStatusCommand : ThrottledDynamicCommand
    {
        private readonly Object _sync = new Object();

        private PerformanceStatusService _performanceStatus;
        private BottleneckAnalysisService _analysis;
        private RTSSProfileWriterService _profileWriter;
        private IFrameTimeTrackerService _frameTimeTracker;
        private IRTSSReaderService _reader;
        private IRTSSAvailabilityMonitorService _availabilityMonitor;
        private IPollTickSchedulerService _pollScheduler;
        private IEffectiveFpsCapService _effectiveCapService;
        private ITelemetryCoordinatorService _coordinator;
        private Func<Int64> _timeProvider;

        public PerformanceStatusCommand()
            : base("Performance Status", "At-a-glance indication of overall system performance condition.", null)
        {
            this.GroupName = "2. Performance";
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

            lock (this._sync)
            {
                if (this._coordinator != null)
                {
                    this._coordinator.TelemetryUpdated -= this.OnTelemetryUpdated;
                    this._coordinator = null;
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

                this._performanceStatus = null;
                this._analysis = null;
                this._profileWriter = null;
                this._frameTimeTracker = null;
                this._reader = null;
                this._effectiveCapService = null;
            }

            return true;
        }

        protected override void EnsureServicesBound()
        {
            var plugin = RTSSPerformancePlugin.Instance;

            if (plugin == null)
            {
                return;
            }

            lock (this._sync)
            {
                if (this._coordinator == null && plugin.TelemetryCoordinator != null)
                {
                    this._coordinator = plugin.TelemetryCoordinator;
                    this._coordinator.TelemetryUpdated += this.OnTelemetryUpdated;
                }

                this._performanceStatus = this._performanceStatus ?? plugin.PerformanceStatus;
                this._analysis = this._analysis ?? plugin.BottleneckAnalysis;
                this._profileWriter = this._profileWriter ?? plugin.ProfileWriter;
                this._frameTimeTracker = this._frameTimeTracker ?? plugin.FrameTimeTracker;
                this._reader = this._reader ?? plugin.Reader;
                this._effectiveCapService = this._effectiveCapService ?? plugin.EffectiveFpsCap;

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
        }

        private Boolean IsRtssAvailable
        {
            get
            {
                this.EnsureServicesBound();
                return this._availabilityMonitor?.IsAvailable ??
                       RTSSPerformancePlugin.Instance?.AvailabilityMonitor?.IsAvailable ??
                       false;
            }
        }

        private Int32 _renderVersion;

        private void OnTelemetryUpdated(Object sender, TelemetrySnapshot snapshot) => this.NotifyIfChanged();

        private void OnPollTick(Object sender, PollTickEventArgs e) => this.NotifyIfChanged();

        private void OnAvailabilityChanged(Object sender, RTSSAvailability status)
        {
            PluginLog.Info($"[PERFORMANCE STATUS KEY] RTSS is {status}.");
            this.ForceNotify();
        }

        private void OnServicesReady(Object sender, EventArgs e) => this.ForceNotify();

        private (String Label, ColorTier Tier) ReadStatus()
        {
            var rtssUp = this.IsRtssAvailable;
            var snapshot = this._coordinator?.CurrentSnapshot?.Rtss ?? this._reader?.LastSnapshot;
            var hasForegroundGame = rtssUp && snapshot != null &&
                snapshot.SelectionReason != ActiveAppSelection.None && snapshot.InstantaneousFps >= 1.0;

            var perfResult = this._performanceStatus?.Current;
            var bottleneckResult = this._analysis?.Current;

            var hasLiveData = hasForegroundGame &&
                ((perfResult != null && perfResult.SampleCount > 0) || (bottleneckResult != null && bottleneckResult.SampleCount > 0));

            if (!hasLiveData)
            {
                return ("NO GAME", ColorTier.Neutral);
            }

            var nowMs = this._timeProvider != null ? this._timeProvider() : Environment.TickCount64;

            if (perfResult != null && perfResult.SampleCount > 0)
            {
                return PerformanceStatusResolver.ResolveStatus(true, perfResult, nowMs);
            }

            var frameStats = this._frameTimeTracker?.Current;
            var processName = snapshot?.ProcessFileName;
            var cap = this._effectiveCapService != null && this._effectiveCapService.CurrentCap > 0
                ? this._effectiveCapService.CurrentCap
                : (this._profileWriter != null && this._profileWriter.TryReadEffectiveFpsCeiling(processName, out var limit) ? limit : 0);

            return BottleneckStatusResolver.ResolveStatus(true, bottleneckResult, frameStats, cap, 0, nowMs);
        }

        private String ReadLabel() => this.ReadStatus().Label;

        private void NotifyIfChanged() => this.CoreNotifyIfChanged();

        protected override String BuildVisualSignature()
        {
            var rtssUp = this.IsRtssAvailable;
            var status = this.ReadStatus();
            return $"{status.Label}|{status.Tier}|{(rtssUp ? "up" : "down")}";
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            this.EnsureServicesBound();

            var rtssUp = this.IsRtssAvailable;
            var status = this.ReadStatus();
            var image = KeyRenderer.RenderPerformanceStatusLabelKey(imageSize, status.Label, status.Tier, isRtssAvailable: rtssUp);

            var version = System.Threading.Interlocked.Increment(ref this._renderVersion);

            PluginLog.InfoThrottled(
                "perf-status-key-render",
                $"{status.Label}|{status.Tier}|{rtssUp}",
                $"[PERFORMANCE STATUS KEY RENDER] label={status.Label} tier={status.Tier} isRtssAvailable={rtssUp} " +
                $"size={(Int32)imageSize} renderVersion={version}");

            return image;
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "Performance Status";

        internal PerformanceStatusCommand(
            PerformanceStatusService performanceStatus = null,
            BottleneckAnalysisService analysis = null,
            RTSSProfileWriterService profileWriter = null,
            IFrameTimeTrackerService frameTimeTracker = null,
            RTSSReaderService reader = null,
            IRTSSAvailabilityMonitorService availabilityMonitor = null,
            IPollTickSchedulerService pollScheduler = null,
            IEffectiveFpsCapService effectiveCapService = null,
            Func<Int64> timeProvider = null)
            : this()
        {
            this._performanceStatus = performanceStatus;
            this._analysis = analysis;
            this._profileWriter = profileWriter;
            this._frameTimeTracker = frameTimeTracker;
            this._reader = reader;
            this._availabilityMonitor = availabilityMonitor;
            this._pollScheduler = pollScheduler;
            this._effectiveCapService = effectiveCapService;
            this._timeProvider = timeProvider;
            if (this._availabilityMonitor != null)
            {
                this._availabilityMonitor.StatusChanged += this.OnAvailabilityChanged;
            }
        }

        internal String DoBuildVisualSignature() => this.BuildVisualSignature();

        internal (String Label, ColorTier Tier) DoReadStatus() => this.ReadStatus();

        internal BitmapImage DoGetCommandImage(PluginImageSize imageSize) => this.GetCommandImage(null, imageSize);

        internal String DoGetCommandDisplayName(PluginImageSize imageSize) => this.GetCommandDisplayName(null, imageSize);
    }
}
