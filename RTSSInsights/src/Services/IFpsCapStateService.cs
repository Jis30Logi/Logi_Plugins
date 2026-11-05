namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// Contract for the centralized FPS-cap state machine and serialized operation pipeline:
    /// User action -> Pending visual state -> Serialized async RTSS write/readback -> Confirmed state.
    /// </summary>
    public interface IFpsCapStateService : IEffectiveFpsCapService
    {
        Int32 ConfirmedCap { get; }

        Int32? PendingCap { get; }

        String ActiveProfile { get; }

        Int32 EffectiveCap { get; }

        Boolean IsPending { get; }

        /// <summary>True only after an Unlimited (limit 0) write was read back; cleared by any later cap.</summary>
        Boolean IsUnlimited { get; }

        event EventHandler<FpsCapStateChangedEventArgs> StateChanged;

        Task<FpsCapApplyResult> RequestSetCapAsync(Int32 targetCap, String profileOverride = null);

        

        /// <summary>Sets the target profile's limit to 0 (unlimited), verified by readback. A cap is never restored automatically.</summary>
        Task<FpsCapApplyResult> RequestUnlimitedAsync();

        Task<FpsCapApplyResult> RequestSetExactCapAsync(Int32 targetCap);

        /// <summary>The profile a cap write would target right now (same resolution the preset actions use).</summary>
        String ResolveTargetProfileName();

        Task<FpsCapApplyResult> RequestIncreaseAsync();

        Task<FpsCapApplyResult> RequestDecreaseAsync();
    }
}
