namespace Loupedeck.ETS2Plugin.Actions
{
    using System;
    using System.Threading;
    using Loupedeck.ETS2Plugin.Services;

    /// <summary>
    /// Fatigue/rest warning indicator.
    ///   restStop > 11  → OK       (green, static)
    ///   restStop 1–11  → Yawning  (yellow, blinking) — yawning sound active
    ///   restStop = 0   → Sleeping (red, blinking)    — black screen / driver asleep
    ///   disconnected   → Offline  (black, static)
    /// </summary>
    public class FatigueWarningCommand : PluginDynamicCommand
    {
        private TelemetryService _telemetryService;

        private Timer _blinkTimer;
        private bool _blinkOn = false;

        private enum FatigueState { Offline, OK, Yawning, Sleeping }

        public FatigueWarningCommand()
            : base(displayName: "Rest Required",
                   description: "Green=alert, yellow blink=yawning, red blink=driver asleep",
                   groupName: "ETS2 Indicators")
        {
            this.AddParameter("fatigue", "Rest Required", "ETS2 Indicators");
        }

        protected override bool OnLoad()
        {
            var plugin = (ETS2Plugin)this.Plugin;
            _telemetryService = plugin.TelemetryService;

            if (_telemetryService != null)
            {
                _telemetryService.RestStopChanged += OnRestStopChanged;
                _telemetryService.ConnectionStatusChanged += OnConnectionStatusChanged;
                plugin.EnsureTelemetryStarted();
                PluginLog.Info("FatigueWarningCommand loaded");
            }
            else
            {
                PluginLog.Warning("FatigueWarningCommand: TelemetryService is null");
            }

            return true;
        }

        protected override bool OnUnload()
        {
            if (_telemetryService != null)
            {
                _telemetryService.RestStopChanged -= OnRestStopChanged;
                _telemetryService.ConnectionStatusChanged -= OnConnectionStatusChanged;
            }

            StopBlinking();
            PluginLog.Info("FatigueWarningCommand unloaded");
            return true;
        }

        private void OnConnectionStatusChanged(object sender, bool isConnected)
        {
            if (!isConnected)
                StopBlinking();

            this.ActionImageChanged();
        }

        private void OnRestStopChanged(object sender, int restStop)
        {
            var state = GetState();

            if (state == FatigueState.Yawning)
            {
                // Slow blink (700ms) — caution
                StartBlinking(700);
            }
            else if (state == FatigueState.Sleeping)
            {
                // Fast blink (350ms) — urgent
                StartBlinking(350);
            }
            else
            {
                StopBlinking();
                this.ActionImageChanged();
            }
        }

        private void StartBlinking(int intervalMs)
        {
            // Only restart if not already blinking at same rate
            if (_blinkTimer != null)
            {
                _blinkTimer.Change(0, intervalMs);
            }
            else
            {
                _blinkOn = true;
                _blinkTimer = new Timer(OnBlinkTick, null, 0, intervalMs);
            }
        }

        private void StopBlinking()
        {
            _blinkTimer?.Dispose();
            _blinkTimer = null;
            _blinkOn = false;
        }

        private void OnBlinkTick(object state)
        {
            _blinkOn = !_blinkOn;
            this.ActionImageChanged();
        }

        private FatigueState GetState()
        {
            if (_telemetryService == null || !_telemetryService.IsConnected)
                return FatigueState.Offline;

            var restStop = _telemetryService.CurrentRestStop;

            // -1 = initial/unknown → OK
            // <= 0 (but not -1) = at limit or overdue → red blink
            // 1–90 = warning/yawning → yellow blink
            // > 90 = well rested → green
            if (restStop != -1 && restStop <= 0)
                return FatigueState.Sleeping;

            if (restStop >= 1 && restStop <= 90)
                return FatigueState.Yawning;

            return FatigueState.OK;
        }

        protected override void RunCommand(string actionParameter)
        {
            PluginLog.Info($"FatigueWarning: restStop={_telemetryService?.CurrentRestStop} state={GetState()}");
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
                    _telemetryService.RestStopChanged += OnRestStopChanged;
                    _telemetryService.ConnectionStatusChanged += OnConnectionStatusChanged;
                }
            }

            var state = GetState();

            switch (state)
            {
                case FatigueState.Offline:
                    return PluginResources.ReadImage("Rest_Offline.svg");

                case FatigueState.OK:
                    return PluginResources.ReadImage("Rest_OK.svg");

                case FatigueState.Yawning:
                    // Blink between yellow warning and black
                    return PluginResources.ReadImage(_blinkOn ? "Rest_Warning.svg" : "Rest_Offline.svg");

                case FatigueState.Sleeping:
                    // Blink between red required and black
                    return PluginResources.ReadImage(_blinkOn ? "Rest_Required.svg" : "Rest_Offline.svg");

                default:
                    return PluginResources.ReadImage("Rest_OK.svg");
            }
        }
    }
}
