namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Threading;

    /// <summary>
    /// Executes exactly ONE telemetry acquisition cycle per scheduler tick:
    /// Samples RTSS, CPU, GPU, RAM, effective FPS cap, and advisor metrics once,
    /// publishes ONE immutable <see cref="TelemetrySnapshot"/>, and notifies all consumers.
    /// </summary>
    public sealed class TelemetryCoordinatorService : ITelemetryCoordinatorService
    {
        private readonly Object _sync = new Object();
        private readonly IRTSSReaderService _reader;
        private readonly IRTSSAvailabilityMonitorService _availabilityMonitor;
        private readonly IFrameTimeTrackerService _frameTimeTracker;
        private readonly IZeroPointOnePercentLowTrackerService _lowTracker;
        private readonly ICpuUsageSource _cpuSource;
        private readonly IGpuUsageSource _gpuSource;
        private readonly IRamUsageSource _ramSource;
        private readonly IEffectiveFpsCapService _effectiveCapService;
        private readonly IRTSSProfileWriterService _profileWriter;
        private readonly BottleneckAnalysisService _bottleneckAnalysis;
        private readonly PerformanceHistoryService _performanceHistory;
        private readonly FpsAdvisorService _fpsAdvisor;
        private readonly FpsHealthStateService _healthState;
        private readonly PerformanceStatusService _performanceStatus;
        private readonly RTSSStatisticsRecordingService _statisticsRecordingService;

        private TelemetrySnapshot _currentSnapshot = TelemetrySnapshot.Empty();
        private String _lastProcessName;

        public TelemetrySnapshot CurrentSnapshot => Volatile.Read(ref this._currentSnapshot);

        public event EventHandler<TelemetrySnapshot> TelemetryUpdated;

        public TelemetryCoordinatorService(
            IRTSSReaderService reader,
            IRTSSAvailabilityMonitorService availabilityMonitor,
            IFrameTimeTrackerService frameTimeTracker,
            IZeroPointOnePercentLowTrackerService lowTracker,
            ICpuUsageSource cpuSource,
            IGpuUsageSource gpuSource,
            IRamUsageSource ramSource,
            IEffectiveFpsCapService effectiveCapService,
            IRTSSProfileWriterService profileWriter,
            BottleneckAnalysisService bottleneckAnalysis,
            PerformanceHistoryService performanceHistory,
            FpsAdvisorService fpsAdvisor,
            FpsHealthStateService healthState,
            PerformanceStatusService performanceStatus = null,
            RTSSStatisticsRecordingService statisticsRecordingService = null)
        {
            this._reader = reader;
            this._availabilityMonitor = availabilityMonitor;
            this._frameTimeTracker = frameTimeTracker;
            this._lowTracker = lowTracker;
            this._cpuSource = cpuSource;
            this._gpuSource = gpuSource;
            this._ramSource = ramSource;
            this._effectiveCapService = effectiveCapService;
            this._profileWriter = profileWriter;
            this._bottleneckAnalysis = bottleneckAnalysis;
            this._performanceHistory = performanceHistory;
            this._fpsAdvisor = fpsAdvisor;
            this._healthState = healthState;
            this._performanceStatus = performanceStatus;
            this._statisticsRecordingService = statisticsRecordingService;
        }

        public TelemetrySnapshot AcquireCycle()
        {
            lock (this._sync)
            {
                var rtssAvailable = this._availabilityMonitor?.IsAvailable ?? false;
                var rtssSnapshot = rtssAvailable ? this._reader?.ReadActiveApplicationSnapshot() : null;

                if (!rtssAvailable)
                {
                    this._statisticsRecordingService?.HandleRTSSUnavailable();
                }
                else if (rtssSnapshot == null)
                {
                    this._statisticsRecordingService?.StopManagingApplication();
                }
                else
                {
                    this._statisticsRecordingService?.EnsureStatisticsRecordingEnabled(rtssSnapshot);
                }
                if (rtssSnapshot != null)
                {
                    this._frameTimeTracker?.AddSample(rtssSnapshot);
                    this._lowTracker?.AddSample(rtssSnapshot);
                }

                var frameStats = this._frameTimeTracker?.Current;
                var lowStats = this._lowTracker?.Current;

                var hasCpu = this._cpuSource != null && this._cpuSource.HasCpuUsageReading;
                var cpuUsage = hasCpu ? this._cpuSource.CpuUsagePercent : 0.0;

                var hasGpu = this._gpuSource != null && this._gpuSource.HasGpuUsageReading;
                var gpuUsage = hasGpu ? this._gpuSource.GpuUsagePercent : 0.0;

                var hasRam = this._ramSource != null && this._ramSource.HasRamUsageReading;
                var ramUsage = hasRam ? this._ramSource.RamUsagePercent : 0.0;

                var processName = rtssSnapshot?.ProcessFileName;
                if (this._lastProcessName != null && !String.Equals(processName, this._lastProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    this._frameTimeTracker?.Reset();
                    this._lowTracker?.Reset();
                    this._bottleneckAnalysis?.Reset();
                    this._performanceHistory?.Reset();
                    this._fpsAdvisor?.DecisionEngine?.Reset();
                }
                this._lastProcessName = processName;

                var effectiveCap = this._effectiveCapService != null
                    ? this._effectiveCapService.UpdateEffectiveCap(processName, this._profileWriter)
                    : 0;

                if (rtssSnapshot != null && this._performanceHistory != null)
                {
                    var ft = rtssSnapshot.FrameTimeMilliseconds > 0
                        ? rtssSnapshot.FrameTimeMilliseconds
                        : (frameStats?.AverageFrameTimeMs > 0
                            ? frameStats.AverageFrameTimeMs
                            : (rtssSnapshot.InstantaneousFps > 0 ? 1000.0 / rtssSnapshot.InstantaneousFps : (Double?)null));

                    var oneLow = frameStats?.SampleCount > 0 && frameStats.HasNativeOnePercentLow && frameStats.NativeOnePercentLowFps > 0
                        ? frameStats.NativeOnePercentLowFps
                        : (rtssSnapshot.HasNativeOnePercentLow && rtssSnapshot.NativeOnePercentLowFps > 0 ? rtssSnapshot.NativeOnePercentLowFps : (Double?)null);

                    var zeroOneLow = lowStats?.SampleCount > 0 && lowStats.HasNativeZeroPointOnePercentLow && lowStats.ZeroPointOnePercentLowFps > 0
                        ? lowStats.ZeroPointOnePercentLowFps
                        : (rtssSnapshot.HasNativeZeroPointOnePercentLow && rtssSnapshot.NativeZeroPointOnePercentLowFps > 0 ? rtssSnapshot.NativeZeroPointOnePercentLowFps : (Double?)null);

                    var capturedAt = rtssSnapshot.CapturedAtUtc.Kind == DateTimeKind.Utc && rtssSnapshot.CapturedAtUtc != default
                        ? rtssSnapshot.CapturedAtUtc
                        : DateTime.UtcNow;

                    this._performanceHistory.AddSnapshot(new PerformanceSnapshot(
                        capturedAt,
                        fps: rtssSnapshot.InstantaneousFps > 0 ? rtssSnapshot.InstantaneousFps : (Double?)null,
                        frameTimeMilliseconds: ft,
                        onePercentLowFps: oneLow,
                        zeroPointOnePercentLowFps: zeroOneLow,
                        cpuUsagePercent: hasCpu ? cpuUsage : (Double?)null,
                        gpuUsagePercent: hasGpu ? gpuUsage : (Double?)null,
                        ramUsagePercent: hasRam ? ramUsage : (Double?)null));

                    this._bottleneckAnalysis?.Analyze(this._performanceHistory, effectiveCap);
                    this._performanceStatus?.Analyze(this._performanceHistory, effectiveCap);
                }

                var bottleneck = this._bottleneckAnalysis?.Current;

                var advice = this._fpsAdvisor != null
                    ? this._fpsAdvisor.Recommend(
                        frameStats,
                        lowStats,
                        bottleneck,
                        effectiveCap,
                        rtssAvailable,
                        this._healthState,
                        hasCpu ? (Double?)cpuUsage : null,
                        hasGpu ? (Double?)gpuUsage : null,
                        processName,
                        this._performanceHistory?.GetSnapshots())
                    : new FpsAdvice(FpsAdvisoryState.Unknown, null, 0);

                var snapshot = new TelemetrySnapshot(
                    DateTime.UtcNow,
                    rtssAvailable,
                    rtssSnapshot,
                    frameStats,
                    lowStats,
                    cpuUsage,
                    hasCpu,
                    gpuUsage,
                    hasGpu,
                    ramUsage,
                    hasRam,
                    effectiveCap,
                    bottleneck,
                    advice);

                Volatile.Write(ref this._currentSnapshot, snapshot);
                this.TelemetryUpdated?.Invoke(this, snapshot);
                return snapshot;
            }
        }
    }
}
