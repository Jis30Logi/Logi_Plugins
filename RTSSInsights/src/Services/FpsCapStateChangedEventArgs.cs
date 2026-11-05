namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;

    public sealed class FpsCapStateChangedEventArgs : EventArgs
    {
        public FpsCapStateChangeReason Reason { get; }
        public Int32 EffectiveCap { get; }
        public Int32 ConfirmedCap { get; }
        public Int32? PendingCap { get; }
        public String ActiveProfile { get; }

        public FpsCapStateChangedEventArgs(
            FpsCapStateChangeReason reason,
            Int32 effectiveCap,
            Int32 confirmedCap,
            Int32? pendingCap,
            String activeProfile)
        {
            this.Reason = reason;
            this.EffectiveCap = effectiveCap;
            this.ConfirmedCap = confirmedCap;
            this.PendingCap = pendingCap;
            this.ActiveProfile = activeProfile;
        }
    }
}
