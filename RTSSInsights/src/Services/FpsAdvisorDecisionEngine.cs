namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Loupedeck.RTSSPerformancePlugin.Helpers;

    public enum AdvisorDecisionType
    {
        Unknown = 0,
        Stable = 1,
        Recommend = 2,
        Diagnostic = 3,
        Recovery = 4,
    }

    public sealed class AdvisorDecision
    {
        public AdvisorDecisionType Type { get; }

        public Int32 TargetFps { get; }

        public String DisplayLabel { get; }

        public FpsAdvisoryState State { get; }

        public ColorTier Tier { get; }

        public AdvisorDecision(
            AdvisorDecisionType type,
            Int32 targetFps,
            String displayLabel,
            FpsAdvisoryState state,
            ColorTier tier = ColorTier.Neutral)
        {
            this.Type = type;
            this.TargetFps = targetFps;
            this.DisplayLabel = displayLabel;
            this.State = state;
            this.Tier = tier;
        }

        public static AdvisorDecision Unknown() =>
            new AdvisorDecision(AdvisorDecisionType.Unknown, 0, null, FpsAdvisoryState.Unknown, ColorTier.Neutral);

        public static AdvisorDecision Stable(Int32 cap) =>
            new AdvisorDecision(AdvisorDecisionType.Stable, cap, "STABLE FPS", FpsAdvisoryState.Stable, ColorTier.Good);

        public static AdvisorDecision Recommend(Int32 candidate) =>
            new AdvisorDecision(AdvisorDecisionType.Recommend, candidate, $"TRY {candidate} FPS?", FpsAdvisoryState.PoorFramePacing, ColorTier.Critical);

        public static AdvisorDecision Recovery(ColorTier tier = ColorTier.Critical) =>
            new AdvisorDecision(AdvisorDecisionType.Recovery, 0, "FPS RECOVERY", FpsAdvisoryState.Recovery, tier);

        public static AdvisorDecision Diagnostic(String limiterLabel, ColorTier tier, FpsAdvisoryState state = FpsAdvisoryState.Diagnostic) =>
            new AdvisorDecision(AdvisorDecisionType.Diagnostic, 0, limiterLabel, state, tier);
    }

    /// <summary>
    /// Telemetry metrics envelope representing the active application's measured runtime distribution.
    /// </summary>
    public sealed class TelemetryPerformanceEnvelope
    {
        public Int32 SampleCount { get; }
        public Double MedianFps { get; }
        public Double P10Fps { get; }
        public Double? P01Fps { get; }
        public Double MedianFrameTimeMs { get; }
        public Double P95FrameTimeMs { get; }
        public Double FrameTimeJitterMs { get; }
        public Double FpsJitter { get; }
        public Double? CpuMedian { get; }
        public Double? CpuP95 { get; }
        public Double? GpuMedian { get; }
        public Double? GpuP95 { get; }
        public Double? RamMedian { get; }
        public Double? RamP95 { get; }
        public Double? OnePercentLowFps { get; }
        public Double? ZeroPointOnePercentLowFps { get; }
        public Double Confidence { get; }
        public Boolean IsStale { get; }

        public TelemetryPerformanceEnvelope(
            Int32 sampleCount,
            Double medianFps,
            Double p10Fps,
            Double? p01Fps,
            Double medianFrameTimeMs,
            Double p95FrameTimeMs,
            Double frameTimeJitterMs,
            Double fpsJitter,
            Double? cpuMedian,
            Double? cpuP95,
            Double? gpuMedian,
            Double? gpuP95,
            Double? onePercentLowFps,
            Double? zeroPointOnePercentLowFps,
            Double confidence,
            Boolean isStale,
            Double? ramMedian = null,
            Double? ramP95 = null)
        {
            this.SampleCount = sampleCount;
            this.MedianFps = medianFps;
            this.P10Fps = p10Fps;
            this.P01Fps = p01Fps;
            this.MedianFrameTimeMs = medianFrameTimeMs;
            this.P95FrameTimeMs = p95FrameTimeMs;
            this.FrameTimeJitterMs = frameTimeJitterMs;
            this.FpsJitter = fpsJitter;
            this.CpuMedian = cpuMedian;
            this.CpuP95 = cpuP95;
            this.GpuMedian = gpuMedian;
            this.GpuP95 = gpuP95;
            this.RamMedian = ramMedian;
            this.RamP95 = ramP95;
            this.OnePercentLowFps = onePercentLowFps;
            this.ZeroPointOnePercentLowFps = zeroPointOnePercentLowFps;
            this.Confidence = confidence;
            this.IsStale = isStale;
        }

        public static TelemetryPerformanceEnvelope Empty() =>
            new TelemetryPerformanceEnvelope(0, 0, 0, null, 0, 0, 0, 0, null, null, null, null, null, null, 0, false, null, null);
    }

    /// <summary>
    /// Pure dynamic performance advisor for the currently active foreground application.
    /// Infers the application's actual sustainable performance envelope from real runtime telemetry history,
    /// evaluates every supported candidate cap independently, and dynamically selects the highest sustainable cap.
    /// Has no static FPS-to-recommendation lookup table and never blindly falls back to 30 FPS.
    /// </summary>
    public sealed class FpsAdvisorDecisionEngine
    {
        public static readonly Int32[] SupportedTargets = { 30, 60, 90, 120, 144, 180 };

        public const Int32 MinimumSamplesForPacing = 6;
        public const Double PacingVarianceRatioLimit = 0.35;
        public const Int32 RequiredCandidateStreak = 3;
        public const Int32 RequiredUnstableStreak = 3;

        private readonly Object _sync = new Object();
        private String _activeProcessName = null;
        private Int32 _proposedCandidate = 0;
        private Int32 _candidateStreak = 0;
        private Int32 _unstableStreak = 0;
        private AdvisorDecision _lastDecision = AdvisorDecision.Unknown();

        public AdvisorDecision LastDecision
        {
            get
            {
                lock (this._sync)
                {
                    return this._lastDecision;
                }
            }
        }

        public static Int32 GetMaxTargetForRefreshRate(Int32 refreshRateHz)
        {
            if (refreshRateHz <= 0)
            {
                return SupportedTargets[SupportedTargets.Length - 1]; // 180
            }

            // Standard NTSC refresh rates can be reported as (hz - 1), e.g. 59 for 59.94Hz or 143 for 143.97Hz
            var normalizedHz = refreshRateHz switch
            {
                59 => 60,
                119 => 120,
                143 => 144,
                179 => 180,
                239 => 240,
                _ => refreshRateHz,
            };

            var max = SupportedTargets[0];
            foreach (var target in SupportedTargets)
            {
                if (target <= normalizedHz + 1)
                {
                    max = target;
                }
            }

            return max;
        }

        public void NotifyCapApplied(Int32 appliedCap)
        {
            lock (this._sync)
            {
                this._proposedCandidate = 0;
                this._candidateStreak = 0;
                this._unstableStreak = 0;
                this._lastDecision = AdvisorDecision.Unknown();
            }
        }

        public void Reset()
        {
            lock (this._sync)
            {
                this._proposedCandidate = 0;
                this._candidateStreak = 0;
                this._unstableStreak = 0;
                this._lastDecision = AdvisorDecision.Unknown();
            }
        }

        public AdvisorDecision Evaluate(
            Int32 currentCap,
            Int32 displayRefreshRateHz,
            FrameTimeStatistics frameStats,
            ZeroPointOnePercentLowStatistics lowStats = null,
            Double? cpuUsage = null,
            Double? gpuUsage = null,
            BottleneckAnalysisResult bottleneck = null,
            FpsHealthStateService healthState = null,
            Boolean rtssAvailable = true,
            Boolean bypassHysteresis = false,
            String processName = null)
        {
            return this.Evaluate(
                currentCap,
                displayRefreshRateHz,
                frameStats,
                lowStats,
                cpuUsage,
                gpuUsage,
                bottleneck,
                healthState,
                rtssAvailable,
                bypassHysteresis,
                processName,
                null);
        }

        public AdvisorDecision Evaluate(
            Int32 currentCap,
            Int32 displayRefreshRateHz,
            FrameTimeStatistics frameStats,
            ZeroPointOnePercentLowStatistics lowStats,
            Double? cpuUsage,
            Double? gpuUsage,
            BottleneckAnalysisResult bottleneck,
            FpsHealthStateService healthState,
            Boolean rtssAvailable,
            Boolean bypassHysteresis,
            String processName,
            IReadOnlyList<PerformanceSnapshot> history)
        {
            // 1. Missing telemetry or RTSS not readable -> NEVER claim STABLE FPS
            if (!rtssAvailable)
            {
                return AdvisorDecision.Unknown();
            }

            // 2. Process change detection: discard/segregate previous application's performance history
            if (!String.IsNullOrEmpty(processName))
            {
                lock (this._sync)
                {
                    if (this._activeProcessName != null && !String.Equals(this._activeProcessName, processName, StringComparison.OrdinalIgnoreCase))
                    {
                        this.Reset();
                    }
                    this._activeProcessName = processName;
                }
            }

            // 3. Build robust telemetry metrics envelope
            var envelope = BuildEnvelope(history, frameStats, lowStats, cpuUsage, gpuUsage, rtssAvailable);
            if (envelope.SampleCount < 2 && !(envelope.SampleCount >= 1 && (envelope.MedianFps < 20.0 || envelope.MedianFrameTimeMs > 50.0)))
            {
                return AdvisorDecision.Unknown();
            }

            if (envelope.Confidence < 0.35 || envelope.IsStale)
            {
                return AdvisorDecision.Unknown();
            }

            if (bottleneck == null && history != null && history.Count >= BottleneckAnalysisService.MinimumSampleCount)
            {
                var analyzer = new BottleneckAnalysisService();
                bottleneck = analyzer.Analyze(history, currentCap, displayRefreshRateHz);
            }

            var rawDecision = this.ComputeRawDecision(
                currentCap,
                displayRefreshRateHz,
                envelope,
                bottleneck,
                healthState,
                frameStats);

            if (bypassHysteresis)
            {
                return rawDecision;
            }

            lock (this._sync)
            {
                return this.ApplyHysteresis(rawDecision, currentCap);
            }
        }

        public AdvisorDecision ComputeRawDecision(
            Int32 currentCap,
            Int32 displayRefreshRateHz,
            TelemetryPerformanceEnvelope envelope,
            BottleneckAnalysisResult bottleneck,
            FpsHealthStateService healthState,
            FrameTimeStatistics frameStats = null)
        {
            var result = FpsSustainabilityAnalyzer.Analyze(
                envelope,
                currentCap,
                displayRefreshRateHz,
                bottleneck,
                healthState);

            return result.State switch
            {
                FpsAdvisorState.Stable => AdvisorDecision.Stable(result.RecommendedCap),
                FpsAdvisorState.Recommend or FpsAdvisorState.Degraded or FpsAdvisorState.Critical =>
                    AdvisorDecision.Recommend(result.RecommendedCap),
                FpsAdvisorState.Recovery => AdvisorDecision.Recovery(),
                _ => AdvisorDecision.Unknown(),
            };
        }

        public static TelemetryPerformanceEnvelope BuildEnvelope(
            IReadOnlyList<PerformanceSnapshot> history,
            FrameTimeStatistics frameStats,
            ZeroPointOnePercentLowStatistics lowStats,
            Double? cpuUsage,
            Double? gpuUsage,
            Boolean rtssAvailable,
            DateTime? nowUtc = null)
        {
            if (!rtssAvailable)
            {
                return TelemetryPerformanceEnvelope.Empty();
            }

            var now = nowUtc ?? DateTime.UtcNow;

            // When full history snapshots are available:
            if (history != null && history.Count > 0)
            {
                var recentHistory = history.Count > 16
                    ? history.Skip(history.Count - 16).ToList()
                    : history;

                var newest = history[history.Count - 1];
                var isStale = (now - newest.TimestampUtc) > TimeSpan.FromSeconds(5.0);

                var fpsList = recentHistory
                    .Where(s => s.Fps.HasValue && s.Fps.Value > 0 && !Double.IsNaN(s.Fps.Value) && !Double.IsInfinity(s.Fps.Value))
                    .Select(s => s.Fps.Value)
                    .OrderBy(v => v)
                    .ToList();

                var ftList = recentHistory
                    .Where(s => s.FrameTimeMilliseconds.HasValue && s.FrameTimeMilliseconds.Value > 0 && !Double.IsNaN(s.FrameTimeMilliseconds.Value))
                    .Select(s => s.FrameTimeMilliseconds.Value)
                    .OrderBy(v => v)
                    .ToList();

                if (ftList.Count == 0 && fpsList.Count > 0)
                {
                    ftList = fpsList.Select(f => 1000.0 / Math.Max(1.0, f)).OrderBy(v => v).ToList();
                }

                var cpuList = recentHistory
                    .Where(s => s.CpuUsagePercent.HasValue && s.CpuUsagePercent.Value >= 0 && s.CpuUsagePercent.Value <= 100)
                    .Select(s => s.CpuUsagePercent.Value)
                    .OrderBy(v => v)
                    .ToList();

                var gpuList = recentHistory
                    .Where(s => s.GpuUsagePercent.HasValue && s.GpuUsagePercent.Value >= 0 && s.GpuUsagePercent.Value <= 100)
                    .Select(s => s.GpuUsagePercent.Value)
                    .OrderBy(v => v)
                    .ToList();

                var ramList = recentHistory
                    .Where(s => s.RamUsagePercent.HasValue && s.RamUsagePercent.Value >= 0 && s.RamUsagePercent.Value <= 100)
                    .Select(s => s.RamUsagePercent.Value)
                    .OrderBy(v => v)
                    .ToList();

                var low1List = recentHistory
                    .Where(s => s.OnePercentLowFps.HasValue && s.OnePercentLowFps.Value > 0.0 && !Double.IsNaN(s.OnePercentLowFps.Value) && !Double.IsInfinity(s.OnePercentLowFps.Value))
                    .Select(s => s.OnePercentLowFps.Value)
                    .OrderBy(v => v)
                    .ToList();

                var low01List = recentHistory
                    .Where(s => s.ZeroPointOnePercentLowFps.HasValue && s.ZeroPointOnePercentLowFps.Value > 0.0 && !Double.IsNaN(s.ZeroPointOnePercentLowFps.Value) && !Double.IsInfinity(s.ZeroPointOnePercentLowFps.Value))
                    .Select(s => s.ZeroPointOnePercentLowFps.Value)
                    .OrderBy(v => v)
                    .ToList();

                var sampleCount = fpsList.Count;
                if (sampleCount < 2)
                {
                    // Fall back to frameStats if available and has more samples
                    if (frameStats != null && frameStats.SampleCount >= 2 && (frameStats.AverageFps > 0 || frameStats.InstantaneousFps > 0))
                    {
                        return BuildEnvelopeFromFrameStats(frameStats, lowStats, cpuUsage, gpuUsage, isStale);
                    }

                    if (sampleCount == 1)
                    {
                        var singleFps = fpsList[0];
                        var singleFt = ftList.Count > 0 ? ftList[0] : 1000.0 / Math.Max(1.0, singleFps);
                        var isSevere = singleFps < 20.0 || singleFt > 50.0;
                        return new TelemetryPerformanceEnvelope(
                            1,
                            singleFps,
                            singleFps,
                            null,
                            singleFt,
                            singleFt,
                            0,
                            0,
                            cpuList.Count > 0 ? cpuList[0] : cpuUsage,
                            cpuList.Count > 0 ? cpuList[0] : cpuUsage,
                            gpuList.Count > 0 ? gpuList[0] : gpuUsage,
                            gpuList.Count > 0 ? gpuList[0] : gpuUsage,
                            low1List.Count > 0 ? low1List[0] : (Double?)null,
                            low01List.Count > 0 ? low01List[0] : (Double?)null,
                            isSevere ? 0.60 : 0.30,
                            isStale);
                    }

                    return new TelemetryPerformanceEnvelope(sampleCount, 0, 0, null, 0, 0, 0, 0, null, null, null, null, null, null, 0.20, isStale, null, null);
                }

                var medianFps = CalculatePercentile(fpsList, 0.50);
                var p10Fps = CalculatePercentile(fpsList, 0.10);
                var p01Fps = sampleCount >= 20 ? (Double?)CalculatePercentile(fpsList, 0.01) : null;

                var medianFt = ftList.Count > 0 ? CalculatePercentile(ftList, 0.50) : (1000.0 / Math.Max(1.0, medianFps));
                var p95Ft = ftList.Count > 0 ? CalculatePercentile(ftList, 0.95) : medianFt;

                // Frame-time jitter: standard deviation of frame times
                Double ftJitter = 0.0;
                if (ftList.Count > 1)
                {
                    var ftAvg = ftList.Average();
                    ftJitter = Math.Sqrt(ftList.Select(x => (x - ftAvg) * (x - ftAvg)).Average());
                }

                // FPS jitter: standard deviation of FPS
                Double fpsJitter = 0.0;
                if (fpsList.Count > 1)
                {
                    var fAvg = fpsList.Average();
                    fpsJitter = Math.Sqrt(fpsList.Select(x => (x - fAvg) * (x - fAvg)).Average());
                }

                Double? cpuMedian = cpuList.Count > 0 ? CalculatePercentile(cpuList, 0.50) : cpuUsage;
                Double? cpuP95 = cpuList.Count > 0 ? CalculatePercentile(cpuList, 0.95) : cpuUsage;
                Double? gpuMedian = gpuList.Count > 0 ? CalculatePercentile(gpuList, 0.50) : gpuUsage;
                Double? gpuP95 = gpuList.Count > 0 ? CalculatePercentile(gpuList, 0.95) : gpuUsage;
                Double? ramMedian = ramList.Count > 0 ? CalculatePercentile(ramList, 0.50) : null;
                Double? ramP95 = ramList.Count > 0 ? CalculatePercentile(ramList, 0.95) : null;

                Double? oneLow = low1List.Count > 0 ? CalculatePercentile(low1List, 0.50) :
                    (frameStats?.HasNativeOnePercentLow == true && frameStats.OnePercentLowFps > 0.0 ? frameStats.OnePercentLowFps : (Double?)null);

                Double? zeroOneLow = low01List.Count > 0 ? CalculatePercentile(low01List, 0.50) :
                    (lowStats?.HasNativeZeroPointOnePercentLow == true && lowStats.ZeroPointOnePercentLowFps > 0.0 ? lowStats.ZeroPointOnePercentLowFps : (Double?)null);

                // Confidence assessment:
                // Once at least 2 valid, non-stale samples exist for the active workload, telemetry confidence
                // is sufficient (>= 0.50). Jitter reflects physical workload behavior (handled in ceiling derivation)
                // and optional hardware metrics (CPU/GPU/RAM/1% low) enrich confidence from 0.70 to 1.0 without ever
                // dropping below 0.50.
                var countFactor = sampleCount >= 8 ? 1.0 : (0.60 + ((sampleCount - 2) / 6.0) * 0.40);
                var completeness = 0.70 + (cpuMedian.HasValue && gpuMedian.HasValue ? 0.15 : 0.0) + (oneLow.HasValue ? 0.15 : 0.0);
                var confidence = isStale ? 0.0 : Math.Clamp(countFactor * completeness, 0.50, 1.0);

                return new TelemetryPerformanceEnvelope(
                    sampleCount,
                    medianFps,
                    p10Fps,
                    p01Fps,
                    medianFt,
                    p95Ft,
                    ftJitter,
                    fpsJitter,
                    cpuMedian,
                    cpuP95,
                    gpuMedian,
                    gpuP95,
                    oneLow,
                    zeroOneLow,
                    confidence,
                    isStale,
                    ramMedian,
                    ramP95);
            }

            // Fallback when only FrameTimeStatistics is supplied
            if (frameStats != null && frameStats.SampleCount >= 2 && (frameStats.AverageFps > 0 || frameStats.InstantaneousFps > 0))
            {
                return BuildEnvelopeFromFrameStats(frameStats, lowStats, cpuUsage, gpuUsage, false);
            }

            return TelemetryPerformanceEnvelope.Empty();
        }

        private static TelemetryPerformanceEnvelope BuildEnvelopeFromFrameStats(
            FrameTimeStatistics frameStats,
            ZeroPointOnePercentLowStatistics lowStats,
            Double? cpuUsage,
            Double? gpuUsage,
            Boolean isStale)
        {
            var sampleCount = frameStats.SampleCount;
            var avgFps = frameStats.AverageFps > 0 ? frameStats.AverageFps : frameStats.InstantaneousFps;
            var stdDevFt = Math.Sqrt(Math.Max(0.0, frameStats.FrameTimeVarianceMs));

            var hasNativeLow = frameStats.HasNativeOnePercentLow && frameStats.OnePercentLowFps > 0.0;
            var oneLow = hasNativeLow ? frameStats.OnePercentLowFps : (Double?)null;

            // Statistical 10th percentile derived from average framerate and variance - NEVER overridden by 1% Low!
            var p10Fps = Math.Max(0.0, avgFps - (stdDevFt > 0 ? (avgFps * (stdDevFt / Math.Max(1.0, frameStats.AverageFrameTimeMs)) * 1.28) : avgFps * 0.05));

            var medianFt = frameStats.AverageFrameTimeMs > 0 ? frameStats.AverageFrameTimeMs : (1000.0 / Math.Max(1.0, avgFps));
            var p95Ft = medianFt + (stdDevFt * 1.645);
            var fpsJitter = stdDevFt > 0 ? (avgFps * (stdDevFt / medianFt)) : 0.0;

            Double? zeroOneLow = (lowStats?.HasNativeZeroPointOnePercentLow == true && lowStats.ZeroPointOnePercentLowFps > 0.0)
                ? lowStats.ZeroPointOnePercentLowFps
                : (Double?)null;

            var countFactor = sampleCount >= 6 ? 1.0 : (0.60 + (Math.Max(0, sampleCount - 2) / 4.0) * 0.40);
            var confidence = isStale ? 0.0 : Math.Clamp(countFactor * 0.90, 0.50, 1.0);

            return new TelemetryPerformanceEnvelope(
                sampleCount,
                avgFps,
                p10Fps,
                null,
                medianFt,
                p95Ft,
                stdDevFt,
                fpsJitter,
                cpuUsage,
                cpuUsage,
                gpuUsage,
                gpuUsage,
                oneLow,
                zeroOneLow,
                confidence,
                isStale);
        }

        public static Double CalculatePercentile(IReadOnlyList<Double> sortedValues, Double percentile)
        {
            if (sortedValues == null || sortedValues.Count == 0)
            {
                return 0.0;
            }

            if (sortedValues.Count == 1)
            {
                return sortedValues[0];
            }

            percentile = Math.Clamp(percentile, 0.0, 1.0);
            var index = percentile * (sortedValues.Count - 1);
            var lower = (Int32)Math.Floor(index);
            var upper = (Int32)Math.Ceiling(index);

            if (lower == upper)
            {
                return sortedValues[lower];
            }

            var fraction = index - lower;
            return sortedValues[lower] + (fraction * (sortedValues[upper] - sortedValues[lower]));
        }

        /// <summary>
        /// Stage 1: Derives the workload's empirical sustainable performance ceiling directly
        /// from the statistical distribution properties of observed telemetry history.
        /// </summary>
        public static Double DeriveSustainablePerformanceCeiling(
            TelemetryPerformanceEnvelope envelope,
            BottleneckAnalysisResult bottleneck = null)
        {
            if (envelope == null || envelope.SampleCount < 4 || envelope.Confidence < 0.50 || envelope.IsStale || envelope.MedianFps <= 0.0)
            {
                return 0.0;
            }

            // 1. Frame-time pacing capability.
            // When pacing dispersion/jitter is significant, delivery cadence is extended by measured pacing dispersion.
            Double pacingCeilingFps = Double.MaxValue;
            if (envelope.MedianFrameTimeMs > 0.0)
            {
                var pacingSpreadMs = Math.Max(0.0, envelope.P95FrameTimeMs - envelope.MedianFrameTimeMs);
                var relativePacingSpread = pacingSpreadMs / envelope.MedianFrameTimeMs;
                var pacingPenaltyMs = pacingSpreadMs * relativePacingSpread;
                var effectiveFrameTimeMs = envelope.MedianFrameTimeMs + pacingPenaltyMs;

                if (effectiveFrameTimeMs > 0.0)
                {
                    pacingCeilingFps = 1000.0 / effectiveFrameTimeMs;
                }
            }

            // 2. Hardware saturation context.
            // Headroom exhaustion relative to signal variation, or external bottleneck diagnosis.
            var isGpuExhausted = false;
            if (envelope.GpuP95.HasValue && envelope.GpuMedian.HasValue)
            {
                var gpuHeadroom = 100.0 - envelope.GpuP95.Value;
                var gpuSpread = envelope.GpuP95.Value - envelope.GpuMedian.Value;
                isGpuExhausted = gpuHeadroom <= (gpuSpread + 1.0);
            }

            var isCpuExhausted = false;
            if (envelope.CpuP95.HasValue && envelope.CpuMedian.HasValue)
            {
                var cpuHeadroom = 100.0 - envelope.CpuP95.Value;
                var cpuSpread = envelope.CpuP95.Value - envelope.CpuMedian.Value;
                isCpuExhausted = cpuHeadroom <= (cpuSpread + 1.0);
            }

            var isRamExhausted = false;
            if (envelope.RamP95.HasValue)
            {
                isRamExhausted = envelope.RamP95.Value >= 95.0;
            }

            var isHardwareBound = (bottleneck != null &&
                                  (bottleneck.State == BottleneckAnalysisState.GpuBound ||
                                   bottleneck.State == BottleneckAnalysisState.CpuBound ||
                                   bottleneck.State == BottleneckAnalysisState.MemoryPressure))
                                  || isGpuExhausted || isCpuExhausted || isRamExhausted;

            // 3. Lower throughput tail.
            var lowerThroughput = envelope.P10Fps;
            if (envelope.OnePercentLowFps.HasValue && envelope.OnePercentLowFps.Value > 0.0)
            {
                // CRITICAL RULE: 1% Low / 0.1% Low must NEVER independently force the Advisor down to a lower FPS cap.
                // 1% Low is only admitted if it is plausible and consistent with the rolling distribution:
                // An extreme outlier (e.g. 1 FPS while rolling window is 60 FPS) must be completely ignored.
                var isConsistentWithWindow = envelope.OnePercentLowFps.Value >= (envelope.MedianFps * 0.70);

                if (isConsistentWithWindow)
                {
                    lowerThroughput = Math.Min(lowerThroughput, envelope.OnePercentLowFps.Value);
                }
            }

            // 4. Synthesize empirical sustainable ceiling.
            Double sustainableCeiling;
            if (isHardwareBound)
            {
                // When hardware is saturated, the ceiling is strictly bounded by demonstrated lower throughput and pacing.
                sustainableCeiling = Math.Min(lowerThroughput, pacingCeilingFps);
            }
            else
            {
                // When unconstrained by hardware saturation, calculate sustainable delivery based on central
                // delivery tendency penalized by relative tail dispersion.
                var tailSpread = Math.Max(0.0, envelope.MedianFps - lowerThroughput);
                var relativeDispersion = envelope.MedianFps > 0.0 ? tailSpread / envelope.MedianFps : 0.0;
                var dispersionPenalty = tailSpread * relativeDispersion;
                var stableDelivery = Math.Max(0.0, envelope.MedianFps - dispersionPenalty);

                sustainableCeiling = Math.Min(stableDelivery, pacingCeilingFps);
            }

            return Math.Max(0.0, sustainableCeiling);
        }

        /// <summary>
        /// Stage 2: Maps the empirical sustainable ceiling to the highest supported cap target
        /// {30, 60, 90, 120, 144, 180} that does not exceed the known display/system ceiling.
        /// </summary>
        public static Int32 MapToSupportedCap(Double sustainableCeiling, Int32 displayCeiling)
        {
            if (Double.IsNaN(sustainableCeiling) || Double.IsInfinity(sustainableCeiling) || sustainableCeiling <= 0.0)
            {
                return 0;
            }

            var effectiveDisplayCeiling = displayCeiling > 0
                ? GetMaxTargetForRefreshRate(displayCeiling)
                : SupportedTargets[SupportedTargets.Length - 1];

            var roundedCeiling = (Int64)Math.Round(sustainableCeiling, MidpointRounding.AwayFromZero);

            Int32 selected = 0;
            foreach (var target in SupportedTargets)
            {
                var margin = Math.Max(2.5, target * 0.03);
                if (target <= effectiveDisplayCeiling && (roundedCeiling >= target || sustainableCeiling >= (target - margin)))
                {
                    selected = target;
                }
            }

            return selected;
        }

        public static Boolean IsCandidateSustainable(Int32 candidate, TelemetryPerformanceEnvelope envelope)
        {
            if (candidate <= 0 || envelope == null || envelope.IsStale)
            {
                return false;
            }

            if (envelope.MedianFps <= 0.0 || Double.IsNaN(envelope.MedianFps) || envelope.MedianFps > 10000.0)
            {
                return false;
            }

            // CRITICAL: A candidate cap CANNOT be sustainable if current median throughput is well below it!
            var minFpsRequired = candidate * 0.90;
            if (envelope.MedianFps < minFpsRequired)
            {
                return false;
            }

            var targetFtMs = 1000.0 / candidate;
            if (envelope.MedianFrameTimeMs > (targetFtMs * 1.15))
            {
                return false;
            }

            // CRITICAL: 1% Low / 0.1% Low must NEVER independently veto candidate sustainability.
            // Pacing, throughput, and hardware headroom are comprehensively modeled in the sustainable ceiling.
            var ceiling = DeriveSustainablePerformanceCeiling(envelope, null);
            var roundedCeiling = (Int64)Math.Round(ceiling, MidpointRounding.AwayFromZero);
            var margin = Math.Max(2.5, candidate * 0.03);
            return roundedCeiling >= candidate || ceiling >= (candidate - margin);
        }

        public Boolean IsCandidateSustainable(
            Int32 candidate,
            Double sustainedFps,
            FrameTimeStatistics frameStats,
            ZeroPointOnePercentLowStatistics lowStats,
            Double? cpuUsage,
            Double? gpuUsage,
            BottleneckAnalysisResult bottleneck,
            Int32 maxAllowedTarget)
        {
            if (candidate <= 0 || candidate > maxAllowedTarget)
            {
                return false;
            }

            var envelope = BuildEnvelope(null, frameStats, lowStats, cpuUsage, gpuUsage, true);
            return IsCandidateSustainable(candidate, envelope);
        }

        public Boolean IsCandidateSustainable(
            Int32 candidate,
            Double sustainedFps,
            FrameTimeStatistics frameStats,
            ZeroPointOnePercentLowStatistics lowStats,
            Double? cpuUsage,
            Double? gpuUsage,
            Int32 maxAllowedTarget)
        {
            return this.IsCandidateSustainable(
                candidate,
                sustainedFps,
                frameStats,
                lowStats,
                cpuUsage,
                gpuUsage,
                null,
                maxAllowedTarget);
        }

        private AdvisorDecision ApplyHysteresis(AdvisorDecision rawDecision, Int32 currentCap)
        {
            if (rawDecision.Type == AdvisorDecisionType.Recovery)
            {
                // Immediate transition to recovery when performance collapses - no delayed streak!
                this._proposedCandidate = 0;
                this._candidateStreak = 0;
                this._unstableStreak = 0;
                this._lastDecision = rawDecision;
                return rawDecision;
            }

            if (rawDecision.Type == AdvisorDecisionType.Diagnostic)
            {
                // If we were previously stable, require consecutive degraded ticks before falling out
                if (this._lastDecision.Type == AdvisorDecisionType.Stable)
                {
                    this._unstableStreak++;
                    if (this._unstableStreak < RequiredUnstableStreak)
                    {
                        return this._lastDecision;
                    }
                }

                this._proposedCandidate = 0;
                this._candidateStreak = 0;
                this._unstableStreak = 0;
                this._lastDecision = rawDecision;
                return rawDecision;
            }

            if (rawDecision.Type == AdvisorDecisionType.Recommend)
            {
                // If we were previously stable, require consecutive degraded ticks before falling out
                if (this._lastDecision.Type == AdvisorDecisionType.Stable)
                {
                    this._unstableStreak++;
                    if (this._unstableStreak < RequiredUnstableStreak)
                    {
                        return this._lastDecision;
                    }
                }

                // Raising vs Lowering recommendation hysteresis
                var isRaising = this._proposedCandidate > 0 && rawDecision.TargetFps > this._proposedCandidate;
                var requiredStreak = isRaising ? 3 : 2;

                if (rawDecision.TargetFps == this._proposedCandidate)
                {
                    this._candidateStreak++;
                }
                else
                {
                    this._proposedCandidate = rawDecision.TargetFps;
                    this._candidateStreak = 1;
                }

                if (this._candidateStreak >= requiredStreak)
                {
                    this._unstableStreak = 0;
                    this._lastDecision = rawDecision;
                    return rawDecision;
                }

                // If we were already recommending this candidate, retain it
                if (this._lastDecision.Type == AdvisorDecisionType.Recommend && this._lastDecision.TargetFps == rawDecision.TargetFps)
                {
                    return this._lastDecision;
                }

                // While settling, NEVER claim STABLE FPS
                return AdvisorDecision.Unknown();
            }

            if (rawDecision.Type == AdvisorDecisionType.Stable)
            {
                this._proposedCandidate = 0;
                this._candidateStreak = 0;
                this._unstableStreak = 0;
                this._lastDecision = rawDecision;
                return rawDecision;
            }

            // Raw decision is Unknown
            if (this._lastDecision.Type == AdvisorDecisionType.Stable)
            {
                this._unstableStreak++;
                if (this._unstableStreak < RequiredUnstableStreak)
                {
                    return this._lastDecision;
                }
            }

            this._lastDecision = AdvisorDecision.Unknown();
            return this._lastDecision;
        }
    }
}
