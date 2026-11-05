namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Aggregate frame-pacing statistics over the tracker's rolling window.
    /// </summary>
    public sealed class FrameTimeStatistics
    {
        /// <summary>
        /// The live framerate RTSS reported for the most recent sample, untouched by the window.
        ///
        /// Carried alongside the rolling statistics because the display and the colour tier are
        /// defined against the current rate, not a smoothed one: a single catastrophic frame time
        /// — an alt-tabbed application recorded a 1,095,291ms "frame" on-device — drags
        /// <see cref="AverageFps"/> to a few fps while the application is in fact running at 60.
        /// </summary>
        public Double InstantaneousFps { get; set; }

        public Double AverageFps { get; set; }

        public Double MinimumFps { get; set; }

        public Double MaximumFps { get; set; }

        public Double OnePercentLowFps { get; set; }

        public Double CalculatedOnePercentLowFps { get; set; }

        public Double NativeOnePercentLowFps { get; set; }

        public Boolean HasNativeOnePercentLow { get; set; }

        public Double NativeZeroPointOnePercentLowFps { get; set; }

        public Boolean HasNativeZeroPointOnePercentLow { get; set; }

        public Double AverageFrameTimeMs { get; set; }

        /// <summary>Standard deviation of frame time; a proxy for stutter.</summary>
        public Double FrameTimeVarianceMs { get; set; }

        public Int32 SampleCount { get; set; }

        public UInt32 ProcessId { get; set; }

        public UInt32 FrameSampleTimestamp { get; set; }
    }

    public interface IFrameTimeTrackerService
    {
        Int32 WindowSize { get; }

        Int32 SampleCount { get; }

        FrameTimeStatistics Current { get; }

        event EventHandler<FrameTimeStatistics> StatisticsUpdated;

        void AddSample(RTSSFrameSnapshot snapshot);

        void AddFrameTimeSample(Double frameTimeMilliseconds);

        FrameTimeStatistics ComputeStatistics();

        IReadOnlyList<Double> GetRecentFrameTimes();

        void Reset();
    }

    /// <summary>
    /// Maintains a rolling window of frame times and derives smoothed FPS / stutter metrics.
    /// </summary>
    public sealed class FrameTimeTrackerService : IFrameTimeTrackerService
    {
        public const Int32 DefaultWindowSize = 120;

        /// <summary>Fraction of frames treated as "fast" when computing the 1% low.</summary>
        public const Double OnePercentLowPercentile = 0.99;

        private readonly Object _sync = new Object();

        private readonly Double[] _frameTimes;

        /// <summary>Last live rate RTSS reported, carried through to the statistics unchanged.</summary>
        private Double _lastInstantaneousFps;

        private Double _lastNativeOnePercentLowFps;

        private Double _lastNativeZeroPointOnePercentLowFps;

        private Boolean _hasNativeOnePercentLow;

        private Boolean _hasNativeZeroPointOnePercentLow;

        /// <summary>
        /// Process id the window is currently accumulating samples for; 0 (RTSS's own "free slot"
        /// value, never a real pid) means no source has fed a sample since the last reset.
        /// </summary>
        private UInt32 _lastProcessId;

        private UInt32 _lastFrameSampleTimestamp;

        /// <summary>Index the next sample is written to; the buffer wraps here.</summary>
        private Int32 _writeIndex;

        private Int32 _sampleCount;

        private FrameTimeStatistics _current = EmptyStatistics();

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

        public FrameTimeStatistics Current
        {
            get
            {
                lock (this._sync)
                {
                    return this._current;
                }
            }
        }

        public event EventHandler<FrameTimeStatistics> StatisticsUpdated;

        public FrameTimeTrackerService(Int32 windowSize = DefaultWindowSize) =>
            this._frameTimes = new Double[Math.Max(1, windowSize)];

        /// <summary>
        /// Feeds one poll into the window. RTSS reports a frame time directly; when it has not
        /// produced one yet the instantaneous framerate is inverted instead, so a fresh hook still
        /// contributes rather than being dropped.
        ///
        /// A change of process id — the active source switching from one application to another
        /// while RTSS itself stays up — clears the window first, so the new application's very
        /// first sample starts a clean window rather than being blended against the previous
        /// application's frame times. <see cref="RTSSFrameSnapshot.ProcessId"/> is the only signal
        /// available for this: a percentile computed across two different applications' frame
        /// times is not a measurement of either one.
        /// </summary>
        public void AddSample(RTSSFrameSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            // Store native percentile metadata BEFORE the frame-time guard so that Current always
            // reflects the latest native RTSS 1% Low value regardless of whether the ring buffer
            // has any frame-time samples yet.  An open RTSS sampling interval produces
            // InstantaneousFps == 0 and FrameTimeMilliseconds == 0, but RTSS still writes
            // dwStatFramerate1Dot0PercentLow continuously.  Without this early store AddSample used
            // to return before the lock block, leaving _hasNativeOnePercentLow == false for the
            // whole poll cycle and making the key show only the icon even with a valid native value.
            var nativeDataChanged = false;
            lock (this._sync)
            {
                if (this._lastProcessId != 0 && snapshot.ProcessId != 0 &&
                    snapshot.ProcessId != this._lastProcessId)
                {
                    this.ResetCore();
                }

                this._lastProcessId = snapshot.ProcessId;
                this._lastInstantaneousFps = snapshot.InstantaneousFps;

                var prevHasNative = this._hasNativeOnePercentLow;
                var prevNative    = this._lastNativeOnePercentLowFps;

                this._lastNativeOnePercentLowFps          = snapshot.NativeOnePercentLowFps;
                this._lastNativeZeroPointOnePercentLowFps = snapshot.NativeZeroPointOnePercentLowFps;
                this._hasNativeOnePercentLow              = snapshot.HasNativeOnePercentLow;
                this._hasNativeZeroPointOnePercentLow     = snapshot.HasNativeZeroPointOnePercentLow;
                this._lastFrameSampleTimestamp            = snapshot.FrameSampleTimestamp;

                // Detect meaningful change to native data so we can fire StatisticsUpdated even
                // when no frame-time sample is added (open interval / _sampleCount still 0).
                nativeDataChanged = (prevHasNative != this._hasNativeOnePercentLow)
                    || (Math.Abs(prevNative - this._lastNativeOnePercentLowFps) > 0.001);
            }

            var frameTime = snapshot.FrameTimeMilliseconds;

            if (!IsUsable(frameTime) && IsUsable(snapshot.InstantaneousFps))
            {
                frameTime = 1000.0 / snapshot.InstantaneousFps;
            }

            if (!IsUsable(frameTime))
            {
                // Frame time is unavailable for this tick (open RTSS interval).  The ring buffer
                // cannot be updated, but native percentile data may have arrived: publish a
                // statistics object so the key shows the number without waiting for the buffer to
                // accumulate samples.
                if (nativeDataChanged)
                {
                    FrameTimeStatistics partial;
                    lock (this._sync)
                    {
                        partial = this.ComputeStatisticsCore();
                        this._current = partial;
                    }

                    this.StatisticsUpdated?.Invoke(this, partial);
                }

                return;
            }

            this.AddFrameTimeSample(frameTime);
        }

        /// <summary>
        /// A zero, negative, NaN or infinite frame time is a gap in RTSS's data, not a measurement:
        /// admitting one would blow up every derived FPS figure.
        ///
        /// Note that statistics produced through this entry point carry InstantaneousFps = 0,
        /// because a bare frame time does not say what RTSS reported as the live rate. Consumers
        /// that grade the live rate — the display and the tier — treat 0 as no reading and skip
        /// it. Feed <see cref="AddSample"/> a snapshot when the live rate matters.
        ///
        /// Every usable frame time is admitted, including one identical to the last, and this is
        /// deliberate: a version of this method once discarded a repeat of the immediately
        /// preceding value on the theory that RTSS had simply not advanced to a new frame between
        /// two polls. That is true for a stable framerate, where it barely matters either way, but
        /// false for a sustained, quantised stutter — a locked hitch genuinely produces several
        /// real, distinct frames at the exact same duration. Discarding those repeats collapsed a
        /// severe multi-frame stall down to a single window entry while leaving a milder, merely
        /// non-adjacent stutter uncollapsed, which shifted the percentile index onto the wrong
        /// entry: measured on-device, a session with an expected ~3 fps 1% low (a real three-frame
        /// stall at 333ms) reported 7 fps instead, because the dedup logic had reduced three real
        /// 333ms frames to one. There is no frame-time-value comparison that can tell "the same
        /// unrendered frame re-polled" apart from "several different frames sharing a duration"
        /// without also knowing whether the underlying frame actually advanced, which is not
        /// something this method is given — so it does not try, and takes every reading as real.
        /// </summary>
        public void AddFrameTimeSample(Double frameTimeMilliseconds)
        {
            if (!IsUsable(frameTimeMilliseconds))
            {
                return;
            }

            FrameTimeStatistics statistics;

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

        public FrameTimeStatistics ComputeStatistics()
        {
            lock (this._sync)
            {
                return this.ComputeStatisticsCore();
            }
        }

        /// <summary>Window contents in arrival order, oldest first.</summary>
        public IReadOnlyList<Double> GetRecentFrameTimes()
        {
            lock (this._sync)
            {
                return this.SnapshotWindow();
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

                var window = this.SnapshotWindow();
                var minimum = Double.MaxValue;
                var maximum = Double.MinValue;

                foreach (var frameTime in window)
                {
                    minimum = Math.Min(minimum, frameTime);
                    maximum = Math.Max(maximum, frameTime);
                }

                return $"oldest={window[0]:F2}ms newest={window[window.Count - 1]:F2}ms "
                    + $"min={minimum:F2}ms max={maximum:F2}ms";
            }
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

            // Cleared with the window: a live rate that outlived its samples is exactly the
            // stale reading the display must never show.
            this._lastInstantaneousFps = 0;
            this._lastNativeOnePercentLowFps = 0;
            this._lastNativeZeroPointOnePercentLowFps = 0;
            this._hasNativeOnePercentLow = false;
            this._hasNativeZeroPointOnePercentLow = false;

            // Forgotten along with the samples: a later sample from this same pid, after a real
            // gap, must start a clean window rather than being compared against a "last pid" that
            // predates the gap.
            this._lastProcessId = 0;
            this._lastFrameSampleTimestamp = 0;

            this._current = EmptyStatistics();
        }

        /// <summary>Caller holds <see cref="_sync"/>.</summary>
        private FrameTimeStatistics ComputeStatisticsCore()
        {
            if (this._sampleCount == 0)
            {
                // The ring buffer has no frame-time samples yet (open RTSS interval, first poll,
                // or just after a Reset), but RTSS may already have a valid native 1% Low value.
                // Return a partial statistics object that surfaces the native reading so the key
                // can display the number immediately without waiting for the ring buffer to fill.
                // All derived fields (average, variance, calculated percentile) remain 0/empty
                // because there are no frame-time samples to compute them from.
                var nativeOnePercent   = IsUsable(this._lastNativeOnePercentLowFps) ? this._lastNativeOnePercentLowFps : 0;
                var nativeZeroOnePercent = IsUsable(this._lastNativeZeroPointOnePercentLowFps) ? this._lastNativeZeroPointOnePercentLowFps : 0;
                var hasNative   = this._hasNativeOnePercentLow && nativeOnePercent > 0;
                var hasZeroNative = this._hasNativeZeroPointOnePercentLow && nativeZeroOnePercent > 0;

                if (!hasNative && !hasZeroNative)
                {
                    return EmptyStatistics();
                }

                return new FrameTimeStatistics
                {
                    InstantaneousFps           = this._lastInstantaneousFps,
                    ProcessId                  = this._lastProcessId,
                    FrameSampleTimestamp       = this._lastFrameSampleTimestamp,
                    OnePercentLowFps           = nativeOnePercent,
                    NativeOnePercentLowFps     = nativeOnePercent,
                    HasNativeOnePercentLow     = hasNative,
                    NativeZeroPointOnePercentLowFps = nativeZeroOnePercent,
                    HasNativeZeroPointOnePercentLow = hasZeroNative,
                    SampleCount                = 0,
                    // Derived fields left at their defaults (0) — no frame times yet.
                };
            }

            var window = this.SnapshotWindow();

            var total = 0.0;
            var slowest = Double.MinValue;
            var fastest = Double.MaxValue;

            foreach (var frameTime in window)
            {
                total += frameTime;
                slowest = Math.Max(slowest, frameTime);
                fastest = Math.Min(fastest, frameTime);
            }

            var mean = total / window.Count;

            var sumOfSquares = 0.0;

            foreach (var frameTime in window)
            {
                var deviation = frameTime - mean;
                sumOfSquares += deviation * deviation;
            }

            var nativeOnePercentLowFps = IsUsable(this._lastNativeOnePercentLowFps)
                ? this._lastNativeOnePercentLowFps
                : 0;

            var calculatedOnePercentLow = window.Count >= 8
                ? ToFps(PercentileLow(window, OnePercentLowPercentile))
                : 0;

            return new FrameTimeStatistics
            {
                // Derived from the mean frame time rather than a mean of per-sample FPS values:
                // averaging rates over-weights the fast frames and flatters a stuttering window.
                InstantaneousFps = this._lastInstantaneousFps,
                ProcessId = this._lastProcessId,
                FrameSampleTimestamp = this._lastFrameSampleTimestamp,
                AverageFps = ToFps(mean),
                MinimumFps = ToFps(slowest),
                MaximumFps = ToFps(fastest),
                OnePercentLowFps = nativeOnePercentLowFps,
                CalculatedOnePercentLowFps = calculatedOnePercentLow,
                NativeOnePercentLowFps = nativeOnePercentLowFps,
                HasNativeOnePercentLow = this._hasNativeOnePercentLow && nativeOnePercentLowFps > 0,
                NativeZeroPointOnePercentLowFps = this._lastNativeZeroPointOnePercentLowFps,
                HasNativeZeroPointOnePercentLow = this._hasNativeZeroPointOnePercentLow && this._lastNativeZeroPointOnePercentLowFps > 0,
                AverageFrameTimeMs = mean,
                FrameTimeVarianceMs = Math.Sqrt(sumOfSquares / window.Count),
                SampleCount = window.Count,
            };
        }

        /// <summary>Caller holds <see cref="_sync"/>. Unrolls the ring into arrival order.</summary>
        private List<Double> SnapshotWindow()
        {
            var window = new List<Double>(this._sampleCount);

            // Once the buffer has wrapped, the oldest sample sits at the write cursor.
            var start = this._sampleCount == this._frameTimes.Length ? this._writeIndex : 0;

            for (var i = 0; i < this._sampleCount; i++)
            {
                window.Add(this._frameTimes[(start + i) % this._frameTimes.Length]);
            }

            return window;
        }

        /// <summary>
        /// Frame time at the given percentile, counted from the slow end: sorted descending, the
        /// worst frames come first, and the 0.99 percentile lands just past the slowest 1%. This
        /// is a frame time, not an FPS — the caller inverts it.
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

            // With fewer than 1/(1-percentile) samples the index collapses to 0, i.e. the single
            // slowest frame, which is the honest answer for a short window.
            index = Math.Min(Math.Max(index, 0), sorted.Count - 1);

            return sorted[index];
        }

        private static Double ToFps(Double frameTimeMilliseconds) =>
            IsUsable(frameTimeMilliseconds) ? 1000.0 / frameTimeMilliseconds : 0;

        private static Boolean IsUsable(Double value) =>
            value > 0 && !Double.IsNaN(value) && !Double.IsInfinity(value);

        private static FrameTimeStatistics EmptyStatistics() => new FrameTimeStatistics();
    }
}
