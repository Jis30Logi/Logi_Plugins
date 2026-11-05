using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("RTSSPerformancePlugin.Tests")]

namespace Loupedeck.RTSSPerformancePlugin.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Reflection;
    using System.Text;

    using SkiaSharp;
    using Svg.Skia;

    /// <summary>
    /// Loads embedded SVG/PNG resources and renders them into device bitmaps.
    /// </summary>
    public static class PluginResources
    {
        public const String IconRoot = "Resources.Icons";

        /// <summary>
        /// Token an SVG can use where the accent colour belongs.
        /// </summary>
        public const String AccentColorToken = "currentColor";

        private static readonly Object Sync = new Object();

        private static Assembly _assembly;

        /// <summary>
        /// Short icon name to the full manifest resource name.
        /// </summary>
        private static Dictionary<String, String> _resourcesByShortName =
            new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);

        private static String[] _resourceNames = Array.Empty<String>();

        static PluginResources()
        {
            try
            {
                Init(typeof(PluginResources).Assembly);
            }
            catch
            {
                // Static constructor must never throw
            }
        }

        public static String GetActionIcon(String actionTypeName) =>
            actionTypeName switch
            {
                "FpsCapFolder" => "icon-fpscap.svg",
                "FpsStatusCommand" => "icon-live-fps.svg",
                "FpsAdvisorCommand" => "icon-fps-advisor.svg",
                "CpuUsageCommand" => "icon-cpu.svg",
                "GpuUsageCommand" => "icon-gpu.svg",
                "GpuStatusCommand" => "icon-gpu.svg",
                "FrameTimeStatusCommand" => "icon-frame-time.svg",
                "OnePercentLowStatusCommand" => "icon-one-percent-low.svg",
                "RTSSLaunchCommand" => "icon-rtss.svg",
                "RTSS Status" => "icon-rtss.svg",
                "PerformanceStatusCommand" => "icon-bottleneck-status.svg",
                "Performance Status" => "icon-bottleneck-status.svg",
                "Bottleneck Status" => "icon-bottleneck-status.svg",

                _ => null
            };

        /// <summary>
        /// Indexes the assembly's manifest resources once at load.
        /// </summary>
        public static void Init(Assembly assembly)
        {
            lock (Sync)
            {
                _assembly = assembly;
                _resourceNames = Array.Empty<String>();

                _resourcesByShortName =
                    new Dictionary<String, String>(
                        StringComparer.OrdinalIgnoreCase);

                if (assembly == null)
                {
                    PluginLog.Warning(
                        "PluginResources.Init called with a null assembly.");

                    return;
                }

                try
                {
                    _resourceNames = assembly.GetManifestResourceNames();
                }
                catch (Exception exception)
                {
                    PluginLog.Error(
                        exception,
                        "Could not enumerate the plugin's embedded resources.");

                    _resourceNames = Array.Empty<String>();

                    return;
                }

                foreach (var name in _resourceNames)
                {
                    var shortName = GetShortName(name);

                    if (!String.IsNullOrEmpty(shortName))
                    {
                        _resourcesByShortName[shortName] = name;
                    }
                }

                if (_resourceNames.Length == 0)
                {
                    PluginLog.Warning(
                        "No embedded resources were found; check that the icons " +
                        "are marked EmbeddedResource.");
                }
                else
                {
                    PluginLog.Info(
                        $"PluginResources initialized with {_resourceNames.Length} embedded resources.");
                }
            }
        }

        public static IEnumerable<String> GetResourceNames()
        {
            lock (Sync)
            {
                return (String[])_resourceNames.Clone();
            }
        }

        public static Boolean ResourceExists(String resourceName)
        {
            return Resolve(resourceName) != null;
        }

        /// <summary>
        /// Caller owns the returned stream.
        /// Returns null when the resource is not present.
        /// </summary>
        public static Stream GetStream(String resourceName)
        {
            Assembly assembly;
            String resolved;

            lock (Sync)
            {
                assembly = _assembly;
                resolved = ResolveCore(resourceName);
            }

            if (assembly == null)
            {
                PluginLog.Warning(
                    $"Cannot load resource '{resourceName}': plugin assembly is not initialized.");

                return null;
            }

            if (resolved == null)
            {
                PluginLog.Warning(
                    $"Embedded resource not found: '{resourceName}'.");

                return null;
            }

            try
            {
                var stream = assembly.GetManifestResourceStream(resolved);

                if (stream == null)
                {
                    PluginLog.Warning(
                        $"GetManifestResourceStream returned null for '{resolved}'.");
                }

                return stream;
            }
            catch (Exception exception)
            {
                PluginLog.Warning(
                    exception,
                    $"Could not open the embedded resource '{resourceName}'.");

                return null;
            }
        }

        public static String ReadText(String resourceName)
        {
            using (var stream = GetStream(resourceName))
            {
                if (stream == null)
                {
                    return null;
                }

                try
                {
                    using (var reader = new StreamReader(stream))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (Exception exception)
                {
                    PluginLog.Warning(
                        exception,
                        $"Could not read the embedded resource '{resourceName}'.");

                    return null;
                }
            }
        }

        public static BitmapImage ReadImage(String resourceName)
        {
            var bytes = ReadBytes(resourceName);

            if (bytes == null)
            {
                return null;
            }

            try
            {
                return BitmapImage.FromArray(bytes);
            }
            catch (Exception exception)
            {
                PluginLog.Warning(
                    exception,
                    $"Could not decode the embedded image '{resourceName}'.");

                return null;
            }
        }

        /// <summary>
        /// Renders an SVG resource, substituting the accent colour token before rasterising.
        /// </summary>
        public static BitmapImage ReadSvg(
            String resourceName,
            PluginImageSize imageSize,
            BitmapColor accentColor)
        {
            var svg = ReadText(resourceName);

            if (String.IsNullOrWhiteSpace(svg))
            {
                return null;
            }

            var hexColor = ToHexColor(accentColor);
            svg = svg.Replace(
                AccentColorToken,
                hexColor);

            if (String.Equals(hexColor, "#566070", StringComparison.OrdinalIgnoreCase))
            {
                if (IsBoldWhiteOfflineIcon(resourceName))
                {
                    svg = ConvertSvgColorsToBoldWhite(svg);
                }
                else
                {
                    svg = DimSvgColorsToUnavailable(svg);
                }
            }

            try
            {
                svg = ApplyRootSize(svg, imageSize);

                if (!BitmapImage.TryCreateFromSvg(
                        svg,
                        out var image) ||
                    image == null)
                {
                    PluginLog.Warning(
                        $"The embedded resource '{resourceName}' is not valid SVG.");

                    return null;
                }

                return image;
            }
            catch (Exception exception)
            {
                PluginLog.Warning(
                    exception,
                    $"Could not render the SVG resource '{resourceName}'.");

                return null;
            }
        }

        public static BitmapImage ReadOriginalSvg(
            String resourceName,
            PluginImageSize imageSize)
        {
            var svg = ReadText(resourceName);
            if (String.IsNullOrWhiteSpace(svg))
            {
                return null;
            }

            try
            {
                svg = ApplyRootSize(svg, imageSize);
                return BitmapImage.TryCreateFromSvg(svg, out var image) ? image : null;
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, $"Could not render original SVG '{resourceName}'.");
                return null;
            }
        }

        public static Boolean IsBoldWhiteOfflineIcon(String resourceName)
        {
            if (String.IsNullOrWhiteSpace(resourceName))
            {
                return false;
            }

            var shortName = GetShortName(resourceName);
            return String.Equals(shortName, "icon-fps-advisor.svg", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(shortName, "icon-rtss.svg", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(shortName, "FpsAdvisorCommand", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(shortName, "RTSSLaunchCommand", StringComparison.OrdinalIgnoreCase);
        }

        public static BitmapImage ReadBoldWhiteSvg(
            String resourceName,
            PluginImageSize imageSize)
        {
            var svg = ReadText(resourceName);
            if (String.IsNullOrWhiteSpace(svg))
            {
                return null;
            }

            svg = ConvertSvgColorsToBoldWhite(svg);

            try
            {
                svg = ApplyRootSize(svg, imageSize);
                return BitmapImage.TryCreateFromSvg(svg, out var image) ? image : null;
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, $"Could not render bold white SVG '{resourceName}'.");
                return null;
            }
        }

        public static BitmapImage ReadOriginalRasterSvg(
            String resourceName,
            PluginImageSize imageSize)
        {
            var svg = ReadText(resourceName);
            if (String.IsNullOrWhiteSpace(svg))
            {
                return null;
            }

            try
            {
                using (var svgStream = new MemoryStream(Encoding.UTF8.GetBytes(svg)))
                using (var rendered = new SKSvg())
                using (var picture = rendered.Load(svgStream))
                {
                    if (picture == null)
                    {
                        return null;
                    }

                    var width = GetImageDimension(imageSize, true);
                    var height = GetImageDimension(imageSize, false);
                    var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);

                    using (var bitmap = new SKBitmap(info))
                    using (var canvas = new SKCanvas(bitmap))
                    {
                        canvas.Clear(SKColors.Transparent);
                        var srcWidth = picture.CullRect.Width > 0 ? picture.CullRect.Width : 256.0f;
                        var srcHeight = picture.CullRect.Height > 0 ? picture.CullRect.Height : 256.0f;
                        canvas.Scale(width / srcWidth, height / srcHeight);
                        canvas.DrawPicture(picture);
                        canvas.Flush();

                        using (var image = SKImage.FromBitmap(bitmap))
                        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                        {
                            return BitmapImage.FromArray(data.ToArray());
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, $"Could not rasterize original SVG '{resourceName}'.");
                return null;
            }
        }

        public static BitmapImage RenderInlineSvg(String svg, PluginImageSize imageSize)
        {
            if (String.IsNullOrWhiteSpace(svg))
            {
                return null;
            }

            try
            {
                return BitmapImage.TryCreateFromSvg(ApplyRootSize(svg, imageSize), out var image)
                    ? image
                    : null;
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, "Could not render inline SVG.");
                return null;
            }
        }

        public static BitmapImage ReadSvgWithLabel(
            String resourceName,
            PluginImageSize imageSize,
            BitmapColor accentColor,
            String label,
            Boolean preserveFullArtwork = false)
        {
            var svg = ReadText(resourceName);

            if (String.IsNullOrWhiteSpace(svg))
            {
                return null;
            }

            var closeTag = svg.LastIndexOf(
                "</svg>",
                StringComparison.OrdinalIgnoreCase);

            if (closeTag < 0)
            {
                return ReadSvg(
                    resourceName,
                    imageSize,
                    accentColor);
            }

            var escapedLabel = EscapeXml(label ?? String.Empty);
            var labelMarkup = String.IsNullOrWhiteSpace(label)
                ? String.Empty
                : $"<text x=\"128\" y=\"244\" " +
                  $"text-anchor=\"middle\" " +
                  $"font-family=\"{EscapeXml(KeyRenderer.LabelFontName)}\" " +
                  $"font-size=\"{KeyRenderer.IconOnlyLabelSize}\" " +
                  $"font-weight=\"600\" " +
                  $"fill=\"#BFC9D6\">" +
                  escapedLabel +
                  "</text>";

            if (preserveFullArtwork)
            {
                svg = svg.Insert(closeTag, labelMarkup);
            }
            else
            {
                var viewBox = GetSvgViewBox(svg) ?? "0 0 256 256";
                var iconMarkup = ExtractSvgContents(svg);
                if (String.IsNullOrWhiteSpace(iconMarkup))
                {
                    return ReadSvg(
                        resourceName,
                        imageSize,
                        accentColor);
                }

                svg =
                    $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"256\" height=\"256\" viewBox=\"0 0 256 256\">" +
                    $"<g transform=\"translate({KeyRenderer.IconOnlyArtworkOffset} 0) scale({(KeyRenderer.IconOnlyArtworkSize / 256.0).ToString(CultureInfo.InvariantCulture)})\">" +
                    iconMarkup +
                    "</g>" +
                    labelMarkup +
                    "</svg>";
            }

            svg = svg.Replace(
                AccentColorToken,
                ToHexColor(accentColor));

            svg = ApplyRootSize(
                svg,
                imageSize);

            try
            {
                if (!BitmapImage.TryCreateFromSvg(
                        svg,
                        out var image) ||
                    image == null)
                {
                    if (preserveFullArtwork)
                    {
                        PluginLog.Warning(
                            $"Could not compose labeled SVG '{resourceName}'; " +
                            "falling back to the raw embedded artwork.");
                        return ReadSvg(resourceName, imageSize, accentColor);
                    }

                    PluginLog.Warning(
                        $"Could not render SVG with label '{resourceName}'.");

                    return null;
                }

                return image;
            }
            catch (Exception exception)
            {
                PluginLog.Warning(
                    exception,
                    $"Could not render SVG with label '{resourceName}'.");

                return null;
            }
        }

        public static BitmapImage ReadControlActionSvg(
            String resourceName,
            PluginImageSize imageSize,
            String stateText,
            BitmapColor stateColor,
            String actionLabel = null,
            Boolean useAdjustedStatusLayout = false)
        {
            var svg = ReadText(resourceName);

            if (String.IsNullOrWhiteSpace(svg))
            {
                return null;
            }

            var iconMarkup = ExtractSvgContents(svg);
            if (String.IsNullOrWhiteSpace(iconMarkup))
            {
                return null;
            }

            var isUnavailable = String.IsNullOrWhiteSpace(stateText);
            if (isUnavailable)
            {
                if (IsBoldWhiteOfflineIcon(resourceName))
                {
                    iconMarkup = ConvertSvgColorsToBoldWhite(iconMarkup);
                }
                else
                {
                    iconMarkup = DimSvgColorsToUnavailable(iconMarkup);
                }
            }

            var escapedState = EscapeXml(stateText ?? String.Empty);
            var stateHex = ToHexColor(stateColor);

            var stateFontSize = escapedState.Length > 6
                ? (useAdjustedStatusLayout ? 40 : KeyRenderer.ControlStateFontSizeLong)
                : (useAdjustedStatusLayout ? 44 : KeyRenderer.ControlStateFontSizeShort);

            var stateTextY = useAdjustedStatusLayout ? 242 : KeyRenderer.ControlStateTextY;

            var stateTextMarkup = isUnavailable
                ? String.Empty
                : $"<text x=\"128\" y=\"{stateTextY}\" " +
                  $"text-anchor=\"middle\" " +
                  $"font-family=\"{EscapeXml(KeyRenderer.LabelFontName)}\" " +
                  $"font-size=\"{stateFontSize}\" " +
                  $"font-weight=\"700\" " +
                  $"fill=\"{stateHex}\">" +
                  escapedState +
                  "</text>";

            var iconOffset = (useAdjustedStatusLayout ? 20 : KeyRenderer.CanonicalIconSlotOffset).ToString(CultureInfo.InvariantCulture);
            var iconScale = (useAdjustedStatusLayout ? 0.84 : KeyRenderer.CanonicalIconSlotScale).ToString(CultureInfo.InvariantCulture);
            var iconTransform = $"translate({iconOffset} {iconOffset}) scale({iconScale})";

            var combinedSvg =
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"256\" height=\"256\" viewBox=\"0 0 256 256\">" +
                $"<g transform=\"{iconTransform}\">" +
                iconMarkup +
                "</g>" +
                stateTextMarkup +
                "</svg>";

            combinedSvg = ApplyRootSize(combinedSvg, imageSize);

            try
            {
                if (!BitmapImage.TryCreateFromSvg(combinedSvg, out var image) || image == null)
                {
                    PluginLog.Warning($"Could not render control action SVG '{resourceName}'.");
                    return null;
                }

                return image;
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, $"Could not render control action SVG '{resourceName}'.");
                return null;
            }
        }

        public static BitmapImage ReadUpperZoneIconSvg(
            String resourceName,
            PluginImageSize imageSize,
            BitmapColor accentColor,
            Boolean isUnavailable = false,
            Boolean preserveOriginalColors = false)
        {
            var svg = ReadText(resourceName);
            if (String.IsNullOrWhiteSpace(svg))
            {
                return null;
            }
            var iconMarkup = ExtractSvgContents(svg);
            if (String.IsNullOrWhiteSpace(iconMarkup))
            {
                return null;
            }

            var hexColor = ToHexColor(accentColor);
            if (!preserveOriginalColors)
            {
                iconMarkup = iconMarkup.Replace(AccentColorToken, hexColor, StringComparison.OrdinalIgnoreCase);
            }

            if (!preserveOriginalColors &&
                (isUnavailable || String.Equals(hexColor, "#566070", StringComparison.OrdinalIgnoreCase)))
            {
                if (IsBoldWhiteOfflineIcon(resourceName))
                {
                    iconMarkup = ConvertSvgColorsToBoldWhite(iconMarkup);
                }
                else
                {
                    iconMarkup = DimSvgColorsToUnavailable(iconMarkup);
                }
            }

            var iconOffset = KeyRenderer.CanonicalIconSlotOffset.ToString(CultureInfo.InvariantCulture);
            var iconScale = KeyRenderer.CanonicalIconSlotScale.ToString(CultureInfo.InvariantCulture);
            var combinedSvg =
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"256\" height=\"256\" viewBox=\"0 0 256 256\">" +
                $"<g transform=\"translate({iconOffset} {iconOffset}) scale({iconScale})\">" +
                iconMarkup +
                "</g>" +
                "</svg>";

            combinedSvg = ApplyRootSize(combinedSvg, imageSize);

            try
            {
                if (!BitmapImage.TryCreateFromSvg(combinedSvg, out var image) || image == null)
                {
                    PluginLog.Warning($"Could not render upper zone icon SVG '{resourceName}'.");
                    return null;
                }

                return image;
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, $"Could not render upper zone icon SVG '{resourceName}'.");
                return null;
            }
        }

        public static BitmapImage ReadCenteredOriginalSvg(
            String resourceName,
            PluginImageSize imageSize,
            Double scale = 0.86,
            String overlay = null,
            Int32? clipHeight = null,
            Boolean isUnavailable = false)
        {
            var svg = ReadText(resourceName);
            if (String.IsNullOrWhiteSpace(svg))
            {
                return null;
            }

            var iconMarkup = ExtractSvgContents(svg);
            if (String.IsNullOrWhiteSpace(iconMarkup))
            {
                return null;
            }

            if (isUnavailable)
            {
                if (IsBoldWhiteOfflineIcon(resourceName))
                {
                    iconMarkup = ConvertSvgColorsToBoldWhite(iconMarkup);
                }
                else
                {
                    iconMarkup = DimSvgColorsToUnavailable(iconMarkup);
                }
            }

            var offset = (256.0 * (1.0 - scale)) / 2.0;
            var clipDefinition = clipHeight.HasValue
                ? $"<defs><clipPath id=\"icon-artwork-clip\"><rect x=\"0\" y=\"0\" width=\"256\" height=\"{clipHeight.Value}\"/></clipPath></defs>"
                : String.Empty;
            var clipAttribute = clipHeight.HasValue
                ? " clip-path=\"url(#icon-artwork-clip)\""
                : String.Empty;
            var combinedSvg =
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"256\" height=\"256\" viewBox=\"0 0 256 256\">" +
                clipDefinition +
                $"<g{clipAttribute} transform=\"translate({offset.ToString(CultureInfo.InvariantCulture)} {offset.ToString(CultureInfo.InvariantCulture)}) scale({scale.ToString(CultureInfo.InvariantCulture)})\">" +
                iconMarkup +
                "</g>" +
                (overlay ?? String.Empty) +
                "</svg>";

            combinedSvg = ApplyRootSize(combinedSvg, imageSize);

            try
            {
                return BitmapImage.TryCreateFromSvg(combinedSvg, out var image) ? image : null;
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, $"Could not render centered original SVG '{resourceName}'.");
                return null;
            }
        }

        public static BitmapImage ReadCenteredIconSvg(
            String resourceName,
            PluginImageSize imageSize,
            BitmapColor accentColor,
            String overlay = null)
        {
            var svg = ReadText(resourceName);
            if (String.IsNullOrWhiteSpace(svg))
            {
                return null;
            }

            var iconMarkup = ExtractSvgContents(svg);
            if (String.IsNullOrWhiteSpace(iconMarkup))
            {
                return null;
            }

            iconMarkup = iconMarkup.Replace(AccentColorToken, ToHexColor(accentColor), StringComparison.OrdinalIgnoreCase);

            var combinedSvg =
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"256\" height=\"256\" viewBox=\"0 0 256 256\">" +
                "<g transform=\"translate(23 23) scale(0.82)\">" +
                iconMarkup +
                "</g>" +
                (overlay ?? String.Empty) +
                "</svg>";

            combinedSvg = ApplyRootSize(combinedSvg, imageSize);

            try
            {
                return BitmapImage.TryCreateFromSvg(combinedSvg, out var image) ? image : null;
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, $"Could not render centered icon SVG '{resourceName}'.");
                return null;
            }
        }

        #if false
            var iconSvg = ReadText(resourceName);

            if (String.IsNullOrWhiteSpace(iconSvg))
            {
                PluginLog.Warning(
                    $"FPS preset icon resource could not be loaded: " +
                    $"'{resourceName}'.");

                return null;
            }

            try
            {
                /*
                 * Replace currentColor before embedding the SVG.
                 */
                iconSvg = iconSvg.Replace(
                    AccentColorToken,
                    ToHexColor(valueColor));

                /*
                 * Extract the source SVG's viewBox.
                 *
                 * We intentionally preserve the original viewBox because
                 * stripping the <svg> tag causes the paths to be interpreted
                 * in the parent SVG's coordinate system.
                 */
                var viewBox = GetSvgViewBox(iconSvg);

                if (String.IsNullOrWhiteSpace(viewBox))
                {
                    /*
                     * Most of the supplied icons should have a viewBox.
                     * Fall back to a 256x256 coordinate system if they don't.
                     */
                    viewBox = "0 0 256 256";

                    PluginLog.Warning(
                        $"FPS preset icon '{resourceName}' has no viewBox; " +
                        "falling back to 0 0 256 256.");
                }

                var iconMarkup = ExtractSvgContents(iconSvg);

                if (String.IsNullOrWhiteSpace(iconMarkup))
                {
                    PluginLog.Warning(
                        $"FPS preset icon '{resourceName}' contains no SVG artwork.");

                    return null;
                }

                /*
                 * The FPS icon is displayed in the upper portion of the key.
                 *
                 * The nested SVG preserves the original icon's coordinate
                 * system while width/height control the displayed size.
                 */
                const Int32 iconX = 98;
                const Int32 iconY = 7;
                const Int32 iconSize = 60;

                var numberSize =
                    !String.IsNullOrEmpty(displayText) &&
                    displayText.Length >= 3
                        ? KeyRenderer.FpsPresetValueFontSizeCompact
                        : KeyRenderer.FpsPresetValueFontSize;

                var background =
                    active
                        ? "#14181E"
                        : "#12151A";

                var safeDisplayText = EscapeXml(displayText);
                var safeLabel = EscapeXml(label);

                var labelSize =
                    String.Equals(
                        label,
                        "RTSS N/A",
                        StringComparison.OrdinalIgnoreCase)
                        ? KeyRenderer.FpsPresetStatusFontSizeLarge
                        : KeyRenderer.FpsPresetStatusFontSize;

                var valueHex = ToHexColor(valueColor);
                var labelHex = ToHexColor(labelColor);

                var svg =
                    $"<svg xmlns=\"http://www.w3.org/2000/svg\" " +
                    $"width=\"256\" height=\"256\" " +
                    $"viewBox=\"0 0 256 256\">" +

                    /*
                     * Background.
                     */
                    $"<rect " +
                    $"x=\"0\" y=\"0\" " +
                    $"width=\"256\" height=\"256\" " +
                    $"fill=\"{background}\"/>" +

                    /*
                     * FPS preset icon.
                     *
                     * IMPORTANT:
                     * The nested SVG retains the source viewBox.
                     */
                    $"<svg " +
                    $"x=\"{iconX}\" " +
                    $"y=\"{iconY}\" " +
                    $"width=\"{iconSize}\" " +
                    $"height=\"{iconSize}\" " +
                    $"viewBox=\"{EscapeXml(viewBox)}\" " +
                    $"preserveAspectRatio=\"xMidYMid meet\">" +

                    iconMarkup +

                    "</svg>" +

                    /*
                     * FPS number.
                     */
                    $"<text " +
                    $"x=\"128\" " +
                    $"y=\"149\" " +
                    $"text-anchor=\"middle\" " +
                    $"font-family=\"{EscapeXml(KeyRenderer.NumberFontName)}\" " +
                    $"font-size=\"{numberSize}\" " +
                    $"fill=\"{valueHex}\">" +
                    safeDisplayText +
                    "</text>" +

                    /*
                     * FPS label.
                     */
                    $"<text " +
                    $"x=\"128\" " +
                    $"y=\"180\" " +
                    $"text-anchor=\"middle\" " +
                    $"font-family=\"{EscapeXml(KeyRenderer.LabelFontName)}\" " +
                    $"font-size=\"24\" " +
                    $"fill=\"{valueHex}\">" +
                    "FPS" +
                    "</text>" +

                    /*
                     * Accent bar.
                     */
                    $"<rect " +
                    $"x=\"84\" " +
                    $"y=\"188\" " +
                    $"width=\"88\" " +
                    $"height=\"7\" " +
                    $"fill=\"{valueHex}\"/>" +

                    /*
                     * Status / preset label.
                     */
                    $"<text " +
                    $"x=\"128\" " +
                    $"y=\"231\" " +
                    $"text-anchor=\"middle\" " +
                    $"font-family=\"{EscapeXml(KeyRenderer.LabelFontName)}\" " +
                    $"font-size=\"{labelSize}\" " +
                    $"fill=\"{labelHex}\">" +
                    safeLabel +
                    "</text>" +

                    "</svg>";

                svg = ApplyRootSize(
                    svg,
                    imageSize);

                if (!BitmapImage.TryCreateFromSvg(
                        svg,
                        out var image) ||
                    image == null)
                {
                    PluginLog.Warning(
                        $"FPS preset SVG failed to render. " +
                        $"fps={fps}, resource='{resourceName}'.");

                    return null;
                }

                return image;
            }
            catch (Exception exception)
            {
                PluginLog.Warning(
                    exception,
                    $"Could not render FPS preset icon. " +
                    $"fps={fps}, resource='{resourceName}'.");

                return null;
            }
        }
        #endif

        /// <summary>
        /// Rewrites width/height on the root SVG element to the size
        /// requested by the device while preserving the viewBox.
        /// </summary>
        private static String ApplyRootSize(
            String svg,
            PluginImageSize imageSize)
        {
            if (String.IsNullOrWhiteSpace(svg))
            {
                return svg;
            }

            Int32 width;
            Int32 height;

            using (var builder = new BitmapBuilder(imageSize))
            {
                width = builder.Width;
                height = builder.Height;
            }

            if (width <= 0 || height <= 0)
            {
                return svg;
            }

            var svgStart = svg.IndexOf(
                "<svg",
                StringComparison.OrdinalIgnoreCase);

            if (svgStart < 0)
            {
                return svg;
            }

            var rootEnd = svg.IndexOf(
                '>',
                svgStart);

            if (rootEnd < 0)
            {
                return svg;
            }

            var rootTag =
                svg.Substring(
                    svgStart,
                    rootEnd - svgStart + 1);

            rootTag = ReplaceSizeAttributes(
                rootTag,
                width,
                height);

            return
                svg.Substring(0, svgStart) +
                rootTag +
                svg.Substring(rootEnd + 1);
        }

        private static Int32 GetImageDimension(PluginImageSize imageSize, Boolean width)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                return width ? builder.Width : builder.Height;
            }
        }

        private static String ReplaceSizeAttributes(
            String rootTag,
            Int32 width,
            Int32 height)
        {
            rootTag = ReplaceAttribute(
                rootTag,
                "width",
                width);

            rootTag = ReplaceAttribute(
                rootTag,
                "height",
                height);

            return rootTag;
        }

        private static String ReplaceAttribute(
            String rootTag,
            String attribute,
            Int32 value)
        {
            var formatted =
                $"{attribute}=\"" +
                value.ToString(CultureInfo.InvariantCulture) +
                "\"";

            var start = rootTag.IndexOf(
                attribute + "=\"",
                StringComparison.OrdinalIgnoreCase);

            if (start < 0)
            {
                var svgIndex = rootTag.IndexOf(
                    "<svg",
                    StringComparison.OrdinalIgnoreCase);

                if (svgIndex < 0)
                {
                    return rootTag;
                }

                var insertPosition = svgIndex + "<svg".Length;

                return rootTag.Insert(
                    insertPosition,
                    " " + formatted);
            }

            var valueStart =
                start + attribute.Length + 2;

            var end = rootTag.IndexOf(
                '"',
                valueStart);

            if (end < 0)
            {
                return rootTag;
            }

            return
                rootTag.Substring(0, start) +
                formatted +
                rootTag.Substring(end + 1);
        }

        /// <summary>
        /// Extracts the viewBox attribute from an SVG root element.
        /// </summary>
        private static String GetSvgViewBox(String svg)
        {
            if (String.IsNullOrWhiteSpace(svg))
            {
                return null;
            }

            var svgStart = svg.IndexOf(
                "<svg",
                StringComparison.OrdinalIgnoreCase);

            if (svgStart < 0)
            {
                return null;
            }

            var rootEnd = svg.IndexOf(
                '>',
                svgStart);

            if (rootEnd < 0)
            {
                return null;
            }

            var rootTag =
                svg.Substring(
                    svgStart,
                    rootEnd - svgStart + 1);

            return GetAttributeValue(
                rootTag,
                "viewBox");
        }

        /// <summary>
        /// Gets an XML attribute value from a tag.
        /// </summary>
        private static String GetAttributeValue(
            String tag,
            String attribute)
        {
            if (String.IsNullOrWhiteSpace(tag) ||
                String.IsNullOrWhiteSpace(attribute))
            {
                return null;
            }

            var marker =
                attribute + "=\"";

            var start =
                tag.IndexOf(
                    marker,
                    StringComparison.OrdinalIgnoreCase);

            if (start < 0)
            {
                return null;
            }

            start += marker.Length;

            var end =
                tag.IndexOf(
                    '"',
                    start);

            if (end < 0)
            {
                return null;
            }

            return tag.Substring(
                start,
                end - start);
        }

        /// <summary>
        /// Extracts everything between the opening and closing SVG tags.
        /// </summary>
        private static String ExtractSvgContents(String svg)
        {
            if (String.IsNullOrWhiteSpace(svg))
            {
                return null;
            }

            var openStart = svg.IndexOf(
                "<svg",
                StringComparison.OrdinalIgnoreCase);

            if (openStart < 0)
            {
                return null;
            }

            var openEnd = svg.IndexOf(
                '>',
                openStart);

            if (openEnd < 0)
            {
                return null;
            }

            var closeStart = svg.LastIndexOf(
                "</svg>",
                StringComparison.OrdinalIgnoreCase);

            if (closeStart < 0 ||
                closeStart <= openEnd)
            {
                return null;
            }

            return svg.Substring(
                openEnd + 1,
                closeStart - openEnd - 1);
        }

        /// <summary>
        /// Resolves an icon by prefix/suffix when a procedural render
        /// is not the most suitable option.
        /// </summary>
        public static String FindIndexedResource(
            String prefix,
            String suffix)
        {
            if (String.IsNullOrEmpty(prefix))
            {
                return null;
            }

            var exact =
                Resolve(
                    prefix +
                    (suffix ?? String.Empty));

            if (exact != null)
            {
                return exact;
            }

            lock (Sync)
            {
                foreach (var name in _resourceNames)
                {
                    var shortName =
                        GetShortName(name);

                    if (shortName.StartsWith(
                            prefix,
                            StringComparison.OrdinalIgnoreCase) &&
                        (String.IsNullOrEmpty(suffix) ||
                         shortName.EndsWith(
                             suffix,
                             StringComparison.OrdinalIgnoreCase)))
                    {
                        return name;
                    }
                }
            }

            return null;
        }

        private static Byte[] ReadBytes(
            String resourceName)
        {
            using (var stream =
                   GetStream(resourceName))
            {
                if (stream == null)
                {
                    return null;
                }

                try
                {
                    using (var memory =
                           new MemoryStream())
                    {
                        stream.CopyTo(memory);

                        return memory.ToArray();
                    }
                }
                catch (Exception exception)
                {
                    PluginLog.Warning(
                        exception,
                        $"Could not read the embedded resource '{resourceName}'.");

                    return null;
                }
            }
        }

        /// <summary>
        /// Full manifest name for a full or short resource name.
        /// </summary>
        private static String Resolve(
            String resourceName)
        {
            lock (Sync)
            {
                return ResolveCore(resourceName);
            }
        }

        /// <summary>
        /// Caller holds Sync.
        /// </summary>
        private static String ResolveCore(
            String resourceName)
        {
            if (String.IsNullOrEmpty(resourceName))
            {
                return null;
            }

            if (_resourcesByShortName.TryGetValue(
                    resourceName,
                    out var byShortName))
            {
                return byShortName;
            }

            foreach (var name in _resourceNames)
            {
                if (String.Equals(
                        name,
                        resourceName,
                        StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(
                        "." + resourceName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }

            return null;
        }

        /// <summary>
        /// Converts an embedded manifest resource name into its short icon filename.
        /// </summary>
        private static String GetShortName(
            String manifestName)
        {
            if (String.IsNullOrEmpty(manifestName))
            {
                return String.Empty;
            }

            var lastDot =
                manifestName.LastIndexOf('.');

            if (lastDot <= 0)
            {
                return manifestName;
            }

            var previousDot =
                manifestName.LastIndexOf(
                    '.',
                    lastDot - 1);

            return previousDot < 0
                ? manifestName
                : manifestName.Substring(
                    previousDot + 1);
        }

        /// <summary>
        /// Escapes text before inserting it into SVG/XML.
        /// </summary>
        private static String EscapeXml(
            String value)
        {
            return (value ?? String.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private static String ToHexColor(
            BitmapColor color)
        {
            return String.Format(
                CultureInfo.InvariantCulture,
                "#{0:X2}{1:X2}{2:X2}",
                color.R,
                color.G,
                color.B);
        }

        internal static String DimSvgColorsToUnavailable(String markup)
        {
            if (String.IsNullOrEmpty(markup))
            {
                return markup;
            }

            const String DimColor = "#566070";

            var result = markup;

            // In choose-profile, the checkmark path is stroke="white" inside a circle.
            // Replace with dark stroke #12151A so it remains visible as a cutout against the #566070 circle,
            // matching the black cutouts in add-profile and remove-profile.
            result = result.Replace("d=\"M188 169L172.661 184L167 177.5\" stroke=\"white\"", "d=\"M188 169L172.661 184L167 177.5\" stroke=\"#12151A\"");

            // Foreground hex colors across all project SVGs to dim to UnavailableAccent (#566070)
            var hexColors = new[]
            {
                "#00BFA5", "#00C8B3", "#00E5FF", "#00F0FF", "#00F3FF",
                "#06B6D4", "#0AF3D4", "#18F69E", "#1A8026", "#1DE9B6",
                "#26B333", "#26FA61", "#33FF4D", "#39FF14", "#3B82F6",
                "#419DFF", "#4285F4", "#4FC3F7", "#64B5F6", "#76FF03",
                "#7A55FF", "#7C3AED", "#7F00C8", "#9533FF", "#A855F7",
                "#B2B2B2", "#BD00FF", "#C084FC", "#CB30E0", "#D97706",
                "#E0E0E0", "#EC4899", "#F2F2F7", "#FBBF24", "#FEF7FF",
                "#FF383C", "#FF3B30", "#FF4D4D", "#FF8A00", "#FF8D28",
                "#FFCC00", "#FFD54F", "#FFD700", "#FFA000", "#FFE61A",
                "#E6B800", "#E67A1E", "#E6CE00",
                "#3CD36E", "#FFFFFF"
            };

            foreach (var hex in hexColors)
            {
                result = result.Replace(hex, DimColor, StringComparison.OrdinalIgnoreCase);
            }

            return result
                .Replace("\"white\"", $"\"{DimColor}\"")
                .Replace("'white'", $"'{DimColor}'");
        }

        internal static String ConvertSvgColorsToBoldWhite(String markup)
        {
            if (String.IsNullOrEmpty(markup))
            {
                return markup;
            }

            const String WhiteColor = "#FFFFFF";

            var result = markup;

            // In choose-profile, the checkmark path is stroke="white" inside a circle.
            // Replace with dark stroke #12151A so it remains visible as a cutout against the #FFFFFF circle if used.
            result = result.Replace("d=\"M188 169L172.661 184L167 177.5\" stroke=\"white\"", "d=\"M188 169L172.661 184L167 177.5\" stroke=\"#12151A\"");

            // Foreground hex colors across all project SVGs to convert to bold white (#FFFFFF)
            var hexColors = new[]
            {
                "#00BFA5", "#00C8B3", "#00E5FF", "#00F0FF", "#00F3FF",
                "#06B6D4", "#0AF3D4", "#18F69E", "#1A8026", "#1DE9B6",
                "#26B333", "#26FA61", "#33FF4D", "#39FF14", "#3B82F6",
                "#419DFF", "#4285F4", "#4FC3F7", "#64B5F6", "#76FF03",
                "#7A55FF", "#7C3AED", "#7F00C8", "#9533FF", "#A855F7",
                "#B2B2B2", "#BD00FF", "#C084FC", "#CB30E0", "#D97706",
                "#E0E0E0", "#EC4899", "#F2F2F7", "#FBBF24", "#FEF7FF",
                "#FF383C", "#FF3B30", "#FF4D4D", "#FF8A00", "#FF8D28",
                "#FFCC00", "#FFD54F", "#FFD700", "#FFA000", "#FFE61A",
                "#E6B800", "#E67A1E", "#E6CE00",
                "#3CD36E", "#566070"
            };

            foreach (var hex in hexColors)
            {
                result = result.Replace(hex, WhiteColor, StringComparison.OrdinalIgnoreCase);
            }

            return result;
        }

        internal static (Byte R, Byte G, Byte B, Byte A) GetSvgPixelRgba(String svgText, Int32 width, Int32 height, Int32 x, Int32 y)
        {
            using var svgStream = new MemoryStream(Encoding.UTF8.GetBytes(svgText));
            using var rendered = new SKSvg();
            using var picture = rendered.Load(svgStream);
            if (picture == null)
            {
                return (0, 0, 0, 0);
            }

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(width / 256.0f, height / 256.0f);
            canvas.DrawPicture(picture);
            canvas.Flush();

            var pixel = bitmap.GetPixel(x, y);
            return (pixel.Red, pixel.Green, pixel.Blue, pixel.Alpha);
        }
    }
}
