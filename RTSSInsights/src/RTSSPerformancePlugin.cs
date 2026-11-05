namespace Loupedeck.RTSSPerformancePlugin
{
    using System;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.InteropServices;

    using Loupedeck.RTSSPerformancePlugin.Actions;
    using Loupedeck.RTSSPerformancePlugin.Helpers;
    using Loupedeck.RTSSPerformancePlugin.Services;

    /// <summary>
    /// Plugin entry point. Owns the service graph and its lifetime.
    /// </summary>
    public class RTSSPerformancePlugin : Plugin
    {
        /// <summary>
        /// True: the plugin drives RTSS through shared memory and its profile files, and never
        /// emulates keystrokes. This sits alongside <see cref="HasNoApplication"/> quite happily —
        /// the two are not contradictory, which a working third-party plugin on the same service
        /// confirms by declaring exactly this pair.
        /// </summary>
        public override Boolean UsesApplicationApiOnly => true;

        /// <summary>
        /// True: the plugin is universal, so its actions are offered no matter which application
        /// has focus. <see cref="RTSSPerformanceApplication"/> still has to exist for the service
        /// to load the plugin at all; see the remarks there.
        /// </summary>
        public override Boolean HasNoApplication => true;

        public override Boolean Install()
        {
            var result = base.Install();
            this.EvaluateInstallationState();
            return result;
        }

        public Boolean IsRtssApplicationInstalled() =>
            this.Onboarding?.IsRTSSInstalled() ?? Helpers.RTSSInstallLocator.IsInstalled();

        /// <summary>Ticks between [LIVE DATA RECEIVED] lines: 40 ticks at 250ms is ~10s.</summary>
        private const Int64 LiveDataLogEveryTicks = 40;

        /// <summary>Tracks the live/no-data edge so only transitions are logged, not every poll.</summary>
        private Boolean _hadLiveData;

        /// <summary>Startup banner is emitted once, from the poll thread.</summary>
        private Boolean _loggedStartup;

        /// <summary>Last application reported as active, so a steady selection is not re-logged at 4Hz.</summary>
        private String _lastSelectedProcess;

        private String _lastProfileTargetProcess;

        /// <summary>When the selection was last written, so a dropped write is retried.</summary>
        private DateTime _lastSelectionLogUtc;

        /// <summary>Heartbeat for [ACTIVE RTSS APP SELECTED] while the selection is unchanged.</summary>
        private static readonly TimeSpan SelectionLogInterval = TimeSpan.FromSeconds(10);

        private static String DescribeSelection(ActiveAppSelection reason)
        {
            switch (reason)
            {
                case ActiveAppSelection.ForegroundWindow:
                    return "owns the foreground window";

                case ActiveAppSelection.HighestFramerate:
                    return "fastest of the applications RTSS is actively updating "
                        + "(nothing hooked is in the foreground)";

                default:
                    return "none";
            }
        }

        public static RTSSPerformancePlugin Instance { get; private set; }

        /// <summary>
        /// Raised once, after <see cref="CreateServices"/> completes, so every action can bind to
        /// the live service graph regardless of which order the host called action.OnLoad() versus
        /// Plugin.Load() in.
        ///
        /// The host does not guarantee that ordering: on-device, an action's OnLoad ran and called
        /// its own EnsureServicesBound while <see cref="Instance"/> was still null — 129ms before
        /// Plugin.Load() even started — so the action's binding failed silently and, because
        /// nothing else ever re-triggered EnsureServicesBound for it (its own poll-tick
        /// subscription is itself part of what a successful bind sets up), it stayed unbound for
        /// the rest of the session. The action rendered its initial static wordmark once and then
        /// never updated again, which is indistinguishable on the physical console from "the whole
        /// pipeline is dead" even though polling, RTSS and every other bound action were working.
        /// See <see cref="Helpers.ActionReadiness"/> for the subscribe-and-catch-up-immediately
        /// pattern every action uses against this event.
        /// </summary>
        public static event EventHandler ServicesReady;

        public RTSSReaderService Reader { get; private set; }

        public RTSSStatisticsRecordingService StatisticsRecording { get; private set; }

        public RTSSProfileWriterService ProfileWriter { get; private set; }

        public RTSSAvailabilityMonitorService AvailabilityMonitor { get; private set; }

        public FrameTimeTrackerService FrameTimeTracker { get; private set; }

        public ZeroPointOnePercentLowTrackerService ZeroPointOnePercentLowTracker { get; private set; }

        public FpsHealthStateService HealthState { get; private set; }

        public RamUsageSourceService RamSource { get; private set; }

        public CpuUsageSourceService CpuSource { get; private set; }

        public GpuUsageSourceService GpuSource { get; private set; }

        public LimitingFactorService LimitingFactor { get; private set; }

        public PollTickSchedulerService PollScheduler { get; private set; }

        public PerformanceHistoryService PerformanceHistory { get; private set; }

        public BottleneckAnalysisService BottleneckAnalysis { get; private set; }

        public PerformanceStatusService PerformanceStatus { get; private set; }

        public DisplayInfoService DisplayInfo { get; private set; }

        public FpsAdvisorService FpsAdvisor { get; private set; }

        public RTSSProfileHelperRunner ProfileHelperRunner { get; private set; }

        public EffectiveFpsCapService EffectiveFpsCap { get; private set; }

        public FpsCapStateService CapStateService { get; private set; }

        public TelemetryCoordinatorService TelemetryCoordinator { get; private set; }

        public RTSSOnboardingService Onboarding { get; private set; }

        /// <summary>
        /// The Loupedeck runtime discovers and instantiates PluginDynamicCommand and
        /// PluginDynamicFolder subclasses in this assembly itself, so Load only has to stand up
        /// the service graph those actions bind to in their own Load overrides.
        /// </summary>
        public override void Load()
        {
            Instance = this;

            PluginLog.Init(this.Log);
            HapticPatterns.Init(this);
            PluginResources.Init(this.Assembly);

            this.CreateServices();

            this.EvaluateInstallationState();

            // Tells every action that already ran its own OnLoad before this point — and so found
            // Instance null and could not bind — to bind now. See ServicesReady's remarks.
            //
            // Invoked one handler at a time, like PollTickSchedulerService's Tick: with up to 16
            // actions subscribed, a plain multicast Invoke would let one throwing handler stop
            // every handler after it in the list from ever binding.
            var subscribers = ServicesReady;

            if (subscribers != null)
            {
                foreach (var handler in subscribers.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler)handler)(this, EventArgs.Empty);
                    }
                    catch (Exception exception)
                    {
                        PluginLog.Warning(exception, "A ServicesReady subscriber threw during Load.");
                    }
                }
            }

            // Deliberately no logging here: the runtime log file is not writable during Load, so
            // anything written is dropped. The startup banner is emitted from the first poll tick
            // instead, in LogStartupOnce.
        }

        public override void Unload()
        {
            this.DisposeServices();

            Instance = null;
        }

        /// <summary>
        /// Every command in this plugin is a PluginDynamicCommand that handles its own press, so
        /// the base dispatch never has real work to do here. It must not throw: the runtime calls
        /// it for any command it fails to route.
        /// </summary>
        public override void RunCommand(String commandName, String parameter)
        {
            if (String.Equals(commandName, "BottleneckStatusCommand", StringComparison.OrdinalIgnoreCase))
            {
                PluginLog.Info("[ACTION ROUTE] Routed unrouted legacy command 'BottleneckStatusCommand'.");
            }
        }

        /// <summary>Same as <see cref="RunCommand"/>: dials are PluginDynamicAdjustments.</summary>
        public override void ApplyAdjustment(String actionName, String parameter, Int32 diff)
        {
        }


        /// <summary>
        /// Builds the service graph in dependency order. The actions resolve these off
        /// <see cref="Instance"/> when the runtime loads them.
        /// </summary>
        private void CreateServices()
        {
            this.ProfileHelperRunner = new RTSSProfileHelperRunner();
            this.Reader = new RTSSReaderService();
            this.StatisticsRecording = new RTSSStatisticsRecordingService();
            this.ProfileWriter = new RTSSProfileWriterService(null, this.ProfileHelperRunner);
            this.AvailabilityMonitor = new RTSSAvailabilityMonitorService(this.Reader);
            this.FrameTimeTracker = new FrameTimeTrackerService();
            this.ZeroPointOnePercentLowTracker = new ZeroPointOnePercentLowTrackerService(this.FrameTimeTracker);
            this.HealthState = new FpsHealthStateService(this.FrameTimeTracker);
            this.RamSource = new RamUsageSourceService();
            this.CpuSource = new CpuUsageSourceService();
            this.GpuSource = new GpuUsageSourceService();
            this.PerformanceHistory = new PerformanceHistoryService();
            this.PerformanceStatus = new PerformanceStatusService();
            this.BottleneckAnalysis = new BottleneckAnalysisService();
            this.DisplayInfo = new DisplayInfoService();
            this.FpsAdvisor = new FpsAdvisorService(this.DisplayInfo);
            this.LimitingFactor = new LimitingFactorService();
            this.CapStateService = new FpsCapStateService(this.ProfileWriter, this.Reader, this.AvailabilityMonitor, this.ProfileHelperRunner);
            this.EffectiveFpsCap = this.CapStateService;
            this.TelemetryCoordinator = new TelemetryCoordinatorService(
                this.Reader,
                this.AvailabilityMonitor,
                this.FrameTimeTracker,
                this.ZeroPointOnePercentLowTracker,
                this.CpuSource,
                this.GpuSource,
                this.RamSource,
                this.EffectiveFpsCap,
                this.ProfileWriter,
                this.BottleneckAnalysis,
                this.PerformanceHistory,
                this.FpsAdvisor,
                this.HealthState,
                this.PerformanceStatus,
                this.StatisticsRecording);
            this.Onboarding = new RTSSOnboardingService(this.UpdatePluginStatusFromOnboarding);
            this.PollScheduler = new PollTickSchedulerService();

            // The poll loop nothing else drives: one tick reads RTSS and pushes a sample into the
            // tracker. FpsHealthStateService already subscribes to the tracker's StatisticsUpdated,
            // so the tier follows from the sample rather than being polled a second time here.
            this.PollScheduler.Tick += this.OnPollTick;
            this.AvailabilityMonitor.StatusChanged += this.OnAvailabilityChanged;

            // The health tier grades live FPS against the ceiling actually in force, so the target
            // has to follow the ceiling. Without this the tier is judged against the default 60
            // for the whole session: cap at 144 and a genuine 100 fps still reads Green, because
            // 100 clears 0.9 x 60 while falling short of 0.7 x 144.
            this.ProfileWriter.FpsCeilingChanged += this.OnFpsCeilingChanged;

            // Follow effective cap changes directly from the authoritative service
            this.EffectiveFpsCap.CapChanged += this.OnEffectiveCapChanged;

            // And follow the profile itself, not just the caps this plugin writes. Adopting a
            // profile that declares no Limit used to leave the tier grading against the previous
            // application's ceiling.
            this.ProfileWriter.ActiveProfileCeilingChanged += this.OnActiveProfileCeilingChanged;

            // Seed from the profile on disk, not from CurrentFpsCeiling: nothing has been written
            // yet, so that would be 0 and the first poll would grade against "unlimited" even
            // though the profile already carries a cap. UpdateActiveProfile will not fill this in
            // either, because it short-circuits when the target profile has not changed.
            if (this.ProfileWriter.TryReadFpsCeiling(this.ProfileWriter.ActiveProfileName, out var initialCeiling))
            {
                this.ApplyHealthTarget(initialCeiling);
            }

            this.AvailabilityMonitor.Start();
            this.PollScheduler.SetIdle(!this.AvailabilityMonitor.IsAvailable);
            this.PollScheduler.Start();
        }

        /// <summary>
        /// Runs on a timer thread. The scheduler logs anything that escapes, but a snapshot miss
        /// is the normal state whenever RTSS is closed, so it is handled here rather than thrown.
        /// </summary>
        private void OnPollTick(Object sender, PollTickEventArgs e) => this.OnPollTickCore(e);

        /// <summary>
        /// Emits the startup summary from the poll thread rather than from <see cref="Load"/>.
        /// The runtime's log file is not writable yet while Load runs, so anything written there
        /// is silently dropped — which is why the startup banner never appeared on-device even
        /// though the plugin had loaded and was polling.
        /// </summary>
        private void LogStartupOnce(Int64 tickIndex)
        {
            // The runtime log file is not writable for the first few seconds after load, so an
            // early banner is silently dropped. Wait until the file has demonstrably settled.
            if (this._loggedStartup || tickIndex < LiveDataLogEveryTicks)
            {
                return;
            }

            this._loggedStartup = true;

            this.LogBuildIdentity();

            PluginLog.Info(
                $"[PLUGIN LOADED] RTSS Insights. " +
                $"RTSS install: {RTSSInstallLocator.TryGetInstallDirectory() ?? "<not found>"}. " +
                $"Availability: {this.AvailabilityMonitor?.Status}.");

            if (this.AvailabilityMonitor?.Status == RTSSAvailability.Available)
            {
                PluginLog.Info("[RTSS DETECTED] Shared memory opened and header validated.");
            }
        }


        /// <summary>
        /// Emits the identity of the assembly the Logi Plugin Service actually loaded.
        ///
        /// The service resolves plugins from its own Plugins directory, not from this repository's
        /// build output, so "the source builds" and "the deployed DLL runs" are separate claims.
        /// BuildId is stamped fresh into the assembly on every build (see the csproj), which makes
        /// this line the only thing that can settle which binary is live.
        /// </summary>
        private void LogBuildIdentity()
        {
            String buildId;
            String assemblyVersion;
            String assemblyPath;
            String moduleVersionId;
            String pluginVersion;

            try
            {
                var assembly = typeof(RTSSPerformancePlugin).Assembly;

                buildId = assembly
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == "BuildId")?.Value ?? "<unstamped>";

                assemblyVersion = assembly.GetName().Version?.ToString() ?? "<none>";

                // Empty when the host loaded the assembly from a byte array rather than from disk,
                // which is itself worth seeing: it would mean no file on disk corresponds to it.
                assemblyPath = String.IsNullOrEmpty(assembly.Location) ? "<loaded from memory>" : assembly.Location;

                // Identifies the compiled module regardless of where it was copied to.
                moduleVersionId = assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 8);

                // The Plugin base class exposes no version, so the package version reaches the log
                // through the assembly attribute the csproj derives from the same Version property.
                pluginVersion = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "<none>";
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, "[RTSS PLUGIN STARTED] Could not read assembly identity.");
                return;
            }

            var process = System.Diagnostics.Process.GetCurrentProcess();

            PluginLog.Info(
                $"[RTSS PLUGIN STARTED] " +
                $"BuildId={buildId} " +
                $"Mvid={moduleVersionId} " +
                $"AssemblyVersion={assemblyVersion} " +
                $"AssemblyPath={assemblyPath} " +
                $"PluginVersion={pluginVersion} " +
                $"Host={process.ProcessName} PID={process.Id} " +
                $"ProcessBitness={(Environment.Is64BitProcess ? "x64" : "x86")} " +
                $"Framework={RuntimeInformation.FrameworkDescription} " +
                $"OS={RuntimeInformation.OSDescription} " +
                $"Resources={PluginResources.GetResourceNames().Count()} " +
                $"PluginApi={DescribePluginApiAssembly()}");
        }

        private static String DescribePluginApiAssembly()
        {
            try
            {
                var assembly = typeof(Plugin).Assembly;
                return $"{assembly.GetName().Version} @ " +
                    $"{(String.IsNullOrEmpty(assembly.Location) ? "<loaded from memory>" : assembly.Location)}";
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, "[RTSS PLUGIN STARTED] Could not read PluginApi identity.");
                return "<unavailable>";
            }
        }

        private void OnPollTickCore(PollTickEventArgs e)
        {
            this.LogStartupOnce(e.TickIndex);

            if (this.AvailabilityMonitor?.IsAvailable != true)
            {
                this.EvaluateInstallationState();
            }

            var telemetry = this.TelemetryCoordinator != null
                ? this.TelemetryCoordinator.AcquireCycle()
                : (this.Reader?.ReadActiveApplicationSnapshot() != null ? TelemetrySnapshot.Empty() : null);
            var snapshot = telemetry?.Rtss;

            if (snapshot == null)
            {
                this.PerformanceHistory?.Reset();
                this.PerformanceStatus?.Reset();
                this.BottleneckAnalysis?.Reset();

                // Nothing is presenting, so the cap the user is about to set is a global one.
                //
                // Without this the write target stayed pointed at the last application that
                // happened to be running — close a game, press 60, and the ceiling landed in that
                // game's profile rather than Global, where nothing would read it until the game
                // came back. Per-application targeting still applies whenever an application is
                // actually being monitored; Global is simply the resting state rather than a
                // first-run default that never returns.
                if (this._lastProfileTargetProcess != null)
                {
                    var retargetedToGlobal = this.ProfileWriter?.UpdateActiveProfile(null) == true;
                    PluginLog.Info(
                        $"[RTSS PROFILE] No application is presenting; " +
                        $"cap changes now target '{this.ProfileWriter?.ActiveProfileName}' " +
                        $"(retargeted={retargetedToGlobal}).");
                }

                this._lastProfileTargetProcess = null;

                // Log the transition only, never the steady state.
                if (this._hadLiveData)
                {
                    this._hadLiveData = false;

                    // A source that was reporting frame times just went silent — RTSS is still up,
                    // but no entry is fresh and eligible any more. The window must not survive the
                    // gap: the next application selected, even one with the same process id after
                    // a genuine gap, must start from an empty window rather than being blended
                    // against samples from a source that is no longer live. Availability loss
                    // already resets this in OnAvailabilityChanged; this covers the narrower case
                    // where RTSS itself never went down.
                    this.FrameTimeTracker?.Reset();
                    this.ZeroPointOnePercentLowTracker?.Reset();

                    PluginLog.Info("[RTSS DISCONNECTED/RECONNECTING] No hooked application is presenting frames.");
                }

                PluginLog.InfoThrottled(
                    "low-debug-no-source",
                    "none",
                    $"[LOW DEBUG] source PID=0 name=<none> current FPS=0.0 frame time=0.00ms " +
                    $"1% samples={this.FrameTimeTracker?.SampleCount ?? 0} " +
                    $"window1%={this.FrameTimeTracker?.GetWindowSummary() ?? "empty"} calculated 1% low=0.0 " +
                    $"0.1% samples={this.ZeroPointOnePercentLowTracker?.SampleCount ?? 0} " +
                    $"window0.1%={this.ZeroPointOnePercentLowTracker?.GetWindowSummary() ?? "empty"} " +
                    "calculated 0.1% low=0.0 RTSS native 1% low=0.0 " +
                    "RTSS native 0.1% low=0.0 final rendered 1%=0 final rendered 0.1%=0");

                return;
            }

            var lowStatistics = telemetry.FrameStats ?? this.FrameTimeTracker?.Current;
            // Value-flow trace: what RTSS reported, and what the window made of it. The two are
            // logged side by side because the display bug was a silent substitution between them.
            PluginLog.InfoThrottled(
                "rtss-raw-fps",
                $"[RTSS RAW FPS] process={snapshot.ProcessFileName} " +
                $"instantaneousFps={snapshot.InstantaneousFps:F1} " +
                $"frameTime={snapshot.FrameTimeMilliseconds:F2}ms");

            var traced = lowStatistics;
            var tracedZeroPointOne = telemetry.LowStats ?? this.ZeroPointOnePercentLowTracker?.Current;

            PluginLog.InfoThrottled(
                "low-debug",
                $"{snapshot.ProcessId}:{snapshot.ProcessFileName}",
                $"[LOW DEBUG] source PID={snapshot.ProcessId} name={snapshot.ProcessFileName} " +
                    $"current FPS={snapshot.InstantaneousFps:F1} frame time={snapshot.FrameTimeMilliseconds:F2}ms " +
                    $"sample timestamp={snapshot.FrameSampleTimestamp} " +
                $"1% samples={traced?.SampleCount ?? 0} window1%={this.FrameTimeTracker?.GetWindowSummary() ?? "empty"} " +
                $"calculated 1% low={traced?.OnePercentLowFps ?? 0:F1} " +
                $"0.1% samples={tracedZeroPointOne?.SampleCount ?? 0} " +
                $"window0.1%={this.ZeroPointOnePercentLowTracker?.GetWindowSummary() ?? "empty"} " +
                $"calculated 0.1% low={tracedZeroPointOne?.CalculatedZeroPointOnePercentLowFps ?? 0:F1} " +
                $"0.1% source={(tracedZeroPointOne?.NativeZeroPointOnePercentLowFps > 0 ? "rtssNative" : "calculated")} " +
                $"RTSS native 1% low={snapshot.OnePercentLowFps:F1} " +
                $"RTSS native 0.1% low={snapshot.NativeZeroPointOnePercentLowFps:F1} " +
                $"final rendered 1%={Math.Round(traced?.OnePercentLowFps ?? 0):F0} " +
                $"final rendered 0.1%={Math.Round(tracedZeroPointOne?.ZeroPointOnePercentLowFps ?? 0):F0}");


            // Keep the write target pointed at whatever is actually presenting. RTSS applies a
            // per-application profile in preference to Global, so the active profile must be
            // retargeted to the running game before a ceiling change is written.
            var retargeted = false;
            if (!String.Equals(
                    this._lastProfileTargetProcess,
                    snapshot.ProcessFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                retargeted = this.ProfileWriter?.UpdateActiveProfile(snapshot.ProcessFileName) ?? false;
                this._lastProfileTargetProcess = snapshot.ProcessFileName;
            }

            // Log the selected application whenever it changes, and otherwise on a slow heartbeat.
            //
            // Change alone is not enough. PluginLog is best-effort — a failed write is swallowed —
            // and the old code recorded "already reported" before writing, so a single dropped line
            // silenced the selection permanently for that application: observed on-device, where an
            // application was demonstrably selected and driving the display for minutes without one
            // [ACTIVE RTSS APP SELECTED] line ever appearing. The heartbeat makes the log
            // self-healing, and the state is now committed only after the write.
            var now = DateTime.UtcNow;
            var processChanged =
                !String.Equals(this._lastSelectedProcess, snapshot.ProcessFileName, StringComparison.OrdinalIgnoreCase);

            if (retargeted || processChanged || now - this._lastSelectionLogUtc >= SelectionLogInterval)
            {
                PluginLog.Info(
                    $"[ACTIVE RTSS APP SELECTED] process={snapshot.ProcessFileName} pid={snapshot.ProcessId} " +
                    $"fps={snapshot.InstantaneousFps:F1} profile={this.ProfileWriter?.ActiveProfileName} " +
                    $"reason={DescribeSelection(snapshot.SelectionReason)}");

                this._lastSelectedProcess = snapshot.ProcessFileName;
                this._lastSelectionLogUtc = now;

                if (processChanged)
                {
                    this.PerformanceHistory?.Reset();
                    this.BottleneckAnalysis?.Reset();
                    this.FrameTimeTracker?.Reset();
                    this.ZeroPointOnePercentLowTracker?.Reset();
                    this.HealthState?.Reset();
                    this.FpsAdvisor?.DecisionEngine?.Reset();
                }
            }

            // The poll loop runs at 4Hz, so logging every sample would produce ~14k lines an hour.
            // Log the first sample after a gap, then roughly every ten seconds.
            var isFirst = !this._hadLiveData;
            this._hadLiveData = true;

            if (isFirst || e.TickIndex % LiveDataLogEveryTicks == 0)
            {
                var statistics = this.FrameTimeTracker?.Current;

                PluginLog.Info(
                    $"[LIVE DATA RECEIVED] {snapshot.ProcessFileName} pid {snapshot.ProcessId} " +
                    $"inst={snapshot.InstantaneousFps:F1} fps, frametime={snapshot.FrameTimeMilliseconds:F2} ms, " +
                    $"window avg={statistics?.AverageFps:F1} 1%low={statistics?.OnePercentLowFps:F1} " +
                    $"n={statistics?.SampleCount}");
            }
        }

        /// <summary>
        /// Fires whenever the ceiling actually changes, including profile adoption and direct
        /// ceiling writes. Every change re-targets the health grading.
        /// </summary>
        private void OnFpsCeilingChanged(Object sender, Int32 fpsCeiling)
        {
            this.ApplyHealthTarget(fpsCeiling);
            this.EffectiveFpsCap?.SetCap(fpsCeiling);
        }

        /// <summary>The active profile changed; grade against its ceiling, or against none.</summary>
        private void OnActiveProfileCeilingChanged(Object sender, Int32 fpsCeiling)
        {
            this.ApplyHealthTarget(fpsCeiling);
            this.EffectiveFpsCap?.SetCap(fpsCeiling);
        }

        private void OnEffectiveCapChanged(Object sender, Int32 cap)
        {
            this.ApplyHealthTarget(cap);
        }

        /// <summary>
        /// Points the health service at <paramref name="fpsCeiling"/> as its target.
        ///
        /// Unlimited (0) is passed through deliberately: with no cap there is nothing to fall
        /// short of, and <see cref="FpsHealthStateService.GetTier"/> already treats a non-positive
        /// target as Green rather than dividing by it.
        /// </summary>
        private void ApplyHealthTarget(Int32 fpsCeiling)
        {
            var health = this.HealthState;

            if (health == null || health.TargetFps == fpsCeiling)
            {
                return;
            }

            health.TargetFps = fpsCeiling;

            PluginLog.Info(
                fpsCeiling > 0
                    ? $"[TARGET UPDATED] Grading live FPS against {fpsCeiling} " +
                      $"(green >= {fpsCeiling * 0.9:F0}, yellow >= {fpsCeiling * 0.7:F0})."
                    : "[TARGET UPDATED] Ceiling is unlimited; no target to fall short of.");
        }

        private void OnAvailabilityChanged(Object sender, RTSSAvailability status)
        {
            this.PollScheduler?.SetIdle(status != RTSSAvailability.Available);

            if (status != RTSSAvailability.Available)
            {
                this.StatisticsRecording?.HandleRTSSUnavailable();
            }

            switch (status)
            {
                case RTSSAvailability.Available:
                    PluginLog.Info(
                        $"[RTSS CONNECTED] Shared memory readable; polling at " +
                        $"{this.PollScheduler?.ActiveIntervalMs}ms.");
                    break;

                case RTSSAvailability.NotRunning:
                    PluginLog.Info(
                        $"[RTSS DISCONNECTED/RECONNECTING] RTSS is not running; backing off to " +
                        $"{this.PollScheduler?.IdleIntervalMs}ms and re-probing.");
                    break;

                case RTSSAvailability.Reconnected:
                    PluginLog.Info(
                        "[RTSS DISCONNECTED/RECONNECTING] RTSS restarted; discarding the previous " +
                        "shared-memory session before accepting new samples.");
                    break;

                default:
                    PluginLog.Warning(
                        $"[RTSS DISCONNECTED/RECONNECTING] RTSS reachable but unusable ({status}); " +
                        $"backing off to {this.PollScheduler?.IdleIntervalMs}ms.");
                    break;
            }

            if (status != RTSSAvailability.Available)
            {
                this.PerformanceHistory?.Reset();
                this.PerformanceStatus?.Reset();
                this.BottleneckAnalysis?.Reset();

                // Stale frame times would otherwise keep the key green after the game exits.
                this.FrameTimeTracker?.Reset();
                this.ZeroPointOnePercentLowTracker?.Reset();
                this.HealthState?.Reset();
            }

            this.EvaluateInstallationState();
        }

        private void CapturePerformanceHistory(RTSSFrameSnapshot snapshot)
        {
            if (snapshot == null || this.PerformanceHistory == null)
            {
                return;
            }

            var frameStatistics = this.FrameTimeTracker?.Current;
            var zeroPointOnePercentStatistics = this.ZeroPointOnePercentLowTracker?.Current;

            this.PerformanceHistory.AddSnapshot(new PerformanceSnapshot(
                snapshot.CapturedAtUtc,
                fps: IsUsable(snapshot.InstantaneousFps) ? snapshot.InstantaneousFps : (Double?)null,
                frameTimeMilliseconds: frameStatistics?.SampleCount > 0 && IsUsable(snapshot.FrameTimeMilliseconds)
                    ? snapshot.FrameTimeMilliseconds
                    : (Double?)null,
                onePercentLowFps: frameStatistics?.SampleCount > 0 && IsUsable(frameStatistics.OnePercentLowFps)
                    ? frameStatistics.OnePercentLowFps
                    : (Double?)null,
                zeroPointOnePercentLowFps: zeroPointOnePercentStatistics?.SampleCount > 0 &&
                    IsUsable(zeroPointOnePercentStatistics.ZeroPointOnePercentLowFps)
                    ? zeroPointOnePercentStatistics.ZeroPointOnePercentLowFps
                    : (Double?)null,
                cpuUsagePercent: this.CpuSource?.HasCpuUsageReading == true
                    ? this.CpuSource.CpuUsagePercent
                    : (Double?)null,
                gpuUsagePercent: this.GpuSource?.HasGpuUsageReading == true
                    ? this.GpuSource.GpuUsagePercent
                    : (Double?)null,
                ramUsagePercent: this.RamSource?.HasRamUsageReading == true ? this.RamSource.RamUsagePercent : (Double?)null));
        }

        private static Boolean IsUsable(Double value) =>
            !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0;

        private void DisposeServices()
        {
            if (this.PollScheduler != null)
            {
                this.PollScheduler.Tick -= this.OnPollTick;
            }

            if (this.AvailabilityMonitor != null)
            {
                this.AvailabilityMonitor.StatusChanged -= this.OnAvailabilityChanged;
            }

            if (this.ProfileWriter != null)
            {
                this.ProfileWriter.FpsCeilingChanged -= this.OnFpsCeilingChanged;
                this.ProfileWriter.ActiveProfileCeilingChanged -= this.OnActiveProfileCeilingChanged;
            }

            if (this.EffectiveFpsCap != null)
            {
                this.EffectiveFpsCap.CapChanged -= this.OnEffectiveCapChanged;
            }

            this.PollScheduler?.Dispose();
            this.AvailabilityMonitor?.Dispose();
            this.RamSource?.Dispose();
            this.CpuSource?.Dispose();
            this.GpuSource?.Dispose();
            this.StatisticsRecording?.Dispose();
            this.PerformanceHistory?.Dispose();
            this.PerformanceStatus?.Dispose();
            this.BottleneckAnalysis?.Dispose();
            this.Reader?.Dispose();

            this.PollScheduler = null;
            this.HealthState = null;
            this.FrameTimeTracker = null;
            this.ZeroPointOnePercentLowTracker = null;
            this.AvailabilityMonitor = null;
            this.ProfileWriter = null;
            this.LimitingFactor = null;
            this.RamSource = null;
            this.CpuSource = null;
            this.GpuSource = null;
            this.PerformanceHistory = null;
            this.PerformanceStatus = null;
            this.BottleneckAnalysis = null;
            this.DisplayInfo = null;
            this.FpsAdvisor = null;
            this.ProfileHelperRunner = null;
            this.Reader = null;
            this.StatisticsRecording = null;
            this.CapStateService?.Dispose();
            this.CapStateService = null;
            this.EffectiveFpsCap = null;
            this.Onboarding = null;
        }

        private void UpdatePluginStatusFromOnboarding(Loupedeck.PluginStatus status, String message, String supportUrl, String supportUrlTitle)
        {
            try
            {
                if (status == Loupedeck.PluginStatus.Normal)
                {
                    this.OnPluginStatusChanged(Loupedeck.PluginStatus.Normal, String.Empty, null, null);
                }
                else
                {
                    this.OnPluginStatusChanged(status, message, supportUrl, supportUrlTitle);
                }
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, "[RTSS ONBOARDING] OnPluginStatusChanged invocation failed.");
            }
        }

        internal void EvaluateInstallationState()
        {
            try
            {
                this.Onboarding?.CheckInstallationState(this.PluginStatus?.Status ?? Loupedeck.PluginStatus.Unknown);
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, "[RTSS ONBOARDING] Failed to evaluate RTSS installation state.");
            }
        }
    }
}
