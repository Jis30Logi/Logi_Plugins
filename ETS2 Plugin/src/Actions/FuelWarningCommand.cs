namespace Loupedeck.ETS2Plugin.Actions
{
    using System;
    using Loupedeck.ETS2Plugin.Helpers;
    using Loupedeck.ETS2Plugin.Services;

    /// <summary>
    /// Fuel warning indicator.
    ///   >= 50%  → Normal (green)
    ///   15–50%  → Half   (yellow)
    ///   < 15%   → Low    (red)
    ///   offline → Offline (black)
    /// </summary>
    public class FuelWarningCommand : PluginDynamicCommand
    {
        private TelemetryService _telemetryService;
        private float _currentFuelPercent = 100f;

        public FuelWarningCommand()
            : base(displayName: "Fuel Warning",
                   description: "Green >= 50%, yellow 15-50%, red < 15%",
                   groupName: "ETS2 Indicators")
        {
            this.AddParameter("fuel", "Fuel Warning", "ETS2 Indicators");
        }

        protected override bool OnLoad()
        {
            var plugin = (ETS2Plugin)this.Plugin;
            _telemetryService = plugin.TelemetryService;

            if (_telemetryService != null)
            {
                _telemetryService.FuelChanged += OnFuelChanged;
                _telemetryService.ConnectionStatusChanged += OnConnectionStatusChanged;
                plugin.EnsureTelemetryStarted();
                PluginLog.Info("FuelWarningCommand loaded and subscribed to fuel changes");
            }
            else
            {
                PluginLog.Warning("FuelWarningCommand: TelemetryService is null");
            }

            return true;
        }

        protected override bool OnUnload()
        {
            if (_telemetryService != null)
            {
                _telemetryService.FuelChanged -= OnFuelChanged;
                _telemetryService.ConnectionStatusChanged -= OnConnectionStatusChanged;
                PluginLog.Info("FuelWarningCommand unloaded");
            }
            return true;
        }

        private void OnConnectionStatusChanged(object sender, bool isConnected)
        {
            this.ActionImageChanged();
        }

        private void OnFuelChanged(object sender, float fuelPercent)
        {
            _currentFuelPercent = fuelPercent;
            
            // Update icon when fuel level changes
            this.ActionImageChanged();
        }

        protected override void RunCommand(string actionParameter)
        {
            PluginLog.Info($"Current fuel: {_currentFuelPercent:F1}% (Low: {ThresholdSettings.FuelLow}%, Half: {ThresholdSettings.FuelHalf}%)");
        }

        protected override string GetCommandDisplayName(string actionParameter, PluginImageSize imageSize)
        {
            // Return space to hide Loupedeck's text overlay - labels are embedded in SVG
            return " ";
        }

        protected override BitmapImage GetCommandImage(string actionParameter, PluginImageSize imageSize)
        {
            // Lazy load service if not initialized
            if (_telemetryService == null)
            {
                var plugin = (ETS2Plugin)this.Plugin;
                _telemetryService = plugin?.TelemetryService;

                if (_telemetryService != null)
                {
                    _telemetryService.FuelChanged += OnFuelChanged;
                    _telemetryService.ConnectionStatusChanged += OnConnectionStatusChanged;
                }
            }

            string svgFileName;

            if (_telemetryService == null || !_telemetryService.IsConnected)
            {
                svgFileName = "Fuel_Offline.svg";
            }
            else
            {
                var fuel = _telemetryService.CurrentFuelPercent;
                if (fuel < ThresholdSettings.FuelLow)
                    svgFileName = "Fuel_Low.svg";
                else if (fuel < ThresholdSettings.FuelHalf)
                    svgFileName = "Fuel_Half.svg";
                else
                    svgFileName = "Fuel_Normal.svg";
            }

            return PluginResources.ReadImage(svgFileName);
        }
    }
}
