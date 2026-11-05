namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;

    using Loupedeck.RTSSPerformancePlugin.Helpers;

    /// <summary>Coarse health tier derived from measured FPS relative to the target ceiling.</summary>
    public enum FpsTier
    {
        /// <summary>At or near target.</summary>
        Green = 0,

        /// <summary>Noticeably below target but still playable.</summary>
        Yellow = 1,

        /// <summary>Well below target.</summary>
        Red = 2,
    }

    public enum FpsHealthState
    {
        /// <summary>No RTSS data available.</summary>
        Unknown = 0,

        /// <summary>Average FPS and frame pacing are within target.</summary>
        Healthy = 1,

        /// <summary>Sustained shortfall against target FPS, or excessive frame-time variance.</summary>
        Degraded = 2,
    }

    public sealed class FpsTierChangedEventArgs : EventArgs
    {
        /// <summary>False on the very first committed tier, when there is no meaningful previous value.</summary>
        public Boolean HasPreviousTier { get; set; }

        public FpsTier PreviousTier { get; set; }

        public FpsTier CurrentTier { get; set; }

        /// <summary>FPS of the poll that completed the debounce run.</summary>
        public Double Fps { get; set; }

        public Double TargetFps { get; set; }
    }

    public sealed class FpsHealthChangedEventArgs : EventArgs
    {
        public FpsHealthState PreviousState { get; set; }

        public FpsHealthState CurrentState { get; set; }

        public FrameTimeStatistics Statistics { get; set; }
    }

    public interface IFpsHealthStateService
    {
        FpsHealthState State { get; }

        /// <summary>Last committed tier. Meaningless until <see cref="HasTier"/> is true.</summary>
        FpsTier Tier { get; }

        /// <summary>True once a tier has survived debouncing at least once.</summary>
        Boolean HasTier { get; }

        Int32 TargetFps { get; set; }

        /// <summary>Fraction of <see cref="TargetFps"/> at or above which the tier is green (e.g. 0.90).</summary>
        Double DegradedThresholdRatio { get; set; }

        /// <summary>Fraction of <see cref="TargetFps"/> at or above which the tier is yellow rather than red (e.g. 0.70).</summary>
        Double WarningThresholdRatio { get; set; }

        /// <summary>Consecutive same-tier polls required before the tier flips.</summary>
        Int32 HysteresisTicks { get; set; }

        event EventHandler<FpsTierChangedEventArgs> TierChanged;

        event EventHandler<FpsHealthChangedEventArgs> HealthChanged;

        /// <summary>Feeds one poll sample through the debounce filter and returns the currently committed tier.</summary>
        FpsTier Poll(Double fps);

        FpsHealthState Evaluate(FrameTimeStatistics statistics);

        void Reset();
    }

    /// <summary>
    /// Classifies FPS samples into a green/yellow/red tier, debouncing transitions so a single
    /// bad poll cannot flip the key colour.
    /// </summary>
    public sealed class FpsHealthStateService : IFpsHealthStateService
    {
        /// <summary>At or above this fraction of target, the tier is green.</summary>
        public const Double DefaultDegradedThresholdRatio = 0.90;

        /// <summary>At or above this fraction of target (but below green), the tier is yellow.</summary>
        public const Double DefaultWarningThresholdRatio = 0.70;

        public const Int32 DefaultTargetFps = 60;

        public const Int32 DefaultHysteresisTicks = 3;

        private readonly Object _sync = new Object();

        private readonly IFrameTimeTrackerService _frameTimeTracker;

        private Int32 _targetFps = DefaultTargetFps;

        private Double _degradedThresholdRatio = DefaultDegradedThresholdRatio;

        private Double _warningThresholdRatio = DefaultWarningThresholdRatio;

        private Int32 _hysteresisTicks = DefaultHysteresisTicks;

        private FpsTier _tier;

        private Boolean _hasTier;

        private FpsTier _candidateTier;

        private Int32 _candidateCount;

        private FpsHealthState _state = FpsHealthState.Unknown;

        public FpsHealthState State
        {
            get
            {
                lock (this._sync)
                {
                    return this._state;
                }
            }
        }

        public FpsTier Tier
        {
            get
            {
                lock (this._sync)
                {
                    return this._tier;
                }
            }
        }

        public Boolean HasTier
        {
            get
            {
                lock (this._sync)
                {
                    return this._hasTier;
                }
            }
        }

        public Int32 TargetFps
        {
            get
            {
                lock (this._sync)
                {
                    return this._targetFps;
                }
            }

            set
            {
                lock (this._sync)
                {
                    if (this._targetFps == value)
                    {
                        return;
                    }

                    this._targetFps = value;

                    PluginLog.Info($"[HEALTH TARGET] TargetFps={value} (tier boundaries: green>={value * this._degradedThresholdRatio:F1} yellow>={value * this._warningThresholdRatio:F1})");

                    // The target defines the tier boundaries, so any in-flight debounce run is stale.
                    this._candidateCount = 0;
                }
            }
        }

        public Double DegradedThresholdRatio
        {
            get
            {
                lock (this._sync)
                {
                    return this._degradedThresholdRatio;
                }
            }

            set
            {
                lock (this._sync)
                {
                    this._degradedThresholdRatio = value;
                    this._candidateCount = 0;
                }
            }
        }

        public Double WarningThresholdRatio
        {
            get
            {
                lock (this._sync)
                {
                    return this._warningThresholdRatio;
                }
            }

            set
            {
                lock (this._sync)
                {
                    this._warningThresholdRatio = value;
                    this._candidateCount = 0;
                }
            }
        }

        public Int32 HysteresisTicks
        {
            get
            {
                lock (this._sync)
                {
                    return this._hysteresisTicks;
                }
            }

            set
            {
                lock (this._sync)
                {
                    this._hysteresisTicks = Math.Max(1, value);
                }
            }
        }

        public event EventHandler<FpsTierChangedEventArgs> TierChanged;

        public event EventHandler<FpsHealthChangedEventArgs> HealthChanged;

        public FpsHealthStateService()
            : this(null)
        {
        }

        public FpsHealthStateService(IFrameTimeTrackerService frameTimeTracker)
        {
            this._frameTimeTracker = frameTimeTracker;

            if (this._frameTimeTracker != null)
            {
                this._frameTimeTracker.StatisticsUpdated += this.OnStatisticsUpdated;
            }
        }

        /// <summary>
        /// Classifies a single FPS reading against a target: at or above 90% of target is green,
        /// at or above 70% is yellow, anything lower is red. A non-positive or non-finite target
        /// means there is nothing to fall short of, so the result is green.
        /// </summary>
        public static FpsTier GetTier(Double fps, Double targetFps) =>
            GetTier(fps, targetFps, DefaultDegradedThresholdRatio, DefaultWarningThresholdRatio);

        public static FpsTier GetTier(Double fps, Double targetFps, Double greenRatio, Double yellowRatio)
        {
            if (Double.IsNaN(targetFps) || Double.IsInfinity(targetFps) || targetFps <= 0)
            {
                return FpsTier.Green;
            }

            if (Double.IsNaN(fps) || fps < 0)
            {
                return FpsTier.Red;
            }

            if (fps >= targetFps * greenRatio)
            {
                return FpsTier.Green;
            }

            return fps >= targetFps * yellowRatio ? FpsTier.Yellow : FpsTier.Red;
        }

        /// <summary>
        /// Feeds one poll through the debounce filter. A tier must be observed on
        /// <see cref="HysteresisTicks"/> consecutive polls before it is committed and
        /// <see cref="TierChanged"/> fires. Returns the committed tier, which is unchanged
        /// while a new tier is still building up its run.
        /// </summary>
        public FpsTier Poll(Double fps)
        {
            FpsTierChangedEventArgs tierArgs = null;
            FpsHealthChangedEventArgs healthArgs = null;
            FpsTier committed;

            lock (this._sync)
            {
                var observed = GetTier(fps, this._targetFps, this._degradedThresholdRatio, this._warningThresholdRatio);

                // The formula in one line, so a wrong tier can be read off the log rather than
                // recomputed by hand. ratio is what the thresholds are actually compared against.
                var ratio = this._targetFps > 0 ? fps / this._targetFps : Double.NaN;

                PluginLog.InfoThrottled(
                    "tier-formula",
                    $"[TIER FORMULA] fps={fps:F1} target={this._targetFps} ratio={ratio:F3} " +
                    $"green>={this._degradedThresholdRatio:F2} yellow>={this._warningThresholdRatio:F2} " +
                    $"result={observed}");

                PluginLog.InfoThrottled(
                    "tier-input",
                    $"[TIER INPUT] fps={fps:F1} target={this._targetFps} tier={observed}");

                if (this._hasTier && observed == this._tier)
                {
                    // Already settled here; abandon whatever run was building.
                    this._candidateCount = 0;

                    PluginLog.InfoThrottled(
                        "debounce",
                        $"[DEBOUNCE] candidate={observed} previous={this._tier} count=0 " +
                        $"threshold={this._hysteresisTicks} committed=False (already settled)");

                    return this._tier;
                }

                if (this._candidateCount > 0 && observed == this._candidateTier)
                {
                    this._candidateCount++;
                }
                else
                {
                    this._candidateTier = observed;
                    this._candidateCount = 1;
                }

                var willCommit = this._candidateCount >= this._hysteresisTicks;

                // Not throttled: a debounce run is a handful of lines around a real transition,
                // and dropping the middle of one would make the log unreadable for the thing it
                // exists to explain.
                PluginLog.Info(
                    $"[DEBOUNCE] candidate={this._candidateTier} " +
                    $"previous={(this._hasTier ? this._tier.ToString() : "none")} " +
                    $"count={this._candidateCount} threshold={this._hysteresisTicks} committed={willCommit}");

                if (this._candidateCount >= this._hysteresisTicks)
                {
                    var previousTier = this._tier;
                    var hadTier = this._hasTier;

                    this._tier = observed;
                    this._hasTier = true;
                    this._candidateCount = 0;

                    tierArgs = new FpsTierChangedEventArgs
                    {
                        HasPreviousTier = hadTier,
                        PreviousTier = previousTier,
                        CurrentTier = observed,
                        Fps = fps,
                        TargetFps = this._targetFps,
                    };

                    var state = observed == FpsTier.Green ? FpsHealthState.Healthy : FpsHealthState.Degraded;
                    if (state != this._state)
                    {
                        healthArgs = new FpsHealthChangedEventArgs
                        {
                            PreviousState = this._state,
                            CurrentState = state,
                            Statistics = this._frameTimeTracker?.Current,
                        };

                        this._state = state;
                    }
                }

                committed = this._tier;
            }

            // Raised outside the lock so handlers cannot deadlock against a re-entrant Poll.
            if (tierArgs != null)
            {
                this.TierChanged?.Invoke(this, tierArgs);
            }

            if (healthArgs != null)
            {
                this.HealthChanged?.Invoke(this, healthArgs);
            }

            return committed;
        }

        /// <summary>
        /// Evaluates a statistics snapshot. A snapshot with no samples yields
        /// <see cref="FpsHealthState.Unknown"/> and resets the debounce run; otherwise the
        /// live FPS is fed through <see cref="Poll"/>.
        /// </summary>
        public FpsHealthState Evaluate(FrameTimeStatistics statistics)
        {
            if (statistics == null || statistics.SampleCount <= 0)
            {
                this.TransitionToUnknown(statistics);
                return FpsHealthState.Unknown;
            }

            // RTSS reports 0 while an interval is open — dwTime1 == dwTime0, so there is no
            // elapsed time to divide by. That is the absence of a reading, not a reading of zero,
            // and the poll loop runs at 4Hz against RTSS's ~1Hz interval close, so several polls
            // in a row can land inside one open interval. Fed to the debounce those look like a
            // sustained 0 fps and commit Red on an application that is running perfectly.
            //
            // Skipping the poll leaves the debounce run untouched rather than substituting a
            // value: no average, no 1% low, no last-known reading.
            if (statistics.InstantaneousFps <= 0)
            {
                return this.State;
            }

            // The live rate, not the window average. The tier grades what the application is doing
            // now; a rolling mean lets one catastrophic frame time hold the key red long after the
            // application recovered, and on-device it reported 5.8 fps against a real 53.
            PluginLog.InfoThrottled(
                "health-fps-source",
                $"[HEALTH FPS SOURCE] selectedValue={statistics.InstantaneousFps:F1} source=Instantaneous " +
                $"(windowAverage={statistics.AverageFps:F1} onePercentLow={statistics.OnePercentLowFps:F1} " +
                $"n={statistics.SampleCount})");

            this.Poll(statistics.InstantaneousFps);
            return this.State;
        }
        public void Reset()
        {
            FpsHealthChangedEventArgs healthArgs = null;

            lock (this._sync)
            {
                this._tier = FpsTier.Green;
                this._hasTier = false;
                this._candidateCount = 0;

                if (this._state != FpsHealthState.Unknown)
                {
                    healthArgs = new FpsHealthChangedEventArgs
                    {
                        PreviousState = this._state,
                        CurrentState = FpsHealthState.Unknown,
                        Statistics = null,
                    };

                    this._state = FpsHealthState.Unknown;
                }
            }

            if (healthArgs != null)
            {
                this.HealthChanged?.Invoke(this, healthArgs);
            }
        }

        private void TransitionToUnknown(FrameTimeStatistics statistics)
        {
            FpsHealthChangedEventArgs healthArgs = null;

            lock (this._sync)
            {
                this._candidateCount = 0;

                if (this._state != FpsHealthState.Unknown)
                {
                    healthArgs = new FpsHealthChangedEventArgs
                    {
                        PreviousState = this._state,
                        CurrentState = FpsHealthState.Unknown,
                        Statistics = statistics,
                    };

                    this._state = FpsHealthState.Unknown;
                }
            }

            if (healthArgs != null)
            {
                this.HealthChanged?.Invoke(this, healthArgs);
            }
        }

        private void OnStatisticsUpdated(Object sender, FrameTimeStatistics statistics) => this.Evaluate(statistics);
    }
}
