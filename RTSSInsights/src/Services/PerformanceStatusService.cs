namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Collections.Generic;

    using Loupedeck.RTSSPerformancePlugin.Helpers;

    public sealed class PerformanceStatusService : IDisposable
    {
        public const Int32 DefaultWindowSize = PerformanceStatusDecisionEngine.DefaultWindowSize;
        public const Int32 MinimumSampleCount = PerformanceStatusDecisionEngine.MinimumSampleCount;
        public const Int32 RequiredStableEvaluations = PerformanceStatusDecisionEngine.RequiredStableEvaluations;

        private readonly PerformanceStatusDecisionEngine _engine;
        private Boolean _isDisposed;

        public PerformanceStatusService(Int32 windowSize = DefaultWindowSize)
        {
            this._engine = new PerformanceStatusDecisionEngine(windowSize);
        }

        public Int32 WindowSize => this._engine.WindowSize;

        public PerformanceStatusDecisionEngine DecisionEngine => this._engine;

        public PerformanceStatusResult Current => this._engine.ConfirmedResult;

        public PerformanceStatusResult Analyze(
            PerformanceHistoryService history,
            Double effectiveCap = 0,
            Double displayRefreshRate = 0,
            Int64 currentTimeMs = 0)
        {
            if (history == null || this._isDisposed)
            {
                return this.Current;
            }

            return this.Analyze(history.GetSnapshots(), effectiveCap, displayRefreshRate, currentTimeMs);
        }

        public PerformanceStatusResult Analyze(
            IReadOnlyList<PerformanceSnapshot> snapshots,
            Double effectiveCap = 0,
            Double displayRefreshRate = 0,
            Int64 currentTimeMs = 0)
        {
            if (this._isDisposed)
            {
                return this.Current;
            }

            return this._engine.EvaluateWithHysteresis(snapshots, effectiveCap, displayRefreshRate, currentTimeMs);
        }

        public void Reset()
        {
            this._engine.Reset();
        }

        public void Dispose()
        {
            this._isDisposed = true;
            this._engine.Reset();
        }
    }

    /// <summary>
    /// Static status resolver that provides formatted display strings and color tiers
    /// for UI actions like PerformanceStatusCommand.
    /// </summary>
    public static class PerformanceStatusResolver
    {
        public static String ResolveLabel(
            Boolean hasLiveData,
            PerformanceStatusResult result) =>
            ResolveStatus(hasLiveData, result, 0).Label;

        public static (String Label, ColorTier Tier) ResolveStatus(
            Boolean hasLiveData,
            PerformanceStatusResult result,
            Int64 currentTimeMs = 0)
        {
            if (!hasLiveData || result == null || result.SampleCount <= 0)
            {
                return ("NO GAME", ColorTier.Neutral);
            }

            if (result.State == PerformanceStatusState.Unknown)
            {
                return ("UNKNOWN", ColorTier.Neutral);
            }

            if (result.ActiveConditions.Count == 0)
            {
                return ("BALANCED", ColorTier.Good);
            }

            if (result.ActiveConditions.Count == 1)
            {
                return (result.ActiveConditions[0], result.Tier);
            }

            const Int64 intervalMs = PerformanceStatusDecisionEngine.DefaultRotationIntervalMs;
            var index = (Int32)((currentTimeMs / intervalMs) % result.ActiveConditions.Count);
            if (index < 0) index = 0;
            return (result.ActiveConditions[index], result.Tier);
        }
    }
}
