namespace Loupedeck.ETS2Plugin.Actions
{
    using System;
    using System.Threading;
    using Loupedeck.ETS2Plugin.Helpers;
    using Loupedeck.ETS2Plugin.Services;

    /// <summary>
    /// Truck body/chassis damage indicator.
    /// Only activates when new damage is taken — blinks orange (light) or red (heavy)
    /// for 4 seconds, then returns to green. Black when offline.
    /// </summary>
    public class TruckDamageCommand : PluginDynamicCommand
    {
        private TelemetryService _telemetryService;

        // -1 = baseline not yet established after connecting
        private float _currentChassisWear = -1f;

        private const int BLINK_INTERVAL_MS = 350;
        private const int   DAMAGE_DISPLAY_MS       = 4000;

        private bool   _damageActive = false;
        private bool   _blinkOn      = true;
        private string _damageIcon   = "TruckDamage_Warning.svg";

        private Timer _blinkTimer;
        private Timer _resetTimer;

        public TruckDamageCommand()
            : base(displayName: "Truck Damage",
                   description: "Blinks orange/red when the truck takes damage. Returns to green after 4 seconds.",
                   groupName: "ETS2 Indicators")
        {
            this.AddParameter("truckdamage", "Truck Damage", "ETS2 Indicators");
        }

        protected override bool OnLoad()
        {
            _blinkTimer = new Timer(OnBlinkTick,  null, Timeout.Infinite, Timeout.Infinite);
            _resetTimer = new Timer(OnResetTick,  null, Timeout.Infinite, Timeout.Infinite);

            var plugin = (ETS2Plugin)this.Plugin;
            _telemetryService = plugin.TelemetryService;

            if (_telemetryService != null)
            {
                _telemetryService.TruckChassisWearChanged += OnChassisWearChanged;
                _telemetryService.ConnectionStatusChanged  += OnConnectionStatusChanged;
                plugin.EnsureTelemetryStarted();
            }

            return true;
        }

        protected override bool OnUnload()
        {
            _blinkTimer?.Dispose();
            _resetTimer?.Dispose();

            if (_telemetryService != null)
            {
                _telemetryService.TruckChassisWearChanged -= OnChassisWearChanged;
                _telemetryService.ConnectionStatusChanged  -= OnConnectionStatusChanged;
            }

            return true;
        }

        private void OnConnectionStatusChanged(object sender, bool isConnected)
        {
            if (!isConnected)
            {
                // Stop any active blink and reset baseline on disconnect
                _damageActive = false;
                _currentChassisWear = -1f;
                _blinkTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _resetTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            this.ActionImageChanged();
        }

        private void OnChassisWearChanged(object sender, float wear)
        {
            if (_currentChassisWear < 0)
            {
                // First reading after connect — establish baseline, no alarm
                _currentChassisWear = wear;
                return;
            }

            if (wear > _currentChassisWear + 0.5f)
            {
                // Damage increase detected
                _damageIcon   = wear >= ThresholdSettings.TruckDamageHeavy ? "TruckDamage_Critical.svg" : "TruckDamage_Warning.svg";
                _damageActive = true;
                _blinkOn      = true;

                // Start blinking, reset after DAMAGE_DISPLAY_MS
                _blinkTimer.Change(BLINK_INTERVAL_MS, BLINK_INTERVAL_MS);
                _resetTimer.Change(DAMAGE_DISPLAY_MS, Timeout.Infinite);
            }

            _currentChassisWear = wear;
        }

        private void OnBlinkTick(object state)
        {
            _blinkOn = !_blinkOn;
            this.ActionImageChanged();
        }

        private void OnResetTick(object state)
        {
            _damageActive = false;
            _blinkOn      = true;
            _blinkTimer.Change(Timeout.Infinite, Timeout.Infinite);
            this.ActionImageChanged();
        }

        protected override void RunCommand(string actionParameter)
        {
            PluginLog.Info($"Truck chassis wear: {_currentChassisWear:F1}%");
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
                    _telemetryService.TruckChassisWearChanged += OnChassisWearChanged;
                    _telemetryService.ConnectionStatusChanged  += OnConnectionStatusChanged;
                }
            }

            if (_telemetryService == null || !_telemetryService.IsConnected)
                return PluginResources.ReadImage("TruckDamage_Offline.svg");

            if (_damageActive && _blinkOn)
                return PluginResources.ReadImage(_damageIcon);

            return PluginResources.ReadImage("TruckDamage_Normal.svg");
        }
    }
}
