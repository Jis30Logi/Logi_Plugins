namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Runtime.InteropServices;

    public interface IRamUsageSource
    {
        Boolean IsAvailable { get; }

        Boolean HasRamUsageReading { get; }

        Double RamUsagePercent { get; }
    }

    public sealed class RamUsageSourceService : IRamUsageSource, IDisposable
    {
        public const Int64 CacheDurationMs = 250;

        private readonly Func<RamMemorySnapshot> _snapshotReader;
        private readonly Func<Int64> _tickProvider;
        private readonly Object _sync = new Object();

        private Int64 _lastReadTicks = Int64.MinValue;
        private Boolean _hasReading;
        private Double _usagePercent;
        private Boolean _isDisposed;

        public RamUsageSourceService()
            : this(ReadMemory, () => Environment.TickCount64)
        {
        }

        public RamUsageSourceService(Func<RamMemorySnapshot> snapshotReader, Func<Int64> tickProvider)
        {
            this._snapshotReader = snapshotReader ?? throw new ArgumentNullException(nameof(snapshotReader));
            this._tickProvider = tickProvider ?? throw new ArgumentNullException(nameof(tickProvider));
        }

        public Boolean IsAvailable
        {
            get
            {
                this.EnsureFresh();
                return this._hasReading;
            }
        }

        public Boolean HasRamUsageReading
        {
            get
            {
                this.EnsureFresh();
                return this._hasReading;
            }
        }

        public Double RamUsagePercent
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
                this._hasReading = false;
                this._usagePercent = 0;

                if (this._isDisposed)
                {
                    return;
                }

                RamMemorySnapshot snapshot;
                try
                {
                    snapshot = this._snapshotReader();
                }
                catch
                {
                    return;
                }

                if (snapshot.TotalPhysicalBytes == 0 ||
                    snapshot.AvailablePhysicalBytes > snapshot.TotalPhysicalBytes)
                {
                    return;
                }

                var usagePercent = 100.0 *
                    (snapshot.TotalPhysicalBytes - snapshot.AvailablePhysicalBytes) /
                    snapshot.TotalPhysicalBytes;

                if (Double.IsNaN(usagePercent) || Double.IsInfinity(usagePercent) ||
                    usagePercent < 0 || usagePercent > 100)
                {
                    return;
                }

                this._usagePercent = usagePercent;
                this._hasReading = true;
            }
        }

        private static RamMemorySnapshot ReadMemory()
        {
            var status = new MemoryStatusEx { Length = (UInt32)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            return NativeMethods.GlobalMemoryStatusEx(ref status)
                ? new RamMemorySnapshot(status.TotalPhysicalBytes, status.AvailablePhysicalBytes)
                : default(RamMemorySnapshot);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public UInt32 Length;
            public UInt32 MemoryLoad;
            public UInt64 TotalPhysicalBytes;
            public UInt64 AvailablePhysicalBytes;
            public UInt64 TotalPageFileBytes;
            public UInt64 AvailablePageFileBytes;
            public UInt64 TotalVirtualBytes;
            public UInt64 AvailableVirtualBytes;
            public UInt64 AvailableExtendedVirtualBytes;
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern Boolean GlobalMemoryStatusEx(ref MemoryStatusEx status);
        }
    }

    public readonly struct RamMemorySnapshot
    {
        public RamMemorySnapshot(UInt64 totalPhysicalBytes, UInt64 availablePhysicalBytes)
        {
            this.TotalPhysicalBytes = totalPhysicalBytes;
            this.AvailablePhysicalBytes = availablePhysicalBytes;
        }

        public UInt64 TotalPhysicalBytes { get; }

        public UInt64 AvailablePhysicalBytes { get; }
    }
}
