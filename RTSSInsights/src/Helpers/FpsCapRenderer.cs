namespace Loupedeck.RTSSPerformancePlugin.Helpers
{
    using System;
    /// <summary>Renders the supplied FPS preset artwork at full button size.</summary>
    public static class FpsCapRenderer
    {
        public const Int32 MainTitleFontSize = 60;

        // Keyed by every input that shapes the picture; holds only static artwork, never live RTSS data.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<String, BitmapImage> PageCache =
            new System.Collections.Concurrent.ConcurrentDictionary<String, BitmapImage>();

        private static BitmapImage GetOrRender(String key, Func<BitmapImage> render)
        {
            // BitmapImage is IDisposable and a disposed one is unusable. The cached master is never
            // handed out, so a host that disposes a returned image cannot poison later redraws.
            if (PageCache.TryGetValue(key, out var cached))
            {
                return cached.Copy();
            }

            var image = render();
            if (image == null)
            {
                return null;
            }

            PageCache[key] = image;
            return image.Copy();
        }

        public static BitmapImage RenderMainButton(PluginImageSize imageSize, Boolean isRtssAvailable = true) =>
            GetOrRender(
                $"main|{(Int32)imageSize}|{isRtssAvailable}",
                () => RenderMainButtonUncached(imageSize, isRtssAvailable));

        private static BitmapImage RenderMainButtonUncached(PluginImageSize imageSize, Boolean isRtssAvailable)
        {
            var color = isRtssAvailable ? "#FF8A00" : "#566070";
            var svg =
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"256\" height=\"256\" viewBox=\"0 0 256 256\">" +
                $"<text x=\"128\" y=\"148\" text-anchor=\"middle\" " +
                $"font-family=\"{KeyRenderer.NumberFontName}\" font-size=\"{MainTitleFontSize}\" font-weight=\"700\" " +
                $"fill=\"{color}\">FPS CAP</text>" +
                "</svg>";

            return PluginResources.RenderInlineSvg(svg, imageSize);
        }

        public static BitmapImage RenderArtworkPage(
            PluginImageSize imageSize,
            String resourceName,
            Boolean isActive = false,
            Boolean isRtssAvailable = true,
            Boolean isPending = false) =>
            GetOrRender(
                $"page|{(Int32)imageSize}|{resourceName}|{isActive}|{isRtssAvailable}|{isPending}",
                () => RenderArtworkPageUncached(imageSize, resourceName, isActive, isRtssAvailable, isPending));

        private static BitmapImage RenderArtworkPageUncached(
            PluginImageSize imageSize,
            String resourceName,
            Boolean isActive,
            Boolean isRtssAvailable,
            Boolean isPending)
        {
            String overlay = null;
            if (isRtssAvailable && isPending)
            {
                overlay = "<rect x=\"6\" y=\"6\" width=\"244\" height=\"244\" rx=\"10\" " +
                          "fill=\"none\" stroke=\"#FF8A00\" stroke-width=\"8\" stroke-dasharray=\"20,10\"/>";
            }
            else if (isActive)
            {
                // Drawn even while RTSS is away: the cap in force is still the cap in force.
                overlay = "<rect x=\"6\" y=\"6\" width=\"244\" height=\"244\" rx=\"10\" " +
                          "fill=\"none\" stroke=\"#FFFFFF\" stroke-width=\"8\"/>";
            }

            return RenderFullPage(imageSize, resourceName, overlay, isRtssAvailable: isRtssAvailable);
        }

        private static BitmapImage RenderFullPage(
            PluginImageSize imageSize,
            String resourceName,
            String overlay,
            Int32? clipHeight = null,
            Boolean isRtssAvailable = true)
        {
            return PluginResources.ReadCenteredOriginalSvg(
                resourceName,
                imageSize,
                1.0,
                overlay,
                clipHeight,
                isUnavailable: !isRtssAvailable);
        }
    }
}