namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Runtime.InteropServices;

    public interface ICpuUsageSource
    {
        Boolean IsAvailable { get; }

        Boolean HasCpuUsageReading { get; }

        Double CpuUsagePercent { get; }
    }

    public sealed class CpuUsageSourceService : ICpuUsageSource, IDisposable
    {
        public const Int64 CacheDurationMs = 1000;

        private readonly Func<CpuTimeSnapshot> _snapshotReader;
        private readonly Func<Int64> _tickProvider;
        private readonly Object _sync = new Object();

        private Int64 _lastReadTicks = Int64.MinValue;
        private Boolean _hasReading;
        private Double _usagePercent;
        private Boolean _isDisposed;
        private Boolean _hasPreviousSnapshot;
        private CpuTimeSnapshot _previousSnapshot;

        public CpuUsageSourceService()
            : this(ReadCpuTimes, () => Environment.TickCount64)
        {
        }

        public CpuUsageSourceService(Func<CpuTimeSnapshot> snapshotReader, Func<Int64> tickProvider)
        {
            this._snapshotReader = snapshotReader ?? throw new ArgumentNullException(nameof(snapshotReader));
            this._tickProvider = tickProvider ?? throw new ArgumentNullException(nameof(tickProvider));

            try
            {
                var initial = this._snapshotReader();
                if (initial.KernelTime > 0 || initial.UserTime > 0)
                {
                    this._previousSnapshot = initial;
                    this._hasPreviousSnapshot = true;
                    this._lastReadTicks = this._tickProvider();
                }
            }
            catch
            {
                // Ignored if reader fails at construction time
            }
        }

        public Boolean IsAvailable
        {
            get
            {
                this.EnsureFresh();
                return this._hasReading;
            }
        }

        public Boolean HasCpuUsageReading
        {
            get
            {
                this.EnsureFresh();
                return this._hasReading;
            }
        }

        public Double CpuUsagePercent
        {
            get
            {
                this.EnsureFresh();
                return this._hasReading ? this._usagePercent : 0;
            }
        }

        public void Invalidate()
        {
            lock (this._sync)
            {
                this._lastReadTicks = Int64.MinValue;
            }
        }

        public void Dispose() => this._isDisposed = true;

        private void EnsureFresh()
        {
            lock (this._sync)
            {
                var now = this._tickProvider();
                if (!this._isDisposed && this._lastReadTicks != Int64.MinValue &&
                    now - this._lastReadTicks < CacheDurationMs)
                {
                    return;
                }

                this._lastReadTicks = now;

                if (this._isDisposed)
                {
                    this._hasReading = false;
                    this._usagePercent = 0;
                    return;
                }

                CpuTimeSnapshot snapshot;
                try
                {
                    snapshot = this._snapshotReader();
                }
                catch
                {
                    this._hasPreviousSnapshot = false;
                    return;
                }

                if (snapshot.KernelTime == 0 && snapshot.UserTime == 0)
                {
                    this._hasPreviousSnapshot = false;
                    return;
                }

                if (!this._hasPreviousSnapshot)
                {
                    this._previousSnapshot = snapshot;
                    this._hasPreviousSnapshot = true;
                    return;
                }

                if (snapshot.IdleTime < this._previousSnapshot.IdleTime ||
                    snapshot.KernelTime < this._previousSnapshot.KernelTime ||
                    snapshot.UserTime < this._previousSnapshot.UserTime)
                {
                    this._hasPreviousSnapshot = false;
                    return;
                }

                var idleDelta = snapshot.IdleTime - this._previousSnapshot.IdleTime;
                var kernelDelta = snapshot.KernelTime - this._previousSnapshot.KernelTime;
                var userDelta = snapshot.UserTime - this._previousSnapshot.UserTime;
                var totalDelta = kernelDelta + userDelta;

                this._previousSnapshot = snapshot;

                if (totalDelta == 0 || idleDelta > totalDelta)
                {
                    return;
                }

                var workDelta = totalDelta - idleDelta;
                var rawPercent = 100.0 * workDelta / (Double)totalDelta;

                if (Double.IsNaN(rawPercent) || Double.IsInfinity(rawPercent))
                {
                    return;
                }

                this._usagePercent = Math.Clamp(rawPercent, 0.0, 100.0);
                this._hasReading = true;
            }
        }

        private static CpuTimeSnapshot ReadCpuTimes()
        {
            if (!NativeMethods.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
            {
                return default;
            }

            return new CpuTimeSnapshot(
                FiletimeToTicks(idleTime),
                FiletimeToTicks(kernelTime),
                FiletimeToTicks(userTime));
        }

        private static UInt64 FiletimeToTicks(UInt64 filetime)
        {
            return filetime;
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern Boolean GetSystemTimes(
                out UInt64 lpIdleTime,
                out UInt64 lpKernelTime,
                out UInt64 lpUserTime);
        }
    }

    public readonly struct CpuTimeSnapshot
    {
        public CpuTimeSnapshot(UInt64 idleTime, UInt64 kernelTime, UInt64 userTime)
        {
            this.IdleTime = idleTime;
            this.KernelTime = kernelTime;
            this.UserTime = userTime;
        }

        public UInt64 IdleTime { get; }

        public UInt64 KernelTime { get; }

        public UInt64 UserTime { get; }
    }
}
