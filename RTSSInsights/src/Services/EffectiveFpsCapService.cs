namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Threading;

    /// <summary>
    /// Thread-safe authoritative service for current effective FPS cap.
    /// Unifies RTSS actual cap, FpsCapFolder, and FpsAdvisorService into a single state flow.
    /// </summary>
    public class EffectiveFpsCapService : IEffectiveFpsCapService
    {
        protected Int32 _currentCap;

        public virtual Int32 CurrentCap => Volatile.Read(ref this._currentCap);

        public event EventHandler<Int32> CapChanged;

        public EffectiveFpsCapService(Int32 initialCap = 0)
        {
            this._currentCap = initialCap;
        }

        public virtual Int32 UpdateEffectiveCap(String processFileName, IRTSSProfileWriterService profileWriter)
        {
            if (profileWriter == null)
            {
                return this.CurrentCap;
            }

            if (profileWriter.TryReadEffectiveFpsCeiling(processFileName, out var limit) && limit > 0)
            {
                this.SetCap(limit);
                return limit;
            }

            return this.CurrentCap;
        }

        public virtual void SetCap(Int32 newCap)
        {
            var old = Interlocked.Exchange(ref this._currentCap, newCap);
            if (old != newCap)
            {
                this.OnCapChanged(newCap);
            }
        }

        protected void OnCapChanged(Int32 cap)
        {
            this.CapChanged?.Invoke(this, cap);
        }
    }
}
