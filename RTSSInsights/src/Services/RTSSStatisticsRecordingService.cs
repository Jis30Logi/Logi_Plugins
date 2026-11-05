namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Runtime.InteropServices;

    using Loupedeck.RTSSPerformancePlugin.Helpers;
    using Loupedeck.RTSSPerformancePlugin.Interop;

    /// <summary>
    /// Narrow write capability for the RTSS application statistics flag. This service only ever
    /// changes dwStatFlags for the currently selected process and app-array index.
    /// </summary>
    public sealed class RTSSStatisticsRecordingService : IDisposable
    {
        public const UInt32 StatFlagRecord = 0x00000001;

        private const Int32 BusyOffset = 36;
        private const Int32 BusyBit = 1;
        private const UInt32 MaxAppArraySize = 4096;
        private const Int64 RetryIntervalMs = 1000;
        private const Int32 StatFlagsEndOffset = RTSSSharedMemoryAppEntry.NativeOffsets.dwStatFlags + sizeof(UInt32);

        private readonly Object _sync = new Object();
        private readonly String _sharedMemoryName;
        private IntPtr _mappingHandle;
        private IntPtr _viewPointer;
        private UInt64 _mappedSize;
        private UInt32 _managedProcessId;
        private Int32 _managedEntryIndex = -1;
        private Int64 _lastWriteAttemptTicks = Int64.MinValue;
        private Boolean _disposed;

        public RTSSStatisticsRecordingService()
            : this(null)
        {
        }

        internal RTSSStatisticsRecordingService(String sharedMemoryName)
        {
            this._sharedMemoryName = String.IsNullOrWhiteSpace(sharedMemoryName)
                ? NativeMethods.RTSSSharedMemoryName
                : sharedMemoryName;
        }

        /// <summary>
        /// Ensures recording for exactly the selected snapshot's entry. Returns true only after
        /// the recording bit has been observed set in shared memory (including successful readback).
        /// </summary>
        public Boolean EnsureStatisticsRecordingEnabled(RTSSFrameSnapshot selectedApplication)
        {
            if (selectedApplication == null || selectedApplication.ProcessId == 0 || selectedApplication.EntryIndex < 0)
            {
                this.StopManagingApplication();
                return false;
            }

            lock (this._sync)
            {
                if (this._disposed)
                {
                    return false;
                }

                this.HandleProcessChange(selectedApplication.ProcessId, selectedApplication.EntryIndex);
                if (!this.TryReadTarget(selectedApplication, out var statFlags, out var processName, out var version))
                {
                    return false;
                }

                if ((statFlags & StatFlagRecord) != 0)
                {
                    PluginLog.InfoThrottled(
                        "rtss-stats-recording-state",
                        $"{selectedApplication.ProcessId}:{selectedApplication.EntryIndex}:{statFlags:X8}:enabled",
                        $"[RTSS STATS] Process={processName} PID={selectedApplication.ProcessId} " +
                        $"Entry={selectedApplication.EntryIndex} RTSSVersion={version} " +
                        $"BeforeFlags=0x{statFlags:X8} RequestedFlags=0x{statFlags:X8} " +
                        $"AfterFlags=0x{statFlags:X8} RecordingEnabled=true");
                    return true;
                }

                var now = Environment.TickCount64;
                if (this._lastWriteAttemptTicks != Int64.MinValue && now - this._lastWriteAttemptTicks < RetryIntervalMs)
                {
                    return false;
                }

                this._lastWriteAttemptTicks = now;
                var requestedFlags = statFlags | StatFlagRecord;
                var entryAddress = this.GetEntryAddress(selectedApplication.EntryIndex);
                var flagsAddress = IntPtr.Add(entryAddress, RTSSSharedMemoryAppEntry.NativeOffsets.dwStatFlags);

                var disconnectAfterRelease = false;
                var busyAcquired = false;
                var busyAddress = IntPtr.Zero;
                try
                {
                    // RTSS v2.14+ dwBusy: bit 0 is set while writing to shared memory and MUST be
                    // reset afterwards or OSD updates lock for every client. If another writer
                    // holds it, skip this attempt; the throttle retries on a later cycle.
                    if (this.TryGetBusyAddress(out busyAddress))
                    {
                        var busy = Marshal.ReadInt32(busyAddress);
                        if ((busy & BusyBit) != 0)
                        {
                            PluginLog.InfoThrottled(
                                "rtss-stats-busy",
                                selectedApplication.ProcessId.ToString(),
                                $"[RTSS STATS] dwBusy is held by another writer; deferring STATFLAG_RECORD for PID={selectedApplication.ProcessId}.");
                            return false;
                        }

                        Marshal.WriteInt32(busyAddress, busy | BusyBit);
                        busyAcquired = true;
                        ThreadMemoryBarrier();
                    }

                    // Re-validate ownership under the lock, and re-read flags so bits RTSS changed
                    // since the snapshot are preserved.
                    var currentPid = unchecked((UInt32)Marshal.ReadInt32(entryAddress, RTSSSharedMemoryAppEntry.NativeOffsets.dwProcessID));
                    if (currentPid != selectedApplication.ProcessId)
                    {
                        PluginLog.Info(
                            $"[RTSS STATS] Entry {selectedApplication.EntryIndex} changed PID {selectedApplication.ProcessId}->{currentPid} before write; no write performed.");
                        return false;
                    }

                    var latestFlags = unchecked((UInt32)Marshal.ReadInt32(flagsAddress));
                    requestedFlags = latestFlags | StatFlagRecord;
                    Marshal.WriteInt32(flagsAddress, unchecked((Int32)requestedFlags));
                    ThreadMemoryBarrier();

                    var processIdAfterWrite = unchecked((UInt32)Marshal.ReadInt32(entryAddress, RTSSSharedMemoryAppEntry.NativeOffsets.dwProcessID));
                    var afterFlags = unchecked((UInt32)Marshal.ReadInt32(flagsAddress));
                    var verified = processIdAfterWrite == selectedApplication.ProcessId && (afterFlags & StatFlagRecord) != 0;

                    PluginLog.Info(
                        $"[RTSS STATS] Process={processName} PID={selectedApplication.ProcessId} " +
                        $"Entry={selectedApplication.EntryIndex} RTSSVersion={version} " +
                        $"BeforeFlags=0x{statFlags:X8} RequestedFlags=0x{requestedFlags:X8} " +
                        $"AfterFlags=0x{afterFlags:X8} RecordingEnabled={verified} " +
                        (processIdAfterWrite == selectedApplication.ProcessId
                            ? String.Empty
                            : $"Failure=EntryProcessChangedTo{processIdAfterWrite}"));

                    return verified;
                }
                catch (Exception exception)
                {
                    PluginLog.Warning(exception,
                        $"[RTSS STATS] Could not set STATFLAG_RECORD for {processName} PID={selectedApplication.ProcessId} " +
                        $"Entry={selectedApplication.EntryIndex}; native statistics remain unavailable.");
                    // The mapping is dropped only after the finally below has released dwBusy.
                    disconnectAfterRelease = true;
                    return false;
                }
                finally
                {
                    if (busyAcquired && busyAddress != IntPtr.Zero)
                    {
                        try
                        {
                            // Clear only bit 0; leave anything else RTSS or others changed.
                            Marshal.WriteInt32(busyAddress, Marshal.ReadInt32(busyAddress) & ~BusyBit);
                        }
                        catch (Exception releaseException)
                        {
                            PluginLog.Warning(releaseException, "[RTSS STATS] Failed to release dwBusy.");
                        }
                    }

                    if (disconnectAfterRelease)
                    {
                        this.DisconnectMapping();
                    }
                }
            }
        }

        /// <summary>
        /// dwBusy sits directly after dwOSDFrame (header offset 36) and exists from v2.14. It is
        /// only used when the app array starts beyond it, so it can never alias an app entry.
        /// </summary>
        private Boolean TryGetBusyAddress(out IntPtr busyAddress)
        {
            busyAddress = IntPtr.Zero;
            var header = (RTSSSharedMemoryHeader)Marshal.PtrToStructure(this._viewPointer, typeof(RTSSSharedMemoryHeader));
            var supportsBusy = header.MajorVersion > 2 || (header.MajorVersion == 2 && header.MinorVersion >= 14);
            if (!supportsBusy || header.dwAppArrOffset < BusyOffset + sizeof(Int32))
            {
                return false;
            }

            busyAddress = IntPtr.Add(this._viewPointer, BusyOffset);
            return true;
        }

        public Boolean IsStatisticsRecordingEnabled(RTSSFrameSnapshot selectedApplication)
        {
            if (selectedApplication == null || selectedApplication.ProcessId == 0 || selectedApplication.EntryIndex < 0)
            {
                return false;
            }

            lock (this._sync)
            {
                return this.TryReadTarget(selectedApplication, out var statFlags, out _, out _) &&
                    (statFlags & StatFlagRecord) != 0;
            }
        }

        public void StopManagingApplication()
        {
            lock (this._sync)
            {
                this._managedProcessId = 0;
                this._managedEntryIndex = -1;
                this._lastWriteAttemptTicks = Int64.MinValue;
            }
        }

        /// <summary>Called when availability monitoring reports RTSS is gone or restarting.</summary>
        public void HandleRTSSUnavailable()
        {
            lock (this._sync)
            {
                this._managedProcessId = 0;
                this._managedEntryIndex = -1;
                this._lastWriteAttemptTicks = Int64.MinValue;
                this.DisconnectMapping();
            }
        }

        private void HandleProcessChange(UInt32 processId, Int32 entryIndex)
        {
            if (this._managedProcessId == processId && this._managedEntryIndex == entryIndex)
            {
                return;
            }

            this._managedProcessId = processId;
            this._managedEntryIndex = entryIndex;
            this._lastWriteAttemptTicks = Int64.MinValue;
        }

        private Boolean TryReadTarget(
            RTSSFrameSnapshot selectedApplication,
            out UInt32 statFlags,
            out String processName,
            out String version)
        {
            statFlags = 0;
            processName = selectedApplication.ProcessFileName ?? "<unknown>";
            version = "unknown";

            if (!this.TryConnectMapping())
            {
                return false;
            }

            try
            {
                var header = (RTSSSharedMemoryHeader)Marshal.PtrToStructure(this._viewPointer, typeof(RTSSSharedMemoryHeader));
                var headerSize = Marshal.SizeOf(typeof(RTSSSharedMemoryHeader));
                if (!header.IsValid ||
                    !header.IsAppArrayRangeSafe(headerSize, RTSSSharedMemoryAppEntry.MinimumAppEntrySize, MaxAppArraySize, this._mappedSize) ||
                    header.dwAppEntrySize < StatFlagsEndOffset ||
                    (UInt32)selectedApplication.EntryIndex >= header.dwAppArrSize)
                {
                    this.DisconnectMapping();
                    return false;
                }

                version = $"{header.MajorVersion}.{header.MinorVersion}";
                var entryAddress = this.GetEntryAddress(selectedApplication.EntryIndex);
                var entryEndOffset = (UInt64)header.dwAppArrOffset +
                    ((UInt64)(UInt32)selectedApplication.EntryIndex * header.dwAppEntrySize) + header.dwAppEntrySize;
                if (entryEndOffset > this._mappedSize)
                {
                    this.DisconnectMapping();
                    return false;
                }

                var processId = unchecked((UInt32)Marshal.ReadInt32(entryAddress, RTSSSharedMemoryAppEntry.NativeOffsets.dwProcessID));
                if (processId != selectedApplication.ProcessId)
                {
                    PluginLog.InfoThrottled(
                        "rtss-stats-target-mismatch",
                        $"{selectedApplication.ProcessId}:{selectedApplication.EntryIndex}:{processId}",
                        $"[RTSS STATS] Selected entry changed before recording enable. " +
                        $"ExpectedPID={selectedApplication.ProcessId} ActualPID={processId} Entry={selectedApplication.EntryIndex}; no write performed.");
                    return false;
                }

                statFlags = unchecked((UInt32)Marshal.ReadInt32(entryAddress, RTSSSharedMemoryAppEntry.NativeOffsets.dwStatFlags));
                return true;
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, "[RTSS STATS] Shared-memory inspection failed; no recording flags were changed.");
                this.DisconnectMapping();
                return false;
            }
        }

        private Boolean TryConnectMapping()
        {
            if (this._viewPointer != IntPtr.Zero)
            {
                return true;
            }

            this._mappingHandle = NativeMethods.OpenFileMapping(
                NativeMethods.FILE_MAP_READ | NativeMethods.FILE_MAP_WRITE,
                false,
                this._sharedMemoryName);
            if (this._mappingHandle == IntPtr.Zero)
            {
                PluginLog.InfoThrottled(
                    "rtss-stats-map-open",
                    Marshal.GetLastWin32Error().ToString(),
                    $"[RTSS STATS] Write-capable mapping open failed (error {Marshal.GetLastWin32Error()}); native recording was not changed.");
                return false;
            }

            this._viewPointer = NativeMethods.MapViewOfFile(
                this._mappingHandle,
                NativeMethods.FILE_MAP_READ | NativeMethods.FILE_MAP_WRITE,
                0,
                0,
                UIntPtr.Zero);
            if (this._viewPointer == IntPtr.Zero ||
                NativeMethods.VirtualQuery(
                    this._viewPointer,
                    out var memoryInfo,
                    (UIntPtr)Marshal.SizeOf(typeof(NativeMethods.MemoryBasicInformation))) == UIntPtr.Zero)
            {
                this.DisconnectMapping();
                return false;
            }

            this._mappedSize = memoryInfo.RegionSize.ToUInt64();
            return true;
        }

        private IntPtr GetEntryAddress(Int32 index)
        {
            var header = (RTSSSharedMemoryHeader)Marshal.PtrToStructure(this._viewPointer, typeof(RTSSSharedMemoryHeader));
            var offset = (UInt64)header.dwAppArrOffset + (UInt64)(UInt32)index * header.dwAppEntrySize;
            return IntPtr.Add(this._viewPointer, checked((Int32)offset));
        }

        private static void ThreadMemoryBarrier() => System.Threading.Thread.MemoryBarrier();

        private void DisconnectMapping()
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
        }

        public void Dispose()
        {
            lock (this._sync)
            {
                if (this._disposed)
                {
                    return;
                }

                this._disposed = true;
                this.DisconnectMapping();
            }
        }
    }
}
