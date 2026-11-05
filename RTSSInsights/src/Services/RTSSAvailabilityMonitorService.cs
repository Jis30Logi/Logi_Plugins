namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Diagnostics;
    using System.Threading;

    using Loupedeck.RTSSPerformancePlugin.Helpers;

    public enum RTSSAvailability
    {
        Unknown = 0,

        /// <summary>RTSS process is not running.</summary>
        NotRunning = 1,

        /// <summary>RTSS is running but the shared memory block could not be opened.</summary>
        SharedMemoryUnavailable = 2,

        /// <summary>Shared memory opened but reports an unsupported layout version.</summary>
        UnsupportedVersion = 3,

        /// <summary>RTSS is running and readable.</summary>
        Available = 4,

        /// <summary>RTSS restarted and the old shared-memory view must be discarded.</summary>
        Reconnected = 5,
    }

    public interface IRTSSAvailabilityMonitorService : IDisposable
    {
        RTSSAvailability Status { get; }

        Boolean IsAvailable { get; }

        event EventHandler<RTSSAvailability> StatusChanged;

        void Start();

        void Stop();

        RTSSAvailability Probe();
    }

    /// <summary>
    /// Tracks whether RTSS is running and its shared memory is readable, raising change notifications.
    /// </summary>
    public sealed class RTSSAvailabilityMonitorService : IRTSSAvailabilityMonitorService
    {
        public const String RTSSProcessName = "RTSS";

        /// <summary>How often the monitor re-probes. Deliberately slow: it spawns a process scan.</summary>
        public const Int32 ProbeIntervalMs = 2000;

        private readonly Object _sync = new Object();

        private readonly IRTSSReaderService _reader;

        private readonly Timer _timer;

        private RTSSAvailability _status = RTSSAvailability.Unknown;

        /// <summary>Process id RTSS had at the last probe; 0 when it was not running.</summary>
        private Int32 _lastProcessId;

        private readonly Func<Int32> _processIdProvider;

        private Boolean _isRunning;

        private Boolean _isDisposed;

        private Boolean _restartDetected;

        public RTSSAvailability Status
        {
            get
            {
                lock (this._sync)
                {
                    return this._status;
                }
            }
        }

        public Boolean IsAvailable => this.Status == RTSSAvailability.Available;

        public event EventHandler<RTSSAvailability> StatusChanged;

        public RTSSAvailabilityMonitorService(IRTSSReaderService reader)
            : this(reader, null)
        {
        }

        /// <summary>
        /// <paramref name="processIdProvider"/> supplies the running RTSS process id, or 0 when it
        /// is not running. Overridable so tests are not at the mercy of whether RTSS happens to be
        /// running on the machine — the same seam the reader has for its mapping name.
        /// </summary>
        public RTSSAvailabilityMonitorService(IRTSSReaderService reader, Func<Int32> processIdProvider)
        {
            this._reader = reader;
            this._processIdProvider = processIdProvider ?? GetRunningRtssProcessId;
            this._timer = new Timer(this.OnTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            lock (this._sync)
            {
                if (this._isDisposed || this._isRunning)
                {
                    return;
                }

                this._isRunning = true;
                this.ChangeTimer(ProbeIntervalMs);
            }

            // Probe once up front so subscribers do not sit on Unknown for a full interval.
            this.Probe();
        }

        public void Stop()
        {
            lock (this._sync)
            {
                if (!this._isRunning)
                {
                    return;
                }

                this._isRunning = false;
                this.ChangeTimer(Timeout.Infinite);
            }
        }

        /// <summary>
        /// Classifies availability in one pass and publishes the result. Never throws: it runs on
        /// a timer thread and off the plugin load path.
        /// </summary>
        public RTSSAvailability Probe()
        {
            this._restartDetected = false;
            var status = this.ProbeCore();

            if (this._restartDetected && status == RTSSAvailability.Available)
            {
                this.SetStatus(RTSSAvailability.Reconnected);
            }

            this.SetStatus(status);

            return status;
        }

        private RTSSAvailability ProbeCore()
        {
            if (this._reader == null)
            {
                return RTSSAvailability.Unknown;
            }

            try
            {
                // Liveness is decided by the process, not by the mapping, and it has to be checked
                // FIRST.
                //
                // Holding a mapped view keeps the shared memory section alive after RTSS exits, so
                // TryConnect keeps succeeding and the header keeps validating against a frozen
                // snapshot of the dead instance. Left alone the plugin reports stale frame data
                // for ever and never picks up a restarted RTSS. Verified by killing RTSS on-device.
                var processId = this.TryGetProcessId();

                if (processId == 0)
                {
                    this.DropStaleView("RTSS is no longer running");
                    return RTSSAvailability.NotRunning;
                }

                // A restart between two probes leaves us mapped to the previous instance, which
                // looks identical from here except for the process identity.
                if (this._lastProcessId != 0 && this._lastProcessId != processId)
                {
                    this._restartDetected = true;
                    this.DropStaleView($"RTSS restarted (pid {this._lastProcessId} -> {processId})");
                }

                this._lastProcessId = processId;

                if (!this._reader.TryConnect())
                {
                    // The process is up but its block is not open to us; worth surfacing as a fault.
                    return RTSSAvailability.SharedMemoryUnavailable;
                }

                // Mapping opened, so the block exists; a failed header read means the layout is
                // not the v2 shape this plugin projects.
                return this._reader.TryReadHeader(out _)
                    ? RTSSAvailability.Available
                    : RTSSAvailability.UnsupportedVersion;
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, "RTSS availability probe failed.");
                return RTSSAvailability.Unknown;
            }
        }

        private Boolean IsProcessRunning() => this.TryGetProcessId() != 0;

        /// <summary>
        /// Process id of the running RTSS, or 0 when it is not running. The id is what lets a
        /// restart be told apart from a process that never went away.
        /// </summary>
        private Int32 TryGetProcessId() => this._processIdProvider();

        /// <summary>Real process lookup; the default provider.</summary>
        private static Int32 GetRunningRtssProcessId()
        {
            try
            {
                var processes = Process.GetProcessesByName(RTSSProcessName);
                var id = 0;

                foreach (var process in processes)
                {
                    if (id == 0)
                    {
                        id = process.Id;
                    }

                    process.Dispose();
                }

                return id;
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, "Could not enumerate processes while probing for RTSS.");
                return 0;
            }
        }

        /// <summary>
        /// Releases the mapped view so the next connect opens whatever RTSS publishes now. Without
        /// this the view outlives the process that created it and freezes the plugin on dead data.
        /// </summary>
        private void DropStaleView(String reason)
        {
            if (this._lastProcessId == 0 && !this._reader.IsConnected)
            {
                return;
            }

            this._lastProcessId = 0;

            if (!this._reader.IsConnected)
            {
                return;
            }

            PluginLog.Info($"[RTSS DISCONNECTED/RECONNECTING] Releasing the shared memory view: {reason}.");
            this._reader.Disconnect();
        }

        private void SetStatus(RTSSAvailability status)
        {
            lock (this._sync)
            {
                if (this._status == status)
                {
                    return;
                }

                this._status = status;
            }

            // Raised outside the lock: subscribers re-enter this service via Status.
            try
            {
                this.StatusChanged?.Invoke(this, status);
            }
            catch (Exception exception)
            {
                PluginLog.Error(exception, $"RTSS availability subscriber threw for status {status}.");
            }
        }

        private void OnTimerElapsed(Object state)
        {
            if (Volatile.Read(ref this._isDisposed))
            {
                return;
            }

            this.Probe();
        }

        /// <summary>Caller holds <see cref="_sync"/>.</summary>
        private void ChangeTimer(Int32 intervalMs)
        {
            try
            {
                this._timer.Change(intervalMs, intervalMs);
            }
            catch (ObjectDisposedException)
            {
                // Raced with Dispose; the monitor is going away anyway.
            }
        }

        /// <summary>Idempotent, for the same reason the scheduler's is.</summary>
        public void Dispose()
        {
            Timer timer;

            lock (this._sync)
            {
                if (this._isDisposed)
                {
                    return;
                }

                this._isDisposed = true;
                this._isRunning = false;
                timer = this._timer;
            }

            // Outside the lock: wait (bounded) for a probe already in flight.
            try
            {
                using (var callbacksDone = new ManualResetEvent(false))
                {
                    if (timer.Dispose(callbacksDone))
                    {
                        callbacksDone.WaitOne(1000);
                    }
                }
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, "RTSS availability monitor timer failed to dispose.");
            }
        }
    }
}
