namespace Loupedeck.RTSSPerformancePlugin.Helpers
{
    using System;
    using System.Globalization;

    using SkiaSharp;

    using Loupedeck.RTSSPerformancePlugin.Services;

    /// <summary>
    /// Shared render helpers for the live RTSS telemetry keys.
    /// </summary>
    public static class KeyRenderer 
    {
        public const String NumberFontName = "Agency FB";
        public const String LabelFontName = NumberFontName;

        // ── LCD Design System ─────────────────────────────────────────────────
        // Reference canvas: 256 × 256 px.  Safe margin: 8 px per side → 240 px usable.
        //
        // Zone layout (top→bottom, ratios of height):
        //   0.03–0.31  Icon zone  — icon sits lower, clear of the top border
        //   0.32–0.76  Value zone — primary numeric value, large and centred
        //   0.77–0.81  Accent rule
        //   0.82–0.95  Label / unit zone
        //
        // CanonicalIconSlotOffset: positive = shift icon group downward inside its
        // SVG wrapper so icons no longer bleed against the top edge of the LCD key.
        // CanonicalIconSlotScale: slight reduction to keep the icon fully within
        // the new zone after the downward shift.
        public const Int32 CanonicalIconSlotOffset = 6;
        public const Double CanonicalIconSlotScale = 1.05;

        public const Int32 IconOnlyArtworkOffset = 30;
        public const Int32 IconOnlyArtworkSize = 196;
        public const Int32 IconOnlyLabelSize = 32;

        public const Int32 ProfileNameFontSize = 40;

        public const Int32 ActiveProfileFontSize = 32;

        public const Int32 FpsPresetValueFontSize = 90;

        public const Int32 FpsPresetValueFontSizeCompact = 74;

        public const Int32 FpsPresetStatusFontSize = 24;

        public const Int32 FpsPresetStatusFontSizeLarge = 28;

        // Control action state text (OSD / Detection Level / Stealth Mode).
        // Slightly lower than the old 224 so long words such as "MEDIUM" clear
        // the bottom of the icon artwork without being clipped at the LCD edge.
        public const Int32 ControlStateTextY = 236;

        // Font sizes for the two-character-length buckets used by ReadControlActionSvg.
        // ControlStateFontSizeShort: short words ("ON", "OFF", "LOW", "HIGH", "NONE")
        // ControlStateFontSizeLong:  longer words ("MEDIUM")
        public const Int32 ControlStateFontSizeShort = 42;
        public const Int32 ControlStateFontSizeLong = 36;

        public const Double LiveLabelRatio = 0.15;
        public const Double LiveUnitRatio = 0.17;

        // Primary-value font-size ratios.  Values are multiplied by the LCD height to
        // obtain the target font size in pixels.  Larger ratios → bigger numerals.
        //   SevenPlus  ≥7 chars  e.g. "16.7 ms"
        //   SevenPlus  ≥7 chars  e.g. "16.7 ms"
        //   Six        6 chars   e.g. "999 ms"
        //   Standard   4–5 chars e.g. "100%", "1000 FPS"
        //   Compact    1–3 chars e.g. "0%", "99%"
        public const Double LiveValueSevenPlusRatio = 0.30;
        public const Double LiveValueSixRatio = 0.32;
        public const Double LiveValueStandardRatio = 0.38;
        public const Double LiveValueCompactRatio = 0.46;

        // Frame Time value ratio stepped down by one size level (from 0.30 to 0.25) to prevent overlapping layout.
        public const Double FrameTimeValueRatio = 0.25;

        public static Int32 GetFrameTimeFontSize(Int32 height, Int32 digits) =>
            (Int32)Math.Round(height * FrameTimeValueRatio);

        public static Int32 GetCpuUsageFontSize(Int32 height, Int32 digits) =>
            GetHardwareUsageFontSize(height, digits);

        public static Int32 GetGpuUsageFontSize(Int32 height, Int32 digits) =>
            GetHardwareUsageFontSize(height, digits);

        public static Int32 GetHardwareUsageFontSize(Int32 height, Int32 digits)
        {
            // Stepped down one level: 4+ chars ("100%") uses LiveValueSixRatio (0.32), 1-3 chars ("50%") uses LiveValueStandardRatio (0.38)
            var ratio = digits >= 4 ? LiveValueSixRatio : LiveValueStandardRatio;
            return (Int32)Math.Round(height * ratio);
        }

        private static readonly BitmapColor CardBackground =
            new BitmapColor(18, 21, 26);

        private static readonly BitmapColor LabelColor =
            new BitmapColor(191, 201, 214);

        public static readonly BitmapColor BackAccent =
            new BitmapColor(124, 146, 178);

        public static readonly BitmapColor IncreaseAccent =
            new BitmapColor(62, 213, 152);

        public static readonly BitmapColor DecreaseAccent =
            new BitmapColor(232, 150, 60);

        public static readonly BitmapColor UnavailableAccent =
            new BitmapColor(86, 96, 112);

        public static BitmapImage RenderPrimaryFpsKey(
            PluginImageSize imageSize,
            Boolean hasLiveData,
            Double fps,
            BitmapColor? tierAccent,
            String badge = null,
            Boolean isRtssAvailable = true)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var height = builder.Height;

                builder.Clear(BitmapColor.Transparent);

                if (!hasLiveData)
                {
                    return RenderUnavailableActionIcon(
                        imageSize,
                        "FpsStatusCommand",
                        "FPS",
                        isRtssAvailable: isRtssAvailable);
                }

                var reading =
                    Math.Round(fps)
                        .ToString("0", CultureInfo.InvariantCulture);

                DrawCentredLine(
                    builder,
                    reading,
                    NumberSize(height, reading.Length),
                    NumberBandTop(height),
                    NumberBandHeight(height),
                    tierAccent ??
                    ColorTierHelper.NeutralText,
                    NumberFontName);

                var accent =
                    tierAccent ??
                    ColorTierHelper.NeutralText;

                DrawStatusRule(
                    builder,
                    accent);

                if (!String.IsNullOrEmpty(badge))
                {
                    DrawCornerBadge(
                        builder,
                        badge);
                }

