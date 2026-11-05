namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;

    public interface ISustainedLoadStateService
    {
        /// <summary>Load at or above which a consecutive-poll run toward the red state begins.</summary>
        Double HighThreshold { get; set; }

        /// <summary>
        /// Load below which the red state clears immediately. Lower than <see cref="HighThreshold"/>
        /// by design — see the class remarks for why the gap between the two matters.
        /// </summary>
        Double RecoveryThreshold { get; set; }

        /// <summary>Consecutive polls at or above <see cref="HighThreshold"/> required to commit red.</summary>
        Int32 SustainedTicks { get; set; }

        /// <summary>True once the sustained-high condition has committed and has not yet recovered.</summary>
        Boolean IsSustainedHigh { get; }

        /// <summary>Feeds one poll sample through the debounce/hysteresis filter.</summary>
        Boolean Poll(Double value);

        void Reset();
    }

    /// <summary>
    /// Tracks whether a single load value (CPU or GPU utilization) has been under sustained heavy
    /// load, independent of any other component. Two separate instances — one per component — is
    /// how the CPU and GPU status commands stay unable to influence each other; this class holds
    /// no notion of "the other component" at all.
    ///
    /// Two distinct mechanisms, both required by the task this exists for:
    ///
    /// 1. A debounce on the way up: <see cref="HighThreshold"/> must be met on
    ///    <see cref="SustainedTicks"/> *consecutive* polls before the state commits to red. A
    ///    single spike above the threshold — one frame of a compositor flush, one GPU driver
    ///    hiccup — resets the run rather than committing, exactly like
    ///    <see cref="FpsHealthStateService"/>'s tier debounce, but with only two states (normal /
    ///    sustained-high) rather than three, because there is no target to grade load against —
    ///    only "is this persistently pegged".
    ///
    /// 2. A hysteresis gap on the way down: recovery uses <see cref="RecoveryThreshold"/>, set
    ///    lower than <see cref="HighThreshold"/> by default (75% vs 90%), not the same value. A
    ///    single shared threshold would flicker red/normal every poll while load hovers exactly at
    ///    the boundary (a game alternating 89%/91% GPU load every 250ms is a completely ordinary
    ///    reading, not two hundred state changes a minute). The gap between the two thresholds is
    ///    the flicker margin.
    ///
    /// Per the task's requirement, recovery itself is immediate and undebounced: the moment a poll
    /// reports a value below <see cref="RecoveryThreshold"/>, the red state clears on that same
    /// poll. Only the climb into red is debounced.
    /// </summary>
    public sealed class SustainedLoadStateService : ISustainedLoadStateService
    {
        public const Double DefaultHighThreshold = 90.0;

        public const Double DefaultRecoveryThreshold = 75.0;

        /// <summary>
        /// 6 consecutive polls. At the plugin's 250ms active poll interval that is 1.5 seconds of
        /// sustained load before the key turns red — long enough that a single spike (one poll,
        /// 250ms) or a brief flurry of two or three cannot trigger it, short enough that a real,
        /// ongoing bottleneck is reflected promptly rather than after a suspiciously long delay.
        /// </summary>
        public const Int32 DefaultSustainedTicks = 6;

        private readonly Object _sync = new Object();

        private Double _highThreshold = DefaultHighThreshold;

        private Double _recoveryThreshold = DefaultRecoveryThreshold;

        private Int32 _sustainedTicks = DefaultSustainedTicks;

        private Int32 _aboveCount;

        private Boolean _isSustainedHigh;

        public Double HighThreshold
        {
            get
            {
                lock (this._sync)
                {
                    return this._highThreshold;
                }
            }

            set
            {
                lock (this._sync)
                {
                    this._highThreshold = value;
                    this._aboveCount = 0;
                }
            }
        }

        public Double RecoveryThreshold
        {
            get
            {
                lock (this._sync)
                {
                    return this._recoveryThreshold;
                }
            }

            set
            {
                lock (this._sync)
                {
                    this._recoveryThreshold = value;
                }
            }
        }

        public Int32 SustainedTicks
        {
            get
            {
                lock (this._sync)
                {
                    return this._sustainedTicks;
                }
            }

            set
            {
                lock (this._sync)
                {
                    this._sustainedTicks = Math.Max(1, value);
                    this._aboveCount = 0;
                }
            }
        }

        public Boolean IsSustainedHigh
        {
            get
            {
                lock (this._sync)
                {
                    return this._isSustainedHigh;
                }
            }
        }

        /// <summary>
        /// Feeds one poll sample through the filter and returns the resulting
        /// <see cref="IsSustainedHigh"/>. A non-finite value (NaN, +/-infinity) is treated as "not
        /// above" rather than fabricating a state from garbage input — it cannot itself trigger
        /// red, and while already red it does not count as a recovery reading either, since it is
        /// not a real measurement in either direction; the run simply does not advance either way
        /// on that poll.
        /// </summary>
        public Boolean Poll(Double value)
        {
            lock (this._sync)
            {
                var isFinite = !Double.IsNaN(value) && !Double.IsInfinity(value);

                if (this._isSustainedHigh)
                {
                    if (isFinite && value < this._recoveryThreshold)
                    {
                        this._isSustainedHigh = false;
                        this._aboveCount = 0;
                    }

                    return this._isSustainedHigh;
                }

                if (!isFinite)
                {
                    // Neither advances nor resets the run: see the remarks above.
                    return this._isSustainedHigh;
                }

                if (value >= this._highThreshold)
                {
                    this._aboveCount++;

                    if (this._aboveCount >= this._sustainedTicks)
                    {
                        this._isSustainedHigh = true;
                        this._aboveCount = 0;
                    }
                }
                else
                {
                    this._aboveCount = 0;
                }

                return this._isSustainedHigh;
            }
        }

        public void Reset()
        {
            lock (this._sync)
            {
                this._isSustainedHigh = false;
                this._aboveCount = 0;
            }
        }
    }
}
