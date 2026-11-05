namespace Loupedeck.RTSSPerformancePlugin.Helpers
{
    using System;

    using Loupedeck.RTSSPerformancePlugin.Services;

    public enum ColorTier
    {
        /// <summary>No data.</summary>
        Neutral = 0,

        /// <summary>Well below target.</summary>
        Critical = 1,

        /// <summary>Below target but playable.</summary>
        Warning = 2,

        /// <summary>At or above target.</summary>
        Good = 3,
    }

    /// <summary>
    /// Maps FPS values and health states onto the plugin's key colour palette.
    /// </summary>
    public static class ColorTierHelper
    {
        public static ColorTier GetTier(Double fps, Int32 targetFps) =>
            GetTier(FpsHealthStateService.GetTier(fps, targetFps));

        public static ColorTier GetTier(FpsTier fpsTier)
        {
            switch (fpsTier)
            {
                case FpsTier.Green:
                    return ColorTier.Good;

                case FpsTier.Yellow:
                    return ColorTier.Warning;

                case FpsTier.Red:
                    return ColorTier.Critical;

                default:
                    return ColorTier.Neutral;
            }
        }

        /// <summary>
        /// Direct, immediate tier for a current-load percentage (CPU/GPU usage): no target to
        /// grade against, just fixed bands on the reading itself — 0-69 Good, 70-89 Warning,
        /// 90-100 Critical. Unlike <see cref="GetTier(FpsTier)"/>'s FPS tiers, this has no debounce
        /// or hysteresis: the tier changes on the very next reading, so the boundaries (70, 90) are
        /// exact.
        /// </summary>
        public static ColorTier GetLoadTier(Double loadPercent) =>
            loadPercent >= 90 ? ColorTier.Critical :
            loadPercent >= 70 ? ColorTier.Warning :
            ColorTier.Good;

        public static ColorTier GetRelativeTier(Double currentValue, Double referenceMaximum)
        {
            if (referenceMaximum <= 0 || Double.IsNaN(currentValue) || Double.IsInfinity(currentValue))
            {
                return ColorTier.Neutral;
            }

            var relativePercent = Math.Min(100, Math.Max(0, currentValue / referenceMaximum * 100));
            return GetLoadTier(relativePercent);
        }

        /// <summary>Frame time bands (ms): up to 16.67ms Good (~60 FPS), 16.67-33.34ms Warning (~30-60 FPS), above 33.34ms Critical (&lt;~30 FPS).</summary>
        public static ColorTier GetFrameTimeTier(Double frameTimeMilliseconds)
        {
            if (frameTimeMilliseconds <= 0 || Double.IsNaN(frameTimeMilliseconds) || Double.IsInfinity(frameTimeMilliseconds))
            {
                return ColorTier.Neutral;
            }

            return frameTimeMilliseconds > 33.34 ? ColorTier.Critical :
                frameTimeMilliseconds > 16.67 ? ColorTier.Warning :
                ColorTier.Good;
        }

        /// <summary>CPU temperature bands: below 70C Good, 70-84C Warning, 85C and above Critical.</summary>
        public static ColorTier GetTemperatureTier(Double temperatureCelsius) =>
            temperatureCelsius >= 85 ? ColorTier.Critical :
            temperatureCelsius >= 70 ? ColorTier.Warning :
            ColorTier.Good;

        public static ColorTier GetTier(FpsHealthState healthState)
        {
            switch (healthState)
            {
                case FpsHealthState.Healthy:
                    return ColorTier.Good;

                case FpsHealthState.Degraded:
                    return ColorTier.Critical;

                default:
                    return ColorTier.Neutral;
            }
        }

        public static BitmapColor GetBackgroundColor(ColorTier tier)
        {
            switch (tier)
            {
                case ColorTier.Good:
                    return new BitmapColor(20, 78, 42);

                case ColorTier.Warning:
                    return new BitmapColor(112, 82, 16);

                case ColorTier.Critical:
                    return new BitmapColor(116, 30, 30);

                default:
                    return new BitmapColor(28, 28, 30);
            }
        }

        /// <summary>
        /// The health accent: the one saturated colour on the key, carried by the numerals and the
        /// status rule rather than by the ground.
        ///
        /// Tuned for a dark-mode micro display. Each is bright enough to read as a signal against
        /// near-black at 8.9:1, 10.3:1 and 5.7:1 respectively, and separated far enough in hue that
        /// they are not confusable at a glance or to a red/green deficient eye — the amber sits
        /// well clear of the green in luminance as well as hue, so the three tiers differ in
        /// brightness even where they do not differ in colour.
        ///
        /// Neutral has no accent by design. Absence of colour is the "no reading" state, which is
        /// a stronger signal than a fourth grey would be.
        /// </summary>
        public static BitmapColor GetAccentColor(ColorTier tier)
        {
            switch (tier)
            {
                case ColorTier.Good:
                    return new BitmapColor(60, 203, 110);

                case ColorTier.Warning:
                    return new BitmapColor(255, 176, 32);

                case ColorTier.Critical:
                    return new BitmapColor(255, 77, 77);

                default:
                    return NeutralText;
            }
        }

        /// <summary>
        /// Ground for the primary key, on every tier. A very dark neutral rather than black: at
        /// pure black the display's own bezel and the key edge stop being distinguishable, and the
        /// antialiasing under the numerals has nothing to sit on.
        /// </summary>
        public static readonly BitmapColor KeyBackground = new BitmapColor(14, 16, 19);

        /// <summary>Type colour where there is no tier to report. Soft, not dim.</summary>
        public static readonly BitmapColor NeutralText = new BitmapColor(200, 203, 210);

        /// <summary>
        /// The unit label and the bottleneck mark, on every tier. Deliberately never tinted: the
        /// accent means something only while it is the single coloured thing on the key.
        /// </summary>
        public static readonly BitmapColor NeutralLabel = new BitmapColor(139, 146, 158);

        /// <summary>#RRGGBB, for logging what the renderer actually resolved.</summary>
        public static String ToHex(BitmapColor color) =>
            $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        public static String GetTierName(ColorTier tier)
        {
            switch (tier)
            {
                case ColorTier.Good:
                    return "Good";

                case ColorTier.Warning:
                    return "Warning";

                case ColorTier.Critical:
                    return "Critical";

                default:
                    return "No data";
            }
        }
    }
}
