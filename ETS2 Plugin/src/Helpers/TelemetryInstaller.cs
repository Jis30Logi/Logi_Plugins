namespace Loupedeck.ETS2Plugin.Helpers
{
    using System;
    using System.IO;
    using System.Reflection;
    using Microsoft.Win32;

    /// <summary>
    /// Auto-installs the scs-telemetry.dll game plugin into the ETS2 plugins folder.
    /// The DLL is embedded in the plugin assembly and extracted on first load.
    /// </summary>
    public static class TelemetryInstaller
    {
        private const string DllName       = "scs-telemetry.dll";
        private const string GameFolderName = "Euro Truck Simulator 2";
        private const string PluginsSubPath = @"bin\win_x64\plugins";

        public static void Install(Assembly pluginAssembly)
        {
            try
            {
                PluginLog.Info($"TelemetryInstaller: Starting — assembly={pluginAssembly?.GetName().Name}");

                // Log all embedded resource names so we can confirm the DLL is present
                var resourceNames = pluginAssembly?.GetManifestResourceNames() ?? Array.Empty<string>();
                PluginLog.Info($"TelemetryInstaller: Embedded resources ({resourceNames.Length}): {string.Join(", ", resourceNames)}");

                var ets2Path = FindEts2Path();

                if (ets2Path == null)
                {
                    PluginLog.Warning("TelemetryInstaller: ETS2 not found — skipping auto-install. Check log above for paths tried.");
                    return;
                }

                PluginLog.Info($"TelemetryInstaller: ETS2 found at {ets2Path}");

                var pluginsDir = Path.Combine(ets2Path, PluginsSubPath);
                var targetDll  = Path.Combine(pluginsDir, DllName);

                if (File.Exists(targetDll))
                {
                    PluginLog.Info($"TelemetryInstaller: {DllName} already installed at {targetDll}");
                    return;
                }

                PluginLog.Info($"TelemetryInstaller: Plugins dir = {pluginsDir}");
                Directory.CreateDirectory(pluginsDir);

                // Find the embedded resource — try exact name first, then partial match
                var resourceName = FindResourceName(pluginAssembly, DllName);
                if (resourceName == null)
                {
                    PluginLog.Warning($"TelemetryInstaller: '{DllName}' not found in embedded resources. Available: {string.Join(", ", resourceNames)}");
                    return;
                }

                PluginLog.Info($"TelemetryInstaller: Extracting resource '{resourceName}' → {targetDll}");

                using var stream = pluginAssembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    PluginLog.Warning($"TelemetryInstaller: Stream for '{resourceName}' returned null.");
                    return;
                }

                using var file = File.Create(targetDll);
                stream.CopyTo(file);

                PluginLog.Info($"TelemetryInstaller: SUCCESS — installed to {targetDll}");
                PluginLog.Info("TelemetryInstaller: Please restart Euro Truck Simulator 2 to activate telemetry.");
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"TelemetryInstaller: Failed — {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Find the embedded resource by exact name or by suffix match (handles namespace prefixes)
        private static string FindResourceName(Assembly asm, string dllName)
        {
            if (asm == null) return null;

            foreach (var name in asm.GetManifestResourceNames())
            {
                if (name.Equals(dllName, StringComparison.OrdinalIgnoreCase))
                    return name;
                if (name.EndsWith("." + dllName, StringComparison.OrdinalIgnoreCase))
                    return name;
            }

            return null;
        }

        private static string FindEts2Path()
        {
            // 1. Steam registry — HKCU first (most common), then HKLM
            var steamPath = ReadRegistry(Registry.CurrentUser,  @"SOFTWARE\Valve\Steam",            "SteamPath")
                         ?? ReadRegistry(Registry.LocalMachine, @"SOFTWARE\Valve\Steam",            "SteamPath")
                         ?? ReadRegistry(Registry.LocalMachine, @"SOFTWARE\Wow6432Node\Valve\Steam", "SteamPath");

            if (steamPath != null)
            {
                PluginLog.Info($"TelemetryInstaller: Steam path = {steamPath}");
                var path = FindInSteamLibraries(steamPath);
                if (path != null) return path;
            }
            else
            {
                PluginLog.Info("TelemetryInstaller: Steam not found in registry.");
            }

            // 2. Common non-Steam install locations
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"C:\Games",
                @"D:\Games",
                @"E:\Games",
                @"D:\SteamLibrary",
                @"E:\SteamLibrary",
            })
            {
                var candidate = Path.Combine(root, GameFolderName);
                PluginLog.Info($"TelemetryInstaller: Trying {candidate}");
                if (IsValidEts2Path(candidate)) return candidate;
            }

            return null;
        }

        private static string FindInSteamLibraries(string steamPath)
        {
            steamPath = steamPath.Replace('/', Path.DirectorySeparatorChar);

            // Default Steam library
            var defaultLib = Path.Combine(steamPath, "steamapps", "common", GameFolderName);
            PluginLog.Info($"TelemetryInstaller: Trying default Steam lib: {defaultLib}");
            if (IsValidEts2Path(defaultLib)) return defaultLib;

            // Additional libraries from libraryfolders.vdf
            var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath))
            {
                PluginLog.Info($"TelemetryInstaller: libraryfolders.vdf not found at {vdfPath}");
                return null;
            }

            foreach (var line in File.ReadAllLines(vdfPath))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parts = trimmed.Split('"');
                if (parts.Length < 4) continue;

                var libPath  = parts[3].Replace("\\\\", "\\");
                var candidate = Path.Combine(libPath, "steamapps", "common", GameFolderName);
                PluginLog.Info($"TelemetryInstaller: Trying Steam lib: {candidate}");
                if (IsValidEts2Path(candidate)) return candidate;
            }

            return null;
        }

        private static bool IsValidEts2Path(string path)
        {
            var exe = Path.Combine(path, "bin", "win_x64", "eurotrucks2.exe");
            var valid = Directory.Exists(path) && File.Exists(exe);
            if (valid) PluginLog.Info($"TelemetryInstaller: Valid ETS2 path confirmed: {path}");
            return valid;
        }

        private static string ReadRegistry(RegistryKey hive, string subKey, string valueName)
        {
            try
            {
                using var key = hive.OpenSubKey(subKey);
                return key?.GetValue(valueName)?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
