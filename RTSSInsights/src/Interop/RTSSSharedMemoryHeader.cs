namespace Loupedeck.RTSSPerformancePlugin.Interop
{
    using System;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Header block at offset 0 of the "RTSSSharedMemoryV2" file mapping.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RTSSSharedMemoryHeader
    {
        /// <summary>Signature: 'RTSS' when the provider is RivaTuner Statistics Server.</summary>
        public UInt32 dwSignature;

        /// <summary>Shared memory layout version (high word = major, low word = minor).</summary>
        public UInt32 dwVersion;

        /// <summary>Size in bytes of a single <see cref="RTSSSharedMemoryAppEntry"/> record.</summary>
        public UInt32 dwAppEntrySize;

        /// <summary>Byte offset from the start of the mapping to the first app entry.</summary>
        public UInt32 dwAppArrOffset;

        /// <summary>Number of slots in the app entry array.</summary>
        public UInt32 dwAppArrSize;

        /// <summary>Size in bytes of a single OSD entry record.</summary>
        public UInt32 dwOSDEntrySize;

        /// <summary>Byte offset from the start of the mapping to the first OSD entry.</summary>
        public UInt32 dwOSDArrOffset;

        /// <summary>Number of slots in the OSD entry array.</summary>
        public UInt32 dwOSDArrSize;

        /// <summary>Incremented by RTSS whenever OSD content changes.</summary>
        public UInt32 dwOSDFrame;

        /// <summary>
        /// Expected value of <see cref="dwSignature"/>. RTSS assigns the C multi-character literal
        /// 'RTSS', which packs the FIRST character into the MOST significant byte: 'R'=0x52 leads.
        /// Laid out little-endian the bytes read "SSTR", so deriving this constant from the string
        /// order gives 0x53535452 — a value that never matches and rejects every real RTSS block.
        /// Verified against RTSS 7.3.5 (shared memory v2.21) on 2026-08-21.
        /// </summary>
        public const UInt32 ExpectedSignature = 0x52545353; // 'RTSS'

        /// <summary>
        /// Deallocated signature sentinel set by RTSS when shared memory is released (0xDEAD).
        /// </summary>
        public const UInt32 DeallocatedSignature = 0xDEAD;

        /// <summary>
        /// ASCII 'DEAD' (0x44454144) sentinel.
        /// </summary>
        public const UInt32 DeallocatedSignatureAscii = 0x44454144;

        public Boolean IsDeallocated =>
            this.dwSignature == DeallocatedSignature || this.dwSignature == DeallocatedSignatureAscii;

        /// <summary>Minimum layout version carrying the app array this plugin reads.</summary>
        public const UInt16 MinimumMajorVersion = 2;

        public const UInt16 MinimumNativeLowMinorVersion = 13;

        public const UInt16 MinimumForegroundAppTrackingMinorVersion = 16;

        public const UInt16 MinimumPercentileBufPosMinorVersion = 17;

        public Boolean IsValid =>
            !this.IsDeallocated &&
            this.dwSignature == ExpectedSignature &&
            this.MajorVersion >= MinimumMajorVersion &&
            this.dwAppEntrySize > 0 &&
            this.dwAppArrOffset > 0 &&
            this.dwAppArrSize > 0;

        public UInt16 MajorVersion => (UInt16)(this.dwVersion >> 16);

        public UInt16 MinorVersion => (UInt16)(this.dwVersion & 0xFFFF);

        public Boolean HasNativeLowStatistics =>
            this.MajorVersion > MinimumMajorVersion ||
            (this.MajorVersion == MinimumMajorVersion && this.MinorVersion >= MinimumNativeLowMinorVersion);

        public Boolean SupportsNativeOnePercentLow =>
            this.HasNativeLowStatistics &&
            this.dwAppEntrySize >= (UInt32)RTSSSharedMemoryAppEntry.NativeOnePercentLowRequiredEntrySize;

        public Boolean SupportsNativeZeroPointOnePercentLow =>
            this.HasNativeLowStatistics &&
            this.dwAppEntrySize >= (UInt32)RTSSSharedMemoryAppEntry.NativeZeroPointOnePercentLowRequiredEntrySize;

        public Boolean HasPercentileBufPos =>
            this.MajorVersion > MinimumMajorVersion ||
            (this.MajorVersion == MinimumMajorVersion && this.MinorVersion >= MinimumPercentileBufPosMinorVersion);

        public Boolean SupportsPercentileBufPos =>
            this.HasPercentileBufPos &&
            this.dwAppEntrySize >= (UInt32)RTSSSharedMemoryAppEntry.NativePercentileBufPosRequiredEntrySize;

        public Boolean HasForegroundAppTracking =>
            this.MajorVersion > MinimumMajorVersion ||
            (this.MajorVersion == MinimumMajorVersion && this.MinorVersion >= MinimumForegroundAppTrackingMinorVersion);

        public Boolean IsAppArrayRangeSafe(
            Int32 headerSize,
            Int32 minimumEntrySize,
            UInt32 maximumSlotCount)
        {
            return this.IsAppArrayRangeSafe(headerSize, minimumEntrySize, maximumSlotCount, UInt64.MaxValue);
        }

        public Boolean IsAppArrayRangeSafe(
            Int32 headerSize,
            Int32 minimumEntrySize,
            UInt32 maximumSlotCount,
            UInt64 mappedSize)
        {
            if (headerSize <= 0 ||
                minimumEntrySize <= 0 ||
                this.dwAppEntrySize < (UInt32)minimumEntrySize ||
                this.dwAppArrOffset < (UInt32)headerSize ||
                this.dwAppArrSize == 0 ||
                this.dwAppArrSize > maximumSlotCount)
            {
                return false;
            }

            var arrayBytes = (UInt64)this.dwAppArrSize * (UInt64)this.dwAppEntrySize;
            if ((UInt64)this.dwAppArrSize > 0 && arrayBytes / (UInt64)this.dwAppArrSize != (UInt64)this.dwAppEntrySize)
            {
                return false;
            }

            var arrayEnd = (UInt64)this.dwAppArrOffset + arrayBytes;
            if (arrayEnd < (UInt64)this.dwAppArrOffset)
            {
                return false;
            }

            return arrayEnd <= Int32.MaxValue &&
                (UInt64)headerSize <= mappedSize &&
                arrayEnd <= mappedSize;
        }
    }
}
