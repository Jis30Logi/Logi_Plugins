namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Diagnostics;

    using Loupedeck;
    using Loupedeck.RTSSPerformancePlugin.Helpers;

    public interface IRTSSOnboardingService
    {
        String PluginTitle { get; }
        String MissingInstallationMessage { get; }
        String PrimaryButtonTitle { get; }
        String DownloadUrl { get; }
        Boolean IsOnboardingRequired { get; }
        Boolean IsOnboardingActive { get; }

        event EventHandler<Boolean> OnboardingStateChanged;

        Boolean CheckInstallationState(PluginStatus currentPluginStatus = PluginStatus.Unknown);
        Boolean OpenDownloadPage();
        BitmapImage RenderOnboardingCard(PluginImageSize imageSize);
    }

    /// <summary>
    /// Manages onboarding lifecycle when RTSS is not installed on the system.
    /// Communicates onboarding status, guidance message, and primary download action
    /// to Logi Options+ using supported Logi Actions SDK plugin status channels.
    /// </summary>
    public sealed class RTSSOnboardingService : IRTSSOnboardingService
    {
        public const String DefaultPluginTitle = "RTSS Performance & Control";

        public const String DefaultMissingInstallationMessage =
            "RTSS is required for RTSS Performance & Control.";

        public const String DefaultPrimaryButtonTitle = "INSTALL RTSS";

        public const String DefaultDownloadUrl =
            "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/";

        private readonly Object _sync = new Object();
        private readonly Func<Boolean> _installationChecker;
        private readonly Action<String> _urlOpener;
        private readonly Action<PluginStatus, String, String, String> _statusNotifier;

        private Boolean _isOnboardingActive;

        public String PluginTitle { get; }
        public String MissingInstallationMessage { get; }
        public String PrimaryButtonTitle { get; }
        public String DownloadUrl { get; }

        public Boolean IsOnboardingRequired => !this.IsRTSSInstalled();

        public Boolean IsOnboardingActive
        {
            get
            {
                lock (this._sync)
                {
                    return this._isOnboardingActive;
                }
            }
            private set
            {
                var changed = false;
                lock (this._sync)
                {
                    if (this._isOnboardingActive != value)
                    {
                        this._isOnboardingActive = value;
                        changed = true;
                    }
                }

                if (changed)
                {
                    this.OnboardingStateChanged?.Invoke(this, value);
                }
            }
        }

        public event EventHandler<Boolean> OnboardingStateChanged;

        public RTSSOnboardingService(
            Action<PluginStatus, String, String, String> statusNotifier = null,
            Func<Boolean> installationChecker = null,
            Action<String> urlOpener = null,
            String pluginTitle = DefaultPluginTitle,
            String missingInstallationMessage = DefaultMissingInstallationMessage,
            String primaryButtonTitle = DefaultPrimaryButtonTitle,
            String downloadUrl = DefaultDownloadUrl)
        {
            this._statusNotifier = statusNotifier;
            this._installationChecker = installationChecker ?? RTSSInstallLocator.IsInstalled;
            this._urlOpener = urlOpener ?? SafeOpenBrowserUrl;
            this.PluginTitle = pluginTitle ?? DefaultPluginTitle;
            this.MissingInstallationMessage = missingInstallationMessage ?? DefaultMissingInstallationMessage;
            this.PrimaryButtonTitle = primaryButtonTitle ?? DefaultPrimaryButtonTitle;
            this.DownloadUrl = downloadUrl ?? DefaultDownloadUrl;
        }

        public Boolean IsRTSSInstalled()
        {
            try
            {
                return this._installationChecker();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[RTSS ONBOARDING] RTSS installation check threw exception; assuming not installed.");
                return false;
            }
        }

        /// <summary>
        /// Evaluates whether RTSS is installed.
        /// If missing: activates the onboarding state inside Logi Options+.
        /// If installed: removes the onboarding state so normal offline/online behavior proceeds.
        /// Returns true if onboarding is currently active (RTSS missing).
        /// </summary>
        public Boolean CheckInstallationState(PluginStatus currentPluginStatus = PluginStatus.Unknown)
        {
            var isInstalled = this.IsRTSSInstalled();

            if (!isInstalled)
            {
                if (!this.IsOnboardingActive || currentPluginStatus != PluginStatus.Warning)
                {
                    PluginLog.Info("[RTSS ONBOARDING] RTSS is not installed. Displaying onboarding prompt in Logi Options+.");
                    this.IsOnboardingActive = true;
                    this._statusNotifier?.Invoke(
                        PluginStatus.Warning,
                        this.MissingInstallationMessage,
                        this.DownloadUrl,
                        this.PrimaryButtonTitle);
                }

                return true;
            }

            if (this.IsOnboardingActive || currentPluginStatus == PluginStatus.Warning)
            {
                PluginLog.Info("[RTSS ONBOARDING] RTSS installation detected. Dismissing onboarding state.");
                this.IsOnboardingActive = false;
                this._statusNotifier?.Invoke(
                    PluginStatus.Normal,
                    String.Empty,
                    null,
                    null);
            }

            return false;
        }

        /// <summary>
        /// Opens the official RTSS download page in the user's default browser.
        /// </summary>
        public Boolean OpenDownloadPage()
        {
            try
            {
                PluginLog.Info($"[RTSS ONBOARDING] Opening RTSS download page: {this.DownloadUrl}");
                this._urlOpener(this.DownloadUrl);
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"[RTSS ONBOARDING] Failed to open browser at URL '{this.DownloadUrl}'.");
                return false;
            }
        }

        /// <summary>
        /// Safe browser-opening implementation avoiding process hangs or terminal blocks.
        /// </summary>
        public static void SafeOpenBrowserUrl(String url)
        {
            if (String.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };

            Process.Start(psi);
        }

        /// <summary>
        /// Generates a visual onboarding card matching the Logi Options+ design language:
        /// clean header with RTSS icon, bold title, message, and prominent primary action button.
        /// </summary>
        public BitmapImage RenderOnboardingCard(PluginImageSize imageSize)
        {
            var svg =
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"256\" height=\"256\" viewBox=\"0 0 256 256\">" +
                "  <rect width=\"256\" height=\"256\" rx=\"16\" fill=\"#1B1E24\" stroke=\"#2F3542\" stroke-width=\"2\"/>" +
                "  <!-- Header / Icon Area -->" +
                "  <rect x=\"18\" y=\"16\" width=\"36\" height=\"36\" rx=\"8\" fill=\"#272B35\"/>" +
                "  <path d=\"M26 34 L36 24 L46 34 M36 25 L36 43\" fill=\"none\" stroke=\"#00B0FF\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>" +
                "  <text x=\"62\" y=\"38\" font-family=\"Segoe UI, sans-serif\" font-size=\"14\" font-weight=\"700\" fill=\"#FFFFFF\">RTSS Performance</text>" +
                "  <!-- Message -->" +
                "  <text x=\"20\" y=\"82\" font-family=\"Segoe UI, sans-serif\" font-size=\"12\" fill=\"#B0B7C3\">" +
                "    <tspan x=\"20\" dy=\"0\">RTSS is required for</tspan>" +
                "    <tspan x=\"20\" dy=\"16\">RTSS Performance &amp; Control.</tspan>" +
                "  </text>" +
                "  <!-- Primary Button: INSTALL RTSS -->" +
                "  <rect x=\"20\" y=\"182\" width=\"216\" height=\"44\" rx=\"22\" fill=\"#00B0FF\"/>" +
                "  <text x=\"128\" y=\"210\" text-anchor=\"middle\" font-family=\"Segoe UI, sans-serif\" font-size=\"15\" font-weight=\"700\" fill=\"#FFFFFF\">INSTALL RTSS</text>" +
                "</svg>";

            return PluginResources.RenderInlineSvg(svg, imageSize);
        }
    }
}
