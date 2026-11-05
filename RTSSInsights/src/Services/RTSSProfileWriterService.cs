namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;

    using Loupedeck.RTSSPerformancePlugin.Helpers;
    using Loupedeck.RTSSPerformancePlugin.Interop;

    public enum RtssDetectionLevel
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3
    }

    public interface IRTSSProfileWriterService
    {
        /// <summary>Profile currently targeted for writes ("Global" or an executable name).</summary>
        String ActiveProfileName { get; }

        /// <summary>Last FPS ceiling successfully written, or 0 for unlimited.</summary>
        Int32 CurrentFpsCeiling { get; }

        event EventHandler<Int32> FpsCeilingChanged;

        /// <summary>
        /// The ceiling in force for the profile now being targeted, or 0 when that profile
        /// declares none. Raised on every change of active profile.
        ///
        /// Distinct from <see cref="FpsCeilingChanged"/> on purpose. That one reports the last cap
        /// this plugin successfully wrote and drives the preset highlight, so it deliberately holds
        /// its value when a newly adopted profile has no readable Limit. A grading target cannot
        /// hold: keeping the previous application's ceiling grades the new one against a cap it
        /// never declared, which is how a 59 fps reading came out Red against a foreign 90.
        /// </summary>
        event EventHandler<Int32> ActiveProfileCeilingChanged;

        /// <summary>Fired when the set of available profiles changes (profile added/removed).</summary>
        event EventHandler ProfilesChanged;

        /// <summary>Fired when the active target profile changes.</summary>
        event EventHandler<String> ActiveProfileChanged;

        /// <summary>Updates in-memory cached state for OSD.</summary>
        void UpdateCachedOsdState(String profileName, Boolean isEnabled);

        /// <summary>Updates in-memory cached state for Detection Level.</summary>
        void UpdateCachedDetectionLevel(String profileName, RtssDetectionLevel level);

        /// <summary>Updates in-memory cached state for Stealth Mode.</summary>
        void UpdateCachedStealthMode(String profileName, Boolean isEnabled);

        /// <summary>
        /// Points subsequent writes at the profile RTSS will actually apply for the given
        /// executable. Returns true when the target changed.
        /// </summary>
        Boolean UpdateActiveProfile(String processFileName);

        /// <summary>Targets the named existing application profile, or Global when it is absent.</summary>
        Boolean UpdateActiveApplicationProfile(String processFileName);

        /// <summary>Enumerate available RTSS profiles (includes "Global" and application-specific ones).</summary>
        IReadOnlyList<String> GetAvailableProfiles();

        /// <summary>Create an application-specific profile for the given executable name.</summary>
        Boolean TryCreateApplicationProfile(String executableName);

        /// <summary>Delete an application-specific profile. "Global" cannot be deleted.</summary>
        Boolean TryDeleteApplicationProfile(String profileName);

        Boolean ProfileExists(String profileName);

        Boolean TryReadFpsCeiling(String profileName, out Int32 limit);

        Boolean TryReadEffectiveFpsCeiling(String processFileName, out Int32 limit);

        Boolean TryWriteFpsCeiling(String profileName, Int32 limit);

        Boolean TryWriteGlobalFpsCeiling(Int32 limit);

        Boolean TryReadOsdState(String profileName, out Boolean isEnabled);

        Boolean TryWriteOsdState(String profileName, Boolean isEnabled);

        Boolean TryReadDetectionLevel(String profileName, out RtssDetectionLevel level);

        Boolean TryWriteDetectionLevel(String profileName, RtssDetectionLevel level);

        Boolean TryReadStealthMode(String profileName, out Boolean isEnabled);

        Boolean TryWriteStealthMode(String profileName, Boolean isEnabled);

        /// <summary>Asks RTSS to reload profiles from disk so the new ceiling takes effect.</summary>
        Boolean ReloadProfiles();

        /// <summary>Invalidates the cached list of available profiles.</summary>
        void InvalidateProfilesCache();

        /// <summary>Returns the single authoritative effective profile name ("Global" or an application executable name).</summary>
        String GetEffectiveProfileName();
    }

    /// <summary>
    /// Writes the FramerateLimit value into RTSS profile files and triggers a profile reload.
    /// Every operation degrades to false rather than throwing: RTSS may not be installed, and a
    /// missing framerate cap is not a reason to fault a key press.
    /// </summary>
    public sealed class RTSSProfileWriterService : IRTSSProfileWriterService
    {
        public const String GlobalProfileName = "Global";

        public const String FramerateLimitSection = "Framerate";

        public const String FramerateLimitKey = "Limit";

        public const String ProfileFileExtension = ".cfg";

        private readonly Object _sync = new Object();

        /// <summary>Set by tests and by callers that already know the folder; null means "ask the registry".</summary>
        private readonly String _profilesDirectoryOverride;
        private readonly IRTSSProfileHelperRunner _helperRunner;

        private String _activeProfileName = GlobalProfileName;
        private String _selectedProfileName = GlobalProfileName;

        private Int32 _currentFpsCeiling;

        private const Int64 CacheTtlMs = 2000;

        private readonly Dictionary<String, (Boolean Value, Int64 Timestamp)> _cachedOsdState =
            new Dictionary<String, (Boolean, Int64)>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<String, (RtssDetectionLevel Value, Int64 Timestamp)> _cachedDetectionLevel =
            new Dictionary<String, (RtssDetectionLevel, Int64)>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<String, (Boolean Value, Int64 Timestamp)> _cachedStealthMode =
            new Dictionary<String, (Boolean, Int64)>(StringComparer.OrdinalIgnoreCase);

        private IReadOnlyList<String> _cachedAvailableProfiles;
        private Int64 _cachedAvailableProfilesTimestamp;

        public void InvalidateProfilesCache()
        {
            lock (this._sync)
            {
                this._cachedAvailableProfiles = null;
                this._cachedAvailableProfilesTimestamp = 0;
            }
        }

        public void UpdateCachedOsdState(String profileName, Boolean isEnabled)
        {
            if (String.IsNullOrWhiteSpace(profileName))
            {
                return;
            }

            lock (this._sync)
            {
                this._cachedOsdState[profileName] = (isEnabled, Environment.TickCount64);
            }
        }

        public void UpdateCachedDetectionLevel(String profileName, RtssDetectionLevel level)
        {
            if (String.IsNullOrWhiteSpace(profileName))
            {
                return;
            }

            lock (this._sync)
            {
                this._cachedDetectionLevel[profileName] = (level, Environment.TickCount64);
            }
        }

        public void UpdateCachedStealthMode(String profileName, Boolean isEnabled)
        {
            if (String.IsNullOrWhiteSpace(profileName))
            {
                return;
            }

            lock (this._sync)
            {
                this._cachedStealthMode[profileName] = (isEnabled, Environment.TickCount64);
            }
        }

        public String ActiveProfileName
        {
            get
            {
                lock (this._sync)
                {
                    if (!IsGlobalProfile(this._activeProfileName) && !this.ProfileExists(this._activeProfileName))
                    {
                        this._activeProfileName = (!String.IsNullOrWhiteSpace(this._selectedProfileName) && this.ProfileExists(this._selectedProfileName))
                            ? this._selectedProfileName
                            : GlobalProfileName;
                    }

                    return this._activeProfileName;
                }
            }
        }

        public String GetEffectiveProfileName()
        {
            lock (this._sync)
            {
                if (!String.IsNullOrWhiteSpace(this._activeProfileName) &&
                    !IsGlobalProfile(this._activeProfileName) &&
                    this.ProfileExists(this._activeProfileName))
                {
                    return this._activeProfileName;
                }

                if (!String.IsNullOrWhiteSpace(this._selectedProfileName) &&
                    !IsGlobalProfile(this._selectedProfileName) &&
                    this.ProfileExists(this._selectedProfileName))
                {
                    return this._selectedProfileName;
                }

                return GlobalProfileName;
            }
        }

        public Int32 CurrentFpsCeiling
        {
            get
            {
                lock (this._sync)
                {
                    return this._currentFpsCeiling;
                }
            }
        }

        public event EventHandler<Int32> FpsCeilingChanged;

        public event EventHandler<Int32> ActiveProfileCeilingChanged;

        public event EventHandler ProfilesChanged;

        public event EventHandler<String> ActiveProfileChanged;

        public RTSSProfileWriterService()
            : this(null, null)
        {
        }

        public RTSSProfileWriterService(String profilesDirectory)
            : this(profilesDirectory, null)
        {
        }

        public RTSSProfileWriterService(String profilesDirectory, IRTSSProfileHelperRunner helperRunner)
        {
            this._profilesDirectoryOverride = profilesDirectory;
            this._helperRunner = helperRunner;
        }

        /// <summary>
        /// Points subsequent writes at the profile RTSS will actually apply for
        /// <paramref name="processFileName"/>.
        ///
        /// RTSS gives a per-application profile precedence over Global. Writing Global while a
        /// running game has its own {exe}.cfg changes a value that game never reads, so the cap
        /// silently does nothing — which is exactly how this presented on-device.
        ///
        /// When the active app has no pre-existing profile on disk, this falls back to Global.
        /// Foreground detection and telemetry must never create an application profile.
        ///
        /// Adopting a profile also re-reads its real ceiling, so the active-preset highlight
        /// reflects the profile in force rather than whatever this plugin last wrote elsewhere.
        /// </summary>
        public Boolean UpdateActiveProfile(String processFileName)
        {
            if (String.IsNullOrWhiteSpace(processFileName))
            {
                String target;
                lock (this._sync)
                {
                    target = (!String.IsNullOrWhiteSpace(this._selectedProfileName) && this.ProfileExists(this._selectedProfileName))
                        ? this._selectedProfileName
                        : GlobalProfileName;
                }
                return this.AdoptProfile(target, null);
            }

            var resolved = this.ResolveTargetProfileName(processFileName);
            return this.AdoptProfile(resolved, processFileName);
        }

        public Boolean UpdateActiveApplicationProfile(String processFileName)
        {
            if (String.IsNullOrWhiteSpace(processFileName))
            {
                lock (this._sync)
                {
                    this._selectedProfileName = GlobalProfileName;
                }
                return this.AdoptProfile(GlobalProfileName, processFileName);
            }

            var normalized = NormalizeProfileName(processFileName);
            if (normalized == null)
            {
                PluginLog.Warning($"Rejected profile name '{processFileName}': invalid bare file name.");
                return false;
            }

            if (!this.ProfileExists(normalized))
            {
                lock (this._sync)
                {
                    this._selectedProfileName = GlobalProfileName;
                }

                return this.AdoptProfile(GlobalProfileName, processFileName);
            }

            lock (this._sync)
            {
                this._selectedProfileName = normalized;
            }

            return this.AdoptProfile(normalized, processFileName);
        }

        private Boolean AdoptProfile(String resolved, String processFileName)
        {
            Int32 ceiling;

            lock (this._sync)
            {
                if (String.Equals(this._activeProfileName, resolved, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                this._activeProfileName = resolved;
            }

            PluginLog.Info($"[RTSS PROFILE] Target profile is now '{resolved}' (process '{processFileName}').");
            this.OnActiveProfileChanged(resolved);

            var hasCeiling = this.TryReadFpsCeiling(resolved, out ceiling);

            // The grading target follows the profile unconditionally, including to 0 when the
            // profile declares no Limit of its own. FpsHealthStateService.GetTier reads a
            // non-positive target as "no cap to fall short of" and returns Green, which is the
            // honest answer for a profile whose ceiling is unknown — and strictly better than
            // grading it against the ceiling of whatever application ran before it.
            this.OnActiveProfileCeilingChanged(hasCeiling ? ceiling : 0);

            if (!hasCeiling)
            {
                // No readable Limit in the new profile: leave the last known ceiling alone rather
                // than reporting a cap of 0 that nobody set. This governs CurrentFpsCeiling and
                // therefore the preset highlight, which is a different question from grading.
                return true;
            }

            Boolean changed;

            lock (this._sync)
            {
                changed = this._currentFpsCeiling != ceiling;
                this._currentFpsCeiling = ceiling;
            }

            if (changed)
            {
                PluginLog.Info($"[RTSS PROFILE] '{resolved}' currently caps at {ceiling} FPS.");
                this.OnFpsCeilingChanged(ceiling);
            }

            return true;
        }

        /// <summary>
        /// The per-application profile for the active executable, even when the .cfg file does not
        /// exist yet. This keeps the write targeted at the active app instead of silently falling
        /// back to Global.cfg, which RTSS would ignore for the active process.
        /// </summary>
        private String ResolveTargetProfileName(String processFileName)
        {
            if (String.IsNullOrWhiteSpace(processFileName))
            {
                return GlobalProfileName;
            }

            var name = processFileName.Trim();

            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                name.IndexOf(Path.AltDirectorySeparatorChar) >= 0 ||
                name == "." ||
                name == "..")
            {
                PluginLog.Warning($"Rejected profile name '{processFileName}': it is not a bare file name.");
                return GlobalProfileName;
            }

            var path = this.ResolveProfilePath(name);
            if (String.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return GlobalProfileName;
            }

            return name;
        }

        public Boolean TryReadFpsCeiling(String profileName, out Int32 limit)
        {
            limit = 0;

            if (String.IsNullOrWhiteSpace(profileName))
            {
                return false;
            }

            var path = this.ResolveProfilePath(profileName);

            if (path == null)
            {
                return false;
            }

            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                return TryFindLimit(File.ReadAllLines(path), out limit);
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, $"Failed to read the FPS ceiling from '{path}'.");
                return false;
            }
        }

        /// <summary>
        /// Resolves the effective FPS ceiling following RTSS profile precedence:
        /// 1. Application-specific profile limit (if application is valid and has a valid Limit &gt; 0).
        /// 2. Global profile limit (if Global has a valid Limit &gt; 0).
        /// 3. Returns false (limit = 0) if neither has a valid limit or on any failure.
        /// </summary>
        public Boolean TryReadEffectiveFpsCeiling(String processFileName, out Int32 limit)
        {
            limit = 0;

            try
            {
                if (!String.IsNullOrWhiteSpace(processFileName) &&
                    !IsGlobalProfile(processFileName) &&
                    this.TryReadFpsCeiling(processFileName, out var appLimit) &&
                    appLimit > 0)
                {
                    limit = appLimit;
                    return true;
                }

                if (this.TryReadFpsCeiling(GlobalProfileName, out var globalLimit) && globalLimit > 0)
                {
                    limit = globalLimit;
                    return true;
                }
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, "[RTSS PROFILE] Failed to read effective FPS ceiling.");
            }

            return false;
        }

        public Boolean TryWriteFpsCeiling(String profileName, Int32 limit)
        {
            if (limit < 0)
            {
                PluginLog.Warning($"[RTSS WRITE] Rejected: {limit} is not a valid FPS ceiling.");
                return false;
            }

            if (!this.IsExistingOrGlobalProfile(profileName))
            {
                PluginLog.Warning($"[RTSS WRITE] Rejected write to missing application profile '{profileName}'.");
                return false;
            }

            var path = this.ResolveProfilePath(profileName);

            if (path == null)
            {
                PluginLog.Warning(
                    $"[RTSS WRITE] Rejected: could not resolve a profile path for '{profileName}'. " +
                    $"RTSS install: {RTSSInstallLocator.TryGetInstallDirectory() ?? "<not found>"}.");
                return false;
            }

            lock (this._sync)
            {
                try
                {
                    var existed = File.Exists(path);
                    var lines = existed ? new List<String>(File.ReadAllLines(path)) : new List<String>();
                    var hadOldValue = TryFindLimit(lines, out var oldLimit);

                    ApplyLimit(lines, limit);
                    WriteAtomically(path, lines);

                    PluginLog.Info(
                        $"[RTSS WRITE] OK. preset={limit} profile='{profileName}' path='{path}' " +
                        $"old={(hadOldValue ? oldLimit.ToString(CultureInfo.InvariantCulture) : "none")} new={limit} " +
                        $"({(existed ? "updated" : "created")})");
                }
                catch (Exception exception)
                {
                    PluginLog.Error(
                        exception,
                        $"[RTSS WRITE] FAILED. preset={limit} profile='{profileName}' path='{path}'.");
                    return false;
                }
            }

            try
            {
                var target = String.IsNullOrWhiteSpace(profileName) ? GlobalProfileName : profileName;
                var runner = this._helperRunner ?? RTSSPerformancePlugin.Instance?.ProfileHelperRunner;
                var helperResult = runner?.RunSetProperty(target, "FramerateLimit", (UInt32)limit);
                this.LastHelperSyncStatus = helperResult?.Status;

                if (helperResult != null && !helperResult.IsSuccess)
                {
                    PluginLog.Warning(
                        $"[RTSS WRITE] Helper sync for '{target}' FramerateLimit={limit} returned " +
                        $"{helperResult.Status} (exit {helperResult.ExitCode}): {helperResult.Error}");
                }
            }
            catch (Exception ex)
            {
                this.LastHelperSyncStatus = ProfileHelperStatus.Error;
                PluginLog.Warning(ex, $"[RTSS WRITE] Isolated helper sync failed for '{profileName}'.");
            }

            this.PublishCeiling(String.IsNullOrWhiteSpace(profileName) ? GlobalProfileName : profileName, limit);

            return true;
        }

        /// <summary>
        /// Outcome of the isolated helper's live-RTSS sync for the most recent write; null when
        /// no helper was available. Only <see cref="ProfileHelperStatus.Error"/> and
        /// <see cref="ProfileHelperStatus.Timeout"/> mean RTSS was reachable and the sync failed:
        /// RTSS not running or the helper not installed leave the on-disk value as the truth.
        /// </summary>
        public ProfileHelperStatus? LastHelperSyncStatus { get; private set; }

        public Boolean TryWriteGlobalFpsCeiling(Int32 limit) => this.TryWriteFpsCeiling(GlobalProfileName, limit);

        /// <summary>
        /// Asks RTSS to pick up the profile just written and apply it to anything running.
        ///
        /// This used to be a no-op returning true, on the assumption that RTSS hot-reloads its
        /// profile folder. That assumption was wrong: RTSS holds profiles in memory, and the
        /// documented way to make a running instance re-read one is the LoadProfile +
        /// UpdateProfiles pair it exports from RTSSHooks64.dll.
        ///
        /// Returns false when the live RTSS API is unavailable or the apply call fails. The value
        /// remains on disk and can take effect the next time RTSS loads that profile.
        /// </summary>
        public Boolean ReloadProfiles()
        {
            this.InvalidateProfilesCache();
            this.OnProfilesChanged();
            return true;
        }

        public IReadOnlyList<String> GetAvailableProfiles()
        {
            lock (this._sync)
            {
                var now = Environment.TickCount64;
                if (this._cachedAvailableProfiles != null && (now - this._cachedAvailableProfilesTimestamp) < CacheTtlMs)
                {
                    return this._cachedAvailableProfiles;
                }

                var profiles = new List<String> { GlobalProfileName };
                var appProfiles = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

                var profilesDir = this._profilesDirectoryOverride ?? RTSSInstallLocator.TryGetProfilesDirectory();

                if (!String.IsNullOrEmpty(profilesDir) && Directory.Exists(profilesDir))
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(profilesDir);
                        foreach (var file in dirInfo.GetFiles())
                        {
                            var normalized = NormalizeProfileName(file.Name);
                            if (normalized != null && IsValidApplicationProfile(normalized))
                            {
                                appProfiles.Add(normalized);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Warning(ex, "[RTSS PROFILES] Failed to enumerate available profiles from disk.");
                    }
                }

                var sortedApps = appProfiles.OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
                profiles.AddRange(sortedApps);

                this._cachedAvailableProfiles = profiles.AsReadOnly();
                this._cachedAvailableProfilesTimestamp = now;
                return this._cachedAvailableProfiles;
            }
        }

        public Boolean TryCreateApplicationProfile(String executableName)
        {
            if (String.IsNullOrWhiteSpace(executableName) || IsGlobalProfile(executableName))
            {
                PluginLog.Warning($"[RTSS PROFILE] Cannot create profile for '{executableName}'.");
                return false;
            }

            var path = this.ResolveProfilePath(executableName);
            if (path == null)
            {
                return false;
            }

            if (File.Exists(path))
            {
                PluginLog.Info($"[RTSS PROFILE] Profile already exists at '{path}'.");
                this.OnProfilesChanged();
                return this.UpdateActiveApplicationProfile(executableName);
            }

            lock (this._sync)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        var directory = Path.GetDirectoryName(path);
                        if (!String.IsNullOrEmpty(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }
                        File.WriteAllText(path, "");
                    }

                    PluginLog.Info($"[RTSS PROFILE] Created new profile: '{executableName}' at '{path}'.");
                    this.InvalidateProfilesCache();
                    this.OnProfilesChanged();
                    return this.UpdateActiveApplicationProfile(executableName);
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, $"[RTSS PROFILE] Failed to create profile at '{path}'.");
                    return false;
                }
            }
        }

        public Boolean TryDeleteApplicationProfile(String profileName)
        {
            if (String.IsNullOrWhiteSpace(profileName) || IsGlobalProfile(profileName))
            {
                PluginLog.Warning($"[RTSS PROFILE] Cannot delete profile '{profileName}' (Global cannot be deleted).");
                return false;
            }

            var path = this.ResolveProfilePath(profileName);
            if (path == null)
            {
                return false;
            }

            lock (this._sync)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    PluginLog.Info($"[RTSS PROFILE] Deleted profile: '{profileName}'.");
                    this.InvalidateProfilesCache();
                    this.OnProfilesChanged();

                    if (String.Equals(this._activeProfileName, profileName, StringComparison.OrdinalIgnoreCase))
                    {
                        this.AdoptProfile(GlobalProfileName, profileName);
                    }

                    if (String.Equals(this._selectedProfileName, profileName, StringComparison.OrdinalIgnoreCase))
                    {
                        this._selectedProfileName = GlobalProfileName;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, $"[RTSS PROFILE] Failed to delete profile at '{path}'.");
                    return false;
                }
            }
        }

        public Boolean ProfileExists(String profileName)
        {
            if (String.IsNullOrWhiteSpace(profileName))
            {
                return false;
            }

            var normalized = NormalizeProfileName(profileName);
            if (normalized == null)
            {
                return false;
            }

            if (IsGlobalProfile(normalized))
            {
                return true;
            }

            var path = this.ResolveProfilePath(normalized);
            return path != null && File.Exists(path);
        }

        public Boolean TryReadOsdState(String profileName, out Boolean isEnabled)
        {
            isEnabled = false;

            if (String.IsNullOrWhiteSpace(profileName))
            {
                return false;
            }

            lock (this._sync)
            {
                var now = Environment.TickCount64;
                if (this._cachedOsdState.TryGetValue(profileName, out var cached) && (now - cached.Timestamp) < CacheTtlMs)
                {
                    isEnabled = cached.Value;
                    return true;
                }
            }

            var path = this.ResolveProfilePath(profileName);
            if (path == null || !File.Exists(path))
            {
                if (!IsGlobalProfile(profileName) && this.TryReadOsdState(GlobalProfileName, out var globalState))
                {
                    isEnabled = globalState;
                    lock (this._sync)
                    {
                        this._cachedOsdState[profileName] = (isEnabled, Environment.TickCount64);
                    }
                    return true;
                }
                return false;
            }

            try
            {
                var lines = File.ReadAllLines(path);
                var inSection = false;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (IsSectionHeader(trimmed, out var section))
                    {
                        inSection = String.Equals(section, "OSD", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (inSection && IsKey(trimmed, "EnableOSD", out var value))
                    {
                        isEnabled = value.Trim().Equals("1", StringComparison.OrdinalIgnoreCase);
                        lock (this._sync)
                        {
                            this._cachedOsdState[profileName] = (isEnabled, Environment.TickCount64);
                        }
                        return true;
                    }
                }

                if (!IsGlobalProfile(profileName) && this.TryReadOsdState(GlobalProfileName, out var fallbackState))
                {
                    isEnabled = fallbackState;
                    lock (this._sync)
                    {
                        this._cachedOsdState[profileName] = (isEnabled, Environment.TickCount64);
                    }
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"[RTSS OSD] Failed to read OSD state from '{profileName}'.");
                return false;
            }
        }

        public Boolean TryWriteOsdState(String profileName, Boolean isEnabled)
        {
            if (!this.IsExistingOrGlobalProfile(profileName))
            {
                PluginLog.Warning($"[RTSS OSD] Rejected write to missing application profile '{profileName}'.");
                return false;
            }

            var path = this.ResolveProfilePath(profileName);
            if (path == null)
            {
                return false;
            }

            lock (this._sync)
            {
                try
                {
                    this._cachedOsdState[profileName] = (isEnabled, Environment.TickCount64);

                    var lines = File.Exists(path) ? new List<String>(File.ReadAllLines(path)) : new List<String>();
                    var value = isEnabled ? "1" : "0";
                    this.ApplyConfigValue(lines, "OSD", "EnableOSD", value);
                    WriteAtomically(path, lines);

                    return true;
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, $"[RTSS OSD] Failed to write OSD state for '{profileName}'.");
                    return false;
                }
            }
        }

        public Boolean TryReadDetectionLevel(String profileName, out RtssDetectionLevel level)
        {
            level = RtssDetectionLevel.Medium;

            if (String.IsNullOrWhiteSpace(profileName))
            {
                return false;
            }

            lock (this._sync)
            {
                var now = Environment.TickCount64;
                if (this._cachedDetectionLevel.TryGetValue(profileName, out var cached) && (now - cached.Timestamp) < CacheTtlMs)
                {
                    level = cached.Value;
                    return true;
                }
            }

            var path = this.ResolveProfilePath(profileName);
            if (path == null || !File.Exists(path))
            {
                if (!IsGlobalProfile(profileName) && this.TryReadDetectionLevel(GlobalProfileName, out var globalLevel))
                {
                    level = globalLevel;
                    lock (this._sync)
                    {
                        this._cachedDetectionLevel[profileName] = (level, Environment.TickCount64);
                    }
                    return true;
                }
                return false;
            }

            try
            {
                var lines = File.ReadAllLines(path);
                var inSection = false;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (IsSectionHeader(trimmed, out var section))
                    {
                        inSection = String.Equals(section, "Hooking", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (inSection && (IsKey(trimmed, "HookLevel", out var value) || IsKey(trimmed, "AppDetectionLevel", out value)))
                    {
                        if (Int32.TryParse(value.Trim(), out var levelInt))
                        {
                            level = (RtssDetectionLevel)levelInt;
                            lock (this._sync)
                            {
                                this._cachedDetectionLevel[profileName] = (level, Environment.TickCount64);
                            }
                            return true;
                        }
                        return false;
                    }
                }

                if (!IsGlobalProfile(profileName) && this.TryReadDetectionLevel(GlobalProfileName, out var fallbackLevel))
                {
                    level = fallbackLevel;
                    lock (this._sync)
                    {
                        this._cachedDetectionLevel[profileName] = (level, Environment.TickCount64);
                    }
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"[RTSS DETECTION] Failed to read detection level from '{profileName}'.");
                return false;
            }
        }

        public Boolean TryWriteDetectionLevel(String profileName, RtssDetectionLevel level)
        {
            if (!this.IsExistingOrGlobalProfile(profileName))
            {
                PluginLog.Warning($"[RTSS DETECTION] Rejected write to missing application profile '{profileName}'.");
                return false;
            }

            var path = this.ResolveProfilePath(profileName);
            if (path == null)
            {
                return false;
            }

            lock (this._sync)
            {
                try
                {
                    this._cachedDetectionLevel[profileName] = (level, Environment.TickCount64);

                    var lines = File.Exists(path) ? new List<String>(File.ReadAllLines(path)) : new List<String>();
                    this.ApplyConfigValue(lines, "Hooking", "HookLevel", ((Int32)level).ToString(CultureInfo.InvariantCulture));
                    WriteAtomically(path, lines);

                    return true;
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, $"[RTSS DETECTION] Failed to write detection level for '{profileName}'.");
                    return false;
                }
            }
        }

        public Boolean TryReadStealthMode(String profileName, out Boolean isEnabled)
        {
            isEnabled = false;

            if (String.IsNullOrWhiteSpace(profileName))
            {
                return false;
            }

            lock (this._sync)
            {
                var now = Environment.TickCount64;
                if (this._cachedStealthMode.TryGetValue(profileName, out var cached) && (now - cached.Timestamp) < CacheTtlMs)
                {
                    isEnabled = cached.Value;
                    return true;
                }
            }

            var path = this.ResolveProfilePath(profileName);
            if (path == null || !File.Exists(path))
            {
                if (!IsGlobalProfile(profileName) && this.TryReadStealthMode(GlobalProfileName, out var globalStealth))
                {
                    isEnabled = globalStealth;
                    lock (this._sync)
                    {
                        this._cachedStealthMode[profileName] = (isEnabled, Environment.TickCount64);
                    }
                    return true;
                }
                return false;
            }

            try
            {
                var lines = File.ReadAllLines(path);
                var inSection = false;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (IsSectionHeader(trimmed, out var section))
                    {
                        inSection = String.Equals(section, "Hooking", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (inSection && IsKey(trimmed, "EnableFloatingInjectionAddress", out var value))
                    {
                        isEnabled = value.Trim().Equals("1", StringComparison.OrdinalIgnoreCase);
                        lock (this._sync)
                        {
                            this._cachedStealthMode[profileName] = (isEnabled, Environment.TickCount64);
                        }
                        return true;
                    }
                }

                if (!IsGlobalProfile(profileName) && this.TryReadStealthMode(GlobalProfileName, out var fallbackStealth))
                {
                    isEnabled = fallbackStealth;
                    lock (this._sync)
                    {
                        this._cachedStealthMode[profileName] = (isEnabled, Environment.TickCount64);
                    }
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"[RTSS STEALTH] Failed to read stealth mode from '{profileName}'.");
                return false;
            }
        }

        public Boolean TryWriteStealthMode(String profileName, Boolean isEnabled)
        {
            if (!this.IsExistingOrGlobalProfile(profileName))
            {
                PluginLog.Warning($"[RTSS STEALTH] Rejected write to missing application profile '{profileName}'.");
                return false;
            }

            var path = this.ResolveProfilePath(profileName);
            if (path == null)
            {
                return false;
            }

            lock (this._sync)
            {
                try
                {
                    this._cachedStealthMode[profileName] = (isEnabled, Environment.TickCount64);

                    var lines = File.Exists(path) ? new List<String>(File.ReadAllLines(path)) : new List<String>();
                    var value = isEnabled ? "1" : "0";
                    this.ApplyConfigValue(lines, "Hooking", "EnableFloatingInjectionAddress", value);
                    WriteAtomically(path, lines);

                    return true;
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, $"[RTSS STEALTH] Failed to write stealth mode for '{profileName}'.");
                    return false;
                }
            }
        }

        private void OnProfilesChanged()
        {
            this.InvalidateProfilesCache();
            try
            {
                this.ProfilesChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[RTSS PROFILE] ProfilesChanged subscriber threw.");
            }
        }

        private void ApplyConfigValue(IList<String> lines, String section, String key, String value)
        {
            var sectionIndex = -1;
            var keyIndex = -1;
            var inSection = false;

            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (IsSectionHeader(trimmed, out var sec))
                {
                    if (String.Equals(sec, section, StringComparison.OrdinalIgnoreCase))
                    {
                        sectionIndex = i;
                        inSection = true;
                    }
                    else if (inSection)
                    {
                        break;
                    }

                    continue;
                }

                if (inSection && trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    keyIndex = i;
                    break;
                }
            }

            if (sectionIndex < 0)
            {
                lines.Add($"[{section}]");
                lines.Add($"{key}={value}");
            }
            else if (keyIndex < 0)
            {
                lines.Insert(sectionIndex + 1, $"{key}={value}");
            }
            else
            {
                lines[keyIndex] = $"{key}={value}";
            }
        }

        /// <summary>
        /// Normalizes raw profile strings (e.g. "Global", "Global.cfg", "Global.exe", "Game.exe", "Game.exe.cfg")
        /// into canonical profile identity ("Global" or "<name>.exe").
        /// Returns null on invalid, empty, or path-traversal input.
        /// </summary>
        public static String NormalizeProfileName(String raw)
        {
            if (String.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var trimmed = raw.Trim();

            // Reject any path separators or traversal
            if (trimmed.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                trimmed.IndexOf(Path.AltDirectorySeparatorChar) >= 0 ||
                trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                trimmed == "." ||
                trimmed == "..")
            {
                return null;
            }

            // Strip trailing .cfg extension if present
            if (trimmed.EndsWith(ProfileFileExtension, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - ProfileFileExtension.Length).Trim();
            }

            // Check for Global variants
            if (String.Equals(trimmed, GlobalProfileName, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "Global.exe", StringComparison.OrdinalIgnoreCase))
            {
                return GlobalProfileName;
            }

            return trimmed;
        }

        /// <summary>
        /// Validates that a profile name represents a valid application profile.
        /// Rejects Global, Config, non-.exe files, directories, path separators, and traversal.
        /// </summary>
        public static Boolean IsValidApplicationProfile(String name)
        {
            if (String.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var normalized = NormalizeProfileName(name);
            if (normalized == null || IsGlobalProfile(normalized))
            {
                return false;
            }

            // Must end with .exe (case-insensitive)
            if (!normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Reject known non-profile files like Config or EncoderServer
            if (String.Equals(normalized, "Config.exe", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(normalized, "EncoderServer.exe", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(normalized, "SaveMedia.exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private Boolean IsExistingOrGlobalProfile(String profileName)
        {
            if (String.IsNullOrWhiteSpace(profileName))
            {
                return true;
            }

            var normalized = NormalizeProfileName(profileName);
            return normalized != null &&
                (IsGlobalProfile(normalized) || this.ProfileExists(normalized));
        }

        /// <summary>
        /// Maps a profile name onto its file. Returns null when RTSS cannot be located, or when
        /// the name carries a path separator — a profile name is a file name, never a path, and
        /// letting one through would write outside the profiles folder.
        /// </summary>
        private String ResolveProfilePath(String profileName)
        {
            if (String.IsNullOrWhiteSpace(profileName))
            {
                profileName = GlobalProfileName;
            }

            var normalized = NormalizeProfileName(profileName);
            if (normalized == null)
            {
                PluginLog.Warning($"Rejected profile name '{profileName}': invalid or contains path traversal.");
                return null;
            }

            var directory = this._profilesDirectoryOverride ?? RTSSInstallLocator.TryGetProfilesDirectory();

            if (String.IsNullOrEmpty(directory))
            {
                return null;
            }

            if (IsGlobalProfile(normalized))
            {
                var bareGlobal = Path.Combine(directory, GlobalProfileName);
                if (File.Exists(bareGlobal))
                {
                    return bareGlobal;
                }

                var cfgGlobal = Path.Combine(directory, GlobalProfileName + ProfileFileExtension);
                if (File.Exists(cfgGlobal))
                {
                    return cfgGlobal;
                }

                return bareGlobal;
            }

            return Path.Combine(directory, normalized + ProfileFileExtension);
        }

        public static Boolean IsGlobalProfile(String profileName) =>
            String.Equals(profileName, GlobalProfileName, StringComparison.OrdinalIgnoreCase);

        private void PublishCeiling(String profileName, Int32 limit)
        {
            Boolean changed;
            Boolean profileChanged;
            lock (this._sync)
            {
                profileChanged = !String.Equals(this._activeProfileName, profileName, StringComparison.OrdinalIgnoreCase);
                changed = this._currentFpsCeiling != limit || profileChanged;
                this._activeProfileName = profileName;

                if (!changed)
                {
                    return;
                }

                // Set before raising: subscribers read CurrentFpsCeiling from the handler.
                this._currentFpsCeiling = limit;
            }

            if (profileChanged)
            {
                this.OnActiveProfileChanged(profileName);
            }

            this.OnFpsCeilingChanged(limit);
            this.OnActiveProfileCeilingChanged(limit);
        }

        private void OnActiveProfileChanged(String profileName)
        {
            try
            {
                this.ActiveProfileChanged?.Invoke(this, profileName);
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, "[RTSS PROFILE] ActiveProfileChanged subscriber threw.");
            }
        }

        private void OnActiveProfileCeilingChanged(Int32 ceiling)
        {
            try
            {
                this.ActiveProfileCeilingChanged?.Invoke(this, ceiling);
            }
            catch (Exception exception)
            {
                // A subscriber must not be able to fault the poll thread that drives retargeting.
                PluginLog.Warning(exception, "[RTSS PROFILE] ActiveProfileCeilingChanged subscriber threw.");
            }
        }

        private void OnFpsCeilingChanged(Int32 limit)
        {
            try
            {
                this.FpsCeilingChanged?.Invoke(this, limit);
            }
            catch (Exception exception)
            {
                PluginLog.Error(exception, $"An FPS ceiling subscriber threw for limit {limit}.");
            }
        }

        /// <summary>Finds Limit= inside the [Framerate] section, ignoring same-named keys elsewhere.</summary>
        private static Boolean TryFindLimit(IReadOnlyList<String> lines, out Int32 limit)
        {
            limit = 0;

            var inSection = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (IsSectionHeader(trimmed, out var section))
                {
                    inSection = String.Equals(section, FramerateLimitSection, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection || !IsKey(trimmed, out var value))
                {
                    continue;
                }

                return Int32.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out limit);
            }

            return false;
        }

        /// <summary>
        /// Rewrites Limit= in place, leaving every other line — including comments and keys this
        /// plugin knows nothing about — exactly as it found them. RTSS profiles are user-editable
        /// and hold far more than the framerate cap.
        /// </summary>
        private static void ApplyLimit(List<String> lines, Int32 limit)
        {
            var formatted = FramerateLimitKey + "=" + limit.ToString(CultureInfo.InvariantCulture);
            var sectionStart = -1;
            var sectionEnd = lines.Count;
            var inSection = false;

            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();

                if (IsSectionHeader(trimmed, out var section))
                {
                    if (inSection)
                    {
                        sectionEnd = i;
                        break;
                    }

                    if (String.Equals(section, FramerateLimitSection, StringComparison.OrdinalIgnoreCase))
                    {
                        inSection = true;
                        sectionStart = i;
                    }

                    continue;
                }

                if (inSection && IsKey(trimmed, out _))
                {
                    lines[i] = formatted;
                    return;
                }
            }

            if (sectionStart < 0)
            {
                lines.Add("[" + FramerateLimitSection + "]");
                lines.Add(formatted);
                return;
            }

            // Section exists but has no Limit key: insert at the end of that section so the key
            // does not land under a later, unrelated one.
            lines.Insert(sectionEnd, formatted);
        }

        /// <summary>
        /// Writes via a temp file and a replace, so a crash mid-write cannot leave the user with a
        /// truncated profile.
        /// </summary>
        private static void WriteAtomically(String path, IReadOnlyList<String> lines)
        {
            var directory = Path.GetDirectoryName(path);

            if (!String.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = path + ".tmp";

            File.WriteAllLines(temporaryPath, lines);

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }

        private static Boolean IsSectionHeader(String trimmedLine, out String section)
        {
            section = null;

            if (trimmedLine.Length < 2 || trimmedLine[0] != '[' || trimmedLine[trimmedLine.Length - 1] != ']')
            {
                return false;
            }

            section = trimmedLine.Substring(1, trimmedLine.Length - 2).Trim();

            return true;
        }

        private static Boolean IsKey(String trimmedLine, String expectedKey, out String value)
        {
            value = null;

            var separator = trimmedLine.IndexOf('=');

            if (separator <= 0)
            {
                return false;
            }

            if (!String.Equals(
                    trimmedLine.Substring(0, separator).Trim(),
                    expectedKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            value = trimmedLine.Substring(separator + 1);

            return true;
        }

        private static Boolean IsKey(String trimmedLine, out String value) =>
            IsKey(trimmedLine, FramerateLimitKey, out value);
    }
}
