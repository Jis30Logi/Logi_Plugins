namespace Loupedeck.RTSSPerformancePlugin.Actions
{
    using System;
    using System.Threading;

    using Loupedeck.RTSSPerformancePlugin.Helpers;
    using Loupedeck.RTSSPerformancePlugin.Services;

    public sealed class RTSSLaunchCommand : ThrottledDynamicCommand
    {
        private IRTSSAvailabilityMonitorService _availabilityMonitor;
        private IRTSSProfileHelperRunner _helperRunner;
        private IPollTickSchedulerService _scheduler;
        private Int64 _operationSequence;
        private Boolean _eventsBound;

        public RTSSLaunchCommand()
            : this(null, null)
        {
        }

        public RTSSLaunchCommand(
            IRTSSAvailabilityMonitorService availabilityMonitor,
            IRTSSProfileHelperRunner helperRunner)
            : base("RTSS Status", "Launch RTSS or bring its window to the foreground.", null)
        {
            this.GroupName = "1. RTSS Status";
            this._availabilityMonitor = availabilityMonitor;
            this._helperRunner = helperRunner;
        }

        protected override Boolean OnLoad()
        {
            this.BindServices();
            ActionReadiness.SubscribeAndCatchUp(this.OnServicesReady);
            return true;
        }

        protected override Boolean OnUnload()
        {
            ActionReadiness.Unsubscribe(this.OnServicesReady);

            if (this._eventsBound)
            {
                if (this._availabilityMonitor != null)
                {
                    this._availabilityMonitor.StatusChanged -= this.OnStatusChanged;
                }

                this._eventsBound = false;
            }

            if (this._scheduler != null)
            {
                this._scheduler.Tick -= this.OnTelemetryTick;
                this._scheduler = null;
            }

            this._availabilityMonitor = null;
            this._helperRunner = null;
            return true;
        }

        protected override void EnsureServicesBound() => this.BindServices();

        private void BindServices()
        {
            var plugin = RTSSPerformancePlugin.Instance;
            if (plugin != null)
            {
                this._availabilityMonitor ??= plugin.AvailabilityMonitor;
                this._helperRunner ??= plugin.ProfileHelperRunner;

                if (this._scheduler == null && plugin.PollScheduler != null)
                {
                    this._scheduler = plugin.PollScheduler;
                    this._scheduler.Tick += this.OnTelemetryTick;
                }
            }

            if (!this._eventsBound && this._availabilityMonitor != null)
            {
                this._availabilityMonitor.StatusChanged += this.OnStatusChanged;
                this._eventsBound = true;
            }
        }

        public Boolean IsRtssAvailable
        {
            get
            {
                this.EnsureServicesBound();
                return this._availabilityMonitor?.IsAvailable ??
                       RTSSPerformancePlugin.Instance?.AvailabilityMonitor?.IsAvailable ??
                       false;
            }
        }

        private void OnServicesReady(Object sender, EventArgs e)
        {
            this.BindServices();
            this.RequestRedraw();
        }

        private void OnStatusChanged(Object sender, RTSSAvailability status) => this.RequestRedraw();

        private void OnTelemetryTick(Object sender, PollTickEventArgs e) => this.RequestRedraw();

        private void RequestRedraw()
        {
            this.EnsureServicesBound();
            var sig = this.BuildVisualSignature();
            if (!String.Equals(sig, this._notifiedSignature, StringComparison.Ordinal))
            {
                this._notifiedSignature = sig;
                try
                {
                    this.ActionImageChanged();
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, "[RTSS LAUNCH] ActionImageChanged failed.");
                }
            }
        }

        protected override String BuildVisualSignature()
        {
            var isAvailable = this.IsRtssAvailable;
            return isAvailable ? "online" : "offline";
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var isAvailable = this.IsRtssAvailable;
            return KeyRenderer.RenderRtssLaunchKey(imageSize, isAvailable);
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) =>
            this.IsRtssAvailable ? "RTSS" : "RTSS OFFLINE";

        protected override void RunCommand(String actionParameter)
        {
            this.EnsureServicesBound();
            var seq = Interlocked.Increment(ref this._operationSequence);

            if (!this.IsRtssAvailable)
            {
                PluginLog.Info("[RTSS LAUNCH] RTSS is offline. Launching RTSS application...");
                var helper = this._helperRunner ?? RTSSPerformancePlugin.Instance?.ProfileHelperRunner;
                if (helper == null)
                {
                    PluginLog.Warning("[RTSS LAUNCH] ProfileHelperRunner is not available.");
                    return;
                }

                var launchResult = helper.RunLaunchOrFocus();
                if (launchResult == null || !launchResult.IsSuccess)
                {
                    PluginLog.Warning($"[RTSS LAUNCH] Launch command failed: {launchResult?.Error ?? "unknown error"}. Leaving offline.");
                    return;
                }

                // Wait for RTSS to become available using existing IRTSSAvailabilityMonitorService Probe()
                var monitor = this._availabilityMonitor ?? RTSSPerformancePlugin.Instance?.AvailabilityMonitor;
                var becameAvailable = false;

                if (monitor != null)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(8);
                    while (DateTime.UtcNow < deadline)
                    {
                        var status = monitor.Probe();
                        if (status == RTSSAvailability.Available || monitor.IsAvailable)
                        {
                            becameAvailable = true;
                            break;
                        }

                        Thread.Sleep(250);
                    }
                }

                if (becameAvailable)
                {
                    PluginLog.Info("[RTSS LAUNCH] RTSS is now available. Enabling Start with Windows...");
                    var swwResult = helper.RunSetStartWithWindows(true);
                    if (swwResult != null && swwResult.IsSuccess)
                    {
                        PluginLog.Info("[RTSS LAUNCH] Start with Windows successfully enabled.");
                    }
                    else
                    {
                        PluginLog.Warning($"[RTSS LAUNCH] Failed to enable Start with Windows: {swwResult?.Error ?? "unknown error"}.");
                    }
                }
                else
                {
                    PluginLog.Warning("[RTSS LAUNCH] Timed out waiting for RTSS to become available. Leaving offline.");
                }

                if (seq == Interlocked.Read(ref this._operationSequence))
                {
                    this._notifiedSignature = null;
                    this.RequestRedraw();
                }
            }
            else
            {
                PluginLog.Info("[RTSS LAUNCH] RTSS is already running. Activating / focusing existing window...");
                var helper = this._helperRunner ?? RTSSPerformancePlugin.Instance?.ProfileHelperRunner;
                if (helper != null)
                {
                    var focusResult = helper.RunLaunchOrFocus();
                    if (focusResult != null && !focusResult.IsSuccess)
                    {
                        PluginLog.Warning($"[RTSS LAUNCH] Focus failed: {focusResult.Error}.");
                    }
                }
            }
        }

        internal void DoRunCommand(String actionParameter = null) => this.RunCommand(actionParameter);

        internal BitmapImage DoGetCommandImage(PluginImageSize size) => this.GetCommandImage(null, size);

        internal String DoGetCommandDisplayName(PluginImageSize size) => this.GetCommandDisplayName(null, size);

        internal String DoBuildVisualSignature() => this.BuildVisualSignature();
    }
}