                return builder.ToImage();
            }
        }

        public static BitmapImage RenderFrameTimeKey(
            PluginImageSize imageSize,
            Boolean hasLiveData,
            Double frameTimeMilliseconds,
            Boolean isRtssAvailable = true)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var height = builder.Height;

                builder.Clear(BitmapColor.Transparent);

                if (!hasLiveData)
                {
                    return RenderUnavailableActionIcon(
                        imageSize,
                        "FrameTimeStatusCommand",
                        "FRAME TIME",
                        isRtssAvailable: isRtssAvailable);
                }

                var reading =
                    FormatFrameTime(frameTimeMilliseconds) + " ms";

                var tier =
                    ColorTierHelper.GetFrameTimeTier(
                        frameTimeMilliseconds);

                var accent =
                    ColorTierHelper.GetAccentColor(tier);

                DrawCentredLine(
                    builder,
                    reading,
                    GetFrameTimeFontSize(height, reading.Length),
                    NumberBandTop(height),
                    NumberBandHeight(height),
                    accent,
                    NumberFontName);

                DrawStatusRule(
                    builder,
                    accent);

                return builder.ToImage();
            }
        }

        private static Boolean IsUsable(Double value) =>
            value >= 0 && !Double.IsNaN(value) && !Double.IsInfinity(value);

        public static BitmapImage RenderPercentileLowKey(
            PluginImageSize imageSize,
            Boolean hasLiveData,
            Double onePercentLowFps,
            Boolean isRtssAvailable = true)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var height = builder.Height;

                builder.Clear(BitmapColor.Transparent);

                var rounded = FormatPercentileLow(onePercentLowFps);

                if (!hasLiveData || !IsUsable(onePercentLowFps))
                {
                    return RenderUnavailableActionIcon(
                        imageSize,
                        "OnePercentLowStatusCommand",
                        "1% LOW",
                        isRtssAvailable: isRtssAvailable);
                }

                var reading =
                    rounded.ToString(
                        "0",
                        CultureInfo.InvariantCulture);

                DrawCentredLine(
                    builder,
                    reading,
                    NumberSize(height, reading.Length),
                    NumberBandTop(height),
                    NumberBandHeight(height),
                    ColorTierHelper.NeutralText,
                    NumberFontName);

                DrawStatusRule(
                    builder,
                    ColorTierHelper.NeutralText);

                return builder.ToImage();
            }
        }

        public static BitmapImage RenderRawOnePercentLowDiagnosticKey(
            PluginImageSize imageSize,
            UInt32 rawValue)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var height = builder.Height;

                builder.Clear(BitmapColor.Transparent);

                var line1 = "RAW 1%";
                var line2 = rawValue.ToString(CultureInfo.InvariantCulture);

                DrawCentredLine(
                    builder,
                    line1,
                    Math.Max(10, (Int32)Math.Round(height * 0.16)),
                    (Int32)Math.Round(height * 0.16),
                    (Int32)Math.Round(height * 0.20),
                    ColorTierHelper.NeutralLabel,
                    LabelFontName);

                DrawCentredLine(
                    builder,
                    line2,
                    NumberSize(height, line2.Length),
                    NumberBandTop(height),
                    NumberBandHeight(height),
                    ColorTierHelper.NeutralText,
                    NumberFontName);

                DrawStatusRule(
                    builder,
                    ColorTierHelper.NeutralText);

                return builder.ToImage();
            }
        }

        internal static String BuildPercentileLowArtworkSvg(String reading)
        {
            var iconResource = PluginResources.GetActionIcon("OnePercentLowStatusCommand");
            var iconSvg = PluginResources.ReadText(iconResource);
            if (String.IsNullOrWhiteSpace(iconSvg))
            {
                return null;
            }

            var contentStart = iconSvg.IndexOf('>') + 1;
            var contentEnd = iconSvg.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
            if (contentStart <= 0 || contentEnd <= contentStart)
            {
                return null;
            }

            const Double iconScale = 0.36;
            const Double iconTop = -5.0;
            var iconLeft = (256.0 - (256.0 * iconScale)) / 2.0;
            var numberFontSize = NumberSize(170, reading?.Length ?? 0);
            var escapedReading = System.Security.SecurityElement.Escape(reading ?? String.Empty);
            var iconContent = iconSvg.Substring(contentStart, contentEnd - contentStart);

            return String.Concat(
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"256\" height=\"256\" viewBox=\"0 0 256 256\">",
                "<g transform=\"translate(", iconLeft.ToString("0.##", CultureInfo.InvariantCulture), " ",
                iconTop.ToString("0.##", CultureInfo.InvariantCulture), ") scale(",
                iconScale.ToString("0.##", CultureInfo.InvariantCulture), " )\">",
                iconContent,
                "</g>",
                "<text x=\"128\" y=\"187\" text-anchor=\"middle\" font-family=\"",
                NumberFontName,
                "\" font-size=\"", numberFontSize.ToString(CultureInfo.InvariantCulture),
                "\" font-weight=\"600\" fill=\"#C8CBD2\">", escapedReading, "</text>",
                "<rect x=\"84.5\" y=\"197\" width=\"87\" height=\"10\" fill=\"#C8CBD2\"/>",
                "</svg>");
        }

        public static Double FormatPercentileLow(Double value)
        {
            return Math.Round(value);
        }

        public static BitmapImage RenderGpuStatusKey(
            PluginImageSize imageSize,
            Boolean hasLiveData,
            Double loadPercent) =>
            RenderComponentLoadKey(
                imageSize,
                hasLiveData,
                loadPercent,
                "GPU",
                "%",
                "GpuStatusCommand",
                emphasizeValue: true);

        public static BitmapImage RenderCpuUsageKey(
            PluginImageSize imageSize,
            Boolean hasLiveData,
            Double loadPercent,
            Boolean isRtssAvailable = true) =>
            RenderComponentLoadKey(
                imageSize,
                hasLiveData,
                loadPercent,
                "CPU",
                "%",
                "CpuUsageCommand",
                emphasizeValue: false,
                isRtssAvailable: isRtssAvailable,
                reducedFontSize: true);

        public static BitmapImage RenderGpuUsageKey(
            PluginImageSize imageSize,
            Boolean hasLiveData,
            Double loadPercent,
            Boolean isRtssAvailable = true) =>
            RenderComponentLoadKey(
                imageSize,
                hasLiveData,
                loadPercent,
                "GPU",
                "%",
                "GpuUsageCommand",
                emphasizeValue: false,
                isRtssAvailable: isRtssAvailable,
                reducedFontSize: true);

        public static BitmapImage RenderCpuClockKey(
            PluginImageSize imageSize,
            Boolean hasLiveData,
            Double clockMegahertz,
            Double referenceMaximum = 0) =>
            RenderCpuClockValueKey(
                imageSize,
                hasLiveData,
                clockMegahertz,
                "CpuClockStatusCommand",
                referenceMaximum);

        public static BitmapImage RenderGpuClockKey(
            PluginImageSize imageSize,
            Boolean hasLiveData,
            Double clockMegahertz) =>
            RenderComponentLoadKey(
                imageSize,
                hasLiveData,
                clockMegahertz,
                "GPU MHz",
                " MHz",
                "GpuClockStatusCommand");

        public static BitmapImage RenderVramUsageKey(
            PluginImageSize imageSize,
            Boolean hasLiveData,
            Double loadPercent) =>
            RenderComponentLoadKey(
                imageSize,
                hasLiveData,
                loadPercent,
                "VRAM",
                "%",
                "VramUsageStatusCommand");

        public static BitmapImage RenderFpsAdjustmentKey(
            PluginImageSize imageSize,
            Double currentFpsCap,
            Boolean canAdjust,
            Boolean isIncrease)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var height = builder.Height;

                builder.Clear(BitmapColor.Transparent);

                var displayFps =
                    Math.Round(currentFpsCap)
                        .ToString(
                            "0",
                            CultureInfo.InvariantCulture) +
                    " FPS";

                var fpsValueColor =
                    IncreaseAccent;

                DrawCentredLine(
                    builder,
                    displayFps,
                    NumberSize(height, displayFps.Length),
                    NumberBandTop(height),
                    NumberBandHeight(height),
                    fpsValueColor,
                    NumberFontName);

                DrawStatusRule(
                    builder,
                    fpsValueColor);

                return builder.ToImage();
            }
        }

        public static BitmapImage RenderUnavailableKey(
            PluginImageSize imageSize,
            String actionTypeName,
            String label) =>
            RenderUnavailableActionIcon(
                imageSize,
                actionTypeName,
                label);

        private static BitmapImage RenderStatusKey(
            PluginImageSize imageSize,
            String title,
            String label)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var height = builder.Height;

                builder.Clear(BitmapColor.Transparent);

                if (label == "..." &&
                    title == "THERMAL")
                {
                    var icon =
                        RenderUnavailableActionIcon(
                            imageSize,
                            "ThermalStatusCommand",
                            "THERMAL");

                    if (icon != null)
                    {
                        return icon;
                    }
                }

                var fontSize =
                    label switch
                    {
                        "..." => Math.Max(
                            12,
                            (Int32)Math.Round(
                                height * 0.23)),

                        _ => Math.Max(
                            14,
                            (Int32)Math.Round(
                                height * 0.24))
                    };

                DrawCentredLine(
                    builder,
                    label,
                    fontSize,
                    (Int32)Math.Round(height * 0.38),
                    (Int32)Math.Round(height * 0.35),
                    ColorTierHelper.NeutralText,
                    NumberFontName);

                return builder.ToImage();
            }
        }

        public static BitmapImage RenderFpsAdvisorKey(
            PluginImageSize imageSize,
            String label) =>
            RenderFpsAdvisorKey(
                imageSize,
                label,
                true,
                ColorTier.Neutral,
                true);

        public static BitmapImage RenderFpsAdvisorKey(
            PluginImageSize imageSize,
            String label,
            Boolean hasForegroundGame,
            Boolean isRtssAvailable = true) =>
            RenderFpsAdvisorKey(
                imageSize,
                label,
                hasForegroundGame,
                ColorTier.Neutral,
                isRtssAvailable);

        public static BitmapImage RenderFpsAdvisorKey(
            PluginImageSize imageSize,
            String label,
            Boolean hasForegroundGame,
            ColorTier tier,
            Boolean isRtssAvailable = true)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var height = builder.Height;

                builder.Clear(BitmapColor.Transparent);

                if (!hasForegroundGame || String.IsNullOrWhiteSpace(label))
                {
                    return RenderUnavailableActionIcon(
                        imageSize,
                        "FpsAdvisorCommand",
                        "FPS ADVISOR",
                        isRtssAvailable: isRtssAvailable);
                }

                if (String.Equals(
                    label,
                    "FPS STABLE",
                    StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(
                    label,
                    "STABLE FPS",
                    StringComparison.OrdinalIgnoreCase))
                {
                    DrawCentredLine(
                        builder,
                        "STABLE",
                        Math.Max(
                            18,
                            (Int32)Math.Round(
                                height * 0.28)),
                        (Int32)Math.Round(
                            height * 0.28),
                        (Int32)Math.Round(
                            height * 0.26),
                        ColorTierHelper.GetAccentColor(
                            ColorTier.Good),
                        NumberFontName);

                    DrawCentredLine(
                        builder,
                        "FPS",
                        Math.Max(
                            18,
                            (Int32)Math.Round(
                                height * 0.24)),
                        (Int32)Math.Round(
                            height * 0.54),
                        (Int32)Math.Round(
                            height * 0.24),
                        ColorTierHelper.GetAccentColor(
                            ColorTier.Good),
                        NumberFontName);
                }
                else if (String.Equals(
                    label,
                    "FPS RECOVERY",
                    StringComparison.OrdinalIgnoreCase))
                {
                    var color = ColorTierHelper.GetAccentColor(ColorTier.Critical);

                    DrawCentredLine(
                        builder,
                        "FPS",
                        Math.Max(
                            18,
                            (Int32)Math.Round(
                                height * 0.24)),
                        (Int32)Math.Round(
                            height * 0.28),
                        (Int32)Math.Round(
                            height * 0.24),
                        color,
                        NumberFontName);

                    DrawCentredLine(
                        builder,
                        "RECOVERY",
                        Math.Max(
                            14,
                            (Int32)Math.Round(
                                height * 0.19)),
                        (Int32)Math.Round(
                            height * 0.54),
                        (Int32)Math.Round(
                            height * 0.24),
                        color,
                        NumberFontName);
                }
                else if (
                    FpsAdvisorService.TryDecodeRecommendedFps(
                        label,
                        out var recommendedFps) &&
                    recommendedFps > 0)
                {
                    var targetText = recommendedFps + " FPS";

                    var isTry = label.StartsWith("TRY", StringComparison.OrdinalIgnoreCase);
                    var headerText = isTry ? "TRY" : "RECOMMENDED";
                    var headerFontRatio = isTry ? 0.22 : 0.14;
                    var headerColor = isTry
                        ? ColorTierHelper.GetAccentColor(ColorTier.Critical)
                        : ColorTierHelper.GetAccentColor(ColorTier.Warning);

                    // Two-line layout: "RECOMMENDED" or "TRY" on line 1, target on line 2
                    DrawCentredLine(
                        builder,
                        headerText,
                        Math.Max(
                            14,
                            (Int32)Math.Round(
                                height * headerFontRatio)),
                        (Int32)Math.Round(
                            height * 0.26),
                        (Int32)Math.Round(
                            height * 0.24),
                        headerColor,
                        NumberFontName);

                    var targetFontRatio =
                        targetText.Length >= 9
                            ? 0.21
                            : 0.25;

                    var displayTarget = isTry ? targetText + "?" : targetText;

                    DrawCentredLine(
                        builder,
                        displayTarget,
                        Math.Max(
                            14,
                            (Int32)Math.Round(
                                height * targetFontRatio)),
                        (Int32)Math.Round(
                            height * 0.52),
                        (Int32)Math.Round(
                            height * 0.26),
                        headerColor,
                        NumberFontName);
                }
                else if (label.EndsWith(" LIMITED", StringComparison.OrdinalIgnoreCase))
                {
                    var color = tier != ColorTier.Neutral
                        ? ColorTierHelper.GetAccentColor(tier)
                        : ColorTierHelper.GetAccentColor(ColorTier.Critical);

                    var prefixLength = label.Length - " LIMITED".Length;
                    var firstLine = label.Substring(0, prefixLength).Trim();
                    var secondLine = "LIMITED";

                    var fontRatio =
                        firstLine.Length >= 11
                            ? 0.17
                            : firstLine.Length >= 9
                                ? 0.20
                                : firstLine.Length >= 7
                                    ? 0.24
                                    : firstLine.Length >= 5
                                        ? 0.28
                                        : 0.32;

                    DrawCentredLine(
                        builder,
                        firstLine,
                        Math.Max(
                            14,
                            (Int32)Math.Round(
                                height * fontRatio)),
                        (Int32)Math.Round(
                            height * 0.28),
                        (Int32)Math.Round(
                            height * 0.24),
                        color,
                        NumberFontName);

                    DrawCentredLine(
                        builder,
                        secondLine,
                        Math.Max(
                            18,
                            (Int32)Math.Round(
                                height * 0.26)),
                        (Int32)Math.Round(
                            height * 0.54),
                        (Int32)Math.Round(
                            height * 0.24),
                        color,
                        NumberFontName);
                }
                else
                {
                    return RenderUnavailableActionIcon(
                        imageSize,
                        "FpsAdvisorCommand",
                        "FPS ADVISOR",
                        isRtssAvailable: isRtssAvailable);
                }

                return builder.ToImage();
            }
        }

        public static BitmapImage RenderBottleneckStatusKey(
            PluginImageSize imageSize,
            String label,
            Boolean isRtssAvailable = true)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var height = builder.Height;

                builder.Clear(BitmapColor.Transparent);

                if (label == "...")
                {
                    var icon =
                        RenderUnavailableActionIcon(
                            imageSize,
                            "PerformanceStatusCommand",
                            "PERFORMANCE",
                            isRtssAvailable: isRtssAvailable);

                    if (icon != null)
                    {
                        return icon;
                    }
                }

                var state =
                    label switch
                    {
                        "CPU" =>
                            BottleneckAnalysisState.CpuBound,

                        "GPU" =>
                            BottleneckAnalysisState.GpuBound,

                        "OK" =>
                            BottleneckAnalysisState.Balanced,

                        _ =>
                            BottleneckAnalysisState.Unknown
                    };

                return RenderBottleneckStatusKey(
                    imageSize,
                    state,
                    true,
                    isRtssAvailable);
            }
        }

        private static SKTypeface _statusTypeface;
        private static readonly Object _statusTypefaceLock = new Object();

        private static SKTypeface GetStatusTypeface()
        {
            if (_statusTypeface == null)
            {
                lock (_statusTypefaceLock)
                {
                    if (_statusTypeface == null)
                    {
                        try
                        {
                            _statusTypeface = SKTypeface.FromFamilyName(
                                NumberFontName,
                                SKFontStyleWeight.Bold,
                                SKFontStyleWidth.Normal,
                                SKFontStyleSlant.Upright)
                                ?? SKTypeface.FromFamilyName(NumberFontName)
                                ?? SKTypeface.Default;
                        }
                        catch
                        {
                            _statusTypeface = SKTypeface.Default;
                        }
                    }
                }
            }

            return _statusTypeface;
        }

        private static (Double Width, Double Height) MeasureStatusTextExtent(String text, Single fontSize)
        {
            if (String.IsNullOrEmpty(text))
            {
                return (0.0, 0.0);
            }

            try
            {
                using var paint = new SKPaint
                {
                    Typeface = GetStatusTypeface(),
                    TextSize = fontSize,
                    IsAntialias = true
                };

                var bounds = new SKRect();
                var advanceWidth = paint.MeasureText(text, ref bounds);
                var inkWidth = bounds.Width;
                var width = Math.Max(advanceWidth, inkWidth);

                var metrics = paint.FontMetrics;
                var fontHeight = metrics.Descent - metrics.Ascent;
                var height = Math.Max(fontHeight, bounds.Height);

                return (width, height);
            }
            catch
            {
                // Fallback estimate for condensed NumberFontName (Agency FB) in restricted environments
                var fallbackWidth = text.Length * fontSize * 0.40;
                var fallbackHeight = fontSize * 1.20;
                return (fallbackWidth, fallbackHeight);
            }
        }

        private static (String FirstLine, String SecondLine) SplitPerformanceStatusLabel(String label)
        {
            if (String.IsNullOrWhiteSpace(label))
            {
                return (String.Empty, String.Empty);
            }

            var trimmed = label.Trim();

            if (String.Equals(trimmed, "BALANCED", StringComparison.OrdinalIgnoreCase))
            {
                return ("BALANCED", String.Empty);
            }

            if (String.Equals(trimmed, "ALL GOOD", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "ALL\nGOOD", StringComparison.OrdinalIgnoreCase))
            {
                return ("ALL", "GOOD");
            }

            if (String.Equals(trimmed, "PERFORMANCE WATCH", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "PERFORMANCE\nWATCH", StringComparison.OrdinalIgnoreCase))
            {
                return ("PERFORMANCE", "WATCH");
            }

            if (String.Equals(trimmed, "CPU LOAD HIGH", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "CPU LOAD\nHIGH", StringComparison.OrdinalIgnoreCase))
            {
                return ("CPU LOAD", "HIGH");
            }

            if (String.Equals(trimmed, "GPU LOAD HIGH", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "GPU LOAD\nHIGH", StringComparison.OrdinalIgnoreCase))
            {
                return ("GPU LOAD", "HIGH");
            }

            if (String.Equals(trimmed, "FPS DROPPING", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "FPS\nDROPPING", StringComparison.OrdinalIgnoreCase))
            {
                return ("FPS", "DROPPING");
            }

            if (String.Equals(trimmed, "MEMORY PRESSURE", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "MEMORY\nPRESSURE", StringComparison.OrdinalIgnoreCase))
            {
                return ("MEMORY", "PRESSURE");
            }

            if (String.Equals(trimmed, "RAM LIMITED", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "RAM\nLIMITED", StringComparison.OrdinalIgnoreCase))
            {
                return ("RAM", "LIMITED");
            }

            if (trimmed.Contains("\n"))
            {
                var parts = trimmed.Split(new[] { '\n' }, 2, StringSplitOptions.RemoveEmptyEntries);
                return (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : String.Empty);
            }

            if (trimmed.EndsWith(" LIMITED", StringComparison.OrdinalIgnoreCase))
            {
                var prefix = trimmed.Substring(0, trimmed.Length - " LIMITED".Length).Trim();
                return (prefix, "LIMITED");
            }

            var spaceIndex = trimmed.IndexOf(' ');
            if (spaceIndex > 0)
            {
                return (trimmed.Substring(0, spaceIndex).Trim(), trimmed.Substring(spaceIndex + 1).Trim());
            }

            return (trimmed, String.Empty);
        }

        private static Int32 EnsureTextFitsWidth(String text, Int32 startFontSize, Int32 maxUsableWidth, Int32 minFontSize)
        {
            var size = startFontSize;
            while (size > minFontSize)
            {
                var (w, _) = MeasureStatusTextExtent(text, size);
                if (w * 1.015 <= maxUsableWidth)
                {
                    return size;
                }

                size--;
            }

            return minFontSize;
        }

        internal static (Int32 FontSize, Int32 BandHeight, Int32 LineSpacing, Int32 TotalHeight) FitStatusTypography(
            String line1,
            String line2,
            Int32 canvasWidth,
            Int32 canvasHeight)
        {
            var isTwoLines = !String.IsNullOrEmpty(line2);

            // Safe internal margins: ensures ink pixels stay strictly within safe margins (safeMargin >= 2 on 90, >= 8 on 256)
            var horizontalPadding = canvasWidth <= 128 ? 2 : 8;
            var verticalPadding = canvasHeight <= 128 ? 2 : 8;

            var maxUsableWidth = canvasWidth - (2 * horizontalPadding);
            var maxUsableHeight = canvasHeight - (2 * verticalPadding);

            var maxStart = isTwoLines
                ? (Int32)Math.Round(canvasHeight * 0.30)
                : (Int32)Math.Round(canvasHeight * 0.34);

            var minSize = Math.Max(8, (Int32)Math.Round(canvasHeight * 0.08));
            var fontSize = Math.Max(maxStart, minSize);

            while (fontSize > minSize)
            {
                var (w1, h1) = MeasureStatusTextExtent(line1, fontSize);
                var (w2, h2) = isTwoLines ? MeasureStatusTextExtent(line2, fontSize) : (0.0, 0.0);

                var maxLineWidth = Math.Max(w1, w2);
                var bandH = (Int32)Math.Ceiling(Math.Max(h1, h2) * 1.05);
                var spacing = isTwoLines ? Math.Max(2, (Int32)Math.Round(fontSize * 0.12)) : 0;
                var totalH = isTwoLines ? (2 * bandH + spacing) : bandH;

                // 1.015 safety factor accounts for rasterization kerning and subpixel anti-aliasing
                if (maxLineWidth * 1.015 <= maxUsableWidth && totalH <= maxUsableHeight)
                {
                    return (fontSize, bandH, spacing, totalH);
                }

                fontSize--;
            }

            // Fallback at minSize
            {
                var (_, h1) = MeasureStatusTextExtent(line1, minSize);
                var (_, h2) = isTwoLines ? MeasureStatusTextExtent(line2, minSize) : (0.0, 0.0);
                var bandH = (Int32)Math.Ceiling(Math.Max(h1, h2) * 1.05);
                var spacing = isTwoLines ? Math.Max(2, (Int32)Math.Round(minSize * 0.12)) : 0;
                var totalH = isTwoLines ? (2 * bandH + spacing) : bandH;
                return (minSize, bandH, spacing, totalH);
            }
        }

        public static BitmapImage RenderPerformanceStatusLabelKey(
            PluginImageSize imageSize,
            String label,
            ColorTier tier,
            Boolean isRtssAvailable = true)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var width = builder.Width;
                var height = builder.Height;

                builder.Clear(BitmapColor.Transparent);

                if (String.IsNullOrWhiteSpace(label) ||
                    label == "NO GAME" ||
                    label == "UNKNOWN" ||
                    label == "...")
                {
                    return RenderUnavailableActionIcon(
                        imageSize,
                        "PerformanceStatusCommand",
                        "PERFORMANCE",
                        isRtssAvailable: isRtssAvailable);
                }

                var isBalanced = String.Equals(label, "BALANCED", StringComparison.OrdinalIgnoreCase) ||
                                 String.Equals(label, "ALL GOOD", StringComparison.OrdinalIgnoreCase);

                var color = isBalanced
                    ? ColorTierHelper.GetAccentColor(ColorTier.Good)
                    : (tier != ColorTier.Neutral
                        ? ColorTierHelper.GetAccentColor(tier)
                        : ColorTierHelper.GetAccentColor(ColorTier.Critical));

                var (firstLine, secondLine) = SplitPerformanceStatusLabel(label);

                var isTwoLines = !String.IsNullOrEmpty(secondLine);
                var horizontalPadding = width <= 128 ? 2 : 8;
                var maxUsableWidth = width - (2 * horizontalPadding);
                var minFontSize = Math.Max(8, (Int32)Math.Round(height * 0.08));

                if (!isTwoLines)
                {
                    // Single line dominant status (e.g. "BALANCED") in the upper-center value zone,
                    // leaving clear space for the SDK action title below without overlap
                    var fontRatio = firstLine.Length <= 4
                        ? 0.34
                        : firstLine.Length <= 6
                            ? 0.30
                            : firstLine.Length <= 8
                                ? 0.26
                                : 0.22;

                    var targetFontSize = Math.Max(minFontSize, (Int32)Math.Round(height * fontRatio));
                    var fontSize = EnsureTextFitsWidth(firstLine, targetFontSize, maxUsableWidth, minFontSize);

                    var top = (Int32)Math.Round(height * 0.32);
                    var bandHeight = (Int32)Math.Round(height * 0.32);

                    DrawCentredLine(
                        builder,
                        firstLine,
                        fontSize,
                        top,
                        bandHeight,
                        color,
                        NumberFontName);
                }
                else
                {
                    // Two lines dominant status (e.g. "ALL GOOD", "CPU LIMITED", "GPU LIMITED", "FPS LIMITED", "RAM LIMITED")
                    // matching the proven typography and layout of FPS Advisor in the upper-center value zone
                    var fontRatio1 = firstLine.Length <= 4
                        ? 0.30
                        : firstLine.Length <= 6
                            ? 0.26
                            : firstLine.Length <= 8
                                ? 0.22
                                : 0.18;

                    var fontRatio2 = secondLine.Length <= 4
                        ? 0.26
                        : secondLine.Length <= 7
                            ? 0.24
                            : 0.20;

                    var targetFontSize1 = Math.Max(minFontSize, (Int32)Math.Round(height * fontRatio1));
                    var targetFontSize2 = Math.Max(minFontSize, (Int32)Math.Round(height * fontRatio2));

                    var fontSize1 = EnsureTextFitsWidth(firstLine, targetFontSize1, maxUsableWidth, minFontSize);
                    var fontSize2 = EnsureTextFitsWidth(secondLine, targetFontSize2, maxUsableWidth, minFontSize);

                    var top1 = (Int32)Math.Round(height * 0.26);
                    var bandHeight1 = (Int32)Math.Round(height * 0.24);

                    var top2 = (Int32)Math.Round(height * 0.52);
                    var bandHeight2 = (Int32)Math.Round(height * 0.24);

                    DrawCentredLine(
                        builder,
                        firstLine,
                        fontSize1,
                        top1,
                        bandHeight1,
                        color,
                        NumberFontName);

                    DrawCentredLine(
                        builder,
                        secondLine,
                        fontSize2,
                        top2,
                        bandHeight2,
                        color,
                        NumberFontName);
                }

                return builder.ToImage();
            }
        }

        public static BitmapImage RenderBottleneckStatusLabelKey(
            PluginImageSize imageSize,
            String label,
            ColorTier tier,
            Boolean isRtssAvailable = true) =>
            RenderPerformanceStatusLabelKey(imageSize, label, tier, isRtssAvailable);

        public static BitmapImage RenderBottleneckStatusLabelKey(
            PluginImageSize imageSize,
            String label,
            Boolean isRtssAvailable = true) =>
            RenderPerformanceStatusLabelKey(imageSize, label, ColorTier.Critical, isRtssAvailable);

        public static BitmapImage RenderBottleneckStatusKey(
            PluginImageSize imageSize,
            BottleneckAnalysisState state)
        {
            return RenderBottleneckStatusKey(
                imageSize,
                state,
                true);
        }

        public static BitmapImage RenderBottleneckStatusKey(
            PluginImageSize imageSize,
            BottleneckAnalysisState state,
            Boolean hasLiveData,
            Boolean isRtssAvailable = true)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var height = builder.Height;

                builder.Clear(BitmapColor.Transparent);

                if (!hasLiveData)
                {
                    return RenderUnavailableActionIcon(
                        imageSize,
                        "PerformanceStatusCommand",
                        "PERFORMANCE",
                        isRtssAvailable: isRtssAvailable);
                }

                var label =
                    GetBottleneckLabel(state);

                var color =
                    GetBottleneckPrimaryColor(state);

                DrawBottleneckIcon(
                    builder,
                    state,
                    color);

                DrawCentredLine(
                    builder,
                    label,
                    Math.Max(
                        16,
                        (Int32)Math.Round(
                            height *
                            (label.Length > 10
                                ? 0.18
                                : 0.22))),
                    (Int32)Math.Round(
                        height * 0.34),
                    (Int32)Math.Round(
                        height * 0.24),
                    color,
                    NumberFontName);

                DrawCentredLine(
                    builder,
                    GetBottleneckCause(state),
                    Math.Max(
                        13,
                        (Int32)Math.Round(
                            height * 0.16)),
                    (Int32)Math.Round(
                        height * 0.60),
                    (Int32)Math.Round(
                        height * 0.18),
                    ColorTierHelper.NeutralText,
                    LabelFontName);

                DrawCentredLine(
                    builder,
                    GetBottleneckAdvice(state),
                    Math.Max(
                        11,
                        (Int32)Math.Round(
                            height * 0.14)),
                    (Int32)Math.Round(
                        height * 0.79),
                    (Int32)Math.Round(
                        height * 0.16),
                    ColorTierHelper.NeutralLabel,
                    LabelFontName);

                return builder.ToImage();
            }
        }

        public static String GetBottleneckLabel(
            BottleneckAnalysisState state)
        {
            switch (state)
            {
                case BottleneckAnalysisState.CpuBound:
                    return "CPU LIMITED";

                case BottleneckAnalysisState.GpuBound:
                    return "GPU LIMITED";

                case BottleneckAnalysisState.FpsLimited:
                    return "FPS LIMITED";

                case BottleneckAnalysisState.MemoryPressure:
                    return "UNKNOWN";

                case BottleneckAnalysisState.Mixed:
                    return "UNKNOWN";

                case BottleneckAnalysisState.Balanced:
                    return "ALL GOOD";

                default:
                    return "UNKNOWN";
            }
        }

        private static String GetBottleneckCause(
            BottleneckAnalysisState state) =>
            state == BottleneckAnalysisState.CpuBound
                ? "CPU"
                : state == BottleneckAnalysisState.GpuBound
                    ? "GPU"
                    : state == BottleneckAnalysisState.FpsLimited
                        ? "FPS"
                        : state == BottleneckAnalysisState.Balanced
                            ? "ALL GOOD"
                            : "NO GAME";

        private static String GetBottleneckAdvice(
            BottleneckAnalysisState state) =>
            state == BottleneckAnalysisState.CpuBound
                ? "CPU SATURATED"
                : state == BottleneckAnalysisState.GpuBound
                    ? "LOWER GPU LOAD"
                    : state == BottleneckAnalysisState.FpsLimited
                        ? "FPS LIMITED"
                        : state == BottleneckAnalysisState.Balanced
                            ? "HEALTHY"
                            : "RTSS WAITING";

        public static BitmapColor GetBottleneckPrimaryColor(
            BottleneckAnalysisState state) =>
            GetBottleneckColors(state).Primary;

        private static (
            BitmapColor Primary,
            BitmapColor Accent)
            GetBottleneckColors(
                BottleneckAnalysisState state)
        {
            switch (state)
            {
                case BottleneckAnalysisState.CpuBound:
                case BottleneckAnalysisState.GpuBound:
                case BottleneckAnalysisState.FpsLimited:
                    return (
                        new BitmapColor(
                            255,
                            75,
                            58),
                        new BitmapColor(
                            255,
                            102,
                            85));

                case BottleneckAnalysisState.MemoryPressure:
                    return (
                        new BitmapColor(
                            255,
                            210,
                            31),
                        new BitmapColor(
                            255,
                            224,
                            102));

                case BottleneckAnalysisState.Mixed:
                    return (
                        new BitmapColor(
                            176,
                            92,
                            255),
                        new BitmapColor(
                            199,
                            125,
                            255));

                case BottleneckAnalysisState.Unknown:
                    return (
                        new BitmapColor(
                            216,
                            222,
                            233),
                        new BitmapColor(
                            138,
                            148,
                            166));

                case BottleneckAnalysisState.Balanced:
                    return (
                        new BitmapColor(
                            126,
                            217,
                            87),
                        new BitmapColor(
                            165,
                            245,
                            138));

                default:
                    return (
                        new BitmapColor(
                            126,
                            217,
                            87),
                        new BitmapColor(
                            139,
                            227,
                            106));
            }
        }

        private static void DrawBottleneckIcon(
            BitmapBuilder builder,
            BottleneckAnalysisState state,
            BitmapColor color)
        {
            var centerX =
                builder.Width / 2;

            // Icon sits in the top icon zone so the layout reads ICON → VALUE → ADVICE,
            // consistent with the rest of the plugin's visual hierarchy.
            var centerY =
                (Int32)Math.Round(
                    builder.Height * 0.16);

            var size =
                Math.Max(
                    5,
                    (Int32)Math.Round(
                        builder.Height * 0.10));

            var stroke =
                Math.Max(
                    1,
                    (Int32)Math.Round(
                        builder.Height * 0.018));

            switch (state)
            {
                case BottleneckAnalysisState.CpuBound:
                    DrawChipIcon(
                        builder,
                        centerX,
                        centerY,
                        size,
                        stroke,
                        color);
                    break;

                case BottleneckAnalysisState.GpuBound:
                    DrawGpuIcon(
                        builder,
                        centerX,
                        centerY,
                        size,
                        stroke,
                        color);
                    break;

                case BottleneckAnalysisState.MemoryPressure:
                    DrawMemoryIcon(
                        builder,
                        centerX,
                        centerY,
                        size,
                        stroke,
                        color);
                    break;

                case BottleneckAnalysisState.Mixed:
                    DrawRingIcon(
                        builder,
                        centerX - size / 2,
                        centerY,
                        size,
                        stroke,
                        color);

                    DrawRingIcon(
                        builder,
                        centerX + size / 2,
                        centerY,
                        size,
                        stroke,
                        color);
                    break;

                case BottleneckAnalysisState.Balanced:
                    DrawCheckIcon(
                        builder,
                        centerX,
                        centerY,
                        size,
                        stroke,
                        color);
                    break;

                default:
                    DrawCentredLine(
                        builder,
                        "● ● ●",
                        Math.Max(
                            8,
                            (Int32)Math.Round(
                                builder.Height * 0.09)),
                        centerY - size / 2,
                        size,
                        color,
                        NumberFontName);
                    break;
            }
        }

        private static void DrawChipIcon(
            BitmapBuilder builder,
            Int32 x,
            Int32 y,
            Int32 size,
            Int32 stroke,
            BitmapColor color)
        {
            builder.DrawLine(
                x - size,
                y - size,
                x + size,
                y - size,
                color,
                stroke);

            builder.DrawLine(
                x + size,
                y - size,
                x + size,
                y + size,
                color,
                stroke);

            builder.DrawLine(
                x + size,
                y + size,
                x - size,
                y + size,
                color,
                stroke);

            builder.DrawLine(
                x - size,
                y + size,
                x - size,
                y - size,
                color,
                stroke);

            var pinLength = Math.Max(1, size / 4);

            for (var index = -1;
                 index <= 1;
                 index++)
            {
                var offset =
                    index * size / 2;

                builder.DrawLine(
                    x + offset,
                    y - size,
                    x + offset,
                    y - size - pinLength,
                    color,
                    stroke);

                builder.DrawLine(
                    x + offset,
                    y + size,
                    x + offset,
                    y + size + pinLength,
                    color,
                    stroke);
            }
        }

        private static void DrawGpuIcon(
            BitmapBuilder builder,
            Int32 x,
            Int32 y,
            Int32 size,
            Int32 stroke,
            BitmapColor color)
        {
            builder.DrawLine(
                x - size * 2,
                y - size,
                x + size * 2,
                y - size,
                color,
                stroke);

            builder.DrawLine(
                x + size * 2,
                y - size,
                x + size * 2,
                y + size,
                color,
                stroke);

            builder.DrawLine(
                x + size * 2,
                y + size,
                x - size * 2,
                y + size,
                color,
                stroke);

            builder.DrawLine(
                x - size * 2,
                y + size,
                x - size * 2,
                y - size,
                color,
                stroke);

            DrawRingIcon(
                builder,
                x,
                y,
                size / 2,
                stroke,
                color);

            builder.DrawLine(
                x + size * 2,
                y - size / 2,
                x + size * 3,
                y - size / 2,
                color,
                stroke);
        }

        private static void DrawMemoryIcon(
            BitmapBuilder builder,
            Int32 x,
            Int32 y,
            Int32 size,
            Int32 stroke,
            BitmapColor color)
        {
            builder.DrawLine(
                x - size * 2,
                y - size,
                x + size * 2,
                y - size,
                color,
                stroke);

            builder.DrawLine(
                x + size * 2,
                y - size,
                x + size * 2,
                y + size,
                color,
                stroke);

            builder.DrawLine(
                x + size * 2,
                y + size,
                x - size * 2,
                y + size,
                color,
                stroke);

            builder.DrawLine(
                x - size * 2,
                y + size,
                x - size * 2,
                y - size,
                color,
                stroke);

            for (var index = -1;
                 index <= 1;
                 index++)
            {
                var block =
                    x + index * size;

                builder.DrawLine(
                    block,
                    y - size / 2,
                    block,
                    y + size / 2,
                    color,
                    stroke);

                builder.DrawLine(
                    block - size / 3,
                    y + size,
                    block - size / 3,
                    y + size * 2,
                    color,
                    stroke);
            }
        }

        private static void DrawRingIcon(
            BitmapBuilder builder,
            Int32 x,
            Int32 y,
            Int32 size,
            Int32 stroke,
            BitmapColor color)
        {
            const Int32 segments = 12;

            for (var index = 0;
                 index < segments;
                 index++)
            {
                var first =
                    2 * Math.PI * index / segments;

                var second =
                    2 * Math.PI *
                    (index + 1) /
                    segments;

                builder.DrawLine(
                    x + (Int32)Math.Round(
                        Math.Cos(first) * size),
                    y + (Int32)Math.Round(
                        Math.Sin(first) * size),
                    x + (Int32)Math.Round(
                        Math.Cos(second) * size),
                    y + (Int32)Math.Round(
                        Math.Sin(second) * size),
                    color,
                    stroke);
            }
        }

        private static void DrawCheckIcon(
            BitmapBuilder builder,
            Int32 x,
            Int32 y,
            Int32 size,
            Int32 stroke,
            BitmapColor color)
        {
            DrawRingIcon(
                builder,
                x,
                y,
                size,
                stroke,
                color);

            builder.DrawLine(
                x - size / 2,
                y,
                x - size / 8,
                y + size / 2,
                color,
                stroke);

            builder.DrawLine(
                x - size / 8,
                y + size / 2,
                x + size * 3 / 4,
                y - size / 2,
                color,
                stroke);
        }

        public static BitmapImage RenderCpuTemperatureKey(
            PluginImageSize imageSize,
            Boolean hasLiveData,
            Double temperatureCelsius) =>
            RenderTemperatureKey(
                imageSize,
                hasLiveData,
                temperatureCelsius,
                "CPU TEMP");

        public static BitmapImage RenderGpuTemperatureKey(
            PluginImageSize imageSize,
            Boolean hasLiveData,
            Double temperatureCelsius) =>
            RenderTemperatureKey(
                imageSize,
                hasLiveData,
                temperatureCelsius,
                "GPU TEMP");

        private static BitmapImage RenderTemperatureKey(
            PluginImageSize imageSize,
            Boolean hasLiveData,
            Double temperatureCelsius,
            String componentLabel)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var height = builder.Height;

                builder.Clear(BitmapColor.Transparent);

                if (!hasLiveData)
                {
                    return RenderUnavailableActionIcon(
                        imageSize,
                        componentLabel.StartsWith(
                            "CPU",
                            StringComparison.OrdinalIgnoreCase)
                            ? "CpuTemperatureStatusCommand"
                            : "GpuTemperatureStatusCommand",
                        componentLabel);
                }

                var reading =
                    Math.Round(temperatureCelsius)
                        .ToString(
                            "0",
                            CultureInfo.InvariantCulture) +
                    "°";

                var accent =
                    ColorTierHelper.GetAccentColor(
                        ColorTierHelper.GetTemperatureTier(
                            temperatureCelsius));

                DrawCentredLine(
                    builder,
                    reading,
                    NumberSize(
                        height,
                        reading.Length),
                    NumberBandTop(height),
                    NumberBandHeight(height),
                    accent,
                    NumberFontName);

                DrawStatusRule(
                    builder,
                    accent);

                return builder.ToImage();
            }
        }

        private static BitmapImage RenderComponentLoadKey(
            PluginImageSize imageSize,
            Boolean hasLiveData,
            Double loadPercent,
            String label,
            String unit,
            String iconActionTypeName,
            Boolean emphasizeValue = false,
            Boolean isRtssAvailable = true,
            Boolean reducedFontSize = false)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var height = builder.Height;

                builder.Clear(BitmapColor.Transparent);

                var canonicalLabel =
                    GetCanonicalLabel(label);

                if (!hasLiveData)
                {
                    return RenderUnavailableActionIcon(
                        imageSize,
                        iconActionTypeName,
                        canonicalLabel,
                        isRtssAvailable: isRtssAvailable);
                }

                var reading =
                    Math.Round(loadPercent)
                        .ToString(
                            "0",
                            CultureInfo.InvariantCulture) +
                    unit;

                var accent =
                    ColorTierHelper.GetAccentColor(
                        ColorTierHelper.GetLoadTier(
                            loadPercent));

                var fontSize = reducedFontSize
                    ? GetHardwareUsageFontSize(height, reading.Length)
                    : NumberSize(
                        height,
                        reading.Length,
                        emphasizeValue);

                DrawCentredLine(
                    builder,
                    reading,
                    fontSize,
                    NumberBandTop(height),
                    NumberBandHeight(height),
                    accent,
                    NumberFontName);

                DrawStatusRule(
                    builder,
                    accent);

                return builder.ToImage();
            }
        }

        private static BitmapImage RenderCpuClockValueKey(
            PluginImageSize imageSize,
            Boolean hasLiveData,
            Double clockMegahertz,
            String iconActionTypeName,
            Double referenceMaximum)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var height = builder.Height;

                builder.Clear(BitmapColor.Transparent);

                if (!hasLiveData)
                {
                    return RenderUnavailableActionIcon(
                        imageSize,
                        iconActionTypeName,
                        "CPU CLOCK");
                }

                var reading =
                    Math.Round(clockMegahertz)
                        .ToString(
                            "0",
                            CultureInfo.InvariantCulture);

                var valueSize =
                    reading.Length >= 5
                        ? Math.Max(
                            18,
                            (Int32)Math.Round(
                                height * 0.23))
                        : Math.Max(
                            20,
                            (Int32)Math.Round(
                                height * 0.32));

                var accent =
                    ColorTierHelper.GetAccentColor(
                        ColorTierHelper.GetRelativeTier(
                            clockMegahertz,
                            referenceMaximum));

                DrawCentredLine(
                    builder,
                    reading,
                    valueSize,
                    (Int32)Math.Round(
                        height * 0.16),
                    (Int32)Math.Round(
                        height * 0.38),
                    accent,
                    NumberFontName);

                DrawCentredLine(
                    builder,
                    "MHz",
                    Math.Max(
                        8,
                        (Int32)Math.Round(
                            height * LiveUnitRatio)),
                    (Int32)Math.Round(
                        height * 0.52),
                    (Int32)Math.Round(
                        height * 0.14),
                    accent,
                    LabelFontName);

                DrawStatusRule(
                    builder,
                    accent);

                return builder.ToImage();
            }
        }

        public static String FormatFrameTime(
            Double frameTimeMilliseconds) =>
            frameTimeMilliseconds < 1000.0
                ? frameTimeMilliseconds.ToString(
                    "0.0",
                    CultureInfo.InvariantCulture)
                : Math.Round(
                    frameTimeMilliseconds)
                    .ToString(
                        "0",
                        CultureInfo.InvariantCulture);

        private static void DrawStatusRule(
            BitmapBuilder builder,
            BitmapColor accent)
        {
            var width = builder.Width;
            var height = builder.Height;

            var ruleWidth =
                (Int32)Math.Round(
                    width * 0.34);

            var ruleHeight =
                Math.Max(
                    2,
                    (Int32)Math.Round(
                        height * 0.0375));

            builder.FillRectangle(
                (width - ruleWidth) / 2,
                RuleTop(height),
                ruleWidth,
                ruleHeight,
                accent);
        }

        private static void DrawCentredLine(
            BitmapBuilder builder,
            String text,
            Int32 fontSize,
            Int32 bandTop,
            Int32 bandHeight,
            BitmapColor color,
            String fontName)
        {
            builder.DrawText(
                text ?? String.Empty,
                0,
                bandTop,
                builder.Width,
                bandHeight,
                color,
                fontSize,
                -1,
                -1,
                fontName);
        }


        private static Int32 NumberSize(
            Int32 height,
            Int32 digits,
            Boolean emphasizeValue = false)
        {
            var ratio =
                digits >= 7
                    ? LiveValueSevenPlusRatio
                    : digits >= 6
                        ? LiveValueSixRatio
                        : digits >= 4
                            ? LiveValueStandardRatio
                            : LiveValueCompactRatio;

            if (emphasizeValue)
            {
                ratio = Math.Min(ratio * 1.18, 0.50);
            }

            return (Int32)Math.Round(height * ratio);
        }

        private static Int32 LabelSize(
            Int32 height) =>
            Math.Max(
                8,
                (Int32)Math.Round(
                    height * 0.135));

        /*
         * Centralized unavailable/action artwork renderer.
         *
         * Important:
         * - Resolves the SVG through PluginResources.
         * - Always supplies the LCD label.
         * - If the SVG cannot be resolved, renders the label itself
         *   instead of returning a blank image.
         */
        private static BitmapImage RenderUnavailableActionIcon(
            PluginImageSize imageSize,
            String actionTypeName,
            String label = null,
            Boolean isRtssAvailable = false)
        {
            var iconName =
                PluginResources.GetActionIcon(
                    actionTypeName);

            if (!String.IsNullOrWhiteSpace(iconName))
            {
                if (isRtssAvailable)
                {
                    return PluginResources.ReadOriginalSvg(
                        iconName,
                        imageSize);
                }

                if (PluginResources.IsBoldWhiteOfflineIcon(iconName))
                {
                    return PluginResources.ReadBoldWhiteSvg(
                        iconName,
                        imageSize);
                }

                var icon =
                    PluginResources.ReadSvg(
                        iconName,
                        imageSize,
                        UnavailableAccent);

                if (icon != null)
                {
                    return icon;
                }
            }

            return null;
        }

        private static String GetCanonicalLabel(
            String label) =>
            label switch
            {
                "GPU" =>
                    "GPU USAGE",

                "RAM" =>
                    "RAM USAGE",

                "VRAM" =>
                    "VRAM USAGE",

                "CPU MHz" =>
                    "CPU CLOCK",

                "GPU MHz" =>
                    "GPU CLOCK",

                _ =>
                    label
            };

        private static BitmapImage RenderInactiveIcon(
            PluginImageSize imageSize,
            String actionTypeName)
        {
            var icon =
                PluginResources.GetActionIcon(
                    actionTypeName);

            if (icon == null)
            {
                return null;
            }

            if (PluginResources.IsBoldWhiteOfflineIcon(icon))
            {
                return PluginResources.ReadBoldWhiteSvg(
                    icon,
                    imageSize);
            }

            return PluginResources.ReadSvg(
                icon,
                imageSize,
                UnavailableAccent);
        }

        private static Int32 RestingSize(
            Int32 height) =>
            (Int32)Math.Round(
                height * 0.33);

        private const Int32 RestingWordmarkReferenceLength = 6;

        private const Double RestingWordmarkReferenceRatio = 0.185;

        private static Int32 RestingWordmarkSize(
            Int32 height,
            Int32 textLength) =>
            Math.Max(
                8,
                (Int32)Math.Round(
                    height *
                    RestingWordmarkReferenceRatio *
                    RestingWordmarkReferenceLength /
                    Math.Max(
                        textLength,
                        1)));

        private static Int32 NumberBandTop(
            Int32 height) =>
            (Int32)Math.Round(
                height * 0.30);

        private static Int32 NumberBandHeight(
            Int32 height) =>
            (Int32)Math.Round(
                height * 0.44);

        private static Int32 LabelBandTop(
            Int32 height) =>
            (Int32)Math.Round(
                height * 0.82);

        private static Int32 RuleTop(
            Int32 height) =>
            (Int32)Math.Round(
                height * 0.77);

        private static Int32 LabelBandHeight(
            Int32 height) =>
            (Int32)Math.Round(
                height * 0.14);

        private static Int32 RestingBandTop(
            Int32 height) =>
            (Int32)Math.Round(
                height * 0.25);

        private static Int32 RestingBandHeight(
            Int32 height) =>
            (Int32)Math.Round(
                height * 0.50);

        private static Int32 BadgeBaselineNudge(
            Int32 height) =>
            (Int32)Math.Round(
                height * 0.0625);

        private static void DrawCornerBadge(
            BitmapBuilder builder,
            String badge)
        {
            var width = builder.Width;
            var height = builder.Height;

            var box =
                Math.Max(
                    11,
                    (Int32)Math.Round(
                        height * 0.12));

            var inset =
                Math.Max(
                    9,
                    (Int32)Math.Round(
                        width * 0.11));

            builder.DrawText(
                badge,
                width - box - inset,
                LabelBandTop(height) + (Int32)Math.Round(height * 0.02),
                box,
                box,
                ColorTierHelper.NeutralLabel,
                LabelSize(height),
                -1,
                -1,
                LabelFontName);
        }

        public enum RemoveProfileFeedbackState
        {
            Idle,
            Removed,
            Protected,
            Failed
        }

        private static String EscapeXml(String value) =>
            (value ?? String.Empty)
                .Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal)
                .Replace("'", "&apos;", StringComparison.Ordinal);

        public static BitmapImage RenderRtssLaunchKey(
            PluginImageSize imageSize,
            Boolean isRtssAvailable)
        {
            if (!isRtssAvailable)
            {
                return PluginResources.ReadControlActionSvg(
                    "icon-rtss.svg",
                    imageSize,
                    stateText: null,
                    stateColor: UnavailableAccent,
                    useAdjustedStatusLayout: true);
            }

            return PluginResources.ReadControlActionSvg(
                "icon-rtss.svg",
                imageSize,
                stateText: "ON",
                stateColor: ColorTierHelper.GetAccentColor(ColorTier.Good),
                useAdjustedStatusLayout: true);
        }

        public static BitmapImage RenderRtssInstallKey(PluginImageSize imageSize)
        {
            return PluginResources.ReadControlActionSvg(
                "icon-rtss.svg",
                imageSize,
                stateText: "INSTALL",
                stateColor: new BitmapColor(0x00, 0xB0, 0xFF),
                useAdjustedStatusLayout: true);
        }

    }
}