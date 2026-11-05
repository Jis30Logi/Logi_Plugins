namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Contract for retrieving display information such as primary screen refresh rate.
    /// </summary>
    public interface IDisplayInfoService
    {
        /// <summary>
        /// Gets the primary display refresh rate in Hz, or 0 if unavailable.
        /// </summary>
        Int32 GetPrimaryDisplayRefreshRate();
    }

    /// <summary>
    /// Safe, non-blocking Win32 implementation to query primary display refresh rate.
    /// </summary>
    public sealed class DisplayInfoService : IDisplayInfoService
    {
        private const Int32 ENUM_CURRENT_SETTINGS = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DEVMODE
        {
            private const Int32 CCHDEVICENAME = 32;
            private const Int32 CCHFORMNAME = 32;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
            public String dmDeviceName;
            public Int16 dmSpecVersion;
            public Int16 dmDriverVersion;
            public Int16 dmSize;
            public Int16 dmDriverExtra;
            public Int32 dmFields;

            public Int32 dmPositionX;
            public Int32 dmPositionY;
            public Int32 dmDisplayOrientation;
            public Int32 dmDisplayFixedOutput;

            public Int16 dmColor;
            public Int16 dmDuplex;
            public Int16 dmYResolution;
            public Int16 dmTTOption;
            public Int16 dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
            public String dmFormName;
            public Int16 dmLogPixels;
            public Int32 dmBitsPerPel;
            public Int32 dmPelsWidth;
            public Int32 dmPelsHeight;
            public Int32 dmDisplayFlags;
            public Int32 dmDisplayFrequency;
            public Int32 dmICMMethod;
            public Int32 dmICMIntent;
            public Int32 dmMediaType;
            public Int32 dmDitherType;
            public Int32 dmReserved1;
            public Int32 dmReserved2;
            public Int32 dmPanningWidth;
            public Int32 dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern Boolean EnumDisplaySettings(String lpszDeviceName, Int32 iModeNum, ref DEVMODE lpDevMode);

        private readonly Func<Int32> _customRefreshRateProvider;

        public DisplayInfoService(Func<Int32> customRefreshRateProvider = null)
        {
            this._customRefreshRateProvider = customRefreshRateProvider;
        }

        public Int32 GetPrimaryDisplayRefreshRate()
        {
            if (this._customRefreshRateProvider != null)
            {
                try
                {
                    return this._customRefreshRateProvider();
                }
                catch
                {
                    return 0;
                }
            }

            try
            {
                var devMode = new DEVMODE();
                devMode.dmSize = (Int16)Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref devMode))
                {
                    var freq = devMode.dmDisplayFrequency;
                    if (freq > 0 && freq <= 1000)
                    {
                        // Normalize standard fractional refresh rates reported as integers (e.g. 59.94Hz -> 59 -> 60)
                        if (freq == 59) freq = 60;
                        else if (freq == 119) freq = 120;
                        else if (freq == 143) freq = 144;
                        else if (freq == 179) freq = 180;
                        else if (freq == 239) freq = 240;
                        return freq;
                    }
                }
            }
            catch
            {
                // Graceful fallback on non-standard displays or restricted environments
            }

            return 0;
        }
    }
}
