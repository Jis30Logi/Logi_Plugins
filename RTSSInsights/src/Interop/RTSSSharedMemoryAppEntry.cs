namespace Loupedeck.RTSSPerformancePlugin.Interop
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;

    /// <summary>
    /// One per-application record in the RTSS shared memory app array.
    /// The dwStatFramerate* statistic fields are expressed in 0.1 FPS (e.g. dwStatFramerateAvg == 73 -> 7.3 FPS).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct RTSSSharedMemoryAppEntry
    {
        /// <summary>Process id of the hooked application; 0 when the slot is free.</summary>
        public UInt32 dwProcessID;

        /// <summary>Full path of the hooked executable.</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxPathLength)]
        public Byte[] szName;

        /// <summary>APPFLAG_* bitfield (render API, profile state).</summary>
        public UInt32 dwFlags;

        /// <summary>Timestamp (ms) of the last presented frame.</summary>
        public UInt32 dwTime0;

        /// <summary>Timestamp (ms) of the current frame.</summary>
        public UInt32 dwTime1;

        /// <summary>Frames presented between <see cref="dwTime0"/> and <see cref="dwTime1"/>.</summary>
        public UInt32 dwFrames;

        /// <summary>Frame time of the last frame, in microseconds.</summary>
        public UInt32 dwFrameTime;

        public UInt32 dwStatFlags;
        public UInt32 dwStatTime0;
        public UInt32 dwStatTime1;
        public UInt32 dwStatFrames;

        /// <summary>Number of frame times accumulated in the statistics buffer.</summary>
        public UInt32 dwStatCount;

        /// <summary>Average framerate over the statistics window, x1000.</summary>
        public UInt32 dwStatFramerateMin;

        /// <summary>Average framerate over the statistics window, x1000.</summary>
        public UInt32 dwStatFramerateAvg;

        /// <summary>Peak framerate over the statistics window, x1000.</summary>
        public UInt32 dwStatFramerateMax;

        /// <summary>RTSS 1% low framerate, x1000.</summary>
        // RTSS SDK v2.13+ fields between dwStatFramerateMax and the native low values:
        // OSD/video/audio capture data, frame-time statistics, four LARGE_INTEGER values,
        // prerecord limits, qwStatTotalTime, and dwStatFrameTimeLowBuf[1024]. The byte
        // projection reaches the exact SDK offsets without inventing meanings for fields this
        // plugin does not consume.
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8860)]
        private Byte[] _sdkFieldsThroughStatFrameTimeLowBuffer;

        /// <summary>RTSS 1% low framerate, x1000, at SDK offset 9176.</summary>
        public UInt32 dwStatFramerate1Dot0PercentLow;

        /// <summary>RTSS 0.1% low framerate, x1000, at SDK offset 9180.</summary>
        public UInt32 dwStatFramerate0Dot1PercentLow;

        /// <summary>RTSS 1% low buffer position (v2.17+), at SDK offset 9184.</summary>
        public UInt32 dw1Dot0PercentLowBufPos;

        /// <summary>RTSS 0.1% low buffer position (v2.17+), at SDK offset 9188.</summary>
        public UInt32 dw0Dot1PercentLowBufPos;

        public const Int32 MaxPathLength = 260;

        public const Int32 MinimumAppEntrySize = 316;

        public const Int32 StatFramerate1Dot0PercentLowOffset = 9176;

        public const Int32 StatFramerate0Dot1PercentLowOffset = 9180;

        public const Int32 Stat1Dot0PercentLowBufPosOffset = 9184;

        public const Int32 Stat0Dot1PercentLowBufPosOffset = 9188;

        public const Int32 NativeOnePercentLowRequiredEntrySize = 9180;

        public const Int32 NativeZeroPointOnePercentLowRequiredEntrySize = 9184;

        public const Int32 NativePercentileBufPosRequiredEntrySize = 9192;

        public static class NativeOffsets
        {
            public const Int32 dwProcessID = 0;
            public const Int32 szName = 4;
            public const Int32 dwFlags = 264;
            public const Int32 dwTime0 = 268;
            public const Int32 dwTime1 = 272;
            public const Int32 dwFrames = 276;
            public const Int32 dwFrameTime = 280;
            public const Int32 dwStatFlags = 284;
            public const Int32 dwStatTime0 = 288;
            public const Int32 dwStatTime1 = 292;
            public const Int32 dwStatFrames = 296;
            public const Int32 dwStatCount = 300;
            public const Int32 dwStatFramerateMin = 304;
            public const Int32 dwStatFramerateAvg = 308;
            public const Int32 dwStatFramerateMax = 312;
            public const Int32 dwStatFramerate1Dot0PercentLow = 9176;
            public const Int32 dwStatFramerate0Dot1PercentLow = 9180;
            public const Int32 dw1Dot0PercentLowBufPos = 9184;
            public const Int32 dw0Dot1PercentLowBufPos = 9188;
        }

        /// <summary>
        /// Divisor applied to the dwStatFramerate* statistic fields (min/avg/max, 1% low, 0.1% low):
        /// RTSS stores them in 0.1 FPS, so raw 27 is 2.7 FPS.
        ///
        /// Evidence (live RTSS 7.3.5 / shared memory v2.21, PathOfExileSteam.exe): dwStatFrames /
        /// ((dwStatTime1 - dwStatTime0) / 1000) was 7.39 and 7.60 FPS while raw dwStatFramerateAvg
        /// read 73 and 76. This does not apply to dwFrames/dwTime0/dwTime1 (instantaneous FPS is
        /// 1000 * frames / elapsed ms) or to dwFrameTime (microseconds).
        /// </summary>
        public const Double FramerateScale = 10.0;

        /// <summary>
        /// How far dwTime1 may lead the clock and still be treated as the present moment.
        ///
        /// RTSS can stamp an entry between the caller reading GetTickCount and reading dwTime1, so
        /// a lead of a few milliseconds is a sampling race rather than a time-travelling entry.
        /// A second is far beyond that race and far short of anything meaningful.
        /// </summary>
        public const UInt32 ClockSkewToleranceMs = 1000;

        /// <summary>
        /// Highest value accepted from the statistics window before it is treated as a sentinel
        /// rather than a framerate. RTSS parks 0xFFFFFFFF in dwStatFramerateMin when it has no
        /// statistics yet — observed on every hooked slot of a live RTSS 7.3.5 — which scales to
        /// 429,496,729 fps. No display should ever inherit that.
        /// </summary>
        private const Double MaxPlausibleFps = 10000.0;

        public Boolean IsActive => this.dwProcessID != 0;

        /// <summary>
        /// Full path of the hooked executable. szName is a fixed-width ANSI buffer that RTSS does
        /// not clear between tenants, so everything past the first NUL is another process's
        /// leftovers and must be dropped.
        /// </summary>
        public String GetProcessPath()
        {
            var buffer = this.szName;

            if (buffer == null || buffer.Length == 0)
            {
                return String.Empty;
            }

            var length = Array.IndexOf<Byte>(buffer, 0);

            if (length < 0)
            {
                length = buffer.Length;
            }

            return length == 0 ? String.Empty : Encoding.Default.GetString(buffer, 0, length);
        }

        public String GetProcessFileName()
        {
            var path = this.GetProcessPath();

            if (String.IsNullOrEmpty(path))
            {
                return String.Empty;
            }

            try
            {
                return Path.GetFileName(path);
            }
            catch (ArgumentException)
            {
                // A garbled slot can hold characters Path rejects; the raw value still beats null.
                return path;
            }
        }

        /// <summary>
        /// Live framerate derived from the frames presented across the current sampling
        /// interval. Returns 0 when RTSS has not yet closed an interval for this entry.
        /// </summary>
        public Double GetInstantaneousFps()
        {
            // dwTime0/dwTime1 are millisecond tick counts and wrap; an unsigned subtraction
            // still yields the correct delta across the wrap.
            var elapsedMs = this.dwTime1 - this.dwTime0;

            return elapsedMs == 0 ? 0 : this.dwFrames * 1000.0 / elapsedMs;
        }

        /// <summary>
        /// The best current framerate this entry can offer: the live rate when RTSS has closed a
        /// sampling interval, and the statistics-window average when it has not.
        ///
        /// <see cref="GetInstantaneousFps"/> is 0 whenever dwTime1 == dwTime0 — an interval that
        /// has only just opened — and callers read that as "no framerate here". That is wrong when
        /// dwStatFramerateAvg on the same entry is live and non-zero: RTSS does have a current
        /// figure for the application, and the key has no reason to fall back to the wordmark.
        ///
        /// The average is only ever a substitute for a momentarily open interval, never a way to
        /// keep a dead application on the glass: whether the entry is live at all is decided by
        /// <see cref="GetUpdateAgeMilliseconds"/> before this is consulted.
        /// </summary>
        public Double GetCurrentFps()
        {
            var instantaneous = this.GetInstantaneousFps();

            if (instantaneous > 0)
            {
                return instantaneous;
            }

            var average = this.GetAverageFps();

            return average > 0 && average <= MaxPlausibleFps ? average : 0;
        }

        /// <summary>
        /// Milliseconds since RTSS last advanced this entry, given the current tick count.
        ///
        /// dwTime1 is a GetTickCount value written by RTSS as the application presents. RTSS never
        /// clears an entry when its process stops presenting, so this age is the only way to tell a
        /// live application from one whose numbers merely persist: verified on-device against an
        /// entry whose last interval had closed 69 minutes earlier while still reporting a framerate.
        ///
        /// Both values are 32-bit tick counts, so the unsigned subtraction stays correct across the
        /// 49-day wrap. A dwTime1 slightly ahead of <paramref name="nowTicks"/> (RTSS sampling
        /// between the two reads) wraps to a huge value, so it is reported as 0 rather than as
        /// ancient.
        ///
        /// That leniency has to be bounded by how far ahead dwTime1 is, not merely by the fact that
        /// it is ahead. Treating every wrapped subtraction as 0 inverted the test for an entry RTSS
        /// has never stamped: dwTime1 == 0 — the state every hooked-but-not-presenting slot is in,
        /// measured against live RTSS 7.3.5 with four hooked applications — makes the age equal to
        /// the machine's uptime, which crosses the half-range once the machine has been up 24.85
        /// days and was then clamped to "perfectly fresh". A never-stamped entry would have read as
        /// live, at 0 fps, on any long-running machine.
        /// </summary>
        public UInt32 GetUpdateAgeMilliseconds(UInt32 nowTicks)
        {
            var age = nowTicks - this.dwTime1;

            if (age <= (UInt32.MaxValue / 2))
            {
                return age;
            }

            // dwTime1 is ahead of the clock. A few milliseconds is the sampling race above; more
            // than that is not a race, and must not be rewarded with the freshest possible age.
            return this.dwTime1 - nowTicks <= ClockSkewToleranceMs ? 0 : UInt32.MaxValue;
        }
        /// <summary>Average framerate over the statistics window; 0 until RTSS has stats.</summary>
        public Double GetAverageFps() => this.dwStatFramerateAvg / FramerateScale;

        /// <summary>
        /// Validates that the RTSS statistics window has accumulated frame statistics and
        /// that the statistics period is strictly positive and non-empty.
        /// </summary>
        public Boolean HasValidStatisticsPeriod =>
            this.dwStatCount > 0 &&
            this.dwStatFrames > 0 &&
            this.dwStatTime1 > this.dwStatTime0;

        /// <summary>1% low framerate over the statistics window; false when sentinel or out of range.</summary>
        public Boolean TryGetOnePercentLowFps(out Double value)
        {
            // RTSS maintains the native percentile buffer independently of the min/average
            // statistics counters and their time period. Validate only the native field itself;
            // those unrelated counters can be zero while this v2.13+ value is already valid.
            return TryConvertNativeFramerate(this.dwStatFramerate1Dot0PercentLow, out value);
        }

        public Double GetOnePercentLowFps() =>
            this.TryGetOnePercentLowFps(out var value) ? value : 0;

        /// <summary>RTSS 0.1% low framerate from dwStatFramerate0Dot1PercentLow (SDK offset 9180); returns true when valid RTSS-native value is present.</summary>
        public Boolean TryGetZeroPointOnePercentLowFps(out Double value)
        {
            value = 0.0;
            if (this.dwStatTime0 > 0)
            {
                if (this.dwStatTime1 <= this.dwStatTime0 || this.dwStatCount == 0 || this.dwStatFrames == 0)
                {
                    return false;
                }
            }

            return TryConvertNativeFramerate(this.dwStatFramerate0Dot1PercentLow, out value);
        }

        public Double GetZeroPointOnePercentLowFps() =>
            this.TryGetZeroPointOnePercentLowFps(out var value) ? value : 0;

        /// <summary>Last frame time in milliseconds; dwFrameTime is microseconds.</summary>
        public Double GetFrameTimeMilliseconds() => this.dwFrameTime / 1000.0;

        private static Double ToUsableFramerate(UInt32 rawValue)
        {
            var value = rawValue / FramerateScale;
            return value > 0 && value <= MaxPlausibleFps ? value : 0;
        }

        private static Boolean TryConvertNativeFramerate(UInt32 rawValue, out Double value)
        {
            value = 0.0;
            // Unavailable: raw 0 (RTSS has no value yet) and the 0xFFFFFFFF sentinel. Every other raw
            // value is a real reading in 0.1 FPS (raw 1 = 0.1 FPS, raw 27 = 2.7 FPS).
            if (rawValue == 0 || rawValue == UInt32.MaxValue)
            {
                return false;
            }

            var fps = rawValue / FramerateScale;
            // Non-physical values (>10000 FPS) are rejected
            if (fps <= 0.0 || fps > MaxPlausibleFps)
            {
                return false;
            }

            value = fps;
            return true;
        }

        private static Boolean TryConvertNativeFramerate(UInt32 statCount, UInt32 statFrames, UInt32 rawValue, out Double value)
        {
            return TryConvertNativeFramerate(rawValue, out value);
        }

        private static Boolean TryConvertNativeFramerate(UInt32 statCount, UInt32 rawValue, out Double value)
        {
            return TryConvertNativeFramerate(rawValue, out value);
        }

        public RTSSPercentileDiagnostics GetDiagnostics(RTSSSharedMemoryHeader header)
        {
            this.TryGetOnePercentLowFps(out var decoded1Low);
            this.TryGetZeroPointOnePercentLowFps(out var decoded01Low);

            return new RTSSPercentileDiagnostics(
                NativeOffsets.dwStatFramerate1Dot0PercentLow,
                NativeOffsets.dwStatFramerate0Dot1PercentLow,
                NativeOffsets.dw1Dot0PercentLowBufPos,
                NativeOffsets.dw0Dot1PercentLowBufPos,
                this.dwStatFramerate1Dot0PercentLow,
                this.dwStatFramerate0Dot1PercentLow,
                decoded1Low,
                decoded01Low,
                header.dwAppEntrySize,
                header.SupportsNativeOnePercentLow,
                header.SupportsNativeZeroPointOnePercentLow,
                header.SupportsPercentileBufPos,
                this.HasValidStatisticsPeriod);
        }

        public PercentileFpsResult GetNativeOnePercentLowResult(Boolean isSupported = true, Boolean isFresh = true)
        {
            if (isSupported && isFresh && this.TryGetOnePercentLowFps(out var fps))
            {
                return PercentileFpsResult.CreateValid(
                    fps,
                    this.dwStatFramerate1Dot0PercentLow,
                    this.dwStatCount,
                    this.dwStatTime0,
                    this.dwStatTime1,
                    this.dwStatFrames);
            }

            return PercentileFpsResult.CreateInvalid(
                isSupported,
                isFresh,
                this.dwStatFramerate1Dot0PercentLow,
                this.dwStatCount,
                this.dwStatTime0,
                this.dwStatTime1,
                this.dwStatFrames);
        }

        public PercentileFpsResult GetNativeZeroPointOnePercentLowResult(Boolean isSupported = true, Boolean isFresh = true)
        {
            if (isSupported && isFresh && this.TryGetZeroPointOnePercentLowFps(out var fps))
            {
                return PercentileFpsResult.CreateValid(
                    fps,
                    this.dwStatFramerate0Dot1PercentLow,
                    this.dwStatCount,
                    this.dwStatTime0,
                    this.dwStatTime1,
                    this.dwStatFrames);
            }

            return PercentileFpsResult.CreateInvalid(
                isSupported,
                isFresh,
                this.dwStatFramerate0Dot1PercentLow,
                this.dwStatCount,
                this.dwStatTime0,
                this.dwStatTime1,
                this.dwStatFrames);
        }
    }

    /// <summary>
    /// Diagnostic audit struct capturing raw native DWORDs, SDK byte offsets, decoded FPS,
    /// entry sizing, and header feature support for percentile telemetry.
    /// </summary>
    public readonly struct RTSSPercentileDiagnostics
    {
        public Int32 OnePercentLowOffset { get; }
        public Int32 ZeroPointOnePercentLowOffset { get; }
        public Int32 OnePercentLowBufPosOffset { get; }
        public Int32 ZeroPointOnePercentLowBufPosOffset { get; }
        public UInt32 RawOnePercentLow { get; }
        public UInt32 RawZeroPointOnePercentLow { get; }
        public Double DecodedOnePercentLowFps { get; }
        public Double DecodedZeroPointOnePercentLowFps { get; }
        public UInt32 EntrySize { get; }
        public Boolean SupportsOnePercentLow { get; }
        public Boolean SupportsZeroPointOnePercentLow { get; }
        public Boolean SupportsPercentileBufPos { get; }
        public Boolean HasValidStatisticsPeriod { get; }

        public RTSSPercentileDiagnostics(
            Int32 onePercentLowOffset,
            Int32 zeroPointOnePercentLowOffset,
            Int32 onePercentLowBufPosOffset,
            Int32 zeroPointOnePercentLowBufPosOffset,
            UInt32 rawOnePercentLow,
            UInt32 rawZeroPointOnePercentLow,
            Double decodedOnePercentLowFps,
            Double decodedZeroPointOnePercentLowFps,
            UInt32 entrySize,
            Boolean supportsOnePercentLow,
            Boolean supportsZeroPointOnePercentLow,
            Boolean supportsPercentileBufPos,
            Boolean hasValidStatisticsPeriod)
        {
            this.OnePercentLowOffset = onePercentLowOffset;
            this.ZeroPointOnePercentLowOffset = zeroPointOnePercentLowOffset;
            this.OnePercentLowBufPosOffset = onePercentLowBufPosOffset;
            this.ZeroPointOnePercentLowBufPosOffset = zeroPointOnePercentLowBufPosOffset;
            this.RawOnePercentLow = rawOnePercentLow;
            this.RawZeroPointOnePercentLow = rawZeroPointOnePercentLow;
            this.DecodedOnePercentLowFps = decodedOnePercentLowFps;
            this.DecodedZeroPointOnePercentLowFps = decodedZeroPointOnePercentLowFps;
            this.EntrySize = entrySize;
            this.SupportsOnePercentLow = supportsOnePercentLow;
            this.SupportsZeroPointOnePercentLow = supportsZeroPointOnePercentLow;
            this.SupportsPercentileBufPos = supportsPercentileBufPos;
            this.HasValidStatisticsPeriod = hasValidStatisticsPeriod;
        }
    }

    /// <summary>
    /// Strongly-typed percentile FPS result carrying value, validity, RTSS feature support, freshness,
    /// and statistics period metadata from the actual RTSS shared memory snapshot.
    /// </summary>
    public readonly struct PercentileFpsResult
    {
        public Double Value { get; }
        public Boolean IsValid { get; }
        public Boolean IsSupported { get; }
        public Boolean IsFresh { get; }
        public UInt32 SampleCount { get; }
        public UInt32 StatisticsTimeStart { get; }
        public UInt32 StatisticsTimeEnd { get; }
        public UInt32 TotalFrames { get; }
        public UInt32 RawValue { get; }
        public String Source { get; }
        public UInt32 StatisticsStart => this.StatisticsTimeStart;
        public UInt32 StatisticsEnd => this.StatisticsTimeEnd;

        public PercentileFpsResult(
            Double value,
            Boolean isValid,
            Boolean isSupported,
            Boolean isFresh,
            UInt32 sampleCount,
            UInt32 statisticsTimeStart,
            UInt32 statisticsTimeEnd,
            UInt32 totalFrames,
            UInt32 rawValue,
            String source = null)
        {
            this.Value = value;
            this.IsValid = isValid;
            this.IsSupported = isSupported;
            this.IsFresh = isFresh;
            this.SampleCount = sampleCount;
            this.StatisticsTimeStart = statisticsTimeStart;
            this.StatisticsTimeEnd = statisticsTimeEnd;
            this.TotalFrames = totalFrames;
            this.RawValue = rawValue;
            this.Source = source ?? (isValid ? "rtssNative" : "none");
        }

        public PercentileFpsResult(
            Double value,
            Boolean isValid,
            Boolean isSupported,
            Boolean isFresh,
            UInt32 rawValue,
            UInt32 sampleCount)
            : this(value, isValid, isSupported, isFresh, sampleCount, 0, 0, 0, rawValue, isValid ? "rtssNative" : "none")
        {
        }

        public static PercentileFpsResult Invalid =>
            new PercentileFpsResult(0.0, false, false, false, 0, 0);

        public static PercentileFpsResult CreateInvalid(
            Boolean isSupported,
            Boolean isFresh,
            UInt32 rawValue,
            UInt32 sampleCount,
            UInt32 time0,
            UInt32 time1,
            UInt32 frames) =>
            new PercentileFpsResult(0.0, false, isSupported, isFresh, sampleCount, time0, time1, frames, rawValue, "none");

        public static PercentileFpsResult CreateValid(
            Double value,
            UInt32 rawValue,
            UInt32 sampleCount,
            UInt32 time0,
            UInt32 time1,
            UInt32 frames) =>
            new PercentileFpsResult(value, true, true, true, sampleCount, time0, time1, frames, rawValue, "rtssNative");
    }
}
