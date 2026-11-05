namespace Loupedeck.ETS2Plugin.Actions
{
    using System.Threading;
    using Loupedeck.ETS2Plugin.Helpers;
    using Loupedeck.ETS2Plugin.Services;

    /// <summary>
    /// Hazard lights indicator. Blinks yellow (<>) when hazard lights are active.
    /// Press to toggle hazard lights in-game.
    /// </summary>
    public class HazardCommand : PluginDynamicCommand
    {
        private TelemetryService _telemetryService;
        private bool _isActive = false;
        private bool _blinkOn  = false;
        private Timer _blinkTimer;
        private const int BLINK_INTERVAL_MS = 450;

        // ETS2 default scan code for hazard lights (F key)
        private const ushort SC_HAZARD = 0x21;

        public HazardCommand()
            : base(displayName: "Hazard Lights",
                   description: "Blinks yellow when hazard lights are on. Press to toggle.",
                   groupName: "ETS2 Indicators")
        {
            this.AddParameter("hazard", "Hazard Lights", "ETS2 Indicators");
        }

        protected override bool OnLoad()
        {
            _blinkTimer = new Timer(OnBlinkTick, null, Timeout.Infinite, Timeout.Infinite);

            var plugin = (ETS2Plugin)this.Plugin;
            _telemetryService = plugin.TelemetryService;

            if (_telemetryService != null)
            {
                _telemetryService.HazardChanged          += OnHazardChanged;
                _telemetryService.ConnectionStatusChanged += OnConnectionStatusChanged;
                plugin.EnsureTelemetryStarted();
            }

            return true;
        }

        protected override bool OnUnload()
        {
            _blinkTimer?.Dispose();

            if (_telemetryService != null)
            {
                _telemetryService.HazardChanged          -= OnHazardChanged;
                _telemetryService.ConnectionStatusChanged -= OnConnectionStatusChanged;
            }

            return true;
        }

        private void OnConnectionStatusChanged(object sender, bool isConnected)
        {
            if (!isConnected)
                StopBlinking();

            this.ActionImageChanged();
        }

        private void OnHazardChanged(object sender, bool isActive)
        {
            _isActive = isActive;

            if (isActive)
            {
                _blinkOn = true;
                _blinkTimer.Change(BLINK_INTERVAL_MS, BLINK_INTERVAL_MS);
            }
            else
            {
                StopBlinking();
            }

            this.ActionImageChanged();
        }

        private void StopBlinking()
        {
            _isActive = false;
            _blinkOn  = false;
            _blinkTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void OnBlinkTick(object state)
        {
            _blinkOn = !_blinkOn;
            this.ActionImageChanged();
        }

        protected override void RunCommand(string actionParameter)
            => KeySender.PressScanCode(SC_HAZARD);

        protected override string GetCommandDisplayName(string actionParameter, PluginImageSize imageSize) => " ";

        protected override BitmapImage GetCommandImage(string actionParameter, PluginImageSize imageSize)
        {
            if (_telemetryService == null)
            {
                var plugin = (ETS2Plugin)this.Plugin;
                _telemetryService = plugin?.TelemetryService;

                if (_telemetryService != null)
                {
                    _telemetryService.HazardChanged          += OnHazardChanged;
                    _telemetryService.ConnectionStatusChanged += OnConnectionStatusChanged;
                }
            }

            var svgFileName = (_isActive && _blinkOn)
                ? "Hazard_On.svg"
                : "Hazard_Off.svg";

            return PluginResources.ReadImage(svgFileName);
        }
    }
}
