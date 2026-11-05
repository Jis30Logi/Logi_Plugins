namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;

    public sealed class FpsCapApplyResult
    {
        public Boolean Success { get; }
        public Int32 AppliedCap { get; }
        public String Message { get; }

        public FpsCapApplyResult(Boolean success, Int32 appliedCap, String message = null)
        {
            this.Success = success;
            this.AppliedCap = appliedCap;
            this.Message = message ?? String.Empty;
        }
    }
}
