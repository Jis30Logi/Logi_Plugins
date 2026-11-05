namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;
    using System.Diagnostics;
    using System.IO;

    using Loupedeck.RTSSPerformancePlugin.Helpers;

    public sealed class RTSSProfileHelperRunner : IRTSSProfileHelperRunner
    {
        public const String HelperExecutableName = "RTSSProfileHelper.exe";
        public const Int32 DefaultTimeoutMs = 5000;

        private readonly String _helperPathOverride;
        private readonly Int32 _timeoutMs;

        public Int32 TimeoutMs => this._timeoutMs;

        public RTSSProfileHelperRunner(String helperPathOverride = null, Int32 timeoutMs = DefaultTimeoutMs)
        {
            this._helperPathOverride = helperPathOverride;
            this._timeoutMs = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
        }

        public ProfileHelperResult RunAddProfile(String executableName)
        {
            if (String.IsNullOrWhiteSpace(executableName))
            {
                return ProfileHelperResult.ErrorResult("Executable name cannot be null or empty.");
            }

            var targetExe = Path.GetFileName(executableName.Trim());
            if (String.IsNullOrWhiteSpace(targetExe))
            {
                return ProfileHelperResult.ErrorResult("Invalid executable name.");
            }

            return this.ExecuteHelperCommand($"add \"{targetExe}\"", targetExe);
        }

        public ProfileHelperResult RunSelectProfile(String profileName)
        {
            if (String.IsNullOrWhiteSpace(profileName))
            {
                return ProfileHelperResult.ErrorResult("Profile name cannot be null or empty.");
            }

            var trimmed = profileName.Trim();
            var isGlobal = String.Equals(trimmed, RTSSProfileWriterService.GlobalProfileName, StringComparison.OrdinalIgnoreCase) ||
                           String.Equals(trimmed, "Global.exe", StringComparison.OrdinalIgnoreCase) ||
                           String.Equals(trimmed, "Global.cfg", StringComparison.OrdinalIgnoreCase);

            var targetName = isGlobal ? RTSSProfileWriterService.GlobalProfileName : Path.GetFileName(trimmed);
            if (String.IsNullOrWhiteSpace(targetName))
            {
                return ProfileHelperResult.ErrorResult("Invalid profile name.");
            }

            return this.ExecuteHelperCommand($"select \"{targetName}\"", targetName);
        }

        public ProfileHelperResult RunDeleteProfile(String profileName)
        {
            if (String.IsNullOrWhiteSpace(profileName))
            {
                return ProfileHelperResult.ErrorResult("Profile name cannot be null or empty.");
            }

            var trimmed = profileName.Trim();
            if (String.Equals(trimmed, RTSSProfileWriterService.GlobalProfileName, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "Global.exe", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "Global.cfg", StringComparison.OrdinalIgnoreCase))
            {
                return ProfileHelperResult.ErrorResult("Cannot delete protected Global profile.");
            }

            var targetExe = Path.GetFileName(trimmed);
            if (String.IsNullOrWhiteSpace(targetExe))
            {
                return ProfileHelperResult.ErrorResult("Invalid profile name.");
            }

            return this.ExecuteHelperCommand($"delete \"{targetExe}\"", targetExe);
        }

        public ProfileHelperResult RunSetProperty(String profileName, String propertyName, UInt32 value)
        {
            if (String.IsNullOrWhiteSpace(propertyName))
            {
                return ProfileHelperResult.ErrorResult("Property name cannot be null or empty.");
            }

            var trimmed = profileName?.Trim() ?? String.Empty;
            if (trimmed.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 4).Trim();
            }

            var isGlobal = String.IsNullOrWhiteSpace(trimmed) ||
                           String.Equals(trimmed, RTSSProfileWriterService.GlobalProfileName, StringComparison.OrdinalIgnoreCase) ||
                           String.Equals(trimmed, "Global.exe", StringComparison.OrdinalIgnoreCase) ||
                           String.Equals(trimmed, "Global.cfg", StringComparison.OrdinalIgnoreCase);

            var targetName = isGlobal ? RTSSProfileWriterService.GlobalProfileName : Path.GetFileName(trimmed);
            if (String.IsNullOrWhiteSpace(targetName))
            {
                return ProfileHelperResult.ErrorResult("Invalid profile name.");
            }

            return this.ExecuteHelperCommand($"set-prop \"{targetName}\" \"{propertyName.Trim()}\" {value}", $"{targetName}:{propertyName}");
        }

        public ProfileHelperResult RunGetProperty(String profileName, String propertyName, out UInt32 value)
        {
            value = 0;
            if (String.IsNullOrWhiteSpace(propertyName))
            {
                return ProfileHelperResult.ErrorResult("Property name cannot be null or empty.");
            }

            var trimmed = profileName?.Trim() ?? String.Empty;
            if (trimmed.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 4).Trim();
            }

            var isGlobal = String.IsNullOrWhiteSpace(trimmed) ||
                           String.Equals(trimmed, RTSSProfileWriterService.GlobalProfileName, StringComparison.OrdinalIgnoreCase) ||
                           String.Equals(trimmed, "Global.exe", StringComparison.OrdinalIgnoreCase) ||
                           String.Equals(trimmed, "Global.cfg", StringComparison.OrdinalIgnoreCase);

            var targetName = isGlobal ? RTSSProfileWriterService.GlobalProfileName : Path.GetFileName(trimmed);
            if (String.IsNullOrWhiteSpace(targetName))
            {
                return ProfileHelperResult.ErrorResult("Invalid profile name.");
            }

            var result = this.ExecuteHelperCommand($"get-prop \"{targetName}\" \"{propertyName.Trim()}\"", $"{targetName}:{propertyName}");
            if (result.IsSuccess)
            {
                var output = result.Output?.Trim() ?? String.Empty;
                if (output.StartsWith("[SUCCESS]", StringComparison.OrdinalIgnoreCase))
                {
                    output = output.Substring("[SUCCESS]".Length).Trim();
                }

                if (UInt32.TryParse(output, out var parsed))
                {
                    value = parsed;
                    return result;
                }
            }

            return result;
        }

        public ProfileHelperResult RunLaunchOrFocus() =>
            this.ExecuteHelperCommand("launch", "launch");

        public ProfileHelperResult RunSetStartWithWindows(Boolean enable) =>
            this.ExecuteHelperCommand($"set-start-with-windows {(enable ? "1" : "0")}", "set-start-with-windows");

        private ProfileHelperResult ExecuteHelperCommand(String arguments, String targetIdentifier)
        {
            var helperPath = this.ResolveHelperPath();
            if (String.IsNullOrEmpty(helperPath) || !File.Exists(helperPath))
            {
                PluginLog.Warning($"[RTSS PROFILE HELPER] Helper executable not found at '{helperPath ?? "<null>"}'.");
                return ProfileHelperResult.NotFound($"Helper executable not found at '{helperPath ?? "<null>"}'.");
            }

            Process process = null;
            try
            {
                PluginLog.Info($"[RTSS PROFILE HELPER] Launching '{helperPath}' {arguments} (timeout={this._timeoutMs}ms).");

                var startInfo = new ProcessStartInfo
                {
                    FileName = helperPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(helperPath) ?? String.Empty
                };

                var stopwatch = Stopwatch.StartNew();
                process = Process.Start(startInfo);

                if (process == null)
                {
                    PluginLog.Warning("[RTSS PROFILE HELPER] Failed to start helper process.");
                    return ProfileHelperResult.ErrorResult("Failed to start helper process.");
                }

                // Read output streams asynchronously to avoid deadlocks
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(this._timeoutMs))
                {
                    stopwatch.Stop();
                    PluginLog.Warning($"[RTSS PROFILE HELPER] Helper timed out after {stopwatch.ElapsedMilliseconds}ms. Killing process.");
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception killEx)
                    {
                        PluginLog.Warning(killEx, "[RTSS PROFILE HELPER] Exception killing timed-out helper process.");
                    }

                    return ProfileHelperResult.TimeoutResult($"Operation timed out after {this._timeoutMs}ms.");
                }

                stopwatch.Stop();
                var stdout = stdoutTask.GetAwaiter().GetResult() ?? String.Empty;
                var stderr = stderrTask.GetAwaiter().GetResult() ?? String.Empty;
                var exitCode = process.ExitCode;

                PluginLog.Info(
                    $"[RTSS PROFILE HELPER] Helper exited with code {exitCode} in {stopwatch.ElapsedMilliseconds}ms. " +
                    $"Output: '{stdout.Trim()}', Error: '{stderr.Trim()}'.");

                switch (exitCode)
                {
                    case 0:
                        return ProfileHelperResult.Success(stdout.Trim());

                    case 1:
                        return ProfileHelperResult.AlreadyExists(stdout.Trim());

                    case 2:
                        return ProfileHelperResult.RTSSUnavailable(stderr.Trim());

                    default:
                        return ProfileHelperResult.ErrorResult(
                            !String.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim(),
                            exitCode);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"[RTSS PROFILE HELPER] Unhandled exception running helper for '{targetIdentifier}'.");
                return ProfileHelperResult.ErrorResult($"Exception running helper: {ex.Message}");
            }
            finally
            {
                try
                {
                    process?.Dispose();
                }
                catch
                {
                    // Ignore disposal errors
                }
            }
        }

        private String ResolveHelperPath()
        {
            if (this._helperPathOverride != null)
            {
                return this._helperPathOverride;
            }

            try
            {
                // 1. Adjacent to the plugin assembly
                var assembly = typeof(RTSSProfileHelperRunner).Assembly;
                if (!String.IsNullOrEmpty(assembly.Location))
                {
                    var assemblyDir = Path.GetDirectoryName(assembly.Location);
                    if (!String.IsNullOrEmpty(assemblyDir))
                    {
                        var adjacent = Path.Combine(assemblyDir, HelperExecutableName);
                        if (File.Exists(adjacent))
                        {
                            return adjacent;
                        }
                    }
                }

                // 2. AppContext.BaseDirectory
                var baseDir = AppContext.BaseDirectory;
                if (!String.IsNullOrEmpty(baseDir))
                {
                    var inBaseDir = Path.Combine(baseDir, HelperExecutableName);
                    if (File.Exists(inBaseDir))
                    {
                        return inBaseDir;
                    }
                }

                // 3. Known Loupedeck plugin deployment path
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!String.IsNullOrEmpty(localAppData))
                {
                    var deployedPath = Path.Combine(
                        localAppData,
                        "Logi",
                        "LogiPluginService",
                        "Plugins",
                        "RTSSPerformance",
                        "bin",
                        HelperExecutableName);

                    if (File.Exists(deployedPath))
                    {
                        return deployedPath;
                    }
                }

                // Development-tree probing is deliberately excluded from production lookup.
            }
            catch
            {
                // Ignored
            }

            return null;
        }
    }
}
