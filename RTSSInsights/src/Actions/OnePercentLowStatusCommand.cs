namespace Loupedeck.RTSSPerformancePlugin.Actions
{
    using System;
    using System.Globalization;

    using Loupedeck.RTSSPerformancePlugin.Helpers;
    using Loupedeck.RTSSPerformancePlugin.Services;

    /// <summary>
    /// The 1% Low key: RTSS's native 1% low field for the currently selected RTSS application.
    /// The coordinator's latest RTSS snapshot is the sole source of truth. No tracker, calculated,
    /// live-FPS, or cached value is eligible for display.
    ///
    /// Like <see cref="FrameTimeStatusCommand"/>, this key holds no cached reading of its own.
    /// <see cref="IFrameTimeTrackerService.Current"/> is read fresh on every tick, and
    /// <see cref="FrameTimeTrackerService"/> itself clears that window the instant the active
    /// source is lost (see <see cref="RTSSPerformancePlugin"/>'s poll loop) or changes to a
    /// different process id (see <see cref="FrameTimeTrackerService.AddSample"/>) â€" so a value on
    /// this key can never be a blend of two applications' frame times, and a source going away
    /// shows up here as the "1% LOW" wordmark on the very next tick rather than as a held-over
    /// number.
    /// </summary>
    public class OnePercentLowStatusCommand : ThrottledDynamicCommand
    {
        private readonly Object _sync = new Object();

        private IRTSSReaderService _reader;
        private IFrameTimeTrackerService _frameTimeTracker;
        private IRTSSAvailabilityMonitorService _availabilityMonitor;
        private IPollTickSchedulerService _pollScheduler;
        private ITelemetryCoordinatorService _coordinator;

        /// <summary>Visible state as of the last notification, so the host is told only on a change.</summary>
        private Boolean _wasShowingNumber;

        private Int32 _renderVersion;

        public OnePercentLowStatusCommand()
            : base("1% Low", "Live RTSS 1% low FPS for the currently active source.", null)
        {
            this.GroupName = "2. Performance";
        }

        protected override Boolean OnLoad()
        {
            this.EnsureServicesBound();

            // Binds again the moment services are up, in case the host called this before
            // RTSSPerformancePlugin.Load — see ActionReadiness's remarks.
            ActionReadiness.SubscribeAndCatchUp(this.OnServicesReady);

            PluginLog.Info(
                $"[1% LOW KEY] Registered as a dynamic command; " +
                $"services {(this._frameTimeTracker == null ? "pending" : "bound")}.");

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

                if (this._frameTimeTracker != null)
                {
                    this._frameTimeTracker.StatisticsUpdated -= this.OnStatisticsUpdated;
                    this._frameTimeTracker = null;
                }

                this._reader = null;
            }

            return true;
        }

        /// <summary>
        /// Binds whatever of the service graph exists now, and can be called again for the rest.
        /// Mirrors <see cref="FpsStatusCommand.EnsureServicesBound"/> for the same reason: the
        /// runtime may construct this action before <see cref="RTSSPerformancePlugin.Load"/> has
        /// built its services.
        /// </summary>
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

                this._reader = this._reader ?? plugin.Reader;

                // Bind to the SAME tracker that powers Live FPS, Frametime, and the rest of the
                // pipeline — populated by the main poll loop. This guarantees 1% Low uses the same
                // live RTSS frame samples as those metrics.
                if (this._frameTimeTracker == null && plugin.FrameTimeTracker != null)
                {
                    this._frameTimeTracker = plugin.FrameTimeTracker;
                    this._frameTimeTracker.StatisticsUpdated -= this.OnStatisticsUpdated;
                    this._frameTimeTracker.StatisticsUpdated += this.OnStatisticsUpdated;
                }

                if (this._availabilityMonitor == null && plugin.AvailabilityMonitor != null)
                {
                    this._availabilityMonitor = plugin.AvailabilityMonitor;
                    this._availabilityMonitor.StatusChanged += this.OnAvailabilityChanged;
                }

                if (this._pollScheduler == null && plugin.PollScheduler != null)
                {
                    // The only driver this key needs, and for the same reason FpsStatusCommand
                    // uses it: a source loss clears the tracker's window without a new sample ever
                    // arriving, so nothing would raise StatisticsUpdated to say so. The scheduler
                    // ticks regardless of whether RTSS produced anything.
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

        private void OnTelemetryUpdated(Object sender, TelemetrySnapshot snapshot)
        {
            // The published snapshot is read directly by ReadCurrentOnePercentLow. In
            // particular, an absent/zero native value must invalidate the display immediately;
            // retaining a previous positive value here would turn a transient cache into a
            // competing source of truth.
            this.NotifyIfChanged();
        }

        private void OnPollTick(Object sender, PollTickEventArgs e) => this.NotifyIfChanged();

        private void OnStatisticsUpdated(Object sender, FrameTimeStatistics statistics)
        {
            var (hasLiveData, value) = this.ReadCurrentOnePercentLow();
            var updated = !String.Equals(this._notifiedSignature, this.BuildVisualSignature(), StringComparison.Ordinal);

            PluginLog.InfoThrottled(
                "one-percent-low-refresh",
                $"{statistics?.ProcessId}:{statistics?.SampleCount ?? 0}:{value:F1}:{hasLiveData}",
                $"[1% LOW STATS] pid={statistics?.ProcessId} samples={statistics?.SampleCount ?? 0} " +
                $"hasNative={statistics?.HasNativeOnePercentLow} native={statistics?.NativeOnePercentLowFps:F1} " +
                $"calc={statistics?.CalculatedOnePercentLowFps:F1} value={value:F1} " +
                $"hasLiveData={hasLiveData} updated={updated}");

            this.NotifyIfChanged();
        }

        private void OnAvailabilityChanged(Object sender, RTSSAvailability status)
        {
            if (status != RTSSAvailability.Available)
            {
                PluginLog.Info(
                    $"[1% LOW KEY] RTSS is {status}; the reading is discarded and the key returns "
                    + "to the wordmark until RTSS reports again.");
            }

            PluginLog.Info($"[1% LOW KEY] RTSS is {status}.");
            this.ForceNotify();
        }

        private void OnServicesReady(Object sender, EventArgs e) => this.ForceNotify();

        /// <summary>
        /// The current reading, fetching directly from RTSS SDK like Live FPS.
        /// </summary>
        private (Boolean HasLiveData, Double OnePercentLowFps) ReadCurrentOnePercentLow()
        {
            var rtssUp = this.IsRtssAvailable;
            if (!rtssUp)
            {
                this.LogSourceState(0.0, false, 0.0, 0.0, 0, String.Empty, "unknown", false, "RTSSUnavailable", "Unavailable");
                return (false, 0.0);
            }

            var statistics = this._frameTimeTracker?.Current;
            var snapshot = this._coordinator != null
                ? this._coordinator.CurrentSnapshot?.Rtss
                : this._reader?.LastSnapshot;

            if (this._coordinator != null && snapshot == null)
            {
                this.LogSourceState(0.0, false, statistics?.CalculatedOnePercentLowFps ?? 0.0,
                    statistics?.InstantaneousFps ?? 0.0, statistics?.ProcessId ?? 0, String.Empty,
                    "unknown", false, "NoActiveRtssApplication", "Unavailable");
                return (false, 0.0);
            }

            // 1. Authoritative native value from active RTSS snapshot
            if (snapshot != null && snapshot.HasNativeOnePercentLow && IsUsable(snapshot.NativeOnePercentLowFps))
            {
                var nativeValue = snapshot.NativeOnePercentLowFps;
                this.LogSourceState(nativeValue, true,
                    statistics?.CalculatedOnePercentLowFps ?? 0.0, snapshot.InstantaneousFps,
                    snapshot.ProcessId, snapshot.ProcessFileName, snapshot.RtssVersion ?? "unknown",
                    true, String.Empty, "NativeRtss");
                return (true, nativeValue);
            }

            // An active RTSS application whose native 1% low is 0 (or not reported yet) shows 0
            // rather than the bare icon. Tracker state is deliberately not consulted, so an old
            // process/value cannot survive a source change.
            if (snapshot != null)
            {
                this.LogSourceState(0.0, false, statistics?.CalculatedOnePercentLowFps ?? 0.0,
                    snapshot.InstantaneousFps, snapshot.ProcessId, snapshot.ProcessFileName,
                    snapshot.RtssVersion ?? "unknown", true, "NativeRtssValueZero", "NativeRtss");
                return (true, 0.0);
            }

            this.LogSourceState(0.0, false, statistics?.CalculatedOnePercentLowFps ?? 0.0,
                snapshot?.InstantaneousFps ?? statistics?.InstantaneousFps ?? 0.0,
                snapshot?.ProcessId ?? statistics?.ProcessId ?? 0,
                snapshot?.ProcessFileName ?? String.Empty,
                snapshot?.RtssVersion ?? "unknown", false,
                snapshot == null ? "NoActiveRtssApplication" : "NativeRtssValueUnavailable", "Unavailable");
            return (false, 0.0);
        }

        private void LogSourceState(
            Double nativeValue,
            Boolean nativeAvailable,
            Double calculatedValue,
            Double currentFps,
            UInt32 processId,
            String processName,
            String rtssVersion,
            Boolean hasDisplayValue,
            String reason,
            String source)
        {
            var displayed = hasDisplayValue
                ? Math.Round(nativeValue).ToString("0", CultureInfo.InvariantCulture)
                : "NO_DATA";
            var token = $"{processId}:{nativeValue:F3}:{nativeAvailable}:{displayed}:{source}:{reason}";
            PluginLog.InfoThrottled(
                "one-percent-low-source",
                token,
                $"[1% LOW SOURCE] NativeRtss={nativeValue:F3} Calculated={calculatedValue:F3} " +
                $"CurrentFps={currentFps:F3} Displayed={displayed} ProcessId={processId} " +
                $"ProcessName={processName ?? String.Empty} RtssVersion={rtssVersion ?? "unknown"} " +
                $"NativeAvailable={nativeAvailable} Source={source} " +
                $"Reason={(String.IsNullOrEmpty(reason) ? "None" : reason)}");
        }

        private static Boolean _showRawDiagnosticGlobal =
            String.Equals(Environment.GetEnvironmentVariable("RTSS_DIAGNOSTIC_RAW1LOW"), "1", StringComparison.OrdinalIgnoreCase);

        protected override void RunCommand(String actionParameter)
        {
            // Clicking must not expose the raw 1% Low value; the key is display-only.
        }

        private static Boolean IsUsable(Double value) =>
            value >= 0 && !Double.IsNaN(value) && !Double.IsInfinity(value);

        private void NotifyIfChanged()
        {
            this.EnsureServicesBound();

            var (hasLiveData, resolvedValue) = this.ReadCurrentOnePercentLow();
            var signature = this.BuildVisualSignature();
            var actionImageChangedRequested = !String.Equals(this._notifiedSignature, signature, StringComparison.Ordinal);

            this.LogTelemetryTrace(hasLiveData, resolvedValue, signature, actionImageChangedRequested);

            this.CoreNotifyIfChanged();
        }

        private void LogTelemetryTrace(
            Boolean hasLiveData,
            Double resolvedValue,
            String signature,
            Boolean actionImageChangedRequested)
        {
            var snapshot = this._coordinator != null
                ? this._coordinator.CurrentSnapshot?.Rtss
                : this._reader?.LastSnapshot;
            var stats = this._frameTimeTracker?.Current;

            var processName = snapshot?.ProcessFileName ?? "none";
            var processId = snapshot?.ProcessId ?? stats?.ProcessId ?? 0;
            var rtssVersion = snapshot?.RtssVersion ?? "unknown";
            var rawInt = snapshot?.RawOnePercentLow ?? 0;
            var hasNative = snapshot?.HasNativeOnePercentLow ?? false;
            var nativeConverted = snapshot?.NativeOnePercentLowFps ?? 0.0;
            var sampleCount = this._frameTimeTracker?.SampleCount ?? stats?.SampleCount ?? 0;
            var calcLow = stats?.CalculatedOnePercentLowFps ?? 0.0;

            var token = $"{processId}:{rawInt}:{hasNative}:{resolvedValue:F1}:{hasLiveData}:{signature}:{actionImageChangedRequested}";
            PluginLog.InfoThrottled(
                "one-percent-low-telemetry-trace",
                token,
                $"[1% LOW TELEMETRY] ProcessName={processName} ProcessId={processId} " +
                $"RtssVersion={rtssVersion} NativeRawInt={rawInt} HasNative={hasNative} " +
                $"NativeConverted={nativeConverted:F3} SampleCount={sampleCount} " +
                $"CalcLow={calcLow:F3} ResolvedValue={resolvedValue:F3} " +
                $"HasLiveData={hasLiveData} VisualState={signature} " +
                $"ActionImageChangedRequested={actionImageChangedRequested}");
        }

        /// <summary>Records the moment the key starts or stops showing a number, and why.</summary>
        private void LogDisplayStateChange()
        {
            var (hasLiveData, onePercentLowFps) = this.ReadCurrentOnePercentLow();

            if (this._wasShowingNumber == hasLiveData)
            {
                return;
            }

            this._wasShowingNumber = hasLiveData;

            if (hasLiveData)
            {
                PluginLog.Info(
                    $"[1% LOW DISPLAY] Showing {Math.Round(onePercentLowFps).ToString("0", CultureInfo.InvariantCulture)} "
                    + "fps from the latest selected RTSS native field.");

                return;
            }

            var rtssUp = this.IsRtssAvailable;

            var cause = !rtssUp
                ? "RTSS is not readable"
                : "no fresh source has an unmixed window with a reading yet "
                    + "(source lost, source just switched, or RTSS just connected)";

            PluginLog.Info(
                $"[1% LOW DISPLAY] Reverting to the static wordmark: {cause}. "
                + "The previous value is discarded, not held.");
        }

        /// <summary>Everything the key draws from, as one comparable string.</summary>
        protected override String BuildVisualSignature()
        {
            if (_showRawDiagnosticGlobal)
            {
                var snapshot = this._coordinator != null
                    ? this._coordinator.CurrentSnapshot?.Rtss
                    : this._reader?.LastSnapshot;
                var stats = this._frameTimeTracker?.Current;
                var rawInt = snapshot?.RawOnePercentLow ?? 0;
                return $"diag|raw1%|{rawInt}|{(this.IsRtssAvailable ? "up" : "down")}";
            }

            var (hasLiveData, onePercentLowFps) = this.ReadCurrentOnePercentLow();

            return String.Concat(
                hasLiveData ? "live" : "none",
                "|",
                hasLiveData
                    ? FormatDisplayValue(onePercentLowFps).ToString("0", CultureInfo.InvariantCulture)
                    : String.Empty,
                "|",
                this.IsRtssAvailable ? "up" : "down");
        }

        private static Double FormatDisplayValue(Double value)
        {
            return Math.Round(value);
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            this.EnsureServicesBound();

            if (_showRawDiagnosticGlobal)
            {
                var snapshot = this._coordinator != null
                    ? this._coordinator.CurrentSnapshot?.Rtss
                    : this._reader?.LastSnapshot;
                var stats = this._frameTimeTracker?.Current;
                var rawInt = snapshot?.RawOnePercentLow ?? 0;
                return KeyRenderer.RenderRawOnePercentLowDiagnosticKey(imageSize, rawInt);
            }

            var (hasLiveData, onePercentLowFps) = this.ReadCurrentOnePercentLow();
            var rtssUp = this.IsRtssAvailable;
            var before = this._notifiedSignature ?? "none";

            var image = KeyRenderer.RenderPercentileLowKey(imageSize, hasLiveData, onePercentLowFps, isRtssAvailable: rtssUp);

            var after = this.BuildVisualSignature();
            var imageUpdated = !String.Equals(before, after, StringComparison.Ordinal);
            var version = System.Threading.Interlocked.Increment(ref this._renderVersion);

            PluginLog.InfoThrottled(
                "one-percent-low-key-render",
                $"{hasLiveData}:{Math.Round(onePercentLowFps):F0}|{rtssUp}",
                $"[1% LOW RENDER] value={onePercentLowFps:F1} isRtssAvailable={rtssUp} imageUpdated={imageUpdated} before={before} after={after} " +
                $"size={(Int32)imageSize} renderVersion={version}");

            return image;
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "1% Low";

        protected override void OnNotifying(String signature)
        {
            this.LogDisplayStateChange();
        }

        internal OnePercentLowStatusCommand(
            IFrameTimeTrackerService frameTimeTracker = null,
            IRTSSAvailabilityMonitorService availabilityMonitor = null,
            IPollTickSchedulerService pollScheduler = null,
            IRTSSReaderService reader = null,
            ITelemetryCoordinatorService coordinator = null)
            : this()
        {
            this._frameTimeTracker = frameTimeTracker;
            this._availabilityMonitor = availabilityMonitor;
            this._pollScheduler = pollScheduler;
            this._reader = reader;
            this._coordinator = coordinator;
            if (this._availabilityMonitor != null)
            {
                this._availabilityMonitor.StatusChanged += this.OnAvailabilityChanged;
            }
            if (this._frameTimeTracker != null)
            {
                this._frameTimeTracker.StatisticsUpdated += this.OnStatisticsUpdated;
            }
            if (this._coordinator != null)
            {
                this._coordinator.TelemetryUpdated += this.OnTelemetryUpdated;
            }
        }

        internal String DoBuildVisualSignature() => this.BuildVisualSignature();
        internal BitmapImage DoGetCommandImage(PluginImageSize size) => this.GetCommandImage(null, size);
        internal (Boolean hasLiveData, Double onePercentLowFps) DoReadCurrentOnePercentLow() => this.ReadCurrentOnePercentLow();
        internal void DoRunCommand() => this.RunCommand(null);
        internal static void SetDiagnosticMode(Boolean enable) => _showRawDiagnosticGlobal = enable;
    }
}