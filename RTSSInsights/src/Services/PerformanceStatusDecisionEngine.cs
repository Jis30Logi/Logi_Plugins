namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Loupedeck.RTSSPerformancePlugin.Helpers;

    public enum PerformanceStatusState
    {
        Unknown = 0,
        Balanced = 1,
        PerformanceWatch = 2,
        FpsDegraded = 3,
        GpuPressure = 4,
        CpuPressure = 5,
        MemoryPressure = 6,
        SeverePerformance = 7,
    }

    public sealed class PerformanceStatusResult
    {
        public PerformanceStatusResult(
            PerformanceStatusState state,
            String label,
            ColorTier tier,
            String reason,
            IReadOnlyList<String> activeConditions,
            Int32 sampleCount = 0,
            Double confidence = 1.0,
            Boolean cpuEvidenceAvailable = true,
            Boolean gpuEvidenceAvailable = true,
            Boolean memoryEvidenceAvailable = true)
        {
            this.State = state;
            this.Label = label ?? String.Empty;
            this.Tier = tier;
            this.Reason = reason ?? String.Empty;
            this.ActiveConditions = activeConditions ?? Array.Empty<String>();
            this.SampleCount = sampleCount;
            this.Confidence = confidence;
            this.CpuEvidenceAvailable = cpuEvidenceAvailable;
            this.GpuEvidenceAvailable = gpuEvidenceAvailable;
            this.MemoryEvidenceAvailable = memoryEvidenceAvailable;
        }

        public PerformanceStatusState State { get; }

        public String Label { get; }

        public ColorTier Tier { get; }

        public String Reason { get; }

        public IReadOnlyList<String> ActiveConditions { get; }

        public Int32 SampleCount { get; }

        public Double Confidence { get; }

        public Boolean CpuEvidenceAvailable { get; }

        public Boolean GpuEvidenceAvailable { get; }

        public Boolean MemoryEvidenceAvailable { get; }

        public static PerformanceStatusResult Unknown() =>
            new PerformanceStatusResult(PerformanceStatusState.Unknown, "UNKNOWN", ColorTier.Neutral, "Waiting for telemetry", Array.Empty<String>(), 0, 1.0, false, false, false);

        public static PerformanceStatusResult NoGame() =>
            new PerformanceStatusResult(PerformanceStatusState.Unknown, "NO GAME", ColorTier.Neutral, "No active foreground game", Array.Empty<String>(), 0, 1.0, false, false, false);
    }

    /// <summary>
    /// Centralized authoritative Performance Status synthesis engine.
    /// Synthesizes the overall system condition by interpreting the authoritative 3-tier severity
    /// (Green, Yellow, Red) already calculated for CPU, GPU, Live FPS, and RAM.
    /// 
    /// Tiles answer: WHAT is happening? (Measurement + Severity)
    /// Performance Status answers: WHAT does the combined state mean? (Interpretation)
    /// </summary>
    public sealed class PerformanceStatusDecisionEngine
    {
        public const Int32 DefaultWindowSize = 10;
        public const Int32 MinimumSampleCount = 4;
        public const Int32 RequiredStableEvaluations = 3;
        public const Int64 DefaultRotationIntervalMs = 2000;

        private readonly Object _sync = new Object();
        private readonly Int32 _windowSize;
        private PerformanceStatusResult _confirmedResult = PerformanceStatusResult.Unknown();
        private PerformanceStatusResult _candidateResult = PerformanceStatusResult.Unknown();
        private Int32 _candidateStreak = 0;

        public PerformanceStatusDecisionEngine(Int32 windowSize = DefaultWindowSize)
        {
            this._windowSize = Math.Max(MinimumSampleCount, windowSize);
        }

        public Int32 WindowSize => this._windowSize;

        public PerformanceStatusResult ConfirmedResult
        {
            get
            {
                lock (this._sync)
                {
                    return this._confirmedResult;
                }
            }
        }

        public void Reset()
        {
            lock (this._sync)
            {
                this._confirmedResult = PerformanceStatusResult.Unknown();
                this._candidateResult = PerformanceStatusResult.Unknown();
                this._candidateStreak = 0;
            }
        }

        public PerformanceStatusResult EvaluateWithHysteresis(
            IReadOnlyList<PerformanceSnapshot> snapshots,
            Double effectiveCap = 0,
            Double displayRefreshRate = 0,
            Int64 currentTimeMs = 0)
        {
            var raw = this.EvaluateRaw(snapshots, effectiveCap, displayRefreshRate, currentTimeMs);

            lock (this._sync)
            {
                if (raw.State == PerformanceStatusState.Unknown)
                {
                    this._candidateStreak = 0;
                    this._candidateResult = raw;
                    this._confirmedResult = raw;
                    return this._confirmedResult;
                }

                if (raw.State == this._candidateResult.State)
                {
                    this._candidateStreak++;
                }
                else
                {
                    this._candidateResult = raw;
                    this._candidateStreak = 1;
                }

                if (this._candidateStreak >= RequiredStableEvaluations ||
                    this._confirmedResult.State == PerformanceStatusState.Unknown)
                {
                    this._confirmedResult = raw;
                }
                else if (this._confirmedResult.ActiveConditions.Count > 1 && currentTimeMs > 0)
                {
                    // Allow label rotation for already confirmed multi-condition states
                    const Int64 intervalMs = DefaultRotationIntervalMs;
                    var index = (Int32)((currentTimeMs / intervalMs) % this._confirmedResult.ActiveConditions.Count);
                    if (index < 0) index = 0;

                    this._confirmedResult = new PerformanceStatusResult(
                        this._confirmedResult.State,
                        this._confirmedResult.ActiveConditions[index],
                        this._confirmedResult.Tier,
                        this._confirmedResult.Reason,
                        this._confirmedResult.ActiveConditions,
                        this._confirmedResult.SampleCount,
                        this._confirmedResult.Confidence,
                        this._confirmedResult.CpuEvidenceAvailable,
                        this._confirmedResult.GpuEvidenceAvailable,
                        this._confirmedResult.MemoryEvidenceAvailable);
                }

                return this._confirmedResult;
            }
        }

        public PerformanceStatusResult EvaluateRaw(
            IReadOnlyList<PerformanceSnapshot> snapshots,
            Double effectiveCap = 0,
            Double displayRefreshRate = 0,
            Int64 currentTimeMs = 0)
        {
            if (snapshots == null || snapshots.Count < MinimumSampleCount)
            {
                return PerformanceStatusResult.Unknown();
            }

            var window = snapshots.Skip(Math.Max(0, snapshots.Count - this._windowSize)).ToList();
            if (window.Count < MinimumSampleCount)
            {
                return PerformanceStatusResult.Unknown();
            }

            var cpuSamples = window
                .Where(s => s.CpuUsagePercent.HasValue && !Double.IsNaN(s.CpuUsagePercent.Value) && s.CpuUsagePercent.Value >= 0)
                .Select(s => s.CpuUsagePercent.Value)
                .OrderBy(v => v)
                .ToList();

            var gpuSamples = window
                .Where(s => s.GpuUsagePercent.HasValue && !Double.IsNaN(s.GpuUsagePercent.Value) && s.GpuUsagePercent.Value >= 0)
                .Select(s => s.GpuUsagePercent.Value)
                .OrderBy(v => v)
                .ToList();

            var ramSamples = window
                .Where(s => s.RamUsagePercent.HasValue && !Double.IsNaN(s.RamUsagePercent.Value) && s.RamUsagePercent.Value >= 0)
                .Select(s => s.RamUsagePercent.Value)
                .OrderBy(v => v)
                .ToList();

            var fpsSamples = window
                .Where(s => s.Fps.HasValue && !Double.IsNaN(s.Fps.Value) && s.Fps.Value > 0)
                .Select(s => s.Fps.Value)
                .OrderBy(v => v)
                .ToList();

            var ftSamples = window
                .Where(s => s.FrameTimeMilliseconds.HasValue && !Double.IsNaN(s.FrameTimeMilliseconds.Value) && s.FrameTimeMilliseconds.Value > 0)
                .Select(s => s.FrameTimeMilliseconds.Value)
                .OrderBy(v => v)
                .ToList();

            var targetFps = effectiveCap > 0
                ? effectiveCap
                : (displayRefreshRate > 0 ? displayRefreshRate : 60.0);

            var cpuMedian = cpuSamples.Count > 0 ? CalculatePercentile(cpuSamples, 0.50) : 0.0;
            var gpuMedian = gpuSamples.Count > 0 ? CalculatePercentile(gpuSamples, 0.50) : 0.0;
            var ramMedian = ramSamples.Count > 0 ? CalculatePercentile(ramSamples, 0.50) : 0.0;
            var fpsMedian = fpsSamples.Count > 0 ? CalculatePercentile(fpsSamples, 0.50) : 0.0;
            var ftMedian = ftSamples.Count > 0 ? CalculatePercentile(ftSamples, 0.50) : (fpsMedian > 0 ? 1000.0 / fpsMedian : 16.67);

            // Derive metric severity directly from the single source of truth: existing 3-tier color system
            var cpuStatus = cpuSamples.Count > 0 ? ColorTierHelper.GetLoadTier(cpuMedian) : ColorTier.Good;
            var gpuStatus = gpuSamples.Count > 0 ? ColorTierHelper.GetLoadTier(gpuMedian) : ColorTier.Good;
            var ramStatus = ramSamples.Count > 0 ? ColorTierHelper.GetLoadTier(ramMedian) : ColorTier.Good;
            var fpsStatus = fpsSamples.Count > 0
                ? ColorTierHelper.GetTier(FpsHealthStateService.GetTier(fpsMedian, targetFps))
                : ColorTier.Good;
            var ftStatus = ColorTierHelper.GetFrameTimeTier(ftMedian);

            return Synthesize(
                cpuStatus,
                gpuStatus,
                fpsStatus,
                ramStatus,
                ftStatus,
                ColorTier.Good,
                ColorTier.Good,
                fpsMedian,
                ftMedian,
                targetFps,
                cpuMedian,
                gpuMedian,
                ramMedian,
                currentTimeMs,
                window.Count,
                cpuSamples.Count > 0,
                gpuSamples.Count > 0,
                ramSamples.Count > 0);
        }

        /// <summary>
        /// Pure status-driven synthesis based directly on the authoritative 3-tier severity
        /// of CPU, GPU, Live FPS, and RAM metrics.
        /// </summary>
        public static PerformanceStatusResult Synthesize(
            ColorTier cpuStatus,
            ColorTier gpuStatus,
            ColorTier fpsStatus,
            ColorTier ramStatus,
            ColorTier frameTimeStatus = ColorTier.Good,
            ColorTier onePercentLowStatus = ColorTier.Good,
            ColorTier zeroPointOnePercentLowStatus = ColorTier.Good,
            Double currentFps = 0.0,
            Double currentFrameTime = 0.0,
            Double currentFpsCap = 0.0,
            Double cpuUsagePercent = 0.0,
            Double gpuUsagePercent = 0.0,
            Double ramUsagePercent = 0.0,
            Int64 currentTimeMs = 0,
            Int32 sampleCount = MinimumSampleCount,
            Boolean cpuEvidenceAvailable = true,
            Boolean gpuEvidenceAvailable = true,
            Boolean memoryEvidenceAvailable = true)
        {
            var isCpuRed = cpuStatus == ColorTier.Critical;
            var isGpuRed = gpuStatus == ColorTier.Critical;
            var isFpsRed = fpsStatus == ColorTier.Critical;
            var isRamRed = ramStatus == ColorTier.Critical;

            var anyRed = isCpuRed || isGpuRed || isFpsRed || isRamRed;

            // 1. All Healthy -> BALANCED (Green)
            // Only red metrics produce a bottleneck. Yellow metrics, frame time alone, or 1% low
            // must NEVER produce a bottleneck or non-green status.
            if (!anyRed)
            {
                return new PerformanceStatusResult(
                    PerformanceStatusState.Balanced,
                    "BALANCED",
                    ColorTier.Good,
                    "All performance metrics operating normally",
                    Array.Empty<String>(),
                    sampleCount,
                    1.0,
                    cpuEvidenceAvailable,
                    gpuEvidenceAvailable,
                    memoryEvidenceAvailable);
            }

            // 2. Critical Red states: calculate relative severities in [0.0, 1.0]
            Double cpuSeverity = isCpuRed ? (cpuUsagePercent >= 90.0 ? (cpuUsagePercent - 90.0) / 10.0 : 0.5) : 0.0;
            Double gpuSeverity = isGpuRed ? (gpuUsagePercent >= 90.0 ? (gpuUsagePercent - 90.0) / 10.0 : 0.5) : 0.0;
            Double ramSeverity = isRamRed ? (ramUsagePercent >= 90.0 ? (ramUsagePercent - 90.0) / 10.0 : 0.5) : 0.0;
            Double fpsSeverity = isFpsRed
                ? (currentFpsCap > 0 && currentFps > 0 ? Math.Clamp(1.0 - (currentFps / Math.Max(1.0, currentFpsCap * 0.70)), 0.0, 1.0) : 0.5)
                : 0.0;

            var candidates = new List<(PerformanceStatusState State, String Label, Double Severity, Int32 Priority)>();

            // Hardware resource saturation takes priority over secondary framerate drop symptom
            if (isGpuRed) candidates.Add((PerformanceStatusState.GpuPressure, "GPU LIMITED", gpuSeverity, 0));
            if (isCpuRed) candidates.Add((PerformanceStatusState.CpuPressure, "CPU LIMITED", cpuSeverity, 1));
            if (isRamRed) candidates.Add((PerformanceStatusState.MemoryPressure, "RAM LIMITED", ramSeverity, 2));
            if (isFpsRed) candidates.Add((PerformanceStatusState.FpsDegraded, "FPS LIMITED", fpsSeverity, 3));

            // Sort candidates by severity descending, with priority as tie-breaker
            candidates.Sort((a, b) =>
            {
                if (Math.Abs(b.Severity - a.Severity) > 0.05)
                {
                    return b.Severity.CompareTo(a.Severity);
                }
                return a.Priority.CompareTo(b.Priority);
            });

            var activeConditions = candidates.Select(c => c.Label).ToList();

            PerformanceStatusState resolvedState;
            String resolvedLabel;

            if (candidates.Count == 1)
            {
                resolvedState = candidates[0].State;
                resolvedLabel = candidates[0].Label;
            }
            else
            {
                var isMultiHardwareSaturated = isCpuRed && isGpuRed;
                resolvedState = isMultiHardwareSaturated ? PerformanceStatusState.SeverePerformance : candidates[0].State;

                const Int64 intervalMs = DefaultRotationIntervalMs;
                var index = (Int32)((currentTimeMs / intervalMs) % candidates.Count);
                if (index < 0) index = 0;

                resolvedLabel = candidates[index].Label;
            }

            return new PerformanceStatusResult(
                resolvedState,
                resolvedLabel,
                ColorTier.Critical,
                "Critical performance pressure detected",
                activeConditions,
                sampleCount,
                1.0,
                cpuEvidenceAvailable,
                gpuEvidenceAvailable,
                memoryEvidenceAvailable);
        }

        public static Double CalculatePercentile(IReadOnlyList<Double> sortedValues, Double percentile)
        {
            if (sortedValues == null || sortedValues.Count == 0)
            {
                return 0.0;
            }

            var count = sortedValues.Count;
            if (count == 1)
            {
                return sortedValues[0];
            }

            percentile = Math.Clamp(percentile, 0.0, 1.0);
            var index = percentile * (count - 1);
            var lower = (Int32)Math.Floor(index);
            var upper = (Int32)Math.Ceiling(index);

            if (lower == upper)
            {
                return sortedValues[lower];
            }

            var fraction = index - lower;
            return sortedValues[lower] + (fraction * (sortedValues[upper] - sortedValues[lower]));
        }
    }
}
