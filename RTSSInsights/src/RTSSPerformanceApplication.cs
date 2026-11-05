namespace Loupedeck.RTSSPerformancePlugin
{
    using System;

    /// <summary>
    /// Application descriptor for the Logi Plugin Service.
    ///
    /// This class must exist even though the plugin targets no host application. The service
    /// reflects over the plugin assembly for a <see cref="ClientApplication"/> subclass during
    /// load, and an assembly without one is rejected outright:
    ///
    ///     ERROR | Cannot load plugin from '...\RTSSPerformancePlugin.dll'
    ///     WARN  | Plugin 'RTSSPerformance' added to disabled plugins list
    ///
    /// The plugin then appears under "Installed Plugins" in Logi Options+ while contributing zero
    /// actions, because action discovery never runs on a plugin that failed to load. Verified
    /// on-device 2026-08-21 by bisecting against a known-good third-party plugin: an otherwise
    /// identical minimal plugin loads with this class present and fails without it.
    ///
    /// The process and bundle names are deliberately empty. Returning a real process name would
    /// scope the plugin to that application, so its actions would only be offered while that
    /// application had focus. This plugin is universal — see
    /// <see cref="RTSSPerformancePlugin.HasNoApplication"/> — so the actions stay available
    /// regardless of what is in the foreground.
    /// </summary>
    public class RTSSPerformanceApplication : ClientApplication
    {
        public RTSSPerformanceApplication()
        {
        }

        protected override String GetProcessName() => String.Empty;

        protected override String GetBundleName() => String.Empty;

        public override ClientApplicationStatus GetApplicationStatus() =>
            Helpers.RTSSInstallLocator.IsInstalled()
                ? ClientApplicationStatus.Installed
                : ClientApplicationStatus.NotInstalled;

        [Obsolete]
        public override Boolean IsInstalled() =>
            Helpers.RTSSInstallLocator.IsInstalled();
    }
}
