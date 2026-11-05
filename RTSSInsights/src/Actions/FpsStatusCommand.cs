namespace Loupedeck.RTSSPerformancePlugin.Actions
{
    using System;
    using System.Globalization;

    using Loupedeck.RTSSPerformancePlugin.Helpers;
    using Loupedeck.RTSSPerformancePlugin.Services;

    /// <summary>
    /// The FPS key: the live RTSS framerate on a physical key.
    ///
    /// This is a <see cref="ThrottledDynamicCommand"/> rather than a folder for one measured reason.
    /// A folder's cover is fetched once per page entry and is not refreshable — every invalidation
    /// route the SDK offers was tried against it and all five were no-ops, while eighteen
    /// invalidations across 24.6 seconds of live FPS produced zero re-requests. A dynamic command
    /// answers <see cref="PluginDynamicAction.ActionImageChanged"/> within ~15ms, every time: 144
    /// image requests against 136 notifications, confirmed on the Creative Console glass. Logitech
    /// build their own live keys — the clock and the stopwatch — the same way.
    ///
    /// The plugin publishes only this "FPS" entry. It is a display-only key.
    /// </summary>
    public class FpsStatusCommand : ThrottledDynamicCommand
    {
        private readonly Object _sync = new Object();

        /// <summary>
        /// How long a reading stays believable after the last sample that produced it.
        ///
        /// Samples arrive every 250ms while something is presenting, so two seconds is eight
        /// missed polls — far beyond any normal gap, and short enough that a key does not sit
        /// lying about the framerate after a game closes.
        /// </summary>
        private const Int64 StaleReadingMs = 2000;

        private IFpsHealthStateService _healthState;
        private IFrameTimeTrackerService _frameTimeTracker;
        private ILimitingFactorService _limitingFactor;
        private IRTSSAvailabilityMonitorService _availabilityMonitor;
        private IPollTickSchedulerService _pollScheduler;
        private ITelemetryCoordinatorService _coordinator;

        /// <summary>Sentinel for "no sample has ever arrived".</summary>
        private const Int64 NoSampleYet = Int64.MinValue;

        /// <summary>When the last usable reading arrived. See <see cref="IsReadingFresh"/>.</summary>
        private Int64 _lastSampleAtMs = NoSampleYet;

        private FpsTier _tier;
        private Boolean _hasTier;
        private Double _fps;

        /// <summary>Process the current reading was measured on; 0 when unknown.</summary>
        private UInt32 _sourceProcessId;

        /// <summary>Visible state as of the last notification, so the host is told only on a change.</summary>
        private Boolean _wasShowingNumber;

        private Int32 _renderVersion;

        public FpsStatusCommand()
            : base("Live FPS", "Live RTSS framerate on the main control center.", null)
        {
            this.GroupName = "2. Performance";
        }

        protected override Boolean OnLoad()
        {
            this.EnsureServicesBound();

            // Binds again the moment services are up, in case the host called this before
            // RTSSPerformancePlugin.Load — see ActionReadiness's remarks.
            ActionReadiness.SubscribeAndCatchUp(this.OnServicesReady);

            PluginLog.Info(
                $"[FPS KEY] Registered as a dynamic command; " +
                $"services {(this._frameTimeTracker == null ? "pending" : "bound")}.");

            return true;
        }

        protected override Boolean OnUnload()
        {
            ActionReadiness.Unsubscribe(this.OnServicesReady);

            if (this._coordinator != null)
            {
                this._coordinator.TelemetryUpdated -= this.OnTelemetryUpdated;
                this._coordinator = null;
            }

            if (this._frameTimeTracker != null)
            {
                this._frameTimeTracker.StatisticsUpdated -= this.OnStatisticsUpdated;
                this._frameTimeTracker = null;
            }

            if (this._healthState != null)
            {
                this._healthState.TierChanged -= this.OnTierChanged;
                this._healthState.HealthChanged -= this.OnHealthChanged;
                this._healthState = null;
            }

            if (this._availabilityMonitor != null)
            {
                this._availabilityMonitor.StatusChanged -= this.OnAvailabilityChanged;
                this._availabilityMonitor = null;
            }

            if (this._pollScheduler != null)
            {
                this._pollScheduler.Tick -= this.OnPollTick;
                this._pollScheduler = null;
            }

            this._limitingFactor = null;

            return true;
        }

        /// <summary>
        /// Binds whatever of the service graph exists now, and can be called again for the rest.
        ///
        /// The runtime may construct actions before <see cref="RTSSPerformancePlugin.Load"/> has
        /// built its services, so every read path calls this and a late graph is picked up on the
        /// next render or poll rather than leaving the key permanently dead.
        /// </summary>
        protected override void EnsureServicesBound()
        {
            var plugin = RTSSPerformancePlugin.Instance;

            if (plugin == null)
            {
                return;
            }

            lock (this._sync)
            {
                if (this._coordinator == null && plugin.TelemetryCoordinator != null)
                {
                    this._coordinator = plugin.TelemetryCoordinator;
                    this._coordinator.TelemetryUpdated += this.OnTelemetryUpdated;
                }

                if (this._frameTimeTracker == null && plugin.FrameTimeTracker != null)
                {
                    this._frameTimeTracker = plugin.FrameTimeTracker;
                    this._frameTimeTracker.StatisticsUpdated += this.OnStatisticsUpdated;
                }

                if (this._healthState == null && plugin.HealthState != null)
                {
                    this._healthState = plugin.HealthState;
                    this._healthState.TierChanged += this.OnTierChanged;
                    this._healthState.HealthChanged += this.OnHealthChanged;
                }

                if (this._availabilityMonitor == null && plugin.AvailabilityMonitor != null)
                {
                    this._availabilityMonitor = plugin.AvailabilityMonitor;
                    this._availabilityMonitor.StatusChanged += this.OnAvailabilityChanged;
                }

                if (this._pollScheduler == null && plugin.PollScheduler != null)
                {
                    // A heartbeat, and the only way the key can notice its reading went stale.
                    // When nothing is presenting the plugin's poll tick returns early — no
                    // statistics, no health change, no event of any kind — so a purely
                    // event-driven staleness check could never fire. The scheduler ticks
                    // regardless, and NotifyIfChanged sends nothing unless the picture differs.
                    this._pollScheduler = plugin.PollScheduler;
                    this._pollScheduler.Tick += this.OnPollTick;
                }

                this._limitingFactor = this._limitingFactor ?? plugin.LimitingFactor;
            }
        }

        private void OnTelemetryUpdated(Object sender, TelemetrySnapshot snapshot)
        {
            var rtss = snapshot?.Rtss;

            if (rtss == null)
            {
                // No RTSS entry is valid and fresh right now: show the unavailable state at once.
                this.DiscardReading();
                this.NotifyIfChanged();
                return;
            }

            this.DiscardReadingIfSourceChanged(rtss.ProcessId);

            if (rtss.InstantaneousFps <= 0)
            {
                return;
            }

            lock (this._sync)
            {
                this._fps = rtss.InstantaneousFps;
                this._sourceProcessId = rtss.ProcessId;
            }

            System.Threading.Volatile.Write(ref this._lastSampleAtMs, Environment.TickCount64);
            this.NotifyIfChanged();
        }

        /// <summary>Drops the reading so the key shows the unavailable state until a new RTSS sample arrives.</summary>
        private void DiscardReading()
        {
            lock (this._sync)
            {
                this._fps = 0;
                this._hasTier = false;
                this._sourceProcessId = 0;
            }

            System.Threading.Volatile.Write(ref this._lastSampleAtMs, NoSampleYet);
        }

        /// <summary>A reading measured on one process must never be shown for another.</summary>
        private void DiscardReadingIfSourceChanged(UInt32 processId)
        {
            Boolean changed;

            lock (this._sync)
            {
                changed = processId != 0 && this._sourceProcessId != 0 && processId != this._sourceProcessId;
            }

            if (changed)
            {
                this.DiscardReading();
            }
        }

        private void OnPollTick(Object sender, PollTickEventArgs e) => this.NotifyIfChanged();

        private void OnServicesReady(Object sender, EventArgs e) => this.NotifyIfChanged();

        /// <summary>
        /// True while the last reading is recent enough to still describe reality.
        ///
        /// RTSS being up is not the same as an application presenting: the process keeps running
        /// after a game closes, so availability alone left the key showing the last framerate
        /// indefinitely — measured at 60 FPS still on the glass a minute after Rocket League
        /// stopped.
        /// </summary>
        private Boolean IsReadingFresh
        {
            get
            {
                var lastSampleAtMs = System.Threading.Volatile.Read(ref this._lastSampleAtMs);

                // The sentinel is tested rather than subtracted. Int64.MinValue in the arithmetic
                // overflows to a large negative gap, which reads as "fresh" — the exact opposite
                // of the truth, and only before the first sample has ever arrived.
                return lastSampleAtMs != NoSampleYet &&
                    Environment.TickCount64 - lastSampleAtMs <= StaleReadingMs;
            }
        }

        private Boolean IsRtssAvailable => this._availabilityMonitor?.IsAvailable ?? false;

        private void OnStatisticsUpdated(Object sender, FrameTimeStatistics statistics)
        {
            // RTSS reports 0 while an interval is still open, which is the absence of a reading
            // rather than a reading of zero. Skipping leaves the key showing its last real value
            // for the ~250ms until the next sample instead of flashing 0 on a game at 60.
            if (statistics == null)
            {
                return;
            }

            this.DiscardReadingIfSourceChanged(statistics.ProcessId);

            if (statistics.InstantaneousFps <= 0)
            {
                return;
            }

            lock (this._sync)
            {
                // The live rate, not the window average: the key shows what the game is doing now.
                this._fps = statistics.InstantaneousFps;
                this._sourceProcessId = statistics.ProcessId;
            }

            System.Threading.Volatile.Write(ref this._lastSampleAtMs, Environment.TickCount64);

            this.NotifyIfChanged();
        }

        private void OnTierChanged(Object sender, FpsTierChangedEventArgs e)
        {
            lock (this._sync)
            {
                this._tier = e.CurrentTier;
                this._hasTier = true;
                this._fps = e.Fps;
            }

            PluginLog.Info(
                $"[TIER CHANGED] previous={(e.HasPreviousTier ? e.PreviousTier.ToString() : "none")} " +
                $"current={e.CurrentTier} fps={e.Fps:F1} target={e.TargetFps:F0}");

            this.NotifyIfChanged();
        }

        private void OnHealthChanged(Object sender, EventArgs e)
        {
            if (e is FpsHealthChangedEventArgs args && args.CurrentState == FpsHealthState.Unknown)
            {
                lock (this._sync)
                {
                    // The tier is no longer known, so the colour goes neutral — but the reading is
                    // not wiped. Zeroing it here discarded a perfectly good framerate the moment
                    // health went Unknown, and the key dropped to the wordmark while RTSS was
                    // still reporting. Expiry is the freshness guard's job, and it does it on a
                    // clock rather than on a health transition.
                    this._hasTier = false;
                }
            }

            this.NotifyIfChanged();
        }

        /// <summary>
        /// RTSS going away clears the reading outright, rather than leaving it to expire.
        ///
        /// Belt and braces alongside the freshness clock, which is the primary guard. This closes
        /// the narrower window where RTSS restarts inside StaleReadingMs and a value measured
        /// against the old session would be drawn as if it belonged to the new one.
        /// </summary>
        private void OnAvailabilityChanged(Object sender, RTSSAvailability status)
        {
            if (status != RTSSAvailability.Available)
            {
                lock (this._sync)
                {
                    this._fps = 0;
                    this._hasTier = false;
                    this._sourceProcessId = 0;
                }

                System.Threading.Volatile.Write(ref this._lastSampleAtMs, NoSampleYet);

                PluginLog.Info(
                    $"[FPS KEY] RTSS is {status}; the reading is discarded and the key returns "
                    + "to the wordmark until RTSS reports again.");
            }

            this.NotifyIfChanged();
        }

        /// <summary>
        /// Tells the host the image is stale, but only when the picture would actually differ.
        ///
        /// Samples arrive four times a second and most draw the same three glyphs — 30.1 and 30.0
        /// are both "30". Notifying regardless floods the host, which then coalesces on its own
        /// schedule and delays the updates that matter; measured at 480 invalidations for five
        /// renders before this was added.
        /// </summary>
        private void NotifyIfChanged() => this.CoreNotifyIfChanged();

        /// <summary>
        /// Records the moment the key starts or stops showing a number, and why.
        ///
        /// The reader logs which RTSS entry was selected; this logs what the glass actually shows,
        /// and the two together are what a "the key is stuck on 4" report needs. A value frozen on
        /// the key with no [FPS DISPLAY] line behind it is a display-layer fault; one that follows
        /// a stale reading is a source-layer fault. Transitions only — this runs at 4Hz.
        /// </summary>
        private void LogDisplayStateChange()
        {
            Double fps;

            lock (this._sync)
            {
                fps = this._fps;
            }

            var rtssUp = this.IsRtssAvailable;
            var fresh = this.IsReadingFresh;
            var showsNumber = rtssUp && fresh && Math.Round(fps) >= 1;

            if (this._wasShowingNumber == showsNumber)
            {
                return;
            }

            this._wasShowingNumber = showsNumber;

            if (showsNumber)
            {
                PluginLog.Info(
                    $"[FPS DISPLAY] Showing {Math.Round(fps).ToString("0", CultureInfo.InvariantCulture)} FPS "
                    + "from a fresh RTSS reading.");

                return;
            }

            // Exactly one of these is the cause, and naming it is the point of the line.
            var cause = !rtssUp
                ? "RTSS is not readable"
                : !fresh
                    ? $"the last reading is older than {StaleReadingMs}ms and its source has stopped"
                    : $"the reading is below one whole frame ({fps:F2} fps)";

            PluginLog.Info(
                $"[FPS DISPLAY] Reverting to the static wordmark: {cause}. "
                + "The previous value is discarded, not held.");
        }

        /// <summary>
        /// Everything the key draws from, as one comparable string. Built from what is rendered
        /// rather than what was measured — the reading goes in rounded, because that is the number
        /// the key actually sets.
        /// </summary>
        protected override String BuildVisualSignature()
        {
            String tierName;
            Double fps;

            lock (this._sync)
            {
                tierName = this._hasTier ? this._tier.ToString() : "none";
                fps = this._fps;
            }

            // Freshness is part of the picture, not just an input to it: when a reading ages out
            // the key changes from a number to the wordmark, and nothing else in this string moves
            // to say so.
            return String.Concat(
                tierName,
                "|",
                Math.Round(fps).ToString("0", CultureInfo.InvariantCulture),
                "|",
                this.IsRtssAvailable ? "up" : "down",
                "|",
                this.IsReadingFresh ? "fresh" : "stale",
                "|",
                this._limitingFactor?.GetHint() ?? String.Empty);
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            this.EnsureServicesBound();

            Double fps;
            FpsTier? tier;

            lock (this._sync)
            {
                fps = this._fps;
                tier = this._hasTier ? this._tier : (FpsTier?)null;
            }

            // Whether there is a number to draw, decided by the reading and by RTSS being up.
            //
            // A settled tier is deliberately not required: the tier is a judgement about the
            // reading, not evidence that one exists. It costs three debounced polls to commit, and
            // any transition to Unknown clears it — so a key with a perfectly good 60 in hand
            // showed the wordmark instead. The number is what the user asked for; the tier only
            // decides what colour it is.
            //
            // Freshness IS required, and holding a reading past it was tried and removed.
            //
            // The hold was bounded only by RTSS availability, and RTSS does not go away — so on a
            // desktop where nothing presents, "bounded" meant "forever". The key sat permanently on
            // 4 FPS, a value left behind by a dead entry (a closed game's fossil interval computes
            // to ~3.7 and rounds to 4). A number that never changes is worse than no number: it
            // reads as live and is not.
            //
            // So the reading expires on the clock again. The two other guards stay: RTSS reachable,
            // and at least one whole frame — without the floor the key drew a red "0" from a
            // trailing 0.3 FPS sample.
            var hasLiveData =
                this.IsRtssAvailable &&
                this.IsReadingFresh &&
                Math.Round(fps) >= 1;

            var colorTier = tier.HasValue ? ColorTierHelper.GetTier(tier.Value) : ColorTier.Neutral;

            // Null rather than a grey. Two different states share it: no reading at all, and a
            // reading whose tier has not settled yet — a number in neutral text with no status
            // rule, which is honest about knowing the framerate but not yet how good it is.
            var accent = hasLiveData && tier.HasValue
                ? ColorTierHelper.GetAccentColor(colorTier)
                : (BitmapColor?)null;

            // The bottleneck badge is a red-tier affordance only: at green and yellow the limiting
            // component is not actionable, so the key stays uncluttered.
            var badge = hasLiveData && tier == FpsTier.Red ? this._limitingFactor?.GetHint() : null;

            var image = KeyRenderer.RenderPrimaryFpsKey(imageSize, hasLiveData, fps, accent, badge, isRtssAvailable: this.IsRtssAvailable);

            var version = System.Threading.Interlocked.Increment(ref this._renderVersion);

            PluginLog.InfoThrottled(
                "fps-key-render",
                $"{(tier.HasValue ? tier.Value.ToString() : "none")}|{hasLiveData}",
                $"[FPS KEY RENDER] fps={fps:F1} tier={(tier.HasValue ? tier.Value.ToString() : "none")} " +
                $"hasLiveData={hasLiveData} " +
                $"drawn={(hasLiveData ? Math.Round(fps).ToString("0", CultureInfo.InvariantCulture) : "FPS")} " +
                $"colour={(accent.HasValue ? ColorTierHelper.ToHex(accent.Value) : "none")} " +
                $"size={(Int32)imageSize} renderVersion={version}");

            return image;
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "Live FPS";

        /// <summary>
        /// The live FPS key is display-only and does not open any page.
        /// </summary>
        protected override void RunCommand(String actionParameter)
        {
            // Intentionally left blank: the live FPS key is now just a display.
        }

        /// <summary>
        /// Called when a notification should be sent. Override to add logging.
        /// </summary>
        protected override void OnNotifying(String signature)
        {
            this.LogDisplayStateChange();
        }

        internal FpsStatusCommand(
            IFpsHealthStateService healthState = null,
            IFrameTimeTrackerService frameTimeTracker = null,
            ILimitingFactorService limitingFactor = null,
            IRTSSAvailabilityMonitorService availabilityMonitor = null,
            IPollTickSchedulerService pollScheduler = null)
            : this()
        {
            this._healthState = healthState;
            this._frameTimeTracker = frameTimeTracker;
            this._limitingFactor = limitingFactor;
            this._availabilityMonitor = availabilityMonitor;
            this._pollScheduler = pollScheduler;
            if (this._availabilityMonitor != null)
            {
                this._availabilityMonitor.StatusChanged += this.OnAvailabilityChanged;
            }
        }

        internal String DoBuildVisualSignature() => this.BuildVisualSignature();
        internal BitmapImage DoGetCommandImage(PluginImageSize size) => this.GetCommandImage(null, size);
    }
}