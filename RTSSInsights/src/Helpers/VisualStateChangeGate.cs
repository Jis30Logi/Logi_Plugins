namespace Loupedeck.RTSSPerformancePlugin.Helpers
{
    using System;

    public sealed class VisualStateChangeGate
    {
        private String _lastSignature;

        public Boolean ShouldNotify(String signature)
        {
            if (String.Equals(signature, this._lastSignature, StringComparison.Ordinal))
            {
                return false;
            }

            this._lastSignature = signature;
            return true;
        }

        public void Reset()
        {
            this._lastSignature = null;
        }
    }
}
