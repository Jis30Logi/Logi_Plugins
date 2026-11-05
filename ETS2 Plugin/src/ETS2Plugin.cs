namespace Loupedeck.ETS2Plugin
{
    using System;
    using System.Threading.Tasks;
    using Loupedeck.ETS2Plugin.Services;

    /// <summary>
    /// Main plugin class for Euro Truck Simulator 2 integration with Logitech MX Creative Console.
    /// Provides real-time telemetry indicators for speed, fuel, parking brake, engine damage, and fatigue.
    /// </summary>
    public class ETS2Plugin : Plugin
    {
        // Gets a value indicating whether this is an API-only plugin.
        public override Boolean UsesApplicationApiOnly => true;

        // Gets a value indicating whether this is a Universal plugin or an Application plugin.
        public override Boolean HasNoApplication => true;

        // Telemetry service for WebSocket connection to ETS2
        public TelemetryService TelemetryService { get; private set; }
        
        // Background task for telemetry service
        private Task _telemetryTask;
        private bool _telemetryStarted = false;
        private readonly object _telemetryLock = new object();

        // Initializes a new instance of the plugin class.
        public ETS2Plugin()
        {
            // Empty constructor - all initialization happens in Load()
        }

        // This method is called when the plugin is loaded.
        public override void Load()
        {
            try
            {
                // Initialize the plugin log.
                PluginLog.Init(this.Log);

                // Initialize the plugin resources.
                PluginResources.Init(this.Assembly);

                // Initialize configurable thresholds (persisted to JSON in AppData).
                Helpers.ThresholdSettings.Init();

                // Auto-install the SCS telemetry game plugin if ETS2 is found and DLL is missing.
                Helpers.TelemetryInstaller.Install(this.Assembly);
                
                PluginLog.Info("ETS2 Plugin loading...");

                // Initialize Telemetry service
                this.TelemetryService = new TelemetryService();
                
                // Handle unobserved task exceptions globally
                TaskScheduler.UnobservedTaskException += (sender, e) =>
                {
                    PluginLog.Warning($"Unobserved task exception: {e.Exception.Message}");
                    e.SetObserved(); // Mark as observed to prevent crash
                };
                
                // Start telemetry service immediately
                PluginLog.Info("Starting telemetry service...");
                this.TelemetryService.Start();
                this._telemetryStarted = true;
                
                PluginLog.Info("ETS2 Plugin loaded successfully - telemetry service started");
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Failed to load ETS2 Plugin: {ex.Message}");
                PluginLog.Error(ex.ToString());
                throw;
            }
        }

        // Public method to start telemetry service (called by actions on first use)
        public void EnsureTelemetryStarted()
        {
            lock (_telemetryLock)
            {
                if (_telemetryStarted || this.TelemetryService == null)
                {
                    return;
                }

                _telemetryStarted = true;
                PluginLog.Info("Starting telemetry service...");

                // Start telemetry service
                try
                {
                    this.TelemetryService.Start();
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"Telemetry service error: {ex.Message}");
                }
            }
        }

        // This method is called when the plugin is unloaded.
        public override void Unload()
        {
            PluginLog.Info("ETS2 Plugin unloading...");

            // Clean up services
            this.TelemetryService?.Stop();
            
            // Wait for telemetry task to complete (with timeout)
            try
            {
                this._telemetryTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"Error waiting for telemetry task: {ex.Message}");
            }
            
            this.TelemetryService?.Dispose();
            this.TelemetryService = null;
            this._telemetryTask = null;

            PluginLog.Info("ETS2 Plugin unloaded");
        }
    }
}
