namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Threading;

    using Loupedeck.RTSSPerformancePlugin.Interop;
    using Loupedeck.RTSSPerformancePlugin.Helpers;

    /// <summary>
    /// Snapshot of the foreground application's frame data for a single poll tick.
    /// </summary>
    public sealed class RTSSFrameSnapshot
    {
        public UInt32 ProcessId { get; set; }

        public Int32 EntryIndex { get; set; }

        public String ProcessFileName { get; set; }

        public Double InstantaneousFps { get; set; }

        public Double AverageFps { get; set; }

        /// <summary>RTSS-native 1% Low decoded from dwStatFramerate1Dot0PercentLow.</summary>
        public Double NativeOnePercentLowFps { get; set; }

        /// <summary>Compatibility alias; this snapshot value is always RTSS-native.</summary>
        public Double OnePercentLowFps
        {
            get => this.NativeOnePercentLowFps;
            set => this.NativeOnePercentLowFps = value;
        }

        public Boolean HasNativeOnePercentLow { get; set; }

        public String RtssVersion { get; set; }

        public UInt32 RawOnePercentLow { get; set; }

        public UInt32 StatFlags { get; set; }

        public UInt32 StatFrames { get; set; }

        public UInt32 StatCount { get; set; }

        public Double NativeZeroPointOnePercentLowFps { get; set; }

        public Boolean HasNativeZeroPointOnePercentLow { get; set; }

        public Double FrameTimeMilliseconds { get; set; }

        public UInt32 FrameSampleTimestamp { get; set; }

        public DateTime CapturedAtUtc { get; set; }

        /// <summary>Why this application was chosen over the other hooked ones.</summary>
        public ActiveAppSelection SelectionReason { get; set; }
    }

    /// <summary>Why a particular hooked application was chosen as the active one.</summary>
    public enum ActiveAppSelection
    {
        /// <summary>Nothing is presenting meaningfully.</summary>
        None = 0,

        /// <summary>It owns the foreground window and is presenting. Definitive when both hold.</summary>
        ForegroundWindow = 1,

        /// <summary>Nothing hooked is in the foreground; this one is rendering fastest.</summary>
        HighestFramerate = 2,
    }

    public interface IRTSSReaderService : IDisposable
    {
        Boolean IsConnected { get; }

        RTSSFrameSnapshot LastSnapshot { get; }

        Boolean TryConnect();

        void Disconnect();

        Boolean TryReadHeader(out RTSSSharedMemoryHeader header);

        Boolean TryReadAppEntry(Int32 index, out RTSSSharedMemoryAppEntry entry);

        /// <summary>Live FPS for a specific hooked process; 0 when unavailable.</summary>
        Double GetLiveFps(UInt32 processId);

        IReadOnlyList<RTSSSharedMemoryAppEntry> ReadActiveAppEntries();

        RTSSFrameSnapshot ReadActiveApplicationSnapshot();

        RTSSFrameSnapshot ReadForegroundHookedApplicationSnapshot() => null;
    }

    /// <summary>
    /// Opens a read-only view of the RTSS shared memory block and projects it into snapshots.
    /// </summary>
    public sealed class RTSSReaderService : IRTSSReaderService
    {
        /// <summary>Upper bound on the app array size, as a guard against a corrupt header.</summary>
        private const UInt32 MaxAppArraySize = 4096;

        /// <summary>
        /// How recently RTSS must have advanced an app entry for it to count as live.
        ///
        /// RTSS closes a sampling interval about once a second per presenting application and
        /// leaves the entry untouched afterwards, so anything older than a few intervals belongs
        /// to an application that has stopped presenting. Measured on-device: a game that stopped
        /// rendering 69 minutes earlier still advertised its last framerate as current.
        ///
        /// Deliberately generous, because this plugin is not game-only — an editing or rendering
        /// application that presents a couple of frames a second must still qualify as live.
        /// </summary>
        private const UInt32 EntryFreshnessWindowMs = 15000;

        /// <summary>
        /// Rate floor every entry must clear to be selectable, foreground included.
        ///
        /// It exempted the foreground window until the key was found showing the wordmark while a
        /// game ran at 144 fps behind a hooked chat client: the exemption let an application with
        /// no framerate claim the key and then outrank everything that had one.
        ///
        /// One frame a second: enough to exclude an application that is not really presenting,
        /// and low enough that nothing a user would want measured is excluded. Monitoring is
        /// global and continuous — whatever RTSS is updating gets reported, foreground or not,
        /// game or not — so this floor is only a "is it actually rendering" test, never a
        /// judgement about what kind of application deserves to be watched.
        ///
        /// It was 5 fps, which was too high: a background editing timeline, a paused viewport or
        /// a menu ticking over at 2 fps was silently ignored, and the key showed nothing while an
        /// application was demonstrably presenting.
        ///
        /// It is not zero, because the freshness window alone does not imply activity. An entry
        /// that blipped a single frame is fresh for the next five seconds while its computed rate
        /// sits near zero; selecting that as the active application is what the original floor was
        /// added to prevent, observed on-device with a desktop app presenting one frame every
        /// 28 seconds.
        /// </summary>
        private const Double MinimumBackgroundFps = 1.0;

        private const Int64 SnapshotCacheDurationMs = 50;


        /// <summary>How often the entry table is repeated while the outcome is unchanged.</summary>
        private static readonly TimeSpan DiagnosticsLogInterval = TimeSpan.FromMinutes(5);

        private static readonly Int32 HeaderSize = Marshal.SizeOf(typeof(RTSSSharedMemoryHeader));

        private static readonly Int32 AppEntrySize = Marshal.SizeOf(typeof(RTSSSharedMemoryAppEntry));

        private readonly Object _sync = new Object();

        /// <summary>
        /// Name of the file mapping to open. Overridable so tests can drive a synthetic block
        /// under a private name; a real RTSS running on the same machine owns the real name and
        /// would otherwise make the tests unrunnable.
        /// </summary>
        private readonly String _sharedMemoryName;

        /// <summary>
        /// Supplies the process id owning the foreground window, or 0 when there is none.
        ///
        /// Overridable for the same reason the mapping name is: the foreground window belongs to
        /// whatever is running on the machine, so the foreground branch of selection is otherwise
        /// unreachable from a test — no synthetic entry can ever match the real foreground pid,
        /// and the rule that decides which application owns the key went unexercised because of it.
        /// Mirrors the process-id seam on <see cref="RTSSAvailabilityMonitorService"/>.
        /// </summary>
        private readonly Func<UInt32> _foregroundProcessIdProvider;

        private readonly Boolean _diagnosticsEnabled;

        private IntPtr _mappingHandle;
        private IntPtr _viewPointer;
        private UInt64 _mappedSize;
        private Boolean _supportsNativeOnePercentLow;
        private Boolean _supportsNativeZeroPointOnePercentLow;
        private String _rtssVersion;
        private Boolean _isDisposed;
        private Byte[] _entryReadBuffer;

        /// <summary>Read outside <see cref="_sync"/> by the surfaces, so accessed volatilely.</summary>
        private RTSSFrameSnapshot _lastSnapshot;

        private Int64 _lastSnapshotReadTicks = Int64.MinValue;

        /// <summary>Last logged selection outcome, so a steady state is not re-logged at 4Hz.</summary>
        private String _lastDiagnosticsOutcome;

        /// <summary>
        /// The FPS source as of the last poll, or null when there was none. Drives the
        /// source-change log, which is deliberately not throttled: transitions are rare and are
        /// exactly what a report of "it fell back to the wordmark" needs to be diagnosed from.
        /// </summary>
        private String _lastSourceDescription;

        private DateTime _lastDiagnosticsUtc;

        public Boolean IsConnected
        {
            get
            {
                lock (this._sync)
                {
                    return this._viewPointer != IntPtr.Zero;
                }
            }
        }

        public RTSSFrameSnapshot LastSnapshot => Volatile.Read(ref this._lastSnapshot);

        public RTSSReaderService()
            : this(null)
        {
        }

        public RTSSReaderService(String sharedMemoryName)
            : this(sharedMemoryName, null)
        {
        }

        public RTSSReaderService(String sharedMemoryName, Func<UInt32> foregroundProcessIdProvider)
            : this(sharedMemoryName, foregroundProcessIdProvider, true)
        {
        }

        public RTSSReaderService(
            String sharedMemoryName,
            Func<UInt32> foregroundProcessIdProvider,
            Boolean diagnosticsEnabled)
        {
            this._sharedMemoryName = String.IsNullOrEmpty(sharedMemoryName)
                ? NativeMethods.RTSSSharedMemoryName
                : sharedMemoryName;

            this._foregroundProcessIdProvider =
                foregroundProcessIdProvider ?? NativeMethods.TryGetForegroundProcessId;
            this._diagnosticsEnabled = diagnosticsEnabled;
        }

        public Boolean TryConnect()
        {
            lock (this._sync)
            {
                return this.TryConnectCore();
            }
        }

        public void Disconnect()
        {
            lock (this._sync)
            {
                this.DisconnectCore();
            }
        }

        public Boolean TryReadHeader(out RTSSSharedMemoryHeader header)
        {
            lock (this._sync)
            {
                return this.TryReadHeaderCore(out header);
            }
        }

        public Boolean TryReadAppEntry(Int32 index, out RTSSSharedMemoryAppEntry entry)
        {
            lock (this._sync)
            {
                entry = default;

                return this.TryReadHeaderCore(out var header) && this.TryReadAppEntryCore(header, index, out entry);
            }
        }

        /// <summary>
        /// Scans the app array for the slot belonging to <paramref name="processId"/> and returns
        /// its live framerate. Returns 0 if the mapping cannot be opened, the header is not a
        /// recognisable RTSS v2 block, the process is not hooked, or RTSS has not yet produced a
        /// sampling interval for it.
        /// </summary>
        public Double GetLiveFps(UInt32 processId)
        {
            if (processId == 0)
            {
                return 0;
            }

            lock (this._sync)
            {
                if (this._isDisposed || !this.TryReadHeaderCore(out var header))
                {
                    return 0;
                }

                var slotCount = (Int32)Math.Min(header.dwAppArrSize, MaxAppArraySize);

                for (var i = 0; i < slotCount; i++)
                {
                    if (!this.TryReadAppEntryCore(header, i, out var entry))
                    {
                        // A failed read means the view went away underneath us; nothing more to scan.
                        return 0;
                    }

                    if (entry.dwProcessID == processId)
                    {
                        return entry.GetCurrentFps();
                    }
                }

                return 0;
            }
        }

        /// <summary>
        /// Every occupied slot in the app array. Returns an empty list rather than null when RTSS
        /// is closed, because callers run on the poll thread and should not branch on null.
        /// </summary>
        public IReadOnlyList<RTSSSharedMemoryAppEntry> ReadActiveAppEntries()
        {
            lock (this._sync)
            {
                return this.ReadActiveAppEntriesCore();
            }
        }

        /// <summary>
        /// Projects the foreground application into a snapshot and publishes it as
        /// <see cref="LastSnapshot"/>. Returns null when nothing is hooked and presenting; the
        /// previous snapshot is cleared in that case so surfaces do not show a stale FPS.
        /// </summary>
        public RTSSFrameSnapshot ReadActiveApplicationSnapshot()
        {
            var nowTicks = Environment.TickCount64;
            var lastReadTicks = Volatile.Read(ref this._lastSnapshotReadTicks);
            if (lastReadTicks != Int64.MinValue && nowTicks - lastReadTicks < SnapshotCacheDurationMs)
            {
                return this.LastSnapshot;
            }

            RTSSFrameSnapshot snapshot = null;

            lock (this._sync)
            {
                if (this.TrySelectForegroundEntry(out var entry, out var entryIndex, out var reason))
                {
                    var native1Low = this._supportsNativeOnePercentLow ? entry.GetOnePercentLowFps() : 0.0;
                    var native01Low = this._supportsNativeZeroPointOnePercentLow ? entry.GetZeroPointOnePercentLowFps() : 0.0;
                    if (this._diagnosticsEnabled)
                    {
                        this.LogRawLowStatistics(entryIndex, entry, native1Low > 0, native1Low, native01Low > 0, native01Low);
                    }
                    snapshot = new RTSSFrameSnapshot
                    {
                        ProcessId = entry.dwProcessID,
                        EntryIndex = entryIndex,
                        ProcessFileName = entry.GetProcessFileName(),
                        InstantaneousFps = entry.GetCurrentFps(),
                        AverageFps = entry.GetAverageFps(),
                        NativeOnePercentLowFps = native1Low,
                        OnePercentLowFps = native1Low,
                        HasNativeOnePercentLow = this._supportsNativeOnePercentLow && native1Low > 0,
                        RtssVersion = this._rtssVersion,
                        RawOnePercentLow = entry.dwStatFramerate1Dot0PercentLow,
                        StatFlags = entry.dwStatFlags,
                        StatFrames = entry.dwStatFrames,
                        StatCount = entry.dwStatCount,
                        NativeZeroPointOnePercentLowFps = native01Low,
                        HasNativeZeroPointOnePercentLow = this._supportsNativeZeroPointOnePercentLow && native01Low > 0,
                        FrameTimeMilliseconds = entry.GetFrameTimeMilliseconds(),
                        FrameSampleTimestamp = entry.dwTime1,
                        CapturedAtUtc = DateTime.UtcNow,
                        SelectionReason = reason,
                    };
                }
            }

            Volatile.Write(ref this._lastSnapshot, snapshot);
            Volatile.Write(ref this._lastSnapshotReadTicks, nowTicks);

            return snapshot;
        }

        public RTSSFrameSnapshot ReadForegroundHookedApplicationSnapshot()
        {
            RTSSFrameSnapshot snapshot = null;
            var cached = this.LastSnapshot;
            var lastReadTicks = Volatile.Read(ref this._lastSnapshotReadTicks);
            if (lastReadTicks != Int64.MinValue &&
                Environment.TickCount64 - lastReadTicks < SnapshotCacheDurationMs)
            {
                return cached?.SelectionReason == ActiveAppSelection.ForegroundWindow
                    ? cached
                    : null;
            }

                    var foregroundProcessId = NativeMethods.TryGetForegroundProcessId();

            lock (this._sync)
            {
                foreach (var pair in this.ReadOccupiedSlotsCore())
                {
                    var entry = pair.Value;

                    if (foregroundProcessId == 0 || entry.dwProcessID != foregroundProcessId)
                    {
                        continue;
                    }

                    var native1Low = this._supportsNativeOnePercentLow ? entry.GetOnePercentLowFps() : 0.0;
                    var native01Low = this._supportsNativeZeroPointOnePercentLow ? entry.GetZeroPointOnePercentLowFps() : 0.0;
                    if (this._diagnosticsEnabled)
                    {
                        this.LogRawLowStatistics(pair.Key, entry, native1Low > 0, native1Low, native01Low > 0, native01Low);
                    }

                    snapshot = new RTSSFrameSnapshot
                    {
                        ProcessId = entry.dwProcessID,
                        EntryIndex = pair.Key,
                        ProcessFileName = entry.GetProcessFileName(),
                        InstantaneousFps = entry.GetCurrentFps(),
                        AverageFps = entry.GetAverageFps(),
                        NativeOnePercentLowFps = native1Low,
                        OnePercentLowFps = native1Low,
                        HasNativeOnePercentLow = this._supportsNativeOnePercentLow && native1Low > 0,
                        RtssVersion = this._rtssVersion,
                        RawOnePercentLow = entry.dwStatFramerate1Dot0PercentLow,
                        StatFlags = entry.dwStatFlags,
                        StatFrames = entry.dwStatFrames,
                        StatCount = entry.dwStatCount,
                        NativeZeroPointOnePercentLowFps = native01Low,
                        HasNativeZeroPointOnePercentLow = this._supportsNativeZeroPointOnePercentLow && native01Low > 0,
                        FrameTimeMilliseconds = entry.GetFrameTimeMilliseconds(),
                        FrameSampleTimestamp = entry.dwTime1,
                        CapturedAtUtc = DateTime.UtcNow,
                        SelectionReason = ActiveAppSelection.ForegroundWindow,
                    };
                    break;
                }
            }

            return snapshot;
        }

        private void LogRawLowStatistics(
            Int32 entryIndex,
            RTSSSharedMemoryAppEntry entry,
            Boolean hasOnePercentLow,
            Double onePercentLow,
            Boolean hasZeroPointOnePercentLow,
            Double zeroPointOnePercentLow)
        {
            PluginLog.InfoThrottled(
                "rtss-native-low-boundary",
                $"{entry.dwProcessID}:{entry.dwStatFlags}:{entry.dwStatFrames}:{entry.dwStatCount}:" +
                    $"{entry.dwStatFramerate1Dot0PercentLow}:{entry.dwStatFramerate0Dot1PercentLow}",
                $"[1% LOW NATIVE] ProcessName={entry.GetProcessFileName()} ProcessId={entry.dwProcessID} " +
                $"EntryIndex={entryIndex} RTSSVersion={this._rtssVersion ?? "unknown"} " +
                $"StatFlags=0x{entry.dwStatFlags:X8} RecordingEnabled={(entry.dwStatFlags & RTSSStatisticsRecordingService.StatFlagRecord) != 0} " +
                $"StatFrames={entry.dwStatFrames} StatCount={entry.dwStatCount} " +
                $"Raw1PercentLow={entry.dwStatFramerate1Dot0PercentLow} " +
                $"Converted1PercentLow={onePercentLow:F3} NativeAvailable={hasOnePercentLow} " +
                $"Raw0.1PercentLow={entry.dwStatFramerate0Dot1PercentLow} " +
                $"Converted0.1PercentLow={zeroPointOnePercentLow:F3} Native0.1Available={hasZeroPointOnePercentLow}");
        }

        /// <summary>
        /// Picks the application RTSS is currently updating.
        ///
        /// "Currently" is the whole problem. RTSS never expires an app entry: when a process stops
        /// presenting, its last closed sampling interval stays in shared memory verbatim, so
        /// <see cref="RTSSSharedMemoryAppEntry.GetInstantaneousFps"/> keeps reporting whatever rate
        /// it last achieved, indefinitely. Selecting on framerate alone therefore picks applications
        /// that stopped rendering hours ago. Freshness — how long ago RTSS last advanced the entry —
        /// is the only field that distinguishes a live application from a fossil.
        ///
        /// Freshness is also the right test for a plugin that is not game-only: it accepts a
        /// compositor, an editing timeline or a viewport render at 2 fps just as readily as a game
        /// at 240, where a framerate floor rejects the first three by design.
        ///
        /// Caller holds <see cref="_sync"/>.
        /// </summary>
        private Boolean TrySelectForegroundEntry(out RTSSSharedMemoryAppEntry entry, out Int32 entryIndex, out ActiveAppSelection reason)
        {
            entry = default;
            entryIndex = -1;
            reason = ActiveAppSelection.None;

            var entries = this.ReadOccupiedSlotsCore();
            var foregroundProcessId = this._foregroundProcessIdProvider();
            var nowTicks = (UInt32)Environment.TickCount;

            if (this.TryReadHeaderCore(out var header) && header.HasForegroundAppTracking && this._mappedSize >= 72 && header.dwAppArrOffset >= 72 && this._viewPointer != IntPtr.Zero)
            {
                var rtssFgAppIndex = (UInt32)Marshal.ReadInt32(this._viewPointer, 64);
                var rtssFgPid = (UInt32)Marshal.ReadInt32(this._viewPointer, 68);
                if (rtssFgAppIndex < header.dwAppArrSize && rtssFgPid != 0)
                {
                    if (this.TryReadAppEntryCore(header, (Int32)rtssFgAppIndex, out var fgCandidate) &&
                        fgCandidate.dwProcessID == rtssFgPid)
                    {
                        var fps = fgCandidate.GetCurrentFps();
                        var ageMs = fgCandidate.GetUpdateAgeMilliseconds(nowTicks);
                        if (ageMs <= EntryFreshnessWindowMs && fps >= MinimumBackgroundFps)
                        {
                            entry = fgCandidate;
                            entryIndex = (Int32)rtssFgAppIndex;
                            reason = ActiveAppSelection.ForegroundWindow;
                            if (this._diagnosticsEnabled)
                            {
                                this.LogSourceChange(true, entry, reason, fps);
                            }
                            return true;
                        }
                    }
                }
            }

            var found = false;
            var bestFps = 0.0;
            var foregroundIsMonitored = false;
            var anyFresh = false;
            for (var i = 0; i < entries.Count; i++)
            {
                var slot = entries[i].Key;
                var candidate = entries[i].Value;
                var fps = candidate.GetCurrentFps();
                var ageMs = candidate.GetUpdateAgeMilliseconds(nowTicks);
                var isFresh = ageMs <= EntryFreshnessWindowMs;
                var isForeground = foregroundProcessId != 0 && candidate.dwProcessID == foregroundProcessId;

                // Eligible = RTSS is still updating it AND it has a framerate worth showing.
                //
                // The foreground window used to be exempt from the rate floor, and that is what
                // tied the key to one application instead of to whatever RTSS is measuring. A
                // hooked foreground window that is merely fresh — Slack, Zoom and a habit tracker
                // are all hooked on the measured machine — claimed the key at 0 fps and, because
                // the foreground branch below outranks every other entry, locked out a game
                // rendering at 144 behind it. The key then went to the wordmark, with RTSS
                // actively measuring a framerate the whole time.
                //
                // Owning the foreground is now a preference among entries that have something to
                // report, not a licence to claim the key with nothing.
                var isEligible = isFresh && fps >= MinimumBackgroundFps;

                foregroundIsMonitored |= isForeground;
                anyFresh |= isFresh;

                if (!isEligible)
                {
                    // 0. Nothing to offer. Fall through so a live source elsewhere can still win —
                    //    this is the "switch away and it follows the real source" behaviour.
                }
                else if (isForeground)
                {
                    // 1. The application the user is looking at, when it is actually presenting:
                    //    the one they mean, whatever its framerate relative to the others.
                    entry = candidate;
                    entryIndex = slot;
                    reason = ActiveAppSelection.ForegroundWindow;
                    bestFps = fps;
                    found = true;
                }
                else if (reason != ActiveAppSelection.ForegroundWindow && (!found || fps > bestFps))
                {
                    // 2. Otherwise the fastest application RTSS is still updating. Among live
                    //    entries framerate is a reasonable tiebreak; among stale ones it is noise.
                    entry = candidate;
                    entryIndex = slot;
                    reason = ActiveAppSelection.HighestFramerate;
                    bestFps = fps;
                    found = true;
                }

            }

            if (this._diagnosticsEnabled)
            {
                this.LogSourceChange(found, entry, reason, bestFps);

                this.LogSelectionDiagnostics(
                    entries, foregroundProcessId, foregroundIsMonitored, anyFresh, found, entry, reason);
            }

            return found;
        }

        /// <summary>Why one candidate was or was not usable as the FPS source.</summary>
        private static String DescribeCandidate(Boolean isFresh, Double fps, UInt32 ageMs)
        {
            if (!isFresh)
            {
                // RTSS leaves a stopped application's last interval in shared memory verbatim, so
                // a stale entry still reports a plausible framerate. Age is what exposes it.
                return ageMs == UInt32.MaxValue
                    ? "rejected:never-stamped-by-rtss"
                    : $"rejected:stale-by-{ageMs}ms";
            }

            return fps >= MinimumBackgroundFps
                ? "usable"
                : $"rejected:below-{MinimumBackgroundFps:F0}fps-floor";
        }

        /// <summary>
        /// Records every change of FPS source, in both directions, unthrottled.
        ///
        /// This is the transition the throttled table cannot show: which application the key was
        /// following, which it follows now, and why it moved. A source going away and a source
        /// being replaced look identical in a periodic dump; they do not look identical here.
        /// Caller holds <see cref="_sync"/>.
        /// </summary>
        private void LogSourceChange(
            Boolean found, RTSSSharedMemoryAppEntry entry, ActiveAppSelection reason, Double fps)
        {
            var current = found ? $"{entry.GetProcessFileName()} (pid {entry.dwProcessID})" : null;

            if (String.Equals(this._lastSourceDescription, current, StringComparison.Ordinal))
            {
                return;
            }

            var previous = this._lastSourceDescription;

            this._lastSourceDescription = current;

            if (current == null)
            {
                PluginLog.Info(
                    $"[FPS SOURCE LOST] was={previous}; no entry RTSS is still updating has a usable "
                    + "framerate, so the key falls back to the static wordmark.");

                return;
            }

            PluginLog.Info(
                previous == null
                    ? $"[FPS SOURCE ACQUIRED] now={current} fps={fps:F1} reason={reason}"
                    : $"[FPS SOURCE CHANGED] was={previous} now={current} fps={fps:F1} reason={reason}");
        }

        /// <summary>
        /// Writes the per-entry table and the reason for a negative result to the plugin log.
        ///
        /// Throttled two ways, because the poll loop runs at 4Hz: the table is written when the
        /// outcome changes, and otherwise once per <see cref="DiagnosticsLogInterval"/>.
        /// Caller holds <see cref="_sync"/>.
        /// </summary>
        private void LogSelectionDiagnostics(
            List<KeyValuePair<Int32, RTSSSharedMemoryAppEntry>> entries,
            UInt32 foregroundProcessId,
            Boolean foregroundIsMonitored,
            Boolean anyFresh,
            Boolean found,
            RTSSSharedMemoryAppEntry entry,
            ActiveAppSelection reason)
        {
            var outcome = found
                ? $"selected:{entry.dwProcessID}:{reason}"
                : $"none:{entries.Count}:{foregroundProcessId}:{foregroundIsMonitored}";

            var now = DateTime.UtcNow;

            if (String.Equals(this._lastDiagnosticsOutcome, outcome, StringComparison.Ordinal) &&
                now - this._lastDiagnosticsUtc < DiagnosticsLogInterval)
            {
                return;
            }

            this._lastDiagnosticsOutcome = outcome;
            this._lastDiagnosticsUtc = now;

            foreach (var pair in entries)
            {
                var slot = pair.Key;
                var candidate = pair.Value;
                var fps = candidate.GetCurrentFps();
                var ageMs = candidate.GetUpdateAgeMilliseconds((UInt32)Environment.TickCount);
                var isFresh = ageMs <= EntryFreshnessWindowMs;
                var isForeground = foregroundProcessId != 0 && candidate.dwProcessID == foregroundProcessId;
                var isEligible = isFresh && fps >= MinimumBackgroundFps;
                var line =
                    $"[RTSS ENTRY] slot={slot} process={candidate.GetProcessFileName()} pid={candidate.dwProcessID} " +
                    $"dwFrames={candidate.dwFrames} frameTime={candidate.GetFrameTimeMilliseconds():F2}ms " +
                    $"calculatedFps={fps:F1} ageMs={ageMs} fresh={isFresh} foregroundPid={foregroundProcessId} " +
                    $"foregroundMatch={isForeground} eligible={isEligible} " +
                    $"verdict={DescribeCandidate(isFresh, fps, ageMs)}";
                PluginLog.Info(line);
            }

            if (found)
            {
                // The plugin owns [ACTIVE RTSS APP SELECTED]: it is the only layer that knows which
                // profile the selection resolved to. The reader explains negative results only.
                return;
            }

            if (foregroundProcessId != 0 && !foregroundIsMonitored)
            {
                PluginLog.Info(
                    $"[FOREGROUND APP NOT MONITORED BY RTSS] process={DescribeProcess(foregroundProcessId)} " +
                    $"pid={foregroundProcessId}");
            }

            PluginLog.Info(
                $"[NO ACTIVE RTSS APP] reason={DescribeNoSelection(entries.Count, foregroundProcessId, foregroundIsMonitored, anyFresh)}");
        }

        private static String DescribeNoSelection(
            Int32 entryCount, UInt32 foregroundProcessId, Boolean foregroundIsMonitored, Boolean anyFresh)
        {
            if (entryCount == 0)
            {
                return "RTSS shared memory holds no application entries; nothing is hooked.";
            }

            if (foregroundIsMonitored)
            {
                return "the foreground application is hooked by RTSS but has not presented a frame in the "
                    + $"last {EntryFreshnessWindowMs}ms, and no other entry is live either.";
            }

            if (anyFresh)
            {
                return $"every application RTSS is still updating is presenting below the "
                    + $"{MinimumBackgroundFps:F0} fps floor, so none of them has a framerate to show.";
            }

            return foregroundProcessId == 0
                ? $"none of the {entryCount} hooked applications presented a frame in the last {EntryFreshnessWindowMs}ms."
                : $"the foreground application is not hooked, and none of the {entryCount} hooked applications "
                    + $"presented a frame in the last {EntryFreshnessWindowMs}ms.";
        }

        /// <summary>Best-effort name for a pid; the pid alone is the fallback.</summary>
        private static String DescribeProcess(UInt32 processId)
        {
            try
            {
                using (var process = System.Diagnostics.Process.GetProcessById((Int32)processId))
                {
                    return process.ProcessName;
                }
            }
            catch (Exception)
            {
                // Exited between the foreground query and here, or not readable from this session.
                return "<unknown>";
            }
        }


        /// <summary>Caller holds <see cref="_sync"/>.</summary>
        private IReadOnlyList<RTSSSharedMemoryAppEntry> ReadActiveAppEntriesCore()
        {
            var occupied = this.ReadOccupiedSlotsCore();
            var entries = new List<RTSSSharedMemoryAppEntry>(occupied.Count);

            for (var i = 0; i < occupied.Count; i++)
            {
                entries.Add(occupied[i].Value);
            }

            return entries;
        }

        /// <summary>
        /// Every occupied slot paired with its index in the shared memory app array. The index is
        /// kept because the array is sparse — RTSS frees slots in place — so the position in a
        /// filtered list is not the slot number, and a diagnostic that conflates the two points at
        /// the wrong record. Caller holds <see cref="_sync"/>.
        /// </summary>
        private List<KeyValuePair<Int32, RTSSSharedMemoryAppEntry>> ReadOccupiedSlotsCore()
        {
            var entries = new List<KeyValuePair<Int32, RTSSSharedMemoryAppEntry>>();

            if (this._isDisposed || !this.TryReadHeaderCore(out var header))
            {
                return entries;
            }

            var slotCount = (Int32)Math.Min(header.dwAppArrSize, MaxAppArraySize);

            for (var i = 0; i < slotCount; i++)
            {
                if (!this.TryReadAppEntryCore(header, i, out var entry))
                {
                    // The view went away underneath us; return what was read rather than throwing.
                    break;
                }

                if (entry.IsActive)
                {
                    entries.Add(new KeyValuePair<Int32, RTSSSharedMemoryAppEntry>(i, entry));
                }
            }

            return entries;
        }

        /// <summary>Opens the mapping if it is not already open. Caller holds <see cref="_sync"/>.</summary>
        private Boolean TryConnectCore()
        {
            if (this._isDisposed)
            {
                return false;
            }

            if (this._viewPointer != IntPtr.Zero)
            {
                return true;
            }

            // RTSS may not be running, in which case the object simply does not exist.
            this._mappingHandle = NativeMethods.OpenFileMapping(
                NativeMethods.FILE_MAP_READ,
                false,
                this._sharedMemoryName);

            if (this._mappingHandle == IntPtr.Zero)
            {
                PluginLog.InfoThrottled(
                    "rtss-map-open",
                    $"[RTSS MAP] OpenFileMapping failed for '{this._sharedMemoryName}' " +
                    $"(error {Marshal.GetLastWin32Error()}); source remains unavailable.");
                return false;
            }

            this._viewPointer = NativeMethods.MapViewOfFile(
                this._mappingHandle,
                NativeMethods.FILE_MAP_READ,
                0,
                0,
                UIntPtr.Zero);

            if (this._viewPointer == IntPtr.Zero)
            {
                PluginLog.InfoThrottled(
                    "rtss-map-view",
                    $"[RTSS MAP] MapViewOfFile failed for '{this._sharedMemoryName}' " +
                    $"(error {Marshal.GetLastWin32Error()}); source remains unavailable.");
                NativeMethods.CloseHandle(this._mappingHandle);
                this._mappingHandle = IntPtr.Zero;
                return false;
            }

            if (NativeMethods.VirtualQuery(
                    this._viewPointer,
                    out var memoryInfo,
                    (UIntPtr)Marshal.SizeOf(typeof(NativeMethods.MemoryBasicInformation))) == UIntPtr.Zero)
            {
                this.DisconnectCore();
                return false;
            }

            this._mappedSize = memoryInfo.RegionSize.ToUInt64();

            return true;
        }

        /// <summary>Caller holds <see cref="_sync"/>.</summary>
        private void DisconnectCore()
        {
            if (this._viewPointer != IntPtr.Zero)
            {
                NativeMethods.UnmapViewOfFile(this._viewPointer);
                this._viewPointer = IntPtr.Zero;
            }

            if (this._mappingHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(this._mappingHandle);
                this._mappingHandle = IntPtr.Zero;
            }

            this._mappedSize = 0;
            this._rtssVersion = null;

            Volatile.Write(ref this._lastSnapshot, null);
            Volatile.Write(ref this._lastSnapshotReadTicks, Int64.MinValue);
        }

        /// <summary>Caller holds <see cref="_sync"/>.</summary>
        private Boolean TryReadHeaderCore(out RTSSSharedMemoryHeader header)
        {
            header = default;

            if (!this.TryConnectCore())
            {
                return false;
            }

            try
            {
                header = (RTSSSharedMemoryHeader)Marshal.PtrToStructure(
                    this._viewPointer,
                    typeof(RTSSSharedMemoryHeader));
            }
            catch (Exception)
            {
                // The view is stale (RTSS restarted); drop it so the next call re-opens.
                this.DisconnectCore();
                return false;
            }

            if (!header.IsValid ||
                header.IsDeallocated ||
                !header.IsAppArrayRangeSafe(HeaderSize, RTSSSharedMemoryAppEntry.MinimumAppEntrySize, MaxAppArraySize, this._mappedSize))
            {
                this.DisconnectCore();
                return false;
            }

            this._supportsNativeOnePercentLow = header.SupportsNativeOnePercentLow;
            this._supportsNativeZeroPointOnePercentLow = header.SupportsNativeZeroPointOnePercentLow;
            this._rtssVersion = $"{header.MajorVersion}.{header.MinorVersion}";

            return true;
        }

        /// <summary>Caller holds <see cref="_sync"/> and has validated <paramref name="header"/>.</summary>
        private Boolean TryReadAppEntryCore(RTSSSharedMemoryHeader header, Int32 index, out RTSSSharedMemoryAppEntry entry)
        {
            entry = default;

            if (index < 0 || (UInt32)index >= header.dwAppArrSize || this._viewPointer == IntPtr.Zero)
            {
                return false;
            }

            if (header.dwAppEntrySize < (UInt32)RTSSSharedMemoryAppEntry.MinimumAppEntrySize)
            {
                return false;
            }

            // Stride comes from the header, not from the managed struct: newer RTSS builds
            // append fields, so the record can be larger than what this plugin projects.
            // Reject 64-bit integer overflow and out-of-bounds calculations safely.
            var entryOffset = (UInt64)header.dwAppArrOffset + ((UInt64)(UInt32)index * (UInt64)header.dwAppEntrySize);
            var entryEnd = entryOffset + (UInt64)header.dwAppEntrySize;

            if (entryOffset < (UInt64)header.dwAppArrOffset ||
                entryEnd < entryOffset ||
                entryEnd > this._mappedSize ||
                entryEnd > (UInt64)Int32.MaxValue)
            {
                return false;
            }

            try
            {
                var entryPtr = IntPtr.Add(this._viewPointer, (Int32)entryOffset);

                if (header.dwAppEntrySize >= (UInt32)AppEntrySize)
                {
                    entry = (RTSSSharedMemoryAppEntry)Marshal.PtrToStructure(
                        entryPtr,
                        typeof(RTSSSharedMemoryAppEntry));
                }
                else
                {
                    // Mapped entry is smaller than the managed struct (e.g. older RTSS v2.12).
                    // NEVER use blind Marshal.PtrToStructure directly on native memory here.
                    // Copy only the advertised dwAppEntrySize bytes into a zeroed buffer.
                    if (this._entryReadBuffer == null || this._entryReadBuffer.Length != AppEntrySize)
                    {
                        this._entryReadBuffer = new Byte[AppEntrySize];
                    }
                    Array.Clear(this._entryReadBuffer, 0, AppEntrySize);

                    Marshal.Copy(entryPtr, this._entryReadBuffer, 0, (Int32)header.dwAppEntrySize);

                    var handle = GCHandle.Alloc(this._entryReadBuffer, GCHandleType.Pinned);
                    try
                    {
                        entry = (RTSSSharedMemoryAppEntry)Marshal.PtrToStructure(
                            handle.AddrOfPinnedObject(),
                            typeof(RTSSSharedMemoryAppEntry));
                    }
                    finally
                    {
                        handle.Free();
                    }
                }

                // Explicitly read native 1% Low and 0.1% Low from verified offsets
                if (header.SupportsNativeOnePercentLow &&
                    header.dwAppEntrySize >= (UInt32)RTSSSharedMemoryAppEntry.NativeOnePercentLowRequiredEntrySize &&
                    entryOffset + (UInt64)RTSSSharedMemoryAppEntry.NativeOnePercentLowRequiredEntrySize <= this._mappedSize)
                {
                    entry.dwStatFramerate1Dot0PercentLow = (UInt32)Marshal.ReadInt32(entryPtr, RTSSSharedMemoryAppEntry.StatFramerate1Dot0PercentLowOffset);
                }
                else
                {
                    entry.dwStatFramerate1Dot0PercentLow = 0;
                }

                if (header.SupportsNativeZeroPointOnePercentLow &&
                    header.dwAppEntrySize >= (UInt32)RTSSSharedMemoryAppEntry.NativeZeroPointOnePercentLowRequiredEntrySize &&
                    entryOffset + (UInt64)RTSSSharedMemoryAppEntry.NativeZeroPointOnePercentLowRequiredEntrySize <= this._mappedSize)
                {
                    entry.dwStatFramerate0Dot1PercentLow = (UInt32)Marshal.ReadInt32(entryPtr, RTSSSharedMemoryAppEntry.StatFramerate0Dot1PercentLowOffset);
                }
                else
                {
                    entry.dwStatFramerate0Dot1PercentLow = 0;
                }

                if (header.SupportsPercentileBufPos &&
                    header.dwAppEntrySize >= (UInt32)RTSSSharedMemoryAppEntry.NativePercentileBufPosRequiredEntrySize &&
                    entryOffset + (UInt64)RTSSSharedMemoryAppEntry.NativePercentileBufPosRequiredEntrySize <= this._mappedSize)
                {
                    entry.dw1Dot0PercentLowBufPos = (UInt32)Marshal.ReadInt32(entryPtr, RTSSSharedMemoryAppEntry.Stat1Dot0PercentLowBufPosOffset);
                    entry.dw0Dot1PercentLowBufPos = (UInt32)Marshal.ReadInt32(entryPtr, RTSSSharedMemoryAppEntry.Stat0Dot1PercentLowBufPosOffset);
                }
                else
                {
                    entry.dw1Dot0PercentLowBufPos = 0;
                    entry.dw0Dot1PercentLowBufPos = 0;
                }

                return true;
            }
            catch (Exception)
            {
                this.DisconnectCore();
                return false;
            }
        }

        public void Dispose()
        {
            lock (this._sync)
            {
                if (this._isDisposed)
                {
                    return;
                }

                this._isDisposed = true;
                this.DisconnectCore();
            }
        }
    }
}
