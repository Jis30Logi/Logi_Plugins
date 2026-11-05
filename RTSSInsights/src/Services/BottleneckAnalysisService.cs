namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Loupedeck.RTSSPerformancePlugin.Helpers;

    public enum FactorLimitationState
    {
        Healthy = 0,
        Unknown = 1,
        Yellow = 2,
        Red = 3,
    }

    public enum BottleneckAnalysisState
    {
        Unknown = 0,
        Balanced = 1,
        CpuBound = 2,
        GpuBound = 3,
        Mixed = 4,
        MemoryPressure = 5,
        FpsLimited = 6,
    }

    public sealed class BottleneckAnalysisResult
    {
        public BottleneckAnalysisResult(
            BottleneckAnalysisState state,
            Double confidence,
            Int32 sampleCount,
            Boolean cpuEvidenceAvailable,
            Boolean gpuEvidenceAvailable,
            Boolean memoryEvidenceAvailable,
            String reason)
            : this(
                state,
                confidence,
                sampleCount,
                cpuEvidenceAvailable,
                gpuEvidenceAvailable,
                memoryEvidenceAvailable,
                reason,
                FactorLimitationState.Healthy,
                FactorLimitationState.Healthy,
                FactorLimitationState.Healthy,
                ColorTier.Good,
                "BALANCED",
                Array.Empty<String>(),
                Array.Empty<String>())
        {
        }

        public BottleneckAnalysisResult(
            BottleneckAnalysisState state,
            Double confidence,
            Int32 sampleCount,
            Boolean cpuEvidenceAvailable,
            Boolean gpuEvidenceAvailable,
            Boolean memoryEvidenceAvailable,
            String reason,
            FactorLimitationState gpuLimitation,
            FactorLimitationState cpuLimitation,
            FactorLimitationState fpsLimitation,
            ColorTier overallTier,
            String resolvedLabel,
            IReadOnlyList<String> activeFactors)
            : this(
                state,
                confidence,
                sampleCount,
                cpuEvidenceAvailable,
                gpuEvidenceAvailable,
                memoryEvidenceAvailable,
                reason,
                gpuLimitation,
                cpuLimitation,
                fpsLimitation,
                overallTier,
                resolvedLabel,
                activeFactors,
                null)
        {
        }

        public BottleneckAnalysisResult(
            BottleneckAnalysisState state,
            Double confidence,
            Int32 sampleCount,
            Boolean cpuEvidenceAvailable,
            Boolean gpuEvidenceAvailable,
            Boolean memoryEvidenceAvailable,
            String reason,
            FactorLimitationState gpuLimitation,
            FactorLimitationState cpuLimitation,
            FactorLimitationState fpsLimitation,
            ColorTier overallTier,
            String resolvedLabel,
            IReadOnlyList<String> activeFactors,
            IReadOnlyList<String> activeBottlenecks)
        {
            this.State = state;
            this.Confidence = confidence;
            this.SampleCount = sampleCount;
            this.CpuEvidenceAvailable = cpuEvidenceAvailable;
            this.GpuEvidenceAvailable = gpuEvidenceAvailable;
            this.MemoryEvidenceAvailable = memoryEvidenceAvailable;
            this.Reason = reason;
            this.GpuLimitation = gpuLimitation;
            this.CpuLimitation = cpuLimitation;
            this.FpsLimitation = fpsLimitation;
            this.OverallTier = overallTier;
            this.ResolvedLabel = resolvedLabel ?? "UNKNOWN";
            this.ActiveFactors = activeFactors ?? Array.Empty<String>();

            if (activeBottlenecks != null)
            {
                this.ActiveBottlenecks = activeBottlenecks;
            }
            else if (activeFactors != null && activeFactors.Count > 0)
            {
                var list = new List<String>();
                if (activeFactors.Contains("CPU", StringComparer.OrdinalIgnoreCase)) list.Add("CPU LOAD HIGH");
                if (activeFactors.Contains("GPU", StringComparer.OrdinalIgnoreCase)) list.Add("GPU LOAD HIGH");
                if (activeFactors.Contains("FPS", StringComparer.OrdinalIgnoreCase)) list.Add("FPS DROPPING");
                if (activeFactors.Contains("RAM", StringComparer.OrdinalIgnoreCase)) list.Add("MEMORY PRESSURE");
                this.ActiveBottlenecks = list;
            }
            else
            {
                this.ActiveBottlenecks = Array.Empty<String>();
            }
        }

        public BottleneckAnalysisState State { get; }

        public Double Confidence { get; }

        public Int32 SampleCount { get; }

        public Boolean CpuEvidenceAvailable { get; }

        public Boolean GpuEvidenceAvailable { get; }

        public Boolean MemoryEvidenceAvailable { get; }

        public String Reason { get; }

        public FactorLimitationState GpuLimitation { get; }

        public FactorLimitationState CpuLimitation { get; }

        public FactorLimitationState FpsLimitation { get; }

        public ColorTier OverallTier { get; }

        public String ResolvedLabel { get; }

        public IReadOnlyList<String> ActiveFactors { get; }

        public IReadOnlyList<String> ActiveBottlenecks { get; }
    }

    public static class BottleneckStatusResolver
    {
        public static String ResolveLabel(
            Boolean hasLiveData,
            BottleneckAnalysisResult result,
            FrameTimeStatistics frameStats,
            Double cap) =>
            ResolveStatus(hasLiveData, result, frameStats, cap, 0, 0, false).Label;

        public static (String Label, ColorTier Tier) ResolveStatus(
            Boolean hasLiveData,
            BottleneckAnalysisResult result,
            FrameTimeStatistics frameStats,
            Double cap,
            Double displayRefreshRate = 0) =>
            ResolveStatus(hasLiveData, result, frameStats, cap, displayRefreshRate, 0, false);

        public static (String Label, ColorTier Tier) ResolveStatus(
            Boolean hasLiveData,
            BottleneckAnalysisResult result,
            FrameTimeStatistics frameStats,
            Double cap,
            Double displayRefreshRate,
            Int64 currentTimeMs) =>
            ResolveStatus(hasLiveData, result, frameStats, cap, displayRefreshRate, currentTimeMs, false);

        public static (String Label, ColorTier Tier) ResolveStatus(
            Boolean hasLiveData,
            BottleneckAnalysisResult result,
            FrameTimeStatistics frameStats,
            Double cap,
            Double displayRefreshRate,
            Int64 currentTimeMs,
            Boolean isLimiterProvenActive)
        {
            if (!hasLiveData || result == null || result.SampleCount <= 0)
            {
                return ("NO GAME", ColorTier.Neutral);
            }

            if (result.State == BottleneckAnalysisState.Unknown && result.ActiveFactors.Count == 0 && result.OverallTier == ColorTier.Neutral)
            {
                return ("UNKNOWN", ColorTier.Neutral);
            }

            var active = ResolveActiveBottlenecks(hasLiveData, result, frameStats, cap, displayRefreshRate, isLimiterProvenActive);

            if (active.Count == 0)
            {
                return result.OverallTier == ColorTier.Warning
                    ? ("PERFORMANCE WATCH", ColorTier.Warning)
                    : ("BALANCED", ColorTier.Good);
            }

            var tier = result.OverallTier != ColorTier.Neutral ? result.OverallTier : ColorTier.Critical;

            if (active.Count == 1)
            {
                return (active[0], tier);
            }

            // Rotate multiple active limits every ~2 seconds (2000 ms)
            const Int64 intervalMs = PerformanceStatusDecisionEngine.DefaultRotationIntervalMs;
            var index = (Int32)((currentTimeMs / intervalMs) % active.Count);
            if (index < 0) index = 0;
            return (active[index], tier);
        }

        public static IReadOnlyList<String> ResolveActiveBottlenecks(
            Boolean hasLiveData,
            BottleneckAnalysisResult result,
            FrameTimeStatistics frameStats,
            Double cap,
            Double displayRefreshRate = 0) =>
            ResolveActiveBottlenecks(hasLiveData, result, frameStats, cap, displayRefreshRate, false);

        public static IReadOnlyList<String> ResolveActiveBottlenecks(
            Boolean hasLiveData,
            BottleneckAnalysisResult result,
            FrameTimeStatistics frameStats,
            Double cap,
            Double displayRefreshRate,
            Boolean isLimiterProvenActive)
        {
            if (!hasLiveData || result == null || result.SampleCount <= 0)
            {
                return Array.Empty<String>();
            }

            return result.ActiveBottlenecks ?? Array.Empty<String>();
        }
    }

    public sealed class BottleneckAnalysisService : IDisposable
    {
        public const Int32 DefaultWindowSize = BottleneckDecisionEngine.DefaultWindowSize;
        public const Int32 MinimumSampleCount = BottleneckDecisionEngine.MinimumSampleCount;
        public const Int32 RequiredStableEvaluations = BottleneckDecisionEngine.RequiredStableEvaluations;

        private readonly BottleneckDecisionEngine _engine;
        private Boolean _isDisposed;

        public BottleneckAnalysisService(Int32 windowSize = DefaultWindowSize)
        {
            this._engine = new BottleneckDecisionEngine(windowSize);
        }

        public Int32 WindowSize => this._engine.WindowSize;

        public BottleneckDecisionEngine DecisionEngine => this._engine;

        public BottleneckAnalysisResult Current => this._engine.ConfirmedResult;

        public BottleneckAnalysisResult Analyze(
            PerformanceHistoryService history,
            Double effectiveCap = 0,
            Double displayRefreshRate = 0,
            Int64 currentTimeMs = 0,
            Boolean isLimiterProvenActive = false)
        {
            if (history == null || this._isDisposed)
            {
                return this.Current;
            }

            return this.Analyze(history.GetSnapshots(), effectiveCap, displayRefreshRate, currentTimeMs, isLimiterProvenActive);
        }

        public BottleneckAnalysisResult Analyze(
            IReadOnlyList<PerformanceSnapshot> snapshots,
            Double effectiveCap = 0,
            Double displayRefreshRate = 0,
            Int64 currentTimeMs = 0,
            Boolean isLimiterProvenActive = false)
        {
            if (this._isDisposed)
            {
                return this.Current;
            }

            return this._engine.EvaluateWithHysteresis(snapshots, effectiveCap, displayRefreshRate, currentTimeMs, isLimiterProvenActive);
        }

        public void Reset()
        {
            this._engine.Reset();
        }

        public void Dispose()
        {
            this._isDisposed = true;
            this._engine.Reset();
        }

        public static Double CalculatePercentile(IReadOnlyList<Double> sortedValues, Double percentile) =>
            BottleneckDecisionEngine.CalculatePercentile(sortedValues, percentile);
    }
}
