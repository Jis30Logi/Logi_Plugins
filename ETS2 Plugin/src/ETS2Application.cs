namespace Loupedeck.ETS2Plugin
{
    using System;

    /// <summary>
    /// ETS2 Application class for the plugin.
    /// Since this is an API-only plugin (universal plugin), this class is minimal.
    /// </summary>
    public class ETS2Application : ClientApplication
    {
        public ETS2Application()
        {
        }

        protected override String GetProcessName() => "";
    }
}
