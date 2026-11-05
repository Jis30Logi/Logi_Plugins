namespace Loupedeck.ETS2Plugin.Actions
{
    using System.Threading;
    using Loupedeck.ETS2Plugin.Helpers;
    using Loupedeck.ETS2Plugin.Services;

    /// <summary>
    /// Truck damage warning indicator.
    ///   0–1%    → Healthy  (green, static)
    ///   1–50%   → Warning  (yellow, static)
    ///   > 50%   → Damaged  (red, blinking)
    ///   offline → Offline  (black, static)
    /// Uses the highest wear value across engine, transmission, cabin, chassis, wheels.
    /// </summary>
    public class EngineDamageCommand : PluginDynamicCommand
    {
        private TelemetryService _telemetryService;

        private Timer _blinkTimer;
        private Timer _refreshTimer;
        private bool _blinkOn = false;

        private enum DamageState { Offline, Healthy, Warning, Damaged }

        public EngineDamageCommand()
            : base(displayName: "Engine Damage",
                   description: "Green < 1%, yellow 1-50%, red blink > 50%",
                   groupName: "ETS2 Indicators")
        {
            this.AddParameter("engine", "Engine Damage", "ETS2 Indicators");
        }

        protected override bool OnLoad()
        {
            var plugin = (ETS2Plugin)this.Plugin;
            _telemetryService = plugin.TelemetryService;

            if (_telemetryService != null)
            {
                _telemetryService.EngineDamageChanged += OnEngineDamageChanged;
                _telemetryService.ConnectionStatusChanged += OnConnectionStatusChanged;
                plugin.EnsureTelemetryStarted();
                _refreshTimer = new Timer(_ => UpdateState(), null, 1000, 1000);
            }
            else
            {
                PluginLog.Warning("EngineDamageCommand: TelemetryService is null");
            }

            return true;
        }

        protected override bool OnUnload()
        {
            if (_telemetryService != null)
            {
                _telemetryService.EngineDamageChanged -= OnEngineDamageChanged;
                _telemetryService.ConnectionStatusChanged -= OnConnectionStatusChanged;
            }

            StopBlinking();
            _refreshTimer?.Dispose();
            _refreshTimer = null;
            return true;
        }

        private void OnConnectionStatusChanged(object sender, bool isConnected)
        {
            if (!isConnected)
                StopBlinking();
            else
                UpdateState();

            this.ActionImageChanged();
        }

        private void OnEngineDamageChanged(object sender, float engineWear)
        {
            UpdateState();
        }

        private void UpdateState()
        {
            if (GetState() == DamageState.Damaged)
                StartBlinking();
            else
                StopBlinking();

            this.ActionImageChanged();
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

        private DamageState GetState()
        {
            if (_telemetryService == null || !_telemetryService.IsConnected)
                return DamageState.Offline;

            var wear = _telemetryService.WearEngine;

            if (wear > ThresholdSettings.EngineDamageCrit)
                return DamageState.Damaged;

            if (wear > ThresholdSettings.EngineDamageWarn)
                return DamageState.Warning;

            return DamageState.Healthy;
        }

        protected override void RunCommand(string actionParameter)
        {
            PluginLog.Info($"Truck max wear: {_telemetryService?.MaxTruckWear:F1}%");
        }

        protected override string GetCommandDisplayName(string actionParameter, PluginImageSize imageSize)
        {
            return " ";
        }

        protected override BitmapImage GetCommandImage(string actionParameter, PluginImageSize imageSize)
        {
            if (_telemetryService == null)
            {
                var plugin = (ETS2Plugin)this.Plugin;
                _telemetryService = plugin?.TelemetryService;

                if (_telemetryService != null)
                {
                    _telemetryService.EngineDamageChanged += OnEngineDamageChanged;
                    _telemetryService.ConnectionStatusChanged += OnConnectionStatusChanged;
                }
            }

            return GetState() switch
            {
                DamageState.Offline  => PluginResources.ReadImage("Engine_Offline.svg"),
                DamageState.Healthy  => PluginResources.ReadImage("Engine_Healthy.svg"),
                DamageState.Warning  => PluginResources.ReadImage("Engine_Warning.svg"),
                DamageState.Damaged  => PluginResources.ReadImage(_blinkOn ? "Engine_Damaged.svg" : "Engine_Offline.svg"),
                _                    => PluginResources.ReadImage("Engine_Healthy.svg"),
            };
        }
    }
}
