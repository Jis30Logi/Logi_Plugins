namespace Loupedeck.ETS2Plugin.Actions
{
    using System;
    using Loupedeck.ETS2Plugin.Services;

    /// <summary>
    /// Parking brake indicator.
    /// Shows GREEN when off, RED when engaged.
    /// </summary>
    public class ParkingBrakeCommand : PluginDynamicCommand
    {
        private TelemetryService _telemetryService;
        private bool _isParkingBrakeOn = false;

        public ParkingBrakeCommand()
            : base(displayName: "Parking Brake",
                   description: "Shows green when off, red when parking brake is engaged",
                   groupName: "ETS2 Indicators")
        {
            this.AddParameter("parking", "Parking Brake", "ETS2 Indicators");
        }

        protected override bool OnLoad()
        {
            var plugin = (ETS2Plugin)this.Plugin;
            _telemetryService = plugin.TelemetryService;

            if (_telemetryService != null)
            {
                _telemetryService.ParkingBrakeChanged += OnParkingBrakeChanged;
                _telemetryService.ConnectionStatusChanged += OnConnectionStatusChanged;
                plugin.EnsureTelemetryStarted();
                PluginLog.Info("ParkingBrakeCommand loaded and subscribed to parking brake changes");
            }
            else
            {
                PluginLog.Warning("ParkingBrakeCommand: TelemetryService is null");
            }

            return true;
        }

        protected override bool OnUnload()
        {
            if (_telemetryService != null)
            {
                _telemetryService.ParkingBrakeChanged -= OnParkingBrakeChanged;
                _telemetryService.ConnectionStatusChanged -= OnConnectionStatusChanged;
                PluginLog.Info("ParkingBrakeCommand unloaded");
            }
            return true;
        }

        private void OnConnectionStatusChanged(object sender, bool isConnected)
        {
            this.ActionImageChanged();
        }

        private void OnParkingBrakeChanged(object sender, bool isOn)
        {
            _isParkingBrakeOn = isOn;
            
            // Update icon when parking brake state changes
            this.ActionImageChanged();
        }

        protected override void RunCommand(string actionParameter)
        {
            PluginLog.Info($"Parking brake: {(_isParkingBrakeOn ? "ON (Engaged)" : "OFF (Released)")}");
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
                    _telemetryService.ParkingBrakeChanged += OnParkingBrakeChanged;
                    _telemetryService.ConnectionStatusChanged += OnConnectionStatusChanged;
                }
            }

            // 3-state logic: Offline (gray) -> Online Normal (green) -> Warning (red)
            string svgFileName;
            
            if (_telemetryService == null || !_telemetryService.IsConnected)
            {
                svgFileName = "Parking_Offline.svg";
            }
            else if (_isParkingBrakeOn)
            {
                svgFileName = "Parking_On.svg";
            }
            else
            {
                svgFileName = "Parking_Off.svg";
            }

            return PluginResources.ReadImage(svgFileName);
        }
    }
}
