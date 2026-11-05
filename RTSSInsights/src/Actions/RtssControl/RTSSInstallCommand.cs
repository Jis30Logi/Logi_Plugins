namespace Loupedeck.RTSSPerformancePlugin.Actions
{
    using System;

    using Loupedeck.RTSSPerformancePlugin.Helpers;
    using Loupedeck.RTSSPerformancePlugin.Services;

    /// <summary>
    /// Action helper representing the INSTALL RTSS onboarding command.
    /// Does not inherit from PluginDynamicCommand so as not to clutter
    /// the Loupedeck console device action catalog (which strictly exposes the 9 primary actions).
    /// </summary>
    public sealed class RTSSInstallCommand
    {
        private readonly IRTSSOnboardingService _onboardingService;

        public String DisplayName => "INSTALL RTSS";
        public String Description => "Install RivaTuner Statistics Server (RTSS) to use performance monitoring and control features.";
        public String GroupName => "RTSS CONTROLS";

        public RTSSInstallCommand()
            : this(null)
        {
        }

        public RTSSInstallCommand(IRTSSOnboardingService onboardingService)
        {
            this._onboardingService = onboardingService;
        }

        public String BuildVisualSignature()
        {
            var service = this._onboardingService ?? RTSSPerformancePlugin.Instance?.Onboarding;
            return service?.IsOnboardingActive == true ? "onboarding-active" : "onboarding-inactive";
        }

        public BitmapImage GetCommandImage(PluginImageSize imageSize)
        {
            return KeyRenderer.RenderRtssInstallKey(imageSize);
        }

        public String GetCommandDisplayName(PluginImageSize imageSize) => "INSTALL RTSS";

        public void RunCommand(String actionParameter = null)
        {
            var service = this._onboardingService ?? RTSSPerformancePlugin.Instance?.Onboarding;
            if (service != null)
            {
                service.OpenDownloadPage();
            }
            else
            {
                RTSSOnboardingService.SafeOpenBrowserUrl(RTSSOnboardingService.DefaultDownloadUrl);
            }
        }

        internal void DoRunCommand(String actionParameter = null) => this.RunCommand(actionParameter);

        internal BitmapImage DoGetCommandImage(PluginImageSize size) => this.GetCommandImage(size);

        internal String DoGetCommandDisplayName(PluginImageSize size) => this.GetCommandDisplayName(size);

        internal String DoBuildVisualSignature() => this.BuildVisualSignature();
    }
}
