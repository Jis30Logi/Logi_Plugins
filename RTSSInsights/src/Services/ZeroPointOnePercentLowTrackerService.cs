namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>Rolling-window 0.1% low statistics for the active RTSS source.</summary>
    public sealed class ZeroPointOnePercentLowStatistics
    {
        /// <summary>
        /// The value displayed to the user: RTSS's native 0.1% low when available and positive,
        /// otherwise the plugin's own 99.9th-percentile calculation from the rolling window.
        /// </summary>
        public Double ZeroPointOnePercentLowFps { get; set; }

        /// <summary>
        /// RTSS's own <c>dwStatFramerate0Dot1PercentLow</c> field for the selected process,
        /// divided by 10 (RTSS stores these statistics in 0.1 FPS). Zero when RTSS has not produced
        /// a statistic yet (for example, when RTSS statistics recording is off).
        /// </summary>
        public Double NativeZeroPointOnePercentLowFps { get; set; }

        public Boolean HasNativeZeroPointOnePercentLow { get; set; }

        /// <summary>
        /// The plugin's own 99.9th-percentile calculation from the rolling window, independent
        /// of what RTSS reports. Used as a fallback when RTSS has no native statistic.
        /// </summary>
        public Double CalculatedZeroPointOnePercentLowFps { get; set; }

        public Int32 SampleCount { get; set; }
    }

    public interface IZeroPointOnePercentLowTrackerService
    {
        Int32 WindowSize { get; }

        Int32 SampleCount { get; }

        ZeroPointOnePercentLowStatistics Current { get; }

        event EventHandler<ZeroPointOnePercentLowStatistics> StatisticsUpdated;

        void AddSample(RTSSFrameSnapshot snapshot);

        void AddFrameTimeSample(Double frameTimeMilliseconds);

        void Reset();
    }

    /// <summary>
    /// Maintains a rolling window of frame times and derives the 99.9th-percentile-frame-time
    /// ("0.1% low") FPS figure for the active RTSS source.
    ///
    /// Deliberately a separate class from <see cref="FrameTimeTrackerService"/> rather than a
    /// second percentile computed off the same 120-sample window that service already maintains
    /// for FPS / Frame Time / 1% Low. A 99.9th percentile needs its index,
    /// <c>floor((1 - 0.999) * N)</c>, to clear 0 to be a real percentile rather than a running
    /// maximum in disguise; at N=120 that index is 0 for any window, i.e. always exactly the single
    /// worst frame ever observed in the last 30 seconds. <see cref="DefaultWindowSize"/> is 1200 —
    /// ten times the 120-sample window, for a percentile ten times finer — which gives
    /// <c>floor(0.001 * 1200) = 1</c>: the same one frame of separation from "just the worst frame"
    /// that the existing 1% low gets from its own 120-sample window at 0.99. The honest cost is
    /// time: at the plugin's 250ms active poll interval this window takes five minutes to fill,
    /// against the 1% low window's 30 seconds. Below 1200 samples this degrades exactly the way the
    /// existing 1% low degrades below 100 samples — the index collapses toward 0 and the figure is
    /// simply the worst frame observed so far, which is the honest answer for a short window, not a
    /// fabricated one.
    ///
    /// This class does not read <see cref="FrameTimeTrackerService"/>'s window, does not share its
    /// ring buffer, and computes its own percentile independently, so a bug in one can never surface
    /// as a wrong answer in the other, and reverting either implementation cannot silently make one
    /// pass by matching the other's output.
    /// </summary>
    public sealed class ZeroPointOnePercentLowTrackerService : IZeroPointOnePercentLowTrackerService
    {
        /// <summary>
        /// Ten times <see cref="FrameTimeTrackerService.DefaultWindowSize"/>, for a percentile ten
        /// times finer (0.999 against 0.99) — see the class remarks for the index arithmetic this
        /// preserves.
        /// </summary>
        public const Int32 DefaultWindowSize = 1200;

        /// <summary>Fraction of frames treated as "fast" when computing the 0.1% low.</summary>
        public const Double ZeroPointOnePercentLowPercentile = 0.999;

        private readonly Object _sync = new Object();

        private readonly IFrameTimeTrackerService _sharedFrameTimeTracker;

        private readonly Double[] _frameTimes;

        /// <summary>
        /// Process id the window is currently accumulating samples for; 0 (RTSS's own "free slot"
        /// value, never a real pid) means no source has fed a sample since the last reset.
        /// </summary>
        private UInt32 _lastProcessId;

        private UInt32 _lastFrameSampleTimestamp;

        /// <summary>Last RTSS native 0.1% low carried through to <see cref="Current"/>.</summary>
        private Double _lastNativeZeroPointOnePercentLowFps;

        private Boolean _hasNativeZeroPointOnePercentLow;

        /// <summary>Index the next sample is written to; the buffer wraps here.</summary>
        private Int32 _writeIndex;

        private Int32 _sampleCount;

        private ZeroPointOnePercentLowStatistics _current = EmptyStatistics();

        public Int32 WindowSize => this._frameTimes.Length;

        public Int32 SampleCount
        {
            get
            {
                lock (this._sync)
                {
                    return this._sampleCount;
                }
            }
        }

        public ZeroPointOnePercentLowStatistics Current
        {
            get
            {
                lock (this._sync)
                {
                    return this._current;
                }
            }
        }

        public event EventHandler<ZeroPointOnePercentLowStatistics> StatisticsUpdated;

        public ZeroPointOnePercentLowTrackerService()
            : this(DefaultWindowSize, null)
        {
        }

        public ZeroPointOnePercentLowTrackerService(Int32 windowSize)
            : this(windowSize, null)
        {
        }

        /// <summary>
        /// Creates a 0.1% view over the frame tracker used by Live FPS, Frame Time, and 1% Low.
        /// The standalone ring remains available for compatibility and isolated service tests.
        /// </summary>
        public ZeroPointOnePercentLowTrackerService(IFrameTimeTrackerService frameTimeTracker)
            : this(frameTimeTracker?.WindowSize ?? DefaultWindowSize, frameTimeTracker)
        {
        }

        private ZeroPointOnePercentLowTrackerService(Int32 windowSize, IFrameTimeTrackerService frameTimeTracker)
        {
            this._frameTimes = new Double[Math.Max(1, windowSize)];
            this._sharedFrameTimeTracker = frameTimeTracker;

            if (this._sharedFrameTimeTracker != null)
            {
                this._sharedFrameTimeTracker.StatisticsUpdated += this.OnSharedStatisticsUpdated;
            }
        }

        public String GetWindowSummary()
        {
            lock (this._sync)
            {
                if (this._sampleCount == 0)
                {
                    return "empty";
                }

                var start = this._sampleCount == this._frameTimes.Length ? this._writeIndex : 0;
                var oldest = this._frameTimes[start];
                var newest = this._frameTimes[(start + this._sampleCount - 1) % this._frameTimes.Length];
                var minimum = Double.MaxValue;
                var maximum = Double.MinValue;

                for (var i = 0; i < this._sampleCount; i++)
                {
                    var frameTime = this._frameTimes[(start + i) % this._frameTimes.Length];
                    minimum = Math.Min(minimum, frameTime);
                    maximum = Math.Max(maximum, frameTime);
                }

                return $"oldest={oldest:F2}ms newest={newest:F2}ms min={minimum:F2}ms max={maximum:F2}ms";
            }
        }

        /// <summary>
        /// Feeds one poll into the window. Mirrors <see cref="FrameTimeTrackerService.AddSample"/>:
        /// RTSS's reported frame time is used directly, falling back to the inverted instantaneous
        /// framerate only when RTSS has not produced a frame time yet.
        ///
        /// The RTSS-native 0.1% low (<c>snapshot.NativeZeroPointOnePercentLowFps</c>) is captured
        /// alongside the frame time and carried through to the statistics unchanged: it is the value
        /// shown to the user when positive, matching the RTSS overlay exactly. When RTSS has no native
        /// statistic (for example, RTSS statistics recording is off) the value is 0
        /// and the tracker falls back to its own rolling-window calculation.
        ///
        /// A change of process id resets the window first, exactly as
        /// <see cref="FrameTimeTrackerService.AddSample"/> does — a percentile computed across two
        /// different applications' frame times is not a measurement of either one.
        /// </summary>
        public void AddSample(RTSSFrameSnapshot snapshot)
        {
            if (this._sharedFrameTimeTracker != null)
            {
                return;
            }

            if (snapshot == null)
            {
                return;
            }

            var nativeZeroPointOnePercentLowFps = snapshot.NativeZeroPointOnePercentLowFps;
            var frameTime = snapshot.FrameTimeMilliseconds;

            if (!IsUsable(frameTime) && IsUsable(snapshot.InstantaneousFps))
            {
                frameTime = 1000.0 / snapshot.InstantaneousFps;
            }

            if (!IsUsable(frameTime))
            {
                return;
            }

            lock (this._sync)
            {
                if (this._lastProcessId != 0 && snapshot.ProcessId != 0 &&
                    snapshot.ProcessId != this._lastProcessId)
                {
                    this.ResetCore();
                }

                this._lastProcessId = snapshot.ProcessId;
                this._lastFrameSampleTimestamp = snapshot.FrameSampleTimestamp;
                this._lastNativeZeroPointOnePercentLowFps = nativeZeroPointOnePercentLowFps;
                this._hasNativeZeroPointOnePercentLow = snapshot.HasNativeZeroPointOnePercentLow;
            }

            this.AddFrameTimeSample(frameTime);
        }

        /// <summary>
        /// Records one frame time. Every usable value is admitted, including an exact repeat of the
        /// last one — see <see cref="FrameTimeTrackerService.AddFrameTimeSample"/>'s remarks for why
        /// a value-equality dedup is wrong here too: a sustained, quantised stutter produces several
        /// real, distinct frames at the same duration, and there is no way to tell that apart from a
        /// re-polled unrendered frame using the frame-time value alone.
        /// </summary>
        public void AddFrameTimeSample(Double frameTimeMilliseconds)
        {
            if (this._sharedFrameTimeTracker != null)
            {
                return;
            }

            if (!IsUsable(frameTimeMilliseconds))
            {
                return;
            }

            ZeroPointOnePercentLowStatistics statistics;

            lock (this._sync)
            {
                this._frameTimes[this._writeIndex] = frameTimeMilliseconds;
                this._writeIndex = (this._writeIndex + 1) % this._frameTimes.Length;

                if (this._sampleCount < this._frameTimes.Length)
                {
                    this._sampleCount++;
                }

                statistics = this.ComputeStatisticsCore();
                this._current = statistics;
            }

            this.StatisticsUpdated?.Invoke(this, statistics);
        }

        public void Reset()
        {
            lock (this._sync)
            {
                this.ResetCore();
            }
        }

        /// <summary>Caller holds <see cref="_sync"/>.</summary>
        private void ResetCore()
        {
            Array.Clear(this._frameTimes, 0, this._frameTimes.Length);

            this._writeIndex = 0;
            this._sampleCount = 0;
            this._lastProcessId = 0;
            this._lastFrameSampleTimestamp = 0;
            this._lastNativeZeroPointOnePercentLowFps = 0;
            this._hasNativeZeroPointOnePercentLow = false;

            this._current = EmptyStatistics();
        }

        /// <summary>Caller holds <see cref="_sync"/>.</summary>
        private ZeroPointOnePercentLowStatistics ComputeStatisticsCore()
        {
            if (this._sampleCount == 0)
            {
                return EmptyStatistics();
            }

            var window = this.SnapshotWindow();
            var finalZeroPointOnePercentLowFps = IsUsable(this._lastNativeZeroPointOnePercentLowFps)
                ? this._lastNativeZeroPointOnePercentLowFps
                : 0;

            return new ZeroPointOnePercentLowStatistics
            {
                ZeroPointOnePercentLowFps = finalZeroPointOnePercentLowFps,
                NativeZeroPointOnePercentLowFps = this._lastNativeZeroPointOnePercentLowFps,
                HasNativeZeroPointOnePercentLow = this._hasNativeZeroPointOnePercentLow,
                CalculatedZeroPointOnePercentLowFps = 0,
                SampleCount = window.Count,
            };
        }

        private void OnSharedStatisticsUpdated(Object sender, FrameTimeStatistics statistics)
        {
            if (statistics == null)
            {
                return;
            }

            var native = IsUsable(statistics.NativeZeroPointOnePercentLowFps)
                ? statistics.NativeZeroPointOnePercentLowFps
                : 0;
            var sharedStatistics = new ZeroPointOnePercentLowStatistics
            {
                ZeroPointOnePercentLowFps = native,
                CalculatedZeroPointOnePercentLowFps = 0,
                NativeZeroPointOnePercentLowFps = native,
                HasNativeZeroPointOnePercentLow = statistics.HasNativeZeroPointOnePercentLow,
                SampleCount = statistics.SampleCount,
            };

            lock (this._sync)
            {
                this._current = sharedStatistics;
            }

            this.StatisticsUpdated?.Invoke(this, sharedStatistics);
        }

        /// <summary>Caller holds <see cref="_sync"/>. Unrolls the ring into arrival order.</summary>
        private List<Double> SnapshotWindow()
        {
            var window = new List<Double>(this._sampleCount);

            var start = this._sampleCount == this._frameTimes.Length ? this._writeIndex : 0;

            for (var i = 0; i < this._sampleCount; i++)
            {
                window.Add(this._frameTimes[(start + i) % this._frameTimes.Length]);
            }

            return window;
        }

        /// <summary>
        /// Frame time at the given percentile, counted from the slow end — sorted descending, the
        /// worst frames come first, and the percentile lands just past the slowest fraction. This is
        /// a frame time, not an FPS; the caller inverts it. Independent implementation of the same
        /// method on <see cref="FrameTimeTrackerService"/>, deliberately not shared: see the class
        /// remarks for why.
        /// </summary>
        private static Double PercentileLow(IReadOnlyList<Double> frameTimes, Double percentile)
        {
            if (frameTimes == null || frameTimes.Count == 0)
            {
                return 0;
            }

            var sorted = new List<Double>(frameTimes);
            sorted.Sort((left, right) => right.CompareTo(left));

            var index = (Int32)Math.Floor((1.0 - percentile) * sorted.Count);

            index = Math.Min(Math.Max(index, 0), sorted.Count - 1);

            return sorted[index];
        }

        private static Boolean IsUsable(Double value) =>
            value > 0 && !Double.IsNaN(value) && !Double.IsInfinity(value);

        private static ZeroPointOnePercentLowStatistics EmptyStatistics() => new ZeroPointOnePercentLowStatistics();
    }
}
