namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Loupedeck.RTSSPerformancePlugin.Helpers;

    public enum FpsAdvisoryState
    {
        Unknown = 0,
        Stable = 1,
        CpuLimited = 2,
        GpuLimited = 3,
        MemoryLimited = 4,
        MixedPressure = 5,
        PoorFramePacing = 6,
        ThermalLimited = 7,
        Diagnostic = 8,
        Recovery = 9,
        Recommend = 10,
        Degraded = 11,
        Critical = 12,
    }

    public sealed class FpsAdvice
    {
        public FpsAdvice(FpsAdvisoryState state, String label, Int32 recommendedCap = 0, ColorTier tier = ColorTier.Neutral)
        {
            this.State = state;
            this.Label = label;
            this.RecommendedCap = recommendedCap;
            this.Tier = tier;
        }

        public FpsAdvisoryState State { get; }

        public String Label { get; }

        public Int32 RecommendedCap { get; }

        public ColorTier Tier { get; }

        public String RecommendationText =>
            this.State == FpsAdvisoryState.Stable && (this.Label == "STABLE FPS" || this.Label == "FPS STABLE")
                ? "FPS STABLE"
                : this.Label;
    }

    public sealed class FpsAdvisorService
    {
        public static readonly Int32[] SupportedTargets = FpsAdvisorDecisionEngine.SupportedTargets;

        public const Int32 MinimumSamplesForPacing = FpsAdvisorDecisionEngine.MinimumSamplesForPacing;

        private readonly IDisplayInfoService _displayInfo;
        private readonly FpsAdvisorDecisionEngine _engine;

        public FpsAdvisorDecisionEngine DecisionEngine => this._engine;

        public FpsAdvisorService(IDisplayInfoService displayInfo = null)
        {
            this._displayInfo = displayInfo ?? new DisplayInfoService();
            this._engine = new FpsAdvisorDecisionEngine();
        }

        public FpsAdvisorService(Func<Int32> refreshRateProvider)
            : this(new DisplayInfoService(refreshRateProvider))
        {
        }

        public static Int32 GetMaxTargetForRefreshRate(Int32 refreshRateHz) =>
            FpsAdvisorDecisionEngine.GetMaxTargetForRefreshRate(refreshRateHz);

        public void NotifyCapApplied(Int32 appliedCap) =>
            this._engine.NotifyCapApplied(appliedCap);

        public static Boolean TryDecodeRecommendedFps(String recommendation, out Int32 recommendedFps)
        {
            recommendedFps = 0;

            if (String.IsNullOrWhiteSpace(recommendation))
            {
                return false;
            }

            var normalized = recommendation.Trim();
            if (normalized.IndexOf("UNCAPPED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("UNLIMITED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("NO CAP", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            var digits = System.Text.RegularExpressions.Regex.Matches(normalized, @"\d+");

            foreach (System.Text.RegularExpressions.Match match in digits)
            {
                if (!Int32.TryParse(match.Value, out var candidate))
                {
                    continue;
                }

                if (Array.IndexOf(SupportedTargets, candidate) < 0)
                {
                    continue;
                }

                if (normalized.IndexOf("FPS", StringComparison.OrdinalIgnoreCase) < 0 &&
                    normalized.IndexOf("TRY", StringComparison.OrdinalIgnoreCase) < 0 &&
                    normalized.IndexOf("REC", StringComparison.OrdinalIgnoreCase) < 0 &&
                    normalized.IndexOf("GOOD", StringComparison.OrdinalIgnoreCase) < 0 &&
                    normalized.IndexOf("BALANCED", StringComparison.OrdinalIgnoreCase) < 0 &&
                    normalized.IndexOf("EXCELLENT", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                recommendedFps = candidate;
                return true;
            }

            return false;
        }

        public FpsAdvice Recommend(
            FrameTimeStatistics frameStats,
            ZeroPointOnePercentLowStatistics zeroPointOnePercentLowStats,
            BottleneckAnalysisResult bottleneck,
            RTSSProfileWriterService profileWriter,
            Boolean rtssAvailable)
        {
            return this.Recommend(
                frameStats,
                zeroPointOnePercentLowStats,
                bottleneck,
                profileWriter,
                rtssAvailable,
                null);
        }

        public FpsAdvice Recommend(
            FrameTimeStatistics frameStats,
            ZeroPointOnePercentLowStatistics zeroPointOnePercentLowStats,
            BottleneckAnalysisResult bottleneck,
            RTSSProfileWriterService profileWriter,
            Boolean rtssAvailable,
            FpsHealthStateService healthState)
        {
            var cap = profileWriter?.CurrentFpsCeiling ?? 0;
            return this.Recommend(
                frameStats,
                zeroPointOnePercentLowStats,
                bottleneck,
                cap,
                rtssAvailable,
                healthState);
        }

        public FpsAdvice Recommend(
            FrameTimeStatistics frameStats,
            ZeroPointOnePercentLowStatistics zeroPointOnePercentLowStats,
            BottleneckAnalysisResult bottleneck,
            Int32 currentCap,
            Boolean rtssAvailable,
            FpsHealthStateService healthState = null,
            Double? cpuUsage = null,
            Double? gpuUsage = null,
            String processName = null,
            IReadOnlyList<PerformanceSnapshot> history = null)
        {
            var refreshRate = this._displayInfo?.GetPrimaryDisplayRefreshRate() ?? 0;

            var decision = this._engine.Evaluate(
                currentCap,
                refreshRate,
                frameStats,
                zeroPointOnePercentLowStats,
                cpuUsage,
                gpuUsage,
                bottleneck,
                healthState,
                rtssAvailable,
                false,
                processName,
                history);

            return new FpsAdvice(decision.State, decision.DisplayLabel, decision.TargetFps, decision.Tier);
        }

        public FpsAdvisoryState Advise(
            BottleneckAnalysisResult bottleneck,
            IFrameTimeTrackerService frameTimeTracker,
            ZeroPointOnePercentLowStatistics zeroPointOnePercentLowStats,
            Boolean rtssAvailable)
        {
            if (!rtssAvailable || bottleneck == null || bottleneck.SampleCount == 0)
            {
                return FpsAdvisoryState.Unknown;
            }

            if (bottleneck.State == BottleneckAnalysisState.MemoryPressure)
            {
                return FpsAdvisoryState.MemoryLimited;
            }

            if (bottleneck.State == BottleneckAnalysisState.Mixed)
            {
                return FpsAdvisoryState.MixedPressure;
            }

            if (bottleneck.State == BottleneckAnalysisState.CpuBound)
            {
                return FpsAdvisoryState.CpuLimited;
            }

            if (bottleneck.State == BottleneckAnalysisState.GpuBound)
            {
                return FpsAdvisoryState.GpuLimited;
            }

            var frameStats = frameTimeTracker?.Current;
            if (frameStats != null && frameStats.SampleCount >= MinimumSamplesForPacing && this.HasPoorPacing(frameStats))
            {
                return FpsAdvisoryState.PoorFramePacing;
            }

            return FpsAdvisoryState.Stable;
        }

        private Boolean HasPoorPacing(FrameTimeStatistics frameStats)
        {
            if (frameStats?.AverageFps <= 0 || frameStats?.OnePercentLowFps <= 0)
            {
                return false;
            }

            var pacingRatio = frameStats.OnePercentLowFps / frameStats.AverageFps;
            return pacingRatio < (1.0 - 0.30);
        }
    }
}
