namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Loupedeck.RTSSPerformancePlugin.Helpers;

    public enum FpsAdvisorState
    {
        InsufficientData = 0,
        Stable = 1,
        Recommend = 2,
        Degraded = 3,
        Critical = 4,
        Recovery = 5,
    }

    public enum ConfidenceTier
    {
        Low = 0,
        Medium = 1,
        High = 2,
    }

    public sealed class FpsSustainabilityResult
    {
        public FpsSustainabilityResult(
            Double sustainableFps,
            Double confidence,
            ConfidenceTier confidenceLevel,
            FpsAdvisorState state,
            Int32 recommendedCap,
            Boolean canRecommendCap,
            Boolean recoveryRequired,
            String displayLabel,
            ColorTier tier,
            String reason)
        {
            this.SustainableFps = sustainableFps;
            this.Confidence = confidence;
            this.ConfidenceLevel = confidenceLevel;
            this.State = state;
            this.RecommendedCap = recommendedCap;
            this.CanRecommendCap = canRecommendCap;
            this.RecoveryRequired = recoveryRequired;
            this.DisplayLabel = displayLabel;
            this.Tier = tier;
            this.Reason = reason ?? String.Empty;
        }

        public Double SustainableFps { get; }

        public Double Confidence { get; }

        public ConfidenceTier ConfidenceLevel { get; }

        public FpsAdvisorState State { get; }

        public Int32 RecommendedCap { get; }

        public Boolean CanRecommendCap { get; }

        public Boolean RecoveryRequired { get; }

        public String DisplayLabel { get; }

        public ColorTier Tier { get; }

        public String Reason { get; }

        public static FpsSustainabilityResult InsufficientData(String reason = "Insufficient telemetry") =>
            new FpsSustainabilityResult(0, 0, ConfidenceTier.Low, FpsAdvisorState.InsufficientData, 0, false, false, null, ColorTier.Neutral, reason);

        public static FpsSustainabilityResult Recovery(Double sustainableFps, Double confidence, ConfidenceTier tier, String reason = "Performance collapsed; no cap currently sustainable") =>
            new FpsSustainabilityResult(sustainableFps, confidence, tier, FpsAdvisorState.Recovery, 0, false, true, "FPS RECOVERY", ColorTier.Critical, reason);

        public static FpsSustainabilityResult Stable(Int32 cap, Double sustainableFps, Double confidence, ConfidenceTier tier, String reason = "Workload is stable at current cap") =>
            new FpsSustainabilityResult(sustainableFps, confidence, tier, FpsAdvisorState.Stable, cap, false, false, "STABLE FPS", ColorTier.Good, reason);

        public static FpsSustainabilityResult Recommend(Int32 cap, Double sustainableFps, Double confidence, ConfidenceTier tier, FpsAdvisorState state, String reason) =>
            new FpsSustainabilityResult(sustainableFps, confidence, tier, state, cap, true, false, $"TRY {cap} FPS?", ColorTier.Critical, reason);
    }

    /// <summary>
    /// Pure, telemetry-driven sustainable FPS decision analyzer.
    /// Infers physical throughput, frame pacing, and hardware saturation from empirical telemetry history.
    /// Evaluates supported RTSS cap candidates {30, 60, 90, 120, 144, 180} dynamically without static game rules.
    /// When throughput collapses below the lowest candidate cap (30 FPS), enters Recovery state.
    /// </summary>
    public static class FpsSustainabilityAnalyzer
    {
        public static readonly Int32[] SupportedTargets = { 30, 60, 90, 120, 144, 180 };
        public const Double MinimumViableCandidateThroughput = 27.5;

        public static Boolean IsCurrentCapStable(
            Int32 currentCap,
            TelemetryPerformanceEnvelope envelope,
            BottleneckAnalysisResult bottleneck = null,
            FpsHealthStateService healthState = null)
        {
            if (currentCap <= 0 || envelope == null || envelope.SampleCount < 2 || envelope.Confidence < 0.40 || envelope.IsStale)
            {
                return false;
            }

            if (envelope.MedianFps <= 0.0 || Double.IsNaN(envelope.MedianFps) || envelope.MedianFps > 10000.0)
            {
                return false;
            }

            // 1. Throughput verification: system is meeting the current cap within margin
            var fpsMargin = Math.Max(2.5, currentCap * 0.04);
            if (envelope.MedianFps < (currentCap - fpsMargin))
            {
                return false;
            }

            // 2. Frame time pacing verification: median frame time matches cap target
            var targetFtMs = 1000.0 / currentCap;
            if (envelope.MedianFrameTimeMs > (targetFtMs * 1.15))
            {
                return false;
            }

            // Frame time jitter/spread must be bounded
            if (envelope.P95FrameTimeMs > (targetFtMs * 1.40))
            {
                return false;
            }

            if (envelope.FrameTimeJitterMs > Math.Max(4.0, targetFtMs * 0.30))
            {
                return false;
            }

            // 3. Hardware headroom: hardware must not be saturated / bottlenecked
            if (bottleneck != null &&
                (bottleneck.State == BottleneckAnalysisState.CpuBound ||
                 bottleneck.State == BottleneckAnalysisState.GpuBound ||
                 bottleneck.State == BottleneckAnalysisState.MemoryPressure))
            {
                return false;
            }

            if (envelope.GpuMedian.HasValue && envelope.GpuMedian.Value >= 96.0)
            {
                return false;
            }

            if (envelope.CpuMedian.HasValue && envelope.CpuMedian.Value >= 96.0)
            {
                return false;
            }

            // 4. Health state verification: only genuine Red health tier blocks stability
            if (healthState != null && healthState.HasTier && healthState.Tier == FpsTier.Red)
            {
                return false;
            }

            return true;
        }

        public static FpsSustainabilityResult Analyze(
            TelemetryPerformanceEnvelope envelope,
            Int32 currentCap,
            Int32 displayRefreshRateHz,
            BottleneckAnalysisResult bottleneck = null,
            FpsHealthStateService healthState = null)
        {
            if (envelope == null || envelope.IsStale)
            {
                return FpsSustainabilityResult.InsufficientData("Insufficient or stale telemetry");
            }

            if (envelope.SampleCount < 2 && !(envelope.SampleCount >= 1 && (envelope.MedianFps < 20.0 || envelope.MedianFrameTimeMs > 50.0)))
            {
                return FpsSustainabilityResult.InsufficientData("Insufficient or stale telemetry");
            }

            // Telemetry validation: ignore unphysical or invalid FPS values
            if (envelope.MedianFps <= 0.0 || Double.IsNaN(envelope.MedianFps) || envelope.MedianFps > 10000.0)
            {
                return FpsSustainabilityResult.InsufficientData("Unusable framerate telemetry");
            }

            var confidenceTier = envelope.SampleCount >= 10 && envelope.Confidence >= 0.80
                ? ConfidenceTier.High
                : (envelope.SampleCount >= 6 ? ConfidenceTier.Medium : ConfidenceTier.Low);

            var displayCeiling = displayRefreshRateHz > 0
                ? FpsAdvisorDecisionEngine.GetMaxTargetForRefreshRate(displayRefreshRateHz)
                : SupportedTargets[SupportedTargets.Length - 1];

            // 1. Current Stability First:
            // When a cap is currently active and empirically proven stable, protect it.
            // A single invalid, stale, or outlier low percentile must NEVER veto proven stable pacing.
            if (currentCap > 0 && IsCurrentCapStable(currentCap, envelope, bottleneck, healthState))
            {
                var sustainableCeiling = FpsAdvisorDecisionEngine.DeriveSustainablePerformanceCeiling(envelope, bottleneck);
                var higherCandidate = FpsAdvisorDecisionEngine.MapToSupportedCap(sustainableCeiling, displayCeiling);

                var isHealthSettled = healthState == null || healthState.HasTier || healthState.State != FpsHealthState.Unknown;
                var isHealthDegraded = healthState != null && healthState.HasTier && healthState.Tier == FpsTier.Red;

                // Check for potential upgrade if system demonstrates stable throughput for a higher target
                if (higherCandidate > currentCap && isHealthSettled && !isHealthDegraded)
                {
                    return FpsSustainabilityResult.Recommend(
                        higherCandidate,
                        sustainableCeiling,
                        envelope.Confidence,
                        confidenceTier,
                        FpsAdvisorState.Recommend,
                        $"Demonstrated capacity for {higherCandidate} FPS");
                }

                return FpsSustainabilityResult.Stable(
                    currentCap,
                    Math.Max((Double)currentCap, sustainableCeiling),
                    envelope.Confidence,
                    confidenceTier,
                    $"Workload stable at {currentCap} FPS");
            }

            // 2. Derive empirical sustainable throughput ceiling
            var ceiling = FpsAdvisorDecisionEngine.DeriveSustainablePerformanceCeiling(envelope, bottleneck);

            // 3. Critical & Emergency Mode: when sustainable throughput is below the minimum viable cap (30 FPS)
            // or median FPS is severely collapsed (< 20 FPS) or frame time > 50ms:
            if (ceiling < MinimumViableCandidateThroughput || envelope.MedianFps < 20.0 || envelope.MedianFrameTimeMs > 50.0)
            {
                return FpsSustainabilityResult.Recovery(
                    ceiling,
                    envelope.Confidence,
                    confidenceTier,
                    $"FPS collapsed ({envelope.MedianFps:F1} FPS); no available cap is sustainable");
            }

            // 4. Map sustainable ceiling to highest supported cap within display ceiling
            var selectedCandidate = FpsAdvisorDecisionEngine.MapToSupportedCap(ceiling, displayCeiling);

            // 5. Closed-loop evaluation against current effective RTSS cap
            if (currentCap > 0)
            {
                var isCurrentCapSustainable = FpsAdvisorDecisionEngine.IsCandidateSustainable(currentCap, envelope);
                var isHealthSettled = healthState == null || healthState.HasTier || healthState.State != FpsHealthState.Unknown;
                var isHealthDegraded = healthState != null && healthState.HasTier && healthState.Tier == FpsTier.Red;

                // A. Upgrade case: measured performance envelope demonstrates a higher target <= displayCeiling
                if (selectedCandidate > currentCap && isHealthSettled && !isHealthDegraded)
                {
                    return FpsSustainabilityResult.Recommend(
                        selectedCandidate,
                        ceiling,
                        envelope.Confidence,
                        confidenceTier,
                        FpsAdvisorState.Recommend,
                        $"Demonstrated capacity for {selectedCandidate} FPS");
                }

                // B. Stable case: current cap is sustainable and not degraded
                if (isCurrentCapSustainable && !isHealthDegraded && isHealthSettled)
                {
                    return FpsSustainabilityResult.Stable(
                        currentCap,
                        ceiling,
                        envelope.Confidence,
                        confidenceTier,
                        $"Workload stable at {currentCap} FPS");
                }

                // C. Downgrade case: current cap is degraded/unstable. Find highest lower sustainable candidate.
                if (selectedCandidate > 0 && selectedCandidate < currentCap)
                {
                    var downgradeState = selectedCandidate <= 30 ? FpsAdvisorState.Critical : FpsAdvisorState.Degraded;
                    return FpsSustainabilityResult.Recommend(
                        selectedCandidate,
                        ceiling,
                        envelope.Confidence,
                        confidenceTier,
                        downgradeState,
                        $"Cap {currentCap} unsustainable; recommend {selectedCandidate} FPS");
                }

                for (var i = SupportedTargets.Length - 1; i >= 0; i--)
                {
                    var candidate = SupportedTargets[i];
                    if (candidate < currentCap && candidate <= displayCeiling && FpsAdvisorDecisionEngine.IsCandidateSustainable(candidate, envelope))
                    {
                        var downgradeState = candidate <= 30 ? FpsAdvisorState.Critical : FpsAdvisorState.Degraded;
                        return FpsSustainabilityResult.Recommend(
                            candidate,
                            ceiling,
                            envelope.Confidence,
                            confidenceTier,
                            downgradeState,
                            $"Recommend lower cap {candidate} FPS");
                    }
                }

                // Current cap is not sustainable, and no lower candidate met sustainability:
                return FpsSustainabilityResult.Recovery(
                    ceiling,
                    envelope.Confidence,
                    confidenceTier,
                    $"Current cap {currentCap} unsustainable and no lower candidate viable");
            }

            // 6. Uncapped (currentCap == 0)
            if (selectedCandidate > 0)
            {
                var candidateTargetFt = 1000.0 / selectedCandidate;
                var isAlreadyDeliveringCandidate =
                    Math.Abs(envelope.MedianFps - selectedCandidate) <= Math.Max(3.0, selectedCandidate * 0.05) &&
                    envelope.MedianFrameTimeMs <= (candidateTargetFt * 1.15) &&
                    envelope.FrameTimeJitterMs <= Math.Max(4.0, candidateTargetFt * 0.30);

                if (isAlreadyDeliveringCandidate)
                {
                    return FpsSustainabilityResult.Stable(
                        selectedCandidate,
                        ceiling,
                        envelope.Confidence,
                        confidenceTier,
                        $"Workload stable at {selectedCandidate} FPS");
                }

                return FpsSustainabilityResult.Recommend(
                    selectedCandidate,
                    ceiling,
                    envelope.Confidence,
                    confidenceTier,
                    FpsAdvisorState.Recommend,
                    $"Recommend target cap {selectedCandidate} FPS for uncapped workload");
            }

            return FpsSustainabilityResult.Recovery(
                ceiling,
                envelope.Confidence,
                confidenceTier,
                "Workload throughput collapsed below supported cap range");
        }
    }
}
