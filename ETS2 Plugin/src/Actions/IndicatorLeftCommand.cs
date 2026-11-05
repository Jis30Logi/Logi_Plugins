namespace Loupedeck.ETS2Plugin.Actions
{
    using System.Threading;
    using Loupedeck.ETS2Plugin.Helpers;
    using Loupedeck.ETS2Plugin.Services;

    /// <summary>
    /// Left turn indicator. Blinks yellow while the left blinker is active.
    /// Black background always; arrow is yellow (lit) or near-black (off).
    /// </summary>
    public class IndicatorLeftCommand : PluginDynamicCommand
    {
        private TelemetryService _telemetryService;
        private bool _isActive  = false;
        private bool _blinkOn   = false;
        private Timer _blinkTimer;
        private const int BLINK_INTERVAL_MS = 450;

        public IndicatorLeftCommand()
            : base(displayName: "Left Indicator",
                   description: "Blinks yellow when the left turn indicator is on",
                   groupName: "ETS2 Indicators")
        {
            this.AddParameter("indicator_left", "Left Indicator", "ETS2 Indicators");
        }

        protected override bool OnLoad()
        {
            _blinkTimer = new Timer(OnBlinkTick, null, Timeout.Infinite, Timeout.Infinite);

            var plugin = (ETS2Plugin)this.Plugin;
            _telemetryService = plugin.TelemetryService;

            if (_telemetryService != null)
            {
                _telemetryService.BlinkerLeftChanged     += OnBlinkerChanged;
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
                _telemetryService.BlinkerLeftChanged     -= OnBlinkerChanged;
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

        private void OnBlinkerChanged(object sender, bool isActive)
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

        // '[' key scan code
        private const ushort SC_LEFT_INDICATOR = 0x1A;

        protected override void RunCommand(string actionParameter)
            => KeySender.PressScanCode(SC_LEFT_INDICATOR);

        protected override string GetCommandDisplayName(string actionParameter, PluginImageSize imageSize) => " ";

        protected override BitmapImage GetCommandImage(string actionParameter, PluginImageSize imageSize)
        {
            if (_telemetryService == null)
            {
                var plugin = (ETS2Plugin)this.Plugin;
                _telemetryService = plugin?.TelemetryService;

                if (_telemetryService != null)
                {
                    _telemetryService.BlinkerLeftChanged     += OnBlinkerChanged;
                    _telemetryService.ConnectionStatusChanged += OnConnectionStatusChanged;
                }
            }

            var svgFileName = (_isActive && _blinkOn)
                ? "IndicatorLeft_On.svg"
                : "IndicatorLeft_Off.svg";

            return PluginResources.ReadImage(svgFileName);
        }
    }
}
