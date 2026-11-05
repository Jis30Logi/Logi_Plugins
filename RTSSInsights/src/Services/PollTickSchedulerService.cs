namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Threading;

    using Loupedeck.RTSSPerformancePlugin.Helpers;

    public sealed class PollTickEventArgs : EventArgs
    {
        public Int64 TickIndex { get; set; }

        public DateTime TimestampUtc { get; set; }
    }

    public interface IPollTickSchedulerService : IDisposable
    {
        Boolean IsRunning { get; }

        /// <summary>Interval used while a hooked application is presenting frames.</summary>
        Int32 ActiveIntervalMs { get; set; }

        /// <summary>Slower interval used when RTSS is unavailable or idle.</summary>
        Int32 IdleIntervalMs { get; set; }

        event EventHandler<PollTickEventArgs> Tick;

        void Start();

        void Stop();

        void SetIdle(Boolean isIdle);

        void ForceTick();
    }

    /// <summary>
    /// Drives the polling cadence for the reader, backing off to a slower interval when idle.
    /// </summary>
    public sealed class PollTickSchedulerService : IPollTickSchedulerService
    {
        public const Int32 DefaultActiveIntervalMs = 250;

        public const Int32 DefaultIdleIntervalMs = 2000;

        /// <summary>Floor on any interval; a runaway timer would peg a core.</summary>
        public const Int32 MinimumIntervalMs = 10;

        private readonly Object _sync = new Object();

        private readonly Timer _timer;

        private Int64 _tickIndex;

        private Int32 _activeIntervalMs = DefaultActiveIntervalMs;

        private Int32 _idleIntervalMs = DefaultIdleIntervalMs;

        private Boolean _isRunning;

        private Boolean _isIdle;

        private Boolean _isDisposed;

        public Boolean IsRunning => Volatile.Read(ref this._isRunning);

        public Int32 ActiveIntervalMs
        {
            get => Volatile.Read(ref this._activeIntervalMs);

            set
            {
                Volatile.Write(ref this._activeIntervalMs, Math.Max(MinimumIntervalMs, value));
                this.ApplyInterval();
            }
        }

        public Int32 IdleIntervalMs
        {
            get => Volatile.Read(ref this._idleIntervalMs);

            set
            {
                Volatile.Write(ref this._idleIntervalMs, Math.Max(MinimumIntervalMs, value));
                this.ApplyInterval();
            }
        }

        public event EventHandler<PollTickEventArgs> Tick;

        public PollTickSchedulerService()
        {
            // Created stopped: Timeout.Infinite until Start applies a real period.
            this._timer = new Timer(this.OnTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Interval currently in force, i.e. the idle one while idle.</summary>
        public Int32 CurrentIntervalMs =>
            Volatile.Read(ref this._isIdle) ? this.IdleIntervalMs : this.ActiveIntervalMs;

        public void Start()
        {
            lock (this._sync)
            {
                if (this._isDisposed || this._isRunning)
                {
                    return;
                }

                Volatile.Write(ref this._isRunning, true);
            }

            this.ApplyInterval();
        }

        public void Stop()
        {
            lock (this._sync)
            {
                if (!this._isRunning)
                {
                    return;
                }

                Volatile.Write(ref this._isRunning, false);
                this.ChangeTimer(Timeout.Infinite);
            }
        }

        /// <summary>
        /// Switches cadence. Changing this while running restarts the countdown from now, which
        /// is the point: going idle should not still fire one fast tick that was already pending.
        /// </summary>
        public void SetIdle(Boolean isIdle)
        {
            if (Volatile.Read(ref this._isIdle) == isIdle)
            {
                return;
            }

            Volatile.Write(ref this._isIdle, isIdle);
            this.ApplyInterval();
        }

        /// <summary>Fires a tick immediately, off-schedule, without disturbing the cadence.</summary>
        public void ForceTick() => this.OnTimerElapsed(null);

        /// <summary>
        /// Subscriber exceptions are swallowed into the log: one bad handler must not tear down
        /// the timer and silently stop every surface from updating. Handlers are invoked one at a
        /// time rather than through the multicast delegate, because a plain Invoke abandons the
        /// rest of the chain the moment any handler throws.
        /// </summary>
        private void OnTimerElapsed(Object state)
        {
            if (Volatile.Read(ref this._isDisposed))
            {
                return;
            }

            var handlers = this.Tick;

            if (handlers == null)
            {
                Interlocked.Increment(ref this._tickIndex);
                return;
            }

            var args = new PollTickEventArgs
            {
                TickIndex = Interlocked.Increment(ref this._tickIndex),
                TimestampUtc = DateTime.UtcNow,
            };

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<PollTickEventArgs>)handler)(this, args);
                }
                catch (Exception exception)
                {
                    PluginLog.Error(exception, $"Poll tick {args.TickIndex} subscriber threw; continuing.");
                }
            }
        }

        private void ApplyInterval()
        {
            lock (this._sync)
            {
                if (this._isDisposed || !this._isRunning)
                {
                    return;
                }

                this.ChangeTimer(this.CurrentIntervalMs);
            }
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
                // Raced with Dispose; the scheduler is going away anyway.
            }
        }

        /// <summary>Idempotent: unload calls this and it must never throw on a second pass.</summary>
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
                Volatile.Write(ref this._isRunning, false);
                timer = this._timer;
            }

            // Outside the lock: wait (bounded) for a tick already in flight so the services it
            // reads are not disposed underneath it.
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
                PluginLog.Warning(exception, "Poll tick scheduler timer failed to dispose.");
            }
        }
    }
}
