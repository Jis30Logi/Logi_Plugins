namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;

    public interface IGpuUsageSource
    {
        Boolean IsAvailable { get; }

        Boolean HasGpuUsageReading { get; }

        Double GpuUsagePercent { get; }
    }

    public readonly struct GpuEngineSample
    {
        public GpuEngineSample(String instanceName, Double value)
        {
            this.InstanceName = instanceName;
            this.Value = value;
        }

        public String InstanceName { get; }

        public Double Value { get; }
    }

    public interface IGpuTelemetryReader : IDisposable
    {
        Boolean IsAvailable { get; }

        Boolean TryReadSamples(out List<GpuEngineSample> samples);
    }

    public sealed class GpuUsageSourceService : IGpuUsageSource, IDisposable
    {
        public const Int64 CacheDurationMs = 1000;

        private readonly IGpuTelemetryReader _telemetryReader;
        private readonly Func<Int64> _tickProvider;
        private readonly Object _sync = new Object();

        private Int64 _lastReadTicks = Int64.MinValue;
        private Boolean _hasReading;
        private Double _usagePercent;
        private Boolean _isDisposed;

        public GpuUsageSourceService()
            : this(new PdhGpuTelemetryReader(), () => Environment.TickCount64)
        {
        }

        public GpuUsageSourceService(IGpuTelemetryReader telemetryReader, Func<Int64> tickProvider)
        {
            this._telemetryReader = telemetryReader ?? throw new ArgumentNullException(nameof(telemetryReader));
            this._tickProvider = tickProvider ?? throw new ArgumentNullException(nameof(tickProvider));
        }

        public Boolean IsAvailable
        {
            get
            {
                this.EnsureFresh();
                return this._hasReading || (this._telemetryReader != null && this._telemetryReader.IsAvailable);
            }
        }

        public Boolean HasGpuUsageReading
        {
            get
            {
                this.EnsureFresh();
                return this._hasReading;
            }
        }

        public Double GpuUsagePercent
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

        public void Dispose()
        {
            this._isDisposed = true;
            lock (this._sync)
            {
                this._telemetryReader?.Dispose();
            }
        }

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

                if (this._telemetryReader == null)
                {
                    this._hasReading = false;
                    this._usagePercent = 0;
                    return;
                }

                if (this._telemetryReader.TryReadSamples(out var samples))
                {
                    this._usagePercent = AggregateEngineSamples(samples);
                    this._hasReading = true;
                }
                else
                {
                    // Keep the last valid measurement through a transient PDH collection failure.
                    // Startup still reports unavailable because _hasReading is initially false.
                }
            }
        }

        public static Double AggregateEngineSamples(IEnumerable<GpuEngineSample> samples)
        {
            if (samples == null)
            {
                return 0.0;
            }

            var engineSums = new Dictionary<String, Double>(StringComparer.OrdinalIgnoreCase);
            var engineToAdapter = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);

            foreach (var sample in samples)
            {
                if (sample.Value <= 0 || Double.IsNaN(sample.Value) || Double.IsInfinity(sample.Value) ||
                    IsTotalInstance(sample.InstanceName))
                {
                    continue;
                }

                var (adapterKey, engineKey) = ParseInstanceKeys(sample.InstanceName);
                if (engineSums.TryGetValue(engineKey, out var currentSum))
                {
                    engineSums[engineKey] = currentSum + sample.Value;
                }
                else
                {
                    engineSums[engineKey] = sample.Value;
                    engineToAdapter[engineKey] = adapterKey;
                }
            }

            if (engineSums.Count == 0)
            {
                return 0.0;
            }

            // Group engine loads by adapter, taking max load across engines for each adapter to avoid double counting
            var adapterLoads = new Dictionary<String, Double>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in engineSums)
            {
                var engineKey = kvp.Key;
                var engineLoad = Math.Clamp(kvp.Value, 0.0, 100.0);
                var adapterKey = engineToAdapter[engineKey];

                if (adapterLoads.TryGetValue(adapterKey, out var currentMax))
                {
                    if (engineLoad > currentMax)
                    {
                        adapterLoads[adapterKey] = engineLoad;
                    }
                }
                else
                {
                    adapterLoads[adapterKey] = engineLoad;
                }
            }

            // Max across all adapters (e.g. iGPU vs dGPU)
            Double maxAdapterLoad = 0.0;
            foreach (var load in adapterLoads.Values)
            {
                if (load > maxAdapterLoad)
                {
                    maxAdapterLoad = load;
                }
            }

            return Math.Clamp(maxAdapterLoad, 0.0, 100.0);
        }

        private static Boolean IsTotalInstance(String instanceName)
        {
            return String.Equals(instanceName, "_Total", StringComparison.OrdinalIgnoreCase) ||
                (instanceName?.EndsWith("\\_Total", StringComparison.OrdinalIgnoreCase) ?? false);
        }

        public static (String AdapterKey, String EngineKey) ParseInstanceKeys(String instanceName)
        {
            if (String.IsNullOrEmpty(instanceName))
            {
                return ("default", "default");
            }

            var luidIdx = instanceName.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            if (luidIdx >= 0)
            {
                var engineKey = instanceName.Substring(luidIdx);
                var physIdx = engineKey.IndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
                var adapterKey = physIdx >= 0 ? engineKey.Substring(0, physIdx) : engineKey;
                return (adapterKey, engineKey);
            }

            return ("default", instanceName);
        }
    }

    public sealed class PdhGpuTelemetryReader : IGpuTelemetryReader
    {
        private const String GpuCounterPath = @"\GPU Engine(*)\Utilization Percentage";
        private readonly Object _sync = new Object();

        private IntPtr _queryHandle = IntPtr.Zero;
        private IntPtr _counterHandle = IntPtr.Zero;
        private IntPtr _buffer = IntPtr.Zero;
        private UInt32 _bufferSize = 0;
        private Boolean _isInitialized;
        private Boolean _isAvailable;
        private Boolean _isDisposed;

        public Boolean IsAvailable
        {
            get
            {
                lock (this._sync)
                {
                    if (!this._isInitialized)
                    {
                        this.Initialize();
                    }

                    return this._isAvailable;
                }
            }
        }

        public Boolean TryReadSamples(out List<GpuEngineSample> samples)
        {
            samples = null;

            lock (this._sync)
            {
                if (this._isDisposed)
                {
                    return false;
                }

                if (!this._isInitialized && !this.Initialize())
                {
                    return false;
                }

                if (!this._isAvailable || this._queryHandle == IntPtr.Zero || this._counterHandle == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    var status = PdhNative.PdhCollectQueryData(this._queryHandle);
                    if (status != PdhNative.ERROR_SUCCESS)
                    {
                        return false;
                    }

                    UInt32 itemCount = 0;
                    var bufferSize = this._bufferSize;

                    status = PdhNative.PdhGetFormattedCounterArrayW(
                        this._counterHandle,
                        PdhNative.PDH_FMT_DOUBLE,
                        ref bufferSize,
                        out itemCount,
                        this._buffer);

                    if (status == PdhNative.PDH_MORE_DATA || (status == PdhNative.ERROR_SUCCESS && this._buffer == IntPtr.Zero && bufferSize > 0))
                    {
                        if (this._buffer != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(this._buffer);
                            this._buffer = IntPtr.Zero;
                        }

                        this._bufferSize = bufferSize;
                        this._buffer = Marshal.AllocHGlobal((Int32)this._bufferSize);

                        status = PdhNative.PdhGetFormattedCounterArrayW(
                            this._counterHandle,
                            PdhNative.PDH_FMT_DOUBLE,
                            ref bufferSize,
                            out itemCount,
                            this._buffer);
                    }

                    if (status != PdhNative.ERROR_SUCCESS)
                    {
                        return false;
                    }

                    var result = new List<GpuEngineSample>((Int32)Math.Min(itemCount, 256));
                    var itemSize = IntPtr.Size == 8 ? 24 : 16;
                    var doubleOffset = IntPtr.Size == 8 ? 16 : 8;

                    for (UInt32 i = 0; i < itemCount; i++)
                    {
                        var itemPtr = IntPtr.Add(this._buffer, (Int32)(i * itemSize));
                        var namePtr = Marshal.ReadIntPtr(itemPtr, 0);
                        var cStatus = unchecked((UInt32)Marshal.ReadInt32(itemPtr, IntPtr.Size));

                        if (cStatus == PdhNative.PDH_CSTATUS_VALID_DATA || cStatus == PdhNative.PDH_CSTATUS_NEW_DATA)
                        {
                            var doubleVal = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(itemPtr, doubleOffset));
                            if (doubleVal > 0 && !Double.IsNaN(doubleVal) && !Double.IsInfinity(doubleVal))
                            {
                                var name = Marshal.PtrToStringUni(namePtr);
                                if (!String.IsNullOrEmpty(name))
                                {
                                    result.Add(new GpuEngineSample(name, doubleVal));
                                }
                            }
                        }
                    }

                    samples = result;
                    return true;
                }
                catch
                {
                    return false;
                }
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
                this.Cleanup();
            }
        }

        private Boolean Initialize()
        {
            this._isInitialized = true;

            try
            {
                var status = PdhNative.PdhOpenQuery(IntPtr.Zero, IntPtr.Zero, out this._queryHandle);
                if (status != PdhNative.ERROR_SUCCESS || this._queryHandle == IntPtr.Zero)
                {
                    this._isAvailable = false;
                    return false;
                }

                status = PdhNative.PdhAddEnglishCounterW(
                    this._queryHandle,
                    GpuCounterPath,
                    IntPtr.Zero,
                    out this._counterHandle);

                if (status != PdhNative.ERROR_SUCCESS || this._counterHandle == IntPtr.Zero)
                {
                    this.Cleanup();
                    this._isAvailable = false;
                    return false;
                }

                // Prime initial query data so rate counters start calculating
                PdhNative.PdhCollectQueryData(this._queryHandle);

                this._isAvailable = true;
                return true;
            }
            catch
            {
                this.Cleanup();
                this._isAvailable = false;
                return false;
            }
        }

        private void Cleanup()
        {
            if (this._queryHandle != IntPtr.Zero)
            {
                try
                {
                    PdhNative.PdhCloseQuery(this._queryHandle);
                }
                catch
                {
                }

                this._queryHandle = IntPtr.Zero;
                this._counterHandle = IntPtr.Zero;
            }

            if (this._buffer != IntPtr.Zero)
            {
                try
                {
                    Marshal.FreeHGlobal(this._buffer);
                }
                catch
                {
                }

                this._buffer = IntPtr.Zero;
                this._bufferSize = 0;
            }
        }

        internal static class PdhNative
        {
            public const UInt32 PDH_FMT_DOUBLE = 0x00000200;
            public const UInt32 PDH_MORE_DATA = 0x800007D2;
            public const UInt32 ERROR_SUCCESS = 0x00000000;
            public const UInt32 PDH_CSTATUS_VALID_DATA = 0x00000000;
            public const UInt32 PDH_CSTATUS_NEW_DATA = 0x00000001;

            [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern UInt32 PdhOpenQuery(
                IntPtr szDataSource,
                IntPtr dwUserData,
                out IntPtr phQuery);

            [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern UInt32 PdhAddEnglishCounterW(
                IntPtr hQuery,
                String szFullCounterPath,
                IntPtr dwUserData,
                out IntPtr phCounter);

            [DllImport("pdh.dll", SetLastError = true)]
            public static extern UInt32 PdhCollectQueryData(IntPtr hQuery);

            [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern UInt32 PdhGetFormattedCounterArrayW(
                IntPtr hCounter,
                UInt32 dwFormat,
                ref UInt32 lpdwBufferSize,
                out UInt32 lpdwItemCount,
                IntPtr lpItemBuffer);

            [DllImport("pdh.dll", SetLastError = true)]
            public static extern UInt32 PdhCloseQuery(IntPtr hQuery);
        }
    }
}

