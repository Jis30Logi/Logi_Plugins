namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Loupedeck.RTSSPerformancePlugin.Helpers;

    /// <summary>
    /// Backward-compatibility adapter that delegates all analysis directly to the single authoritative
    /// <see cref="PerformanceStatusDecisionEngine"/>. Prevents multiple or competing decision engines.
    /// </summary>
    public sealed class BottleneckDecisionEngine
    {
        public const Int32 DefaultWindowSize = PerformanceStatusDecisionEngine.DefaultWindowSize;
        public const Int32 MinimumSampleCount = PerformanceStatusDecisionEngine.MinimumSampleCount;
        public const Int32 RequiredStableEvaluations = PerformanceStatusDecisionEngine.RequiredStableEvaluations;
        public const Int64 DefaultRotationIntervalMs = PerformanceStatusDecisionEngine.DefaultRotationIntervalMs;

        private readonly PerformanceStatusDecisionEngine _engine;

        public BottleneckDecisionEngine(Int32 windowSize = DefaultWindowSize)
        {
            this._engine = new PerformanceStatusDecisionEngine(windowSize);
        }

        public Int32 WindowSize => this._engine.WindowSize;

        public BottleneckAnalysisResult ConfirmedResult =>
            ToBottleneckResult(this._engine.ConfirmedResult);

        public void Reset() => this._engine.Reset();

        public BottleneckAnalysisResult EvaluateRaw(
            IReadOnlyList<PerformanceSnapshot> snapshots,
            Double effectiveCap = 0,
            Double displayRefreshRate = 0,
            Int64 currentTimeMs = 0,
            Boolean isLimiterProvenActive = false)
        {
            var perfResult = this._engine.EvaluateRaw(snapshots, effectiveCap, displayRefreshRate, currentTimeMs);
            return ToBottleneckResult(perfResult);
        }

        public BottleneckAnalysisResult EvaluateWithHysteresis(
            IReadOnlyList<PerformanceSnapshot> snapshots,
            Double effectiveCap = 0,
            Double displayRefreshRate = 0,
            Int64 currentTimeMs = 0,
            Boolean isLimiterProvenActive = false)
        {
            var perfResult = this._engine.EvaluateWithHysteresis(snapshots, effectiveCap, displayRefreshRate, currentTimeMs);
            return ToBottleneckResult(perfResult);
        }

        public static BottleneckAnalysisResult ToBottleneckResult(PerformanceStatusResult perfResult)
        {
            if (perfResult == null || perfResult.State == PerformanceStatusState.Unknown)
            {
                return new BottleneckAnalysisResult(
                    BottleneckAnalysisState.Unknown,
                    0.0,
                    0,
                    false,
                    false,
                    false,
                    perfResult?.Reason ?? "Waiting for telemetry",
                    FactorLimitationState.Unknown,
                    FactorLimitationState.Unknown,
                    FactorLimitationState.Unknown,
                    ColorTier.Neutral,
                    perfResult?.Label ?? "UNKNOWN",
                    Array.Empty<String>(),
                    Array.Empty<String>());
            }

            var state = perfResult.State switch
            {
                PerformanceStatusState.Balanced => BottleneckAnalysisState.Balanced,
                PerformanceStatusState.PerformanceWatch => BottleneckAnalysisState.Balanced,
                PerformanceStatusState.GpuPressure => BottleneckAnalysisState.GpuBound,
                PerformanceStatusState.CpuPressure => BottleneckAnalysisState.CpuBound,
                PerformanceStatusState.FpsDegraded => BottleneckAnalysisState.FpsLimited,
                PerformanceStatusState.MemoryPressure => BottleneckAnalysisState.MemoryPressure,
                PerformanceStatusState.SeverePerformance => BottleneckAnalysisState.Mixed,
                _ => BottleneckAnalysisState.Unknown
            };

            var activeFactors = new List<String>();
            foreach (var cond in perfResult.ActiveConditions)
            {
                if (cond.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0) activeFactors.Add("GPU");
                else if (cond.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0) activeFactors.Add("CPU");
                else if (cond.IndexOf("FPS", StringComparison.OrdinalIgnoreCase) >= 0) activeFactors.Add("FPS");
                else if (cond.IndexOf("MEMORY", StringComparison.OrdinalIgnoreCase) >= 0 || cond.IndexOf("RAM", StringComparison.OrdinalIgnoreCase) >= 0) activeFactors.Add("RAM");
            }

            var gpuLimitation = activeFactors.Contains("GPU") ? FactorLimitationState.Red : FactorLimitationState.Healthy;
            var cpuLimitation = activeFactors.Contains("CPU") ? FactorLimitationState.Red : FactorLimitationState.Healthy;
            var fpsLimitation = activeFactors.Contains("FPS") ? FactorLimitationState.Red : FactorLimitationState.Healthy;

            var resolvedLabel = perfResult.Label switch
            {
                "CPU LIMITED" => "CPU LOAD HIGH",
                "GPU LIMITED" => "GPU LOAD HIGH",
                "FPS LIMITED" => "FPS DROPPING",
                "RAM LIMITED" => "MEMORY PRESSURE",
                _ => perfResult.Label
            };

            var legacyActiveConditions = perfResult.ActiveConditions.Select(c => c switch
            {
                "CPU LIMITED" => "CPU LOAD HIGH",
                "GPU LIMITED" => "GPU LOAD HIGH",
                "FPS LIMITED" => "FPS DROPPING",
                "RAM LIMITED" => "MEMORY PRESSURE",
                _ => c
            }).ToList();

            return new BottleneckAnalysisResult(
                state,
                perfResult.Confidence,
                perfResult.SampleCount,
                perfResult.CpuEvidenceAvailable,
                perfResult.GpuEvidenceAvailable,
                perfResult.MemoryEvidenceAvailable,
                perfResult.Reason,
                gpuLimitation,
                cpuLimitation,
                fpsLimitation,
                perfResult.Tier,
                resolvedLabel,
                activeFactors,
                legacyActiveConditions);
        }

        public static Double CalculatePercentile(IReadOnlyList<Double> sortedValues, Double percentile) =>
            PerformanceStatusDecisionEngine.CalculatePercentile(sortedValues, percentile);
    }
}
