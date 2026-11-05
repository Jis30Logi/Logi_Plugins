namespace Loupedeck.RTSSPerformancePlugin.Actions
{
    using System;
    using System.Globalization;

    using Loupedeck.RTSSPerformancePlugin.Helpers;
    using Loupedeck.RTSSPerformancePlugin.Services;

    /// <summary>
    /// Dial that steps the RTSS frame limit. Every turn goes through <see cref="IFpsCapStateService"/>,
    /// which shows the new value at once and coalesces the RTSS writes, so fast turns do not queue up.
    /// </summary>
    public sealed class FpsCapAdjustment : PluginDynamicAdjustment
    {
        public const Int32 Step = 5;
        public const Int32 ResetCap = 144;

        private IFpsCapStateService _capState;

        public FpsCapAdjustment()
            : base("FPS Cap", "Turn to adjust the RTSS FPS limit; press to reset to 144.", "3. Control", true, DeviceType.None)
        {
            // G HUB lists the reset action as "Reset " + the adjustment's own name, and it was still
            // showing "Reset FPS Cap Dial", so the adjustment is named "FPS Cap" to make that
            // "Reset FPS Cap" whether or not the host honours ResetDisplayName.
            this.ResetDisplayName = "Reset FPS Cap";
            this.ResetGroupName = "3. Control";
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            PluginResources.ReadOriginalSvg("icon-reset-fps-cap.svg", imageSize)
            ?? KeyRenderer.RenderUnavailableKey(imageSize, nameof(FpsCapAdjustment), "RESET");

        protected override Boolean OnLoad()
        {
            this.EnsureServicesBound();
            ActionReadiness.SubscribeAndCatchUp(this.OnServicesReady);
            return true;
        }

        protected override Boolean OnUnload()
        {
            ActionReadiness.Unsubscribe(this.OnServicesReady);
            if (this._capState != null)
            {
                this._capState.StateChanged -= this.OnStateChanged;
                this._capState = null;
            }

            return true;
        }

        protected override void ApplyAdjustment(String actionParameter, Int32 diff)
        {
            try
            {
                this.EnsureServicesBound();
                if (this._capState == null || diff == 0)
                {
                    return;
                }

                // EffectiveCap includes a pending value, so rapid turns accumulate instead of restarting.
                var current = this._capState.EffectiveCap;
                if (current <= 0)
                {
                    current = FpsCapStateService.DefaultFallbackCap;
                }

                var target = Math.Max(FpsCapStateService.MinCap, Math.Min(FpsCapStateService.MaxCap, current + (diff * Step)));
                if (target != current)
                {
                    _ = this._capState.RequestSetExactCapAsync(target);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "[FPS CAP DIAL] ApplyAdjustment failed.");
            }
        }

        protected override void RunCommand(String actionParameter)
        {
            try
            {
                this.EnsureServicesBound();
                if (this._capState != null)
                {
                    _ = this._capState.RequestSetExactCapAsync(ResetCap);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "[FPS CAP DIAL] Reset failed.");
            }
        }

        protected override String GetAdjustmentValue(String actionParameter)
        {
            var cap = this._capState?.EffectiveCap ?? 0;
            return cap > 0 ? cap.ToString(CultureInfo.InvariantCulture) : "--";
        }

        private void EnsureServicesBound()
        {
            if (this._capState != null)
            {
                return;
            }

            var service = RTSSPerformancePlugin.Instance?.CapStateService;
            if (service != null)
            {
                this._capState = service;
                this._capState.StateChanged += this.OnStateChanged;
            }
        }

        private void OnServicesReady(Object sender, EventArgs e)
        {
            this.EnsureServicesBound();
            this.NotifyValueChanged();
        }

        private void OnStateChanged(Object sender, FpsCapStateChangedEventArgs e) => this.NotifyValueChanged();

        private void NotifyValueChanged()
        {
            try
            {
                this.AdjustmentValueChanged(null, this._capState?.EffectiveCap ?? 0);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[FPS CAP DIAL] AdjustmentValueChanged failed.");
            }
        }
    }
}
