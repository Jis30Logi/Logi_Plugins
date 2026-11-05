namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using Loupedeck.RTSSPerformancePlugin.Interop;

    /// <summary>
    /// An immutable snapshot representing a single complete telemetry acquisition cycle across the plugin.
    /// Produced once per tick by <see cref="TelemetryCoordinatorService"/> and consumed by all LCD actions.
    /// </summary>
    public sealed class TelemetrySnapshot
    {
        public DateTime TimestampUtc { get; }

        public Boolean IsRtssAvailable { get; }

        public RTSSFrameSnapshot Rtss { get; }

        public FrameTimeStatistics FrameStats { get; }

        public ZeroPointOnePercentLowStatistics LowStats { get; }

        public Double CpuUsagePercent { get; }

        public Boolean HasCpuUsage { get; }

        public Double GpuUsagePercent { get; }

        public Boolean HasGpuUsage { get; }

        public Double RamUsagePercent { get; }

        public Boolean HasRamUsage { get; }

        public Int32 EffectiveFpsCap { get; }

        public BottleneckAnalysisResult Bottleneck { get; }

        public FpsAdvice AdvisorAdvice { get; }

        public TelemetrySnapshot(
            DateTime timestampUtc,
            Boolean isRtssAvailable,
            RTSSFrameSnapshot rtss,
            FrameTimeStatistics frameStats,
            ZeroPointOnePercentLowStatistics lowStats,
            Double cpuUsagePercent,
            Boolean hasCpuUsage,
            Double gpuUsagePercent,
            Boolean hasGpuUsage,
            Double ramUsagePercent,
            Boolean hasRamUsage,
            Int32 effectiveFpsCap,
            BottleneckAnalysisResult bottleneck,
            FpsAdvice advisorAdvice)
        {
            this.TimestampUtc = timestampUtc;
            this.IsRtssAvailable = isRtssAvailable;
            this.Rtss = rtss;
            this.FrameStats = frameStats;
            this.LowStats = lowStats;
            this.CpuUsagePercent = cpuUsagePercent;
            this.HasCpuUsage = hasCpuUsage;
            this.GpuUsagePercent = gpuUsagePercent;
            this.HasGpuUsage = hasGpuUsage;
            this.RamUsagePercent = ramUsagePercent;
            this.HasRamUsage = hasRamUsage;
            this.EffectiveFpsCap = effectiveFpsCap;
            this.Bottleneck = bottleneck;
            this.AdvisorAdvice = advisorAdvice;
        }

        public static TelemetrySnapshot Empty(Boolean isRtssAvailable = false) =>
            new TelemetrySnapshot(
                DateTime.UtcNow,
                isRtssAvailable,
                null,
                null,
                null,
                0,
                false,
                0,
                false,
                0,
                false,
                0,
                null,
                new FpsAdvice(FpsAdvisoryState.Unknown, "WAITING FOR LIVE DATA", 0));
    }
}
