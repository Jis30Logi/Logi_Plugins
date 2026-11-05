namespace Loupedeck.RTSSPerformancePlugin.Actions
{
    using System;
    using System.Globalization;
    using Loupedeck.RTSSPerformancePlugin.Helpers;
    using Loupedeck.RTSSPerformancePlugin.Services;

    public class CpuUsageCommand : ThrottledDynamicCommand
    {
        private ICpuUsageSource _cpuSource;
        private IPollTickSchedulerService _pollScheduler;
        private IRTSSAvailabilityMonitorService _availabilityMonitor;
        private ITelemetryCoordinatorService _coordinator;

        public CpuUsageCommand()
            : base("CPU Usage", "Live system CPU usage.", null)
        {
            this.GroupName = "4. Hardware";
        }

        protected override Boolean OnLoad()
        {
            this.EnsureServicesBound();

            // Binds again the moment services are up, in case the host called this before
            // RTSSPerformancePlugin.Load — see ActionReadiness's remarks.
            ActionReadiness.SubscribeAndCatchUp(this.OnServicesReady);

            return true;
        }

        protected override Boolean OnUnload()
        {
            ActionReadiness.Unsubscribe(this.OnServicesReady);

            if (this._coordinator != null)
            {
                this._coordinator.TelemetryUpdated -= this.OnTelemetryUpdated;
                this._coordinator = null;
            }

            if (this._availabilityMonitor != null)
            {
                this._availabilityMonitor.StatusChanged -= this.OnAvailabilityChanged;
                this._availabilityMonitor = null;
            }

            if (this._pollScheduler != null)
            {
                this._pollScheduler.Tick -= this.OnPollTick;
                this._pollScheduler = null;
            }

            this._cpuSource = null;
            return true;
        }

        protected override void EnsureServicesBound()
        {
            var plugin = RTSSPerformancePlugin.Instance;
            if (plugin == null)
            {
                return;
            }

            if (this._coordinator == null && plugin.TelemetryCoordinator != null)
            {
                this._coordinator = plugin.TelemetryCoordinator;
                this._coordinator.TelemetryUpdated += this.OnTelemetryUpdated;
            }

            this._cpuSource = this._cpuSource ?? plugin.CpuSource;

            if (this._availabilityMonitor == null && plugin.AvailabilityMonitor != null)
            {
                this._availabilityMonitor = plugin.AvailabilityMonitor;
                this._availabilityMonitor.StatusChanged += this.OnAvailabilityChanged;
            }

            if (this._pollScheduler == null && plugin.PollScheduler != null)
            {
                this._pollScheduler = plugin.PollScheduler;
                this._pollScheduler.Tick += this.OnPollTick;
            }
        }

        private Boolean IsRtssAvailable => this._availabilityMonitor?.IsAvailable ?? false;

        private void OnTelemetryUpdated(Object sender, TelemetrySnapshot snapshot) => this.NotifyIfChanged();

        private void OnAvailabilityChanged(Object sender, RTSSAvailability status) => this.NotifyIfChanged();

        private void OnServicesReady(Object sender, EventArgs e) => this.NotifyIfChanged();

        private void OnPollTick(Object sender, PollTickEventArgs e) => this.NotifyIfChanged();

        private void NotifyIfChanged() => this.CoreNotifyIfChanged();

        private (Boolean HasLiveData, Double UsagePercent) ReadCurrent()
        {
            var rtssUp = this.IsRtssAvailable;
            var snapshot = this._coordinator?.CurrentSnapshot;
            if (snapshot != null)
            {
                var hasData = rtssUp && snapshot.HasCpuUsage;
                return (hasData, hasData ? snapshot.CpuUsagePercent : 0);
            }
            var source = this._cpuSource;
            var hasLiveData = rtssUp && source != null && source.HasCpuUsageReading;
            var usagePercent = hasLiveData ? source.CpuUsagePercent : 0;
            return (hasLiveData, usagePercent);
        }

        protected override String BuildVisualSignature()
        {
            var (hasLiveData, usagePercent) = this.ReadCurrent();
            return String.Concat(
                hasLiveData ? "live" : "none",
                "|",
                hasLiveData ? Math.Round(usagePercent).ToString("0", CultureInfo.InvariantCulture) : String.Empty,
                "|",
                this.IsRtssAvailable ? "up" : "down");
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            this.EnsureServicesBound();

            var (hasLiveData, usagePercent) = this.ReadCurrent();
            return KeyRenderer.RenderCpuUsageKey(imageSize, hasLiveData, usagePercent, isRtssAvailable: this.IsRtssAvailable);
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "CPU Usage";
    }
}
