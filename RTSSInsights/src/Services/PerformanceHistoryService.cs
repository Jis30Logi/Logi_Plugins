namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public sealed class PerformanceSnapshot
    {
        public PerformanceSnapshot(
            DateTime timestampUtc,
            Double? fps = null,
            Double? frameTimeMilliseconds = null,
            Double? onePercentLowFps = null,
            Double? zeroPointOnePercentLowFps = null,
            Double? cpuUsagePercent = null,
            Double? gpuUsagePercent = null,
            Double? ramUsagePercent = null,
            Double? vramUsagePercent = null,
            Double? cpuTemperatureCelsius = null,
            Double? gpuTemperatureCelsius = null,
            Double? cpuClockMegahertz = null,
            Double? gpuClockMegahertz = null)
        {
            if (timestampUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("Snapshot timestamps must be UTC.", nameof(timestampUtc));
            }

            this.TimestampUtc = timestampUtc;
            this.Fps = Normalize(fps);
            this.FrameTimeMilliseconds = Normalize(frameTimeMilliseconds);
            this.OnePercentLowFps = Normalize(onePercentLowFps);
            this.ZeroPointOnePercentLowFps = Normalize(zeroPointOnePercentLowFps);
            this.CpuUsagePercent = NormalizePercent(cpuUsagePercent);
            this.GpuUsagePercent = NormalizePercent(gpuUsagePercent);
            this.RamUsagePercent = NormalizePercent(ramUsagePercent);
            this.VramUsagePercent = NormalizePercent(vramUsagePercent);
            this.CpuTemperatureCelsius = Normalize(cpuTemperatureCelsius);
            this.GpuTemperatureCelsius = Normalize(gpuTemperatureCelsius);
            this.CpuClockMegahertz = NormalizeNonNegative(cpuClockMegahertz);
            this.GpuClockMegahertz = NormalizeNonNegative(gpuClockMegahertz);
        }

        public DateTime TimestampUtc { get; }

        public Double? Fps { get; }

        public Double? FrameTimeMilliseconds { get; }

        public Double? OnePercentLowFps { get; }

        public Double? ZeroPointOnePercentLowFps { get; }

        public Double? CpuUsagePercent { get; }

        public Double? GpuUsagePercent { get; }

        public Double? RamUsagePercent { get; }

        public Double? VramUsagePercent { get; }

        public Double? CpuTemperatureCelsius { get; }

        public Double? GpuTemperatureCelsius { get; }

        public Double? CpuClockMegahertz { get; }

        public Double? GpuClockMegahertz { get; }

        private static Double? Normalize(Double? value) =>
            value.HasValue && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value) && value.Value >= 0
                ? value
                : null;

        private static Double? NormalizePercent(Double? value) =>
            Normalize(value) is Double normalized && normalized <= 100 ? normalized : null;

        private static Double? NormalizeNonNegative(Double? value) => Normalize(value);
    }

    public sealed class PerformanceHistoryService : IDisposable
    {
        public const Int32 DefaultCapacity = 240;

        public static readonly TimeSpan DefaultRetention = TimeSpan.FromMinutes(10);

        private readonly Object _sync = new Object();
        private readonly LinkedList<PerformanceSnapshot> _snapshots = new LinkedList<PerformanceSnapshot>();
        private readonly Int32 _capacity;
        private readonly TimeSpan _retention;
        private Boolean _isDisposed;

        public PerformanceHistoryService(Int32 capacity = DefaultCapacity, TimeSpan? retention = null)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            this._capacity = capacity;
            this._retention = retention ?? DefaultRetention;
            if (this._retention <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(retention));
            }
        }

        public Int32 Capacity => this._capacity;

        public TimeSpan Retention => this._retention;

        public Int32 Count
        {
            get
            {
                lock (this._sync)
                {
                    return this._snapshots.Count;
                }
            }
        }

        public Boolean AddSnapshot(PerformanceSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return false;
            }

            lock (this._sync)
            {
                if (this._isDisposed ||
                    (this._snapshots.Last != null && snapshot.TimestampUtc <= this._snapshots.Last.Value.TimestampUtc))
                {
                    return false;
                }

                this._snapshots.AddLast(snapshot);
                this.RemoveExpired(snapshot.TimestampUtc);

                while (this._snapshots.Count > this._capacity)
                {
                    this._snapshots.RemoveFirst();
                }

                return true;
            }
        }

        public IReadOnlyList<PerformanceSnapshot> GetSnapshots()
        {
            lock (this._sync)
            {
                return this._snapshots.ToArray();
            }
        }

        public void Reset()
        {
            lock (this._sync)
            {
                this._snapshots.Clear();
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
                this._snapshots.Clear();
            }
        }

        private void RemoveExpired(DateTime newestTimestampUtc)
        {
            var cutoff = newestTimestampUtc - this._retention;
            while (this._snapshots.First != null && this._snapshots.First.Value.TimestampUtc < cutoff)
            {
                this._snapshots.RemoveFirst();
            }
        }
    }
}
