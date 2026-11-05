namespace Loupedeck.ETS2Plugin.Actions
{
    using System.Threading;
    using Loupedeck.ETS2Plugin.Helpers;
    using Loupedeck.ETS2Plugin.Services;

    public class SpeedWarningCommand : PluginDynamicCommand
    {
        private TelemetryService _telemetryService;
        private float _currentSpeed = 0f;
        private float _currentSpeedLimit = 0f;

        private Timer _blinkTimer;
        private bool _blinkOn = false;

        public SpeedWarningCommand()
            : base(displayName: "Speed Warning",
                   description: "Shows green when within speed limit, red blink when exceeding",
                   groupName: "ETS2 Indicators")
        {
            this.AddParameter("speed", "Speed Warning", "ETS2 Indicators");
        }

        protected override bool OnLoad()
        {
            var plugin = (ETS2Plugin)this.Plugin;
            _telemetryService = plugin.TelemetryService;

            if (_telemetryService != null)
            {
                _telemetryService.SpeedChanged += OnSpeedChanged;
                _telemetryService.SpeedLimitChanged += OnSpeedLimitChanged;
                _telemetryService.ConnectionStatusChanged += OnConnectionStatusChanged;
                plugin.EnsureTelemetryStarted();
            }

            return true;
        }

        protected override bool OnUnload()
        {
            StopBlinking();

            if (_telemetryService != null)
            {
                _telemetryService.SpeedChanged -= OnSpeedChanged;
                _telemetryService.SpeedLimitChanged -= OnSpeedLimitChanged;
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

        private void OnSpeedChanged(object sender, float speed)
        {
            _currentSpeed = speed;
            UpdateBlink();
        }

        private void OnSpeedLimitChanged(object sender, float speedLimit)
        {
            _currentSpeedLimit = speedLimit;
            UpdateBlink();
        }

        private void UpdateBlink()
        {
            if (IsSpeeding())
                StartBlinking();
            else
                StopBlinking();

            this.ActionImageChanged();
        }

        private bool IsSpeeding()
        {
            var limit = _currentSpeedLimit >= 10f ? _currentSpeedLimit : ThresholdSettings.SpeedFallback;
            return _currentSpeed > limit;
        }

        private void StartBlinking()
        {
            if (_blinkTimer != null) return;
            _blinkOn = true;
            _blinkTimer = new Timer(_ => { _blinkOn = !_blinkOn; this.ActionImageChanged(); }, null, 0, 400);
        }

        private void StopBlinking()
        {
            _blinkTimer?.Dispose();
            _blinkTimer = null;
            _blinkOn = false;
        }

        protected override void RunCommand(string actionParameter)
        {
            PluginLog.Info($"Speed: {_currentSpeed:F1} km/h  Limit: {_currentSpeedLimit:F1} km/h");
        }

        protected override string GetCommandDisplayName(string actionParameter, PluginImageSize imageSize) => " ";

        protected override BitmapImage GetCommandImage(string actionParameter, PluginImageSize imageSize)
        {
            if (_telemetryService == null)
            {
                var plugin = (ETS2Plugin)this.Plugin;
                _telemetryService = plugin?.TelemetryService;

                if (_telemetryService != null)
                {
                    _telemetryService.SpeedChanged += OnSpeedChanged;
                    _telemetryService.SpeedLimitChanged += OnSpeedLimitChanged;
                    _telemetryService.ConnectionStatusChanged += OnConnectionStatusChanged;
                }
            }

            if (_telemetryService == null || !_telemetryService.IsConnected)
                return PluginResources.ReadImage("Speed_Offline.svg");

            if (IsSpeeding())
                return PluginResources.ReadImage(_blinkOn ? "Speed_Warning.svg" : "Speed_Offline.svg");

            return PluginResources.ReadImage("Speed_Normal.svg");
        }
    }
}
