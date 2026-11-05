using System;
using Loupedeck;

#nullable enable

namespace Loupedeck.RTSSPerformancePlugin.Helpers
{
    /// <summary>
    /// Base class for dynamic commands that throttles image change notifications to prevent
    /// flooding the host while still ensuring updates when the signature is stale.
    /// </summary>
    public abstract class ThrottledDynamicCommand : PluginDynamicCommand
    {
        private readonly VisualStateChangeGate _visualStateGate = new VisualStateChangeGate();

        protected String? _notifiedSignature;

        protected ThrottledDynamicCommand(string displayName, string description, string? actionParameter)
            : base(displayName, description, actionParameter)
        {
        }

        protected virtual void EnsureServicesBound()
        {
            // Derived classes should override to bind services.
        }

        protected abstract String BuildVisualSignature();

        protected override LibraryImage GetCommandLibraryImage(String actionParameter)
        {
            var icon = PluginResources.GetActionIcon(this.GetType().Name);

            return icon == null
                ? base.GetCommandLibraryImage(actionParameter)
                : new LibraryImage(icon);
        }

        protected void CoreNotifyIfChanged()
        {
            this.EnsureServicesBound();

            var signature = this.BuildVisualSignature();
            var signatureChanged = this._visualStateGate.ShouldNotify(signature);

            if (signatureChanged)
            {
                this._notifiedSignature = signature;

                this.OnNotifying(signature);

                try
                {
                    this.ActionImageChanged();
                }
                catch (Exception exception)
                {
                    PluginLog.Warning(exception, "[THROTTLED DYNAMIC COMMAND] ActionImageChanged failed.");
                }
            }
        }

        /// <summary>
        /// Called when a notification should be sent. Derived classes can override to add logging
        /// or other behavior before the image change is applied.
        /// </summary>
        /// <param name="signature">The current visual signature.</param>
        protected virtual void OnNotifying(String signature)
        {
            // Default implementation does nothing; derived classes can override for logging.
        }

        /// <summary>
        /// Forces an immediate state reset and visual invalidation, bypassing previous signature caching.
        /// Useful for availability state changes that must trigger an immediate repaint.
        /// </summary>
        protected void ForceNotify()
        {
            this.EnsureServicesBound();
            this._visualStateGate.Reset();
            this._notifiedSignature = null;
            this.CoreNotifyIfChanged();
        }

        internal void DoForceNotify() => this.ForceNotify();

        internal String? NotifiedSignature => this._notifiedSignature;
    }
}