namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;

    /// <summary>
    /// Coordinates single-cycle telemetry acquisition and broadcasts immutable snapshots to all consumers.
    /// </summary>
    public interface ITelemetryCoordinatorService
    {
        TelemetrySnapshot CurrentSnapshot { get; }

        event EventHandler<TelemetrySnapshot> TelemetryUpdated;

        TelemetrySnapshot AcquireCycle();
    }
}
