namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;

    /// <summary>
    /// Contract for the single authoritative FPS-cap state flow:
    /// RTSS actual cap -> Effective FPS Cap State -> FPS Cap action -> FPS Advisor.
    /// </summary>
    public interface IEffectiveFpsCapService
    {
        Int32 CurrentCap { get; }

        event EventHandler<Int32> CapChanged;

        Int32 UpdateEffectiveCap(String processFileName, IRTSSProfileWriterService profileWriter);

        void SetCap(Int32 newCap);
    }
}
