namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using Loupedeck.RTSSPerformancePlugin.Helpers;
    using Loupedeck.RTSSPerformancePlugin.Interop;

    /// <summary>
    /// Centralized authoritative service and serialized operation pipeline for FPS-cap control.
    /// Ensures instantaneous button response with immediate pending state, coalesced serialized
    /// writes to RTSS without blocking the UI thread, and authoritative readback verification.
    /// </summary>
    public sealed class FpsCapStateService : EffectiveFpsCapService, IFpsCapStateService, IDisposable
    {
        public static readonly Int32[] SupportedPresets = { 30, 60, 90, 120, 144, 180 };
        public const Int32 MinCap = 30;
        public const Int32 MaxCap = 180;
        public const Int32 DefaultFallbackCap = 60;

        private readonly Object _stateLock = new Object();
        private readonly SemaphoreSlim _workSignal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _workerTask;

        private IRTSSProfileWriterService _profileWriter;
        private IRTSSReaderService _reader;
        private IRTSSAvailabilityMonitorService _availabilityMonitor;
        private IRTSSProfileHelperRunner _helperRunner;

        private Int32 _confirmedCap;
        private Int32? _pendingCap;
        private String _activeProfile = RTSSProfileWriterService.GlobalProfileName;

        private Boolean _isUnlimited;

        private Int32? _queuedTargetCap;
        private String _queuedProfileOverride;
        private TaskCompletionSource<FpsCapApplyResult> _queuedTcs;

        public Int32 ConfirmedCap
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._confirmedCap;
                }
            }
        }

        public Int32? PendingCap
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._pendingCap;
                }
            }
        }

        public String ActiveProfile
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._activeProfile;
                }
            }
        }

        public Int32 EffectiveCap
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._pendingCap ?? this._confirmedCap;
                }
            }
        }

        public Boolean IsPending
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._pendingCap.HasValue && this._pendingCap.Value != this._confirmedCap;
                }
            }
        }

        public Boolean IsUnlimited
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._isUnlimited && !this._pendingCap.HasValue;
                }
            }
        }

        public override Int32 CurrentCap => this.EffectiveCap;

        public event EventHandler<FpsCapStateChangedEventArgs> StateChanged;

        public FpsCapStateService(
            IRTSSProfileWriterService profileWriter = null,
            IRTSSReaderService reader = null,
            IRTSSAvailabilityMonitorService availabilityMonitor = null,
            IRTSSProfileHelperRunner helperRunner = null,
            Int32 initialCap = 0)
            : base(initialCap)
        {
            this._profileWriter = profileWriter;
            this._reader = reader;
            this._availabilityMonitor = availabilityMonitor;
            this._helperRunner = helperRunner;
            this._confirmedCap = initialCap;
            this._currentCap = initialCap;

            if (this._profileWriter != null)
            {
                this._profileWriter.FpsCeilingChanged += this.OnProfileWriterFpsCeilingChanged;
                this._profileWriter.ActiveProfileCeilingChanged += this.OnProfileWriterActiveProfileCeilingChanged;
                this._profileWriter.ActiveProfileChanged += this.OnProfileWriterActiveProfileChanged;
            }

            this._workerTask = Task.Run(this.ProcessQueueAsync);
        }

        public void BindServices(
            IRTSSProfileWriterService profileWriter,
            IRTSSReaderService reader,
            IRTSSAvailabilityMonitorService availabilityMonitor,
            IRTSSProfileHelperRunner helperRunner)
        {
            lock (this._stateLock)
            {
                if (this._profileWriter != null)
                {
                    this._profileWriter.FpsCeilingChanged -= this.OnProfileWriterFpsCeilingChanged;
                    this._profileWriter.ActiveProfileCeilingChanged -= this.OnProfileWriterActiveProfileCeilingChanged;
                    this._profileWriter.ActiveProfileChanged -= this.OnProfileWriterActiveProfileChanged;
                }

                this._profileWriter = profileWriter;
                this._reader = reader;
                this._availabilityMonitor = availabilityMonitor;
                this._helperRunner = helperRunner;

                if (this._profileWriter != null)
                {
                    this._profileWriter.FpsCeilingChanged += this.OnProfileWriterFpsCeilingChanged;
                    this._profileWriter.ActiveProfileCeilingChanged += this.OnProfileWriterActiveProfileCeilingChanged;
                    this._profileWriter.ActiveProfileChanged += this.OnProfileWriterActiveProfileChanged;
                }
            }
        }

        public Task<FpsCapApplyResult> RequestSetCapAsync(Int32 targetCap, String profileOverride = null) =>
            this.RequestCapCoreAsync(ClampToSupportedPreset(targetCap), profileOverride);

        /// <summary>Like <see cref="RequestSetCapAsync"/> but only clamps to MinCap..MaxCap, so the dial can land between presets.</summary>
        public Task<FpsCapApplyResult> RequestSetExactCapAsync(Int32 targetCap) =>
            this.RequestCapCoreAsync(Math.Max(MinCap, Math.Min(MaxCap, targetCap)), null);

        public Task<FpsCapApplyResult> RequestUnlimitedAsync() =>
            // The explicit profile also stops the "already at requested cap" shortcut: a confirmed cap of 0
            // can just mean "not read yet", so the write and readback must really happen.
            this.RequestCapCoreAsync(0, this.ResolveTargetProfileName());

        private Task<FpsCapApplyResult> RequestCapCoreAsync(Int32 targetCap, String profileOverride)
        {
            TaskCompletionSource<FpsCapApplyResult> tcs;
            Int32 confirmedForEvent;
            lock (this._stateLock)
            {
                // If already at requested cap and not pending another value, no-op immediately
                if (targetCap == this._confirmedCap && !this._pendingCap.HasValue && profileOverride == null)
                {
                    return Task.FromResult(new FpsCapApplyResult(true, targetCap, "Already at requested cap"));
                }

                // Immediate visual responsiveness: set pending cap and notify UI immediately
                this._pendingCap = targetCap;
                this._currentCap = targetCap;
                this._queuedTargetCap = targetCap;
                this._queuedProfileOverride = profileOverride;

                // Coalesce: cancel/complete previous queued TCS if still pending
                var oldTcs = this._queuedTcs;
                tcs = new TaskCompletionSource<FpsCapApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                this._queuedTcs = tcs;

                oldTcs?.TrySetResult(new FpsCapApplyResult(false, this._confirmedCap, "Coalesced by newer request"));

                confirmedForEvent = this._confirmedCap;
            }

            // Subscribers redraw keys; that must not run under the state lock or delay the worker.
            this.RaiseStateChanged(FpsCapStateChangeReason.PendingChanged, targetCap, confirmedForEvent, targetCap);

            this.OnCapChanged(targetCap);

            try
            {
                this._workSignal.Release();
            }
            catch (ObjectDisposedException)
            {
                return Task.FromResult(new FpsCapApplyResult(false, this._confirmedCap, "Service disposed"));
            }

            return tcs.Task;
        }

        public Task<FpsCapApplyResult> RequestIncreaseAsync()
        {
            Int32 current;
            lock (this._stateLock)
            {
                current = this.EffectiveCap;
                if (current <= 0)
                {
                    current = DefaultFallbackCap;
                }
            }

            var next = GetNextIncreasePreset(current);
            if (next == current)
            {
                return Task.FromResult(new FpsCapApplyResult(true, current, "At maximum boundary (180)"));
            }

            return this.RequestSetCapAsync(next);
        }

        public Task<FpsCapApplyResult> RequestDecreaseAsync()
        {
            Int32 current;
            lock (this._stateLock)
            {
                current = this.EffectiveCap;
                if (current <= 0)
                {
                    current = DefaultFallbackCap;
                }
            }

            var next = GetNextDecreasePreset(current);
            if (next == current)
            {
                return Task.FromResult(new FpsCapApplyResult(true, current, "At minimum boundary (30)"));
            }

            return this.RequestSetCapAsync(next);
        }

        public static Int32 GetNextIncreasePreset(Int32 current)
        {
            // Increase sequence: 30 -> 60 -> 90 -> 120 -> 144 -> 180
            for (var i = 0; i < SupportedPresets.Length; i++)
            {
                if (SupportedPresets[i] > current)
                {
                    return SupportedPresets[i];
                }
            }
            return MaxCap;
        }

        public static Int32 GetNextDecreasePreset(Int32 current)
        {
            // Decrease sequence: 180 -> 144 -> 120 -> 90 -> 60 -> 30
            for (var i = SupportedPresets.Length - 1; i >= 0; i--)
            {
                if (SupportedPresets[i] < current)
                {
                    return SupportedPresets[i];
                }
            }
            return MinCap;
        }

        public static Int32 ClampToSupportedPreset(Int32 cap)
        {
            if (SupportedPresets.Contains(cap))
            {
                return cap;
            }

            if (cap < MinCap)
            {
                return MinCap;
            }

            if (cap > MaxCap)
            {
                return MaxCap;
            }

            var nearest = SupportedPresets[0];
            var minDiff = Math.Abs(cap - nearest);
            for (var i = 1; i < SupportedPresets.Length; i++)
            {
                var diff = Math.Abs(cap - SupportedPresets[i]);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    nearest = SupportedPresets[i];
                }
            }
            return nearest;
        }

        public override void SetCap(Int32 newCap)
        {
            Boolean changed = false;
            lock (this._stateLock)
            {
                if (this._confirmedCap != newCap || this._pendingCap.HasValue)
                {
                    this._confirmedCap = newCap;
                    this._isUnlimited = false;
                    this._pendingCap = null;
                    this._currentCap = newCap;
                    changed = true;
                }
            }

            if (changed)
            {
                this.OnCapChanged(newCap);
                this.RaiseStateChanged(FpsCapStateChangeReason.Confirmed, newCap, newCap, null);
            }
        }

        public override Int32 UpdateEffectiveCap(String processFileName, IRTSSProfileWriterService profileWriter)
        {
            if (this.IsPending)
            {
                return this.EffectiveCap;
            }

            var writer = profileWriter ?? this._profileWriter;
            if (writer == null)
            {
                return this.ConfirmedCap;
            }

            // An application profile that explicitly declares Limit=0 overrides Global: that
            // application is unlimited. TryReadEffectiveFpsCeiling cannot express this (it reports
            // "no cap" and falls through to Global), so it has to be checked first.
            if (!String.IsNullOrWhiteSpace(processFileName) &&
                !RTSSProfileWriterService.IsGlobalProfile(processFileName) &&
                writer.TryReadFpsCeiling(processFileName, out var appProfileLimit) &&
                appProfileLimit == 0)
            {
                Boolean unlimitedChanged = false;
                lock (this._stateLock)
                {
                    if (!this.IsPending && (!this._isUnlimited || this._confirmedCap != 0))
                    {
                        this._confirmedCap = 0;
                        this._isUnlimited = true;
                        this._currentCap = 0;
                        unlimitedChanged = true;
                    }
                }

                if (unlimitedChanged)
                {
                    this.OnCapChanged(0);
                    this.RaiseStateChanged(FpsCapStateChangeReason.Confirmed, 0, 0, null);
                }

                return 0;
            }

            if (writer.TryReadEffectiveFpsCeiling(processFileName, out var limit) && limit > 0)
            {
                Boolean changed = false;
                lock (this._stateLock)
                {
                    if (!this.IsPending && this._confirmedCap != limit)
                    {
                        this._confirmedCap = limit;
                        this._isUnlimited = false;
                        this._currentCap = limit;
                        changed = true;
                    }
                }

                if (changed)
                {
                    this.OnCapChanged(limit);
                    this.RaiseStateChanged(FpsCapStateChangeReason.Confirmed, limit, limit, null);
                }

                return limit;
            }

            return this.ConfirmedCap;
        }

        private async Task ProcessQueueAsync()
        {
            while (!this._cts.Token.IsCancellationRequested)
            {
                try
                {
                    await this._workSignal.WaitAsync(this._cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                Int32 targetCap;
                String profileOverride;
                TaskCompletionSource<FpsCapApplyResult> tcs;

                lock (this._stateLock)
                {
                    if (!this._queuedTargetCap.HasValue)
                    {
                        continue;
                    }

                    targetCap = this._queuedTargetCap.Value;
                    profileOverride = this._queuedProfileOverride;
                    tcs = this._queuedTcs;

                    this._queuedTargetCap = null;
                    this._queuedProfileOverride = null;
                    this._queuedTcs = null;

                    // Coalesce: drain any excess signals so we immediately pick up latest work
                    while (this._workSignal.CurrentCount > 0)
                    {
                        this._workSignal.Wait(0);
                    }
                }

                var result = await this.ExecuteApplyToRtssAsync(targetCap, profileOverride).ConfigureAwait(false);
                tcs?.TrySetResult(result);
            }
        }

        private async Task<FpsCapApplyResult> ExecuteApplyToRtssAsync(Int32 targetCap, String profileOverride)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var isAvailable = this._availabilityMonitor?.IsAvailable ?? false;
                    if (!isAvailable)
                    {
                        PluginLog.Warning($"[FPS CAP STATE] RTSS not available when applying cap {targetCap}.");
                        this.RevertPending(targetCap, "RTSS not available");
                        return new FpsCapApplyResult(false, this.ConfirmedCap, "RTSS not available");
                    }

                    var profileWriter = this._profileWriter;
                    if (profileWriter == null)
                    {
                        this.RevertPending(targetCap, "Profile writer not available");
                        return new FpsCapApplyResult(false, this.ConfirmedCap, "Profile writer not available");
                    }

                    String targetProfile = profileOverride;
                    Boolean isAppProfile = false;
                    if (String.IsNullOrWhiteSpace(targetProfile))
                    {
                        targetProfile = this.ResolveTargetProfile(out isAppProfile);
                    }
                    else
                    {
                        isAppProfile = !RTSSProfileWriterService.IsGlobalProfile(targetProfile);
                    }

                    lock (this._stateLock)
                    {
                        this._activeProfile = targetProfile;
                    }

                    if (isAppProfile)
                    {
                        profileWriter.UpdateActiveApplicationProfile(targetProfile);
                    }
                    else
                    {
                        profileWriter.UpdateActiveProfile(RTSSProfileWriterService.GlobalProfileName);
                    }

                    var writeSuccess = profileWriter.TryWriteFpsCeiling(targetProfile, targetCap);
                    if (!writeSuccess)
                    {
                        PluginLog.Warning($"[FPS CAP STATE] Failed to write FPS ceiling {targetCap} to profile '{targetProfile}'.");
                        this.RevertPending(targetCap, "Write failed");
                        return new FpsCapApplyResult(false, this.ConfirmedCap, "Write failed");
                    }

                    profileWriter.ReloadProfiles();

                    // The file write alone is not proof RTSS accepted the value. When the helper
                    // reached RTSS and the sync failed, put the previous value back on disk and
                    // report failure instead of confirming a cap that is not being enforced.
                    var helperStatus = (profileWriter as RTSSProfileWriterService)?.LastHelperSyncStatus;
                    if (helperStatus == ProfileHelperStatus.Error || helperStatus == ProfileHelperStatus.Timeout)
                    {
                        var previous = this.ConfirmedCap;
                        PluginLog.Warning(
                            $"[FPS CAP STATE] RTSS helper sync {helperStatus} for '{targetProfile}' at {targetCap}; " +
                            $"restoring {previous}.");
                        if (previous > 0 || this.IsUnlimited)
                        {
                            profileWriter.TryWriteFpsCeiling(targetProfile, previous);
                        }

                        this.RevertPending(targetCap, $"RTSS helper sync {helperStatus}");
                        return new FpsCapApplyResult(false, this.ConfirmedCap, $"RTSS helper sync {helperStatus}");
                    }

                    if (!profileWriter.TryReadFpsCeiling(targetProfile, out var readback) || readback != targetCap)
                    {
                        PluginLog.Warning($"[FPS CAP STATE] Readback mismatch for '{targetProfile}': expected {targetCap}, got {readback}.");
                        this.RevertPending(targetCap, $"Readback mismatch: expected {targetCap}, read {readback}");
                        return new FpsCapApplyResult(false, this.ConfirmedCap, "Readback mismatch");
                    }

                    lock (this._stateLock)
                    {
                        this._confirmedCap = readback;
                        this._isUnlimited = readback == 0;
                        this._currentCap = readback;
                        if (this._pendingCap == targetCap)
                        {
                            this._pendingCap = null;
                        }
                    }

                    PluginLog.Info($"[FPS CAP STATE] Successfully applied and confirmed cap {readback} for profile '{targetProfile}'.");
                    this.OnCapChanged(readback);
                    this.RaiseStateChanged(FpsCapStateChangeReason.Confirmed, readback, readback, null);

                    return new FpsCapApplyResult(true, readback, "Applied successfully");
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, $"[FPS CAP STATE] Exception applying cap {targetCap}.");
                    this.RevertPending(targetCap, ex.Message);
                    return new FpsCapApplyResult(false, this.ConfirmedCap, ex.Message);
                }
            }).ConfigureAwait(false);
        }

        private void RevertPending(Int32 attemptedCap, String reason)
        {
            Int32 confirmed;
            lock (this._stateLock)
            {
                if (this._pendingCap == attemptedCap)
                {
                    this._pendingCap = null;
                }
                confirmed = this._confirmedCap;
                this._currentCap = confirmed;
            }

            PluginLog.Warning($"[FPS CAP STATE] Reverting pending cap {attemptedCap} to confirmed {confirmed}: {reason}.");
            this.RaiseStateChanged(FpsCapStateChangeReason.Reverted, confirmed, confirmed, null);
        }

        public String ResolveTargetProfileName() => this.ResolveTargetProfile(out _);

        private String ResolveTargetProfile(out Boolean isAppProfile)
        {
            isAppProfile = false;
            var profileWriter = this._profileWriter;
            if (profileWriter == null)
            {
                return RTSSProfileWriterService.GlobalProfileName;
            }

            try
            {
                var snapshot = this._reader?.LastSnapshot;
                var processName = snapshot?.ProcessFileName;

                if (IsValidTarget(processName, snapshot) && profileWriter.ProfileExists(processName))
                {
                    isAppProfile = true;
                    return processName;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[FPS CAP STATE] Failed to read application target.");
            }

            if (!String.IsNullOrWhiteSpace(profileWriter.ActiveProfileName) &&
                !RTSSProfileWriterService.IsGlobalProfile(profileWriter.ActiveProfileName) &&
                profileWriter.ProfileExists(profileWriter.ActiveProfileName))
            {
                isAppProfile = true;
                return profileWriter.ActiveProfileName;
            }

            return RTSSProfileWriterService.GlobalProfileName;
        }

        private static Boolean IsValidTarget(String processName, RTSSFrameSnapshot snapshot)
        {
            if (snapshot == null || String.IsNullOrWhiteSpace(processName))
            {
                return false;
            }

            if (snapshot.SelectionReason != ActiveAppSelection.ForegroundWindow &&
                snapshot.SelectionReason != ActiveAppSelection.HighestFramerate)
            {
                return false;
            }

            var rejected = new[] { "LogiPluginService.exe", "LogiOptionsPlus.exe", "explorer.exe", "notepad.exe", "RTSSPerformancePlugin.dll" };
            return !rejected.Contains(processName, StringComparer.OrdinalIgnoreCase);
        }

        private void OnProfileWriterFpsCeilingChanged(Object sender, Int32 e)
        {
            if (this.IsPending)
            {
                return;
            }

            if (e > 0 && e != this.ConfirmedCap)
            {
                this.SetCap(e);
            }
        }

        private void OnProfileWriterActiveProfileCeilingChanged(Object sender, Int32 e)
        {
            if (this.IsPending)
            {
                return;
            }

            if (e > 0 && e != this.ConfirmedCap)
            {
                this.SetCap(e);
            }
        }

        private void OnProfileWriterActiveProfileChanged(Object sender, String profileName)
        {
            lock (this._stateLock)
            {
                this._activeProfile = profileName ?? RTSSProfileWriterService.GlobalProfileName;
            }
            this.RaiseStateChanged(FpsCapStateChangeReason.ProfileChanged, this.EffectiveCap, this._confirmedCap, this._pendingCap);
        }

        private void RaiseStateChanged(FpsCapStateChangeReason reason, Int32 effectiveCap, Int32 confirmedCap, Int32? pendingCap)
        {
            try
            {
                var args = new FpsCapStateChangedEventArgs(reason, effectiveCap, confirmedCap, pendingCap, this.ActiveProfile);
                this.StateChanged?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[FPS CAP STATE] StateChanged subscriber threw.");
            }
        }

        public void Dispose()
        {
            try
            {
                this._cts.Cancel();
                this._workSignal.Release();
            }
            catch
            {
                // Ignored
            }

            if (this._profileWriter != null)
            {
                this._profileWriter.FpsCeilingChanged -= this.OnProfileWriterFpsCeilingChanged;
                this._profileWriter.ActiveProfileCeilingChanged -= this.OnProfileWriterActiveProfileCeilingChanged;
                this._profileWriter.ActiveProfileChanged -= this.OnProfileWriterActiveProfileChanged;
            }

            try
            {
                this._workerTask?.Wait(500);
            }
            catch
            {
                // Ignored
            }

            this._cts.Dispose();
            this._workSignal.Dispose();
        }
    }
}
