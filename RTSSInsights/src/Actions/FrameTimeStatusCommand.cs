namespace Loupedeck.RTSSPerformancePlugin.Actions
{
    using System;

    using Loupedeck.RTSSPerformancePlugin.Helpers;
    using Loupedeck.RTSSPerformancePlugin.Services;

    /// <summary>
    /// The Frame Time key: the same active RTSS source as <see cref="FpsStatusCommand"/>, read as
    /// its instantaneous frame time in milliseconds rather than its rate.
    ///
    /// Unlike <see cref="FpsStatusCommand"/>, this key holds no cached reading at all. A frame
    /// time is a single-poll value with no window to smooth or accumulate, so there is nothing
    /// that source freshness could corrupt across a switch â€” every render reads
    /// <see cref="IRTSSReaderService.LastSnapshot"/> directly,
    /// which the reader itself nulls out the instant nothing is presenting (the same freshness
    /// rule <see cref="RTSSReaderService"/> already applies when selecting the active source) and
    /// republishes fresh on every poll regardless of which source is active. There is no
    /// source-specific state for a PID change to invalidate, because none is kept between polls.
    /// </summary>
    public class FrameTimeStatusCommand : ThrottledDynamicCommand
    {
        private readonly Object _sync = new Object();

        private IRTSSReaderService _reader;
        private IRTSSAvailabilityMonitorService _availabilityMonitor;
        private IPollTickSchedulerService _pollScheduler;
        private ITelemetryCoordinatorService _coordinator;

        /// <summary>Visible state as of the last notification, so the host is told only on a change.</summary>
        private Boolean _wasShowingNumber;

        private Int32 _renderVersion;

        public FrameTimeStatusCommand()
            : base("Frame Time", "Live RTSS frame time, in milliseconds, for the currently active source.", null)
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
                $"[FRAME TIME KEY] Registered as a dynamic command; " +
                $"services {(this._reader == null ? "pending" : "bound")}.");

            return true;
        }

        protected override Boolean OnUnload()
        {
            ActionReadiness.Unsubscribe(this.OnServicesReady);

            lock (this._sync)
            {
                if (this._coordinator != null)
                {
                    this._coordinator.TelemetryUpdated -= this.OnTelemetryUpdated;
                    this._coordinator = null;
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

                this._reader = null;
            }

            return true;
        }

        /// <summary>
        /// Binds whatever of the service graph exists now, and can be called again for the rest.
        /// Mirrors <see cref="FpsStatusCommand.EnsureServicesBound"/> for the same reason: the
        /// runtime may construct this action before <see cref="RTSSPerformancePlugin.Load"/> has
        /// built its services.
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

                this._reader = this._reader ?? plugin.Reader;

                if (this._availabilityMonitor == null && plugin.AvailabilityMonitor != null)
                {
                    this._availabilityMonitor = plugin.AvailabilityMonitor;
                    this._availabilityMonitor.StatusChanged += this.OnAvailabilityChanged;
                }

                if (this._pollScheduler == null && plugin.PollScheduler != null)
                {
                    // The only driver this key needs. There is no cached reading an event could
                    // invalidate â€” every tick just checks whether the live picture has moved on.
                    this._pollScheduler = plugin.PollScheduler;
                    this._pollScheduler.Tick += this.OnPollTick;
                }
            }
        }

        private Boolean IsRtssAvailable
        {
            get
            {
                this.EnsureServicesBound();
                return this._availabilityMonitor?.IsAvailable ??
                       RTSSPerformancePlugin.Instance?.AvailabilityMonitor?.IsAvailable ??
                       false;
            }
        }

        private void OnTelemetryUpdated(Object sender, TelemetrySnapshot snapshot) => this.NotifyIfChanged();

        private void OnPollTick(Object sender, PollTickEventArgs e) => this.NotifyIfChanged();

        private void OnAvailabilityChanged(Object sender, RTSSAvailability status)
        {
            PluginLog.Info($"[FRAME TIME KEY] RTSS is {status}.");
            this.ForceNotify();
        }

        private void OnServicesReady(Object sender, EventArgs e) => this.ForceNotify();

        /// <summary>
        /// The current reading, read straight through with nothing cached.
        ///
        /// <see cref="IRTSSReaderService.LastSnapshot"/> is null the moment RTSS has no source
        /// fresh enough to select — RTSS being closed, or every hooked application having stopped
        /// presenting — so a live value and "nothing to show" are never ambiguous here the way an
        /// event-driven cache would need a separate clock to tell apart.
        /// </summary>
        private (Boolean HasLiveData, Double FrameTimeMilliseconds) ReadCurrentFrameTime()
        {
            var rtssUp = this.IsRtssAvailable;
            var snapshot = this._coordinator?.CurrentSnapshot?.Rtss ?? this._reader?.LastSnapshot;
            var frameTimeMs = snapshot?.FrameTimeMilliseconds ?? 0;

            // > 0, not >= 1 like the FPS/1% Low floors: a frame time has no "at least one whole
            // frame" equivalent. A non-positive value is RTSS not having produced one yet — an
            // interval that has only just opened, per GetFrameTimeMilliseconds's own contract —
            // rather than a real reading of zero milliseconds.
            var hasLiveData = rtssUp && snapshot != null && frameTimeMs > 0 && IsSnapshotFresh(snapshot);

            return (hasLiveData, frameTimeMs);
        }

        /// <summary>Same two-second horizon the Live FPS key applies to its reading.</summary>
        private static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromSeconds(2);

        /// <summary>
        /// A snapshot the poll loop stopped refreshing must not keep a frame time on the glass.
        /// An unstamped snapshot (default time) is treated as fresh, as the coordinator does.
        /// </summary>
        private static Boolean IsSnapshotFresh(RTSSFrameSnapshot snapshot) =>
            snapshot.CapturedAtUtc == default || DateTime.UtcNow - snapshot.CapturedAtUtc <= MaxSnapshotAge;

        private void NotifyIfChanged() => this.CoreNotifyIfChanged();

        /// <summary>
        /// Records the moment the key starts or stops showing a number, and why. Mirrors the
        /// [FPS DISPLAY] / [1% LOW DISPLAY] lines, which is what made those keys' failure modes
        /// diagnosable from the log alone rather than guessed at.
        /// </summary>
        private void LogDisplayStateChange()
        {
            var (hasLiveData, frameTimeMs) = this.ReadCurrentFrameTime();

            if (this._wasShowingNumber == hasLiveData)
            {
                return;
            }

            this._wasShowingNumber = hasLiveData;

            if (hasLiveData)
            {
                PluginLog.Info($"[FRAME TIME DISPLAY] Showing {frameTimeMs:F2} ms from a fresh RTSS reading.");

                return;
            }

            var rtssUp = this.IsRtssAvailable;
            var snapshot = this._reader?.LastSnapshot;

            var cause = !rtssUp
                ? "RTSS is not readable"
                : snapshot == null
                    ? "no RTSS source is currently fresh"
                    : $"the source has not produced a frame time yet ({frameTimeMs:F2} ms)";

            PluginLog.Info(
                $"[FRAME TIME DISPLAY] Reverting to the static wordmark: {cause}. "
                + "The previous value is discarded, not held.");
        }

        /// <summary>Everything the key draws from, as one comparable string.</summary>
        protected override String BuildVisualSignature()
        {
            var (hasLiveData, frameTimeMs) = this.ReadCurrentFrameTime();

            // Formatted through the same function the renderer draws with, so two frame times
            // that would draw identically (16.71ms and 16.74ms both read "16.7") never trigger a
            // redundant notification — the same coalescing FpsStatusCommand's own signature does
            // by rounding before comparing.
            return String.Concat(
                hasLiveData ? "live" : "none",
                "|",
                hasLiveData ? KeyRenderer.FormatFrameTime(frameTimeMs) : String.Empty,
                "|",
                this.IsRtssAvailable ? "up" : "down");
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            this.EnsureServicesBound();

            var (hasLiveData, frameTimeMs) = this.ReadCurrentFrameTime();
            var rtssUp = this.IsRtssAvailable;

            var image = KeyRenderer.RenderFrameTimeKey(imageSize, hasLiveData, frameTimeMs, isRtssAvailable: rtssUp);

            var version = System.Threading.Interlocked.Increment(ref this._renderVersion);

            PluginLog.InfoThrottled(
                "frame-time-key-render",
                $"{hasLiveData}|{rtssUp}",
                $"[FRAME TIME KEY RENDER] frameTimeMs={frameTimeMs:F2} hasLiveData={hasLiveData} isRtssAvailable={rtssUp} " +
                $"size={(Int32)imageSize} renderVersion={version}");

            return image;
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "Frame Time";

        protected override void OnNotifying(String signature)
        {
            this.LogDisplayStateChange();
        }

        internal FrameTimeStatusCommand(
            IRTSSReaderService reader = null,
            IRTSSAvailabilityMonitorService availabilityMonitor = null,
            IPollTickSchedulerService pollScheduler = null)
            : this()
        {
            this._reader = reader;
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