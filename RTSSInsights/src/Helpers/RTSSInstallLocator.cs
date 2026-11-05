namespace Loupedeck.RTSSPerformancePlugin.Helpers
{
    using System;
    using System.IO;

    using Microsoft.Win32;

    /// <summary>
    /// Resolves where RivaTuner Statistics Server is installed. Every lookup is best-effort:
    /// RTSS may not be installed at all, and the plugin degrades gracefully rather than failing.
    /// </summary>
    public static class RTSSInstallLocator
    {
        public const String ProfilesFolderName = "Profiles";

        public const String InstallDirectoryEnvironmentVariable = "RTSS_INSTALL_DIR";

        private const String UninstallKeyPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RivaTuner Statistics Server_is1";

        public const String ExecutableFileName = "RTSS.exe";

        internal static Func<String> CustomInstallDirectoryResolver { get; set; }

        /// <summary>
        /// True when RTSS installation directory can be resolved on the system.
        /// </summary>
        public static Boolean IsInstalled() => !String.IsNullOrEmpty(TryGetInstallDirectory());

        /// <summary>
        /// Resolves the RTSS installation directory from uninstall registration and standard
        /// Windows installation roots. Returns null when RTSS cannot be found.
        /// </summary>
        public static String TryGetInstallDirectory()
        {
            if (CustomInstallDirectoryResolver != null)
            {
                return CustomInstallDirectoryResolver();
            }

            var fromEnvironment = TryReadEnvironmentInstallDirectory();

            if (!String.IsNullOrEmpty(fromEnvironment))
            {
                return fromEnvironment;
            }

            var fromRegistry = TryReadRegistryInstallDirectory();

            if (!String.IsNullOrEmpty(fromRegistry))
            {
                return fromRegistry;
            }

            foreach (var candidate in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RivaTuner Statistics Server"),
            })
            {
                if (!String.IsNullOrEmpty(candidate) && DirectoryExists(candidate) && FileExists(Path.Combine(candidate, ExecutableFileName)))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>Resolves {installDir}\Profiles, or null when RTSS cannot be found.</summary>
        public static String TryGetProfilesDirectory()
        {
            var installDirectory = TryGetInstallDirectory();

            return String.IsNullOrEmpty(installDirectory)
                ? null
                : Path.Combine(installDirectory, ProfilesFolderName);
        }

        private static String TryReadRegistryInstallDirectory()
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                {
                    try
                    {
                        using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                        {
                            using (var unwinderKey = baseKey.OpenSubKey(@"SOFTWARE\Unwinder\RTSS"))
                            {
                                var location = GetInstallDirectoryFromKey(unwinderKey);

                                if (!String.IsNullOrEmpty(location))
                                {
                                    return location;
                                }
                            }

                            using (var key = baseKey.OpenSubKey(UninstallKeyPath))
                            {
                                var location = GetInstallDirectoryFromKey(key);

                                if (!String.IsNullOrEmpty(location))
                                {
                                    return location;
                                }
                            }

                            using (var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                            {
                                foreach (var subKeyName in uninstall?.GetSubKeyNames() ?? Array.Empty<String>())
                                {
                                    using (var key = uninstall.OpenSubKey(subKeyName))
                                    {
                                        var displayName = key?.GetValue("DisplayName") as String;

                                        if (displayName?.IndexOf("RivaTuner Statistics Server", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            var location = GetInstallDirectoryFromKey(key);

                                            if (!String.IsNullOrEmpty(location))
                                            {
                                                return location;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        PluginLog.Warning(exception, $"Failed to read the RTSS install location from the {hive}/{view} registry view.");
                    }
                }
            }

            return null;
        }

        private static String TryReadEnvironmentInstallDirectory()
        {
            try
            {
                return NormalizeExistingDirectory(
                    Environment.GetEnvironmentVariable(InstallDirectoryEnvironmentVariable),
                    requireExecutable: false);
            }
            catch (Exception exception)
            {
                PluginLog.Warning(exception, $"Failed to read {InstallDirectoryEnvironmentVariable}.");
                return null;
            }
        }

        private static String GetInstallDirectoryFromKey(RegistryKey key)
        {
            if (key == null)
            {
                return null;
            }

            foreach (var valueName in new[] { "InstallPath", "InstallDir", "InstallLocation", "DisplayIcon", "UninstallString" })
            {
                var value = key.GetValue(valueName) as String;
                var directory = NormalizeExistingDirectory(value, requireExecutable: true);

                if (!String.IsNullOrEmpty(directory))
                {
                    return directory;
                }
            }

            return null;
        }

        internal static String NormalizeExistingDirectory(String value, Boolean requireExecutable = true)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var candidate = value.Trim().Trim('"');
            var comma = candidate.IndexOf(',');

            if (comma > 0)
            {
                candidate = candidate.Substring(0, comma).Trim().Trim('"');
            }

            if (File.Exists(candidate))
            {
                candidate = Path.GetDirectoryName(candidate);
            }

            if (!DirectoryExists(candidate))
            {
                return null;
            }

            if (requireExecutable && !FileExists(Path.Combine(candidate, ExecutableFileName)))
            {
                return null;
            }

            return candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static Boolean DirectoryExists(String path)
        {
            try
            {
                return Directory.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Boolean FileExists(String path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
