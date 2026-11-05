namespace RTSSProfileHelper
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.IO.MemoryMappedFiles;
    using System.Runtime.InteropServices;
    using System.Threading;
    using Microsoft.Win32;

    internal static class Program
    {
        public const Int32 ExitSuccess = 0;
        public const Int32 ExitAlreadyExists = 1;
        public const Int32 ExitRTSSUnavailable = 2;
        public const Int32 ExitError = 3;

        private const String HooksLibrary64 = "RTSSHooks64.dll";
        private const String HooksLibrary32 = "RTSSHooks.dll";

        private const UInt32 WM_APP = 0x8000;
        private const UInt32 WM_RTSS_UPDATESETTINGS = WM_APP + 100;
        private const UInt32 WM_RTSS_TRAY = WM_APP + 1; // 0x8001: Verified RTSS tray callback message
        private const Int32 SW_RESTORE = 9;
        private static readonly IntPtr WM_LBUTTONUP = (IntPtr)0x0202;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr LoadLibrary(String lpFileName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, String lpProcName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(String lpClassName, String lpWindowName);

        [DllImport("user32.dll", EntryPoint = "FindWindowA", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr FindWindowA(String lpClassName, String lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern Boolean PostMessage(IntPtr hWnd, UInt32 msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern Boolean ShowWindow(IntPtr hWnd, Int32 nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern Boolean SetForegroundWindow(IntPtr hWnd);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private delegate void LoadProfileDelegate(String profile);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private delegate void SaveProfileDelegate(String profile);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private delegate void DeleteProfileDelegate(String profile);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private delegate Int32 GetProfilePropertyDelegate(String lpPropertyName, IntPtr lpPropertyData, UInt32 dwPropertySize);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private delegate Int32 SetProfilePropertyDelegate(String lpPropertyName, IntPtr lpPropertyData, UInt32 dwPropertySize);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private delegate UInt32 EnumProfilesDelegate(IntPtr lpProfilesList, UInt32 dwProfilesListSize);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void UpdateProfilesDelegate();

        private static Int32 Main(String[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("Usage: RTSSProfileHelper <launch|set-start-with-windows|add|select|delete|get-prop|set-prop> <args...>");
                return ExitError;
            }

            var command = args[0].Trim().ToLowerInvariant();
            switch (command)
            {
                case "launch":
                    return HandleLaunch();

                case "set-start-with-windows":
                    if (args.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: RTSSProfileHelper set-start-with-windows <0|1>");
                        return ExitError;
                    }
                    return HandleSetStartWithWindows(args[1]);

                case "add":
                    if (args.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: RTSSProfileHelper add <executable>");
                        return ExitError;
                    }
                    return HandleAdd(args[1]);

                case "select":
                    if (args.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: RTSSProfileHelper select <profile>");
                        return ExitError;
                    }
                    return HandleSelect(args[1]);

                case "delete":
                    if (args.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: RTSSProfileHelper delete <profile>");
                        return ExitError;
                    }
                    return HandleDelete(args[1]);

                case "get-prop":
                    if (args.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: RTSSProfileHelper get-prop <profile> <property>");
                        return ExitError;
                    }
                    return HandleGetProp(args[1], args[2]);

                case "set-prop":
                    if (args.Length < 4)
                    {
                        Console.Error.WriteLine("Usage: RTSSProfileHelper set-prop <profile> <property> <value>");
                        return ExitError;
                    }
                    return HandleSetProp(args[1], args[2], args[3]);

                default:
                    Console.Error.WriteLine($"Unknown command: {command}");
                    return ExitError;
            }
        }

        private static Int32 HandleLaunch()
        {
            try
            {
                if (IsRtssRunning())
                {
                    Console.WriteLine("[INFO] RTSS is already running. Restoring and focusing window.");
                    var hWnd = FindRtssWindow();
                    if (hWnd != IntPtr.Zero)
                    {
                        // Verified RTSS tray message to open UI: 0x8001 with WM_LBUTTONUP (0x0202)
                        PostMessage(hWnd, WM_RTSS_TRAY, IntPtr.Zero, WM_LBUTTONUP);
                        ShowWindow(hWnd, SW_RESTORE);
                        SetForegroundWindow(hWnd);
                    }

                    Console.WriteLine("[SUCCESS] RTSS window activated.");
                    return ExitSuccess;
                }

                var installDir = TryGetInstallDirectory();
                if (String.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                {
                    Console.Error.WriteLine("RTSS installation directory not found.");
                    return ExitRTSSUnavailable;
                }

                var exePath = Path.Combine(installDir, "RTSS.exe");
                if (!File.Exists(exePath))
                {
                    Console.Error.WriteLine($"RTSS executable not found at '{exePath}'.");
                    return ExitRTSSUnavailable;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                };

                var proc = Process.Start(psi);
                if (proc == null)
                {
                    Console.Error.WriteLine("Failed to launch RTSS process.");
                    return ExitError;
                }

                Console.WriteLine($"[SUCCESS] RTSS launched (PID={proc.Id}).");
                return ExitSuccess;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EXCEPTION] Exception during RTSS launch: {ex.Message}");
                return ExitError;
            }
        }

        private static Int32 HandleSetStartWithWindows(String arg)
        {
            try
            {
                var trimmed = arg?.Trim();
                var enable = String.Equals(trimmed, "1", StringComparison.OrdinalIgnoreCase) ||
                             String.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) ||
                             String.Equals(trimmed, "on", StringComparison.OrdinalIgnoreCase);

                var installDir = TryGetInstallDirectory();
                if (String.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                {
                    Console.Error.WriteLine("RTSS installation directory not found.");
                    return ExitRTSSUnavailable;
                }

                var profilesDir = Path.Combine(installDir, "Profiles");
                var configPath = Path.Combine(profilesDir, "Config");
                if (File.Exists(configPath))
                {
                    UpdateConfigFileSetting(configPath, "Settings", "StartWithWindows", enable ? "1" : "0");
                }

                // Register in HKCU\Software\Microsoft\Windows\CurrentVersion\Run
                try
                {
                    using (var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
                    {
                        if (runKey != null)
                        {
                            if (enable)
                            {
                                var exePath = Path.Combine(installDir, "RTSS.exe");
                                var runValue = $"\"{exePath}\" /s";
                                runKey.SetValue("RTSS", runValue);
                            }
                            else
                            {
                                runKey.DeleteValue("RTSS", throwOnMissingValue: false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WARNING] Failed to update HKCU Run key: {ex.Message}");
                }

                // Notify RTSS so in-memory state and UI update immediately
                NotifyRtssGui();

                Console.WriteLine($"[SUCCESS] StartWithWindows={(enable ? 1 : 0)}");
                return ExitSuccess;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EXCEPTION] Exception setting StartWithWindows: {ex.Message}");
                return ExitError;
            }
        }

        private static Int32 HandleAdd(String rawName)
        {
            var targetExe = Path.GetFileName(rawName.Trim());

            if (String.IsNullOrWhiteSpace(targetExe))
            {
                Console.Error.WriteLine("Executable name cannot be empty.");
                return ExitError;
            }

            if (!targetExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"File '{targetExe}' does not have a .exe extension.");
                return ExitError;
            }

            if (String.Equals(targetExe, "Global", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(targetExe, "Global.exe", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(targetExe, "Global.cfg", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Cannot create profile with reserved name 'Global'.");
                return ExitError;
            }

            // Locate RTSS install
            var installDir = TryGetInstallDirectory();
            if (String.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
            {
                Console.Error.WriteLine("RTSS installation directory not found.");
                return ExitRTSSUnavailable;
            }

            // Confirm RTSS is running
            if (!IsRtssRunning())
            {
                Console.Error.WriteLine("RTSS is not currently running.");
                return ExitRTSSUnavailable;
            }

            var profilesDir = Path.Combine(installDir, "Profiles");
            var profileConfigPath = Path.Combine(profilesDir, $"{targetExe}.cfg");

            // Check if profile already exists on disk
            if (File.Exists(profileConfigPath))
            {
                Console.WriteLine($"[ALREADY_EXISTS] Profile '{targetExe}' already exists at '{profileConfigPath}'.");
                return ExitAlreadyExists;
            }

            // Locate RTSSHooks DLL
            var libraryName = Environment.Is64BitProcess ? HooksLibrary64 : HooksLibrary32;
            var hooksDllPath = Path.Combine(installDir, libraryName);
            if (!File.Exists(hooksDllPath))
            {
                Console.Error.WriteLine($"Hooks library not found at '{hooksDllPath}'.");
                return ExitRTSSUnavailable;
            }

            // Load RTSSHooks library
            var hModule = LoadLibrary(hooksDllPath);
            if (hModule == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                Console.Error.WriteLine($"Failed to load '{hooksDllPath}', error {error}.");
                return ExitRTSSUnavailable;
            }

            var pLoadProfile = GetProcAddress(hModule, "LoadProfile");
            var pSaveProfile = GetProcAddress(hModule, "SaveProfile");
            var pUpdateProfiles = GetProcAddress(hModule, "UpdateProfiles");

            if (pLoadProfile == IntPtr.Zero || pSaveProfile == IntPtr.Zero || pUpdateProfiles == IntPtr.Zero)
            {
                Console.Error.WriteLine("Required profile API exports missing from hooks library.");
                return ExitError;
            }

            var loadProfile = Marshal.GetDelegateForFunctionPointer<LoadProfileDelegate>(pLoadProfile);
            var saveProfile = Marshal.GetDelegateForFunctionPointer<SaveProfileDelegate>(pSaveProfile);
            var updateProfiles = Marshal.GetDelegateForFunctionPointer<UpdateProfilesDelegate>(pUpdateProfiles);

            try
            {
                // Official SDK pattern (see RTSSProfileInterface.h in RTSS SDK samples):
                // 1. Load settings from global profile template (empty string)
                loadProfile(String.Empty);

                // 2. Save settings into application-specific profile
                saveProfile(targetExe);

                // 3. Force all running 3D applications and RTSS runtime to reload profiles
                updateProfiles();

                // 4. Notify RTSS main GUI window
                NotifyRtssGui();

                // 5. Verification
                Boolean verified = false;
                for (Int32 i = 0; i < 20; i++)
                {
                    if (File.Exists(profileConfigPath))
                    {
                        verified = true;
                        break;
                    }
                    Thread.Sleep(50);
                }

                // If SaveProfile in hooks didn't write to disk (e.g. if RTSS buffers until next action),
                // ensure the file is created from Global template as fallback so RTSS reads it on reload.
                if (!verified)
                {
                    var globalPath = Path.Combine(profilesDir, "Global");
                    if (File.Exists(globalPath))
                    {
                        File.Copy(globalPath, profileConfigPath, false);
                    }
                    else
                    {
                        File.WriteAllText(profileConfigPath, "");
                    }

                    // Re-signal RTSS to reload the new file
                    loadProfile(targetExe);
                    updateProfiles();
                    NotifyRtssGui();

                    verified = File.Exists(profileConfigPath);
                }

                if (verified)
                {
                    Console.WriteLine($"[SUCCESS] Profile '{targetExe}' created and verified.");
                    return ExitSuccess;
                }
                else
                {
                    Console.Error.WriteLine($"[ERROR] Profile file '{profileConfigPath}' was not created.");
                    return ExitError;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EXCEPTION] Exception during profile creation: {ex.Message}");
                return ExitError;
            }
        }

        private static Int32 HandleSelect(String rawName)
        {
            var trimmed = rawName?.Trim() ?? String.Empty;

            if (trimmed.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 4).Trim();
            }

            var isGlobal = String.IsNullOrWhiteSpace(trimmed) ||
                           String.Equals(trimmed, "Global", StringComparison.OrdinalIgnoreCase) ||
                           String.Equals(trimmed, "Global.exe", StringComparison.OrdinalIgnoreCase);

            var targetName = isGlobal ? "Global" : Path.GetFileName(trimmed);
            var sdkProfileArg = isGlobal ? String.Empty : targetName;

            // Confirm RTSS is running
            if (!IsRtssRunning())
            {
                Console.Error.WriteLine($"[ERROR] RequestedProfile={targetName};Verified=false;Reason=RTSSNotRunning");
                return ExitRTSSUnavailable;
            }

            // Locate RTSS install
            var installDir = TryGetInstallDirectory();
            if (String.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
            {
                Console.Error.WriteLine($"[ERROR] RequestedProfile={targetName};Verified=false;Reason=RTSSDirectoryNotFound");
                return ExitRTSSUnavailable;
            }

            var profilesDir = Path.Combine(installDir, "Profiles");
            var profileConfigPath = isGlobal
                ? Path.Combine(profilesDir, "Global")
                : Path.Combine(profilesDir, $"{targetName}.cfg");

            if (isGlobal && !File.Exists(profileConfigPath))
            {
                var cfgGlobal = Path.Combine(profilesDir, "Global.cfg");
                if (File.Exists(cfgGlobal))
                {
                    profileConfigPath = cfgGlobal;
                }
            }

            if (!File.Exists(profileConfigPath))
            {
                Console.Error.WriteLine($"[ERROR] RequestedProfile={targetName};Verified=false;Reason=ProfileFileNotFound");
                return ExitError;
            }

            // Locate RTSSHooks DLL
            var libraryName = Environment.Is64BitProcess ? HooksLibrary64 : HooksLibrary32;
            var hooksDllPath = Path.Combine(installDir, libraryName);
            if (!File.Exists(hooksDllPath))
            {
                Console.Error.WriteLine($"[ERROR] RequestedProfile={targetName};Verified=false;Reason=HooksLibraryNotFound");
                return ExitRTSSUnavailable;
            }

            // Load RTSSHooks library
            var hModule = LoadLibrary(hooksDllPath);
            if (hModule == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                Console.Error.WriteLine($"[ERROR] RequestedProfile={targetName};Verified=false;Reason=LoadLibraryFailed_Error{error}");
                return ExitRTSSUnavailable;
            }

            var pLoadProfile = GetProcAddress(hModule, "LoadProfile");
            var pUpdateProfiles = GetProcAddress(hModule, "UpdateProfiles");
            var pGetProfileProperty = GetProcAddress(hModule, "GetProfileProperty");

            if (pLoadProfile == IntPtr.Zero || pUpdateProfiles == IntPtr.Zero || pGetProfileProperty == IntPtr.Zero)
            {
                Console.Error.WriteLine($"[ERROR] RequestedProfile={targetName};Verified=false;Reason=RequiredExportsMissing");
                return ExitError;
            }

            var loadProfile = Marshal.GetDelegateForFunctionPointer<LoadProfileDelegate>(pLoadProfile);
            var updateProfiles = Marshal.GetDelegateForFunctionPointer<UpdateProfilesDelegate>(pUpdateProfiles);
            var getProfileProperty = Marshal.GetDelegateForFunctionPointer<GetProfilePropertyDelegate>(pGetProfileProperty);

            try
            {
                // Official SDK pattern:
                // 1. Load specified profile (empty string for Global) into RTSS hooks context
                loadProfile(sdkProfileArg);

                // 2. Verify RTSS profile interface recognized and loaded profile in memory
                var propBuffer = Marshal.AllocHGlobal(sizeof(UInt32));
                Boolean sdkVerified = false;
                try
                {
                    Marshal.WriteInt32(propBuffer, unchecked((Int32)0xDEADBEEF));
                    var probeResult = getProfileProperty("AppDetectionLevel", propBuffer, sizeof(UInt32));
                    if (probeResult != 0)
                    {
                        sdkVerified = true;
                    }
                    else
                    {
                        // Secondary probe on FramerateLimit
                        Marshal.WriteInt32(propBuffer, unchecked((Int32)0xDEADBEEF));
                        probeResult = getProfileProperty("FramerateLimit", propBuffer, sizeof(UInt32));
                        if (probeResult != 0)
                        {
                            sdkVerified = true;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(propBuffer);
                }

                if (!sdkVerified)
                {
                    Console.Error.WriteLine($"[ERROR] RequestedProfile={targetName};Verified=false;Reason=SDKLoadProfileVerificationFailed");
                    return ExitError;
                }

                // 3. Broadcast profile reload to running 3D applications and RTSS runtime
                updateProfiles();

                // 4. Targeted RTSS window notification (no HWND_BROADCAST)
                NotifyRtssGuiTargeted();

                // 5. Structured success output
                Console.WriteLine($"[SUCCESS] RequestedProfile={targetName};LoadedProfile={targetName};Verified=true;VerificationType=SDKLoadProfileAndRuntimeUpdate");
                return ExitSuccess;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] RequestedProfile={targetName};Verified=false;Reason={ex.Message}");
                return ExitError;
            }
        }

        private static Int32 HandleDelete(String rawName)
        {
            if (String.IsNullOrWhiteSpace(rawName))
            {
                Console.Error.WriteLine("Profile name cannot be empty.");
                return ExitError;
            }

            var trimmed = rawName.Trim();

            if (trimmed.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 4).Trim();
            }

            // Reject path separators or traversal
            if (trimmed.IndexOf('/') >= 0 ||
                trimmed.IndexOf('\\') >= 0 ||
                trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                trimmed == "." ||
                trimmed == "..")
            {
                Console.Error.WriteLine($"Invalid profile name containing path separators or traversal: '{rawName}'.");
                return ExitError;
            }

            var targetExe = Path.GetFileName(trimmed);
            if (String.IsNullOrWhiteSpace(targetExe))
            {
                Console.Error.WriteLine("Profile name is invalid.");
                return ExitError;
            }

            // CRITICAL: Global protection at EVERY layer!
            if (String.Equals(targetExe, "Global", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(targetExe, "Global.exe", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(targetExe, "Global.cfg", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Cannot delete protected Global profile.");
                return ExitError;
            }

            if (!targetExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Application profile '{targetExe}' must end with .exe.");
                return ExitError;
            }

            if (String.Equals(targetExe, "Config.exe", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(targetExe, "EncoderServer.exe", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(targetExe, "SaveMedia.exe", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Cannot delete reserved profile '{targetExe}'.");
                return ExitError;
            }

            // Locate RTSS install
            var installDir = TryGetInstallDirectory();
            if (String.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
            {
                Console.Error.WriteLine("RTSS installation directory not found.");
                return ExitRTSSUnavailable;
            }

            // Confirm RTSS is running
            if (!IsRtssRunning())
            {
                Console.Error.WriteLine("RTSS is not currently running.");
                return ExitRTSSUnavailable;
            }

            var profilesDir = Path.Combine(installDir, "Profiles");
            var profileConfigPath = Path.Combine(profilesDir, $"{targetExe}.cfg");

            if (!File.Exists(profileConfigPath))
            {
                Console.Error.WriteLine($"Profile file '{profileConfigPath}' does not exist.");
                return ExitError;
            }

            // Locate RTSSHooks DLL
            var libraryName = Environment.Is64BitProcess ? HooksLibrary64 : HooksLibrary32;
            var hooksDllPath = Path.Combine(installDir, libraryName);
            if (!File.Exists(hooksDllPath))
            {
                Console.Error.WriteLine($"Hooks library not found at '{hooksDllPath}'.");
                return ExitRTSSUnavailable;
            }

            // Load RTSSHooks library
            var hModule = LoadLibrary(hooksDllPath);
            if (hModule == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                Console.Error.WriteLine($"Failed to load '{hooksDllPath}', error {error}.");
                return ExitRTSSUnavailable;
            }

            var pDeleteProfile = GetProcAddress(hModule, "DeleteProfile");
            var pUpdateProfiles = GetProcAddress(hModule, "UpdateProfiles");
            var pEnumProfiles = GetProcAddress(hModule, "EnumProfiles");

            if (pDeleteProfile == IntPtr.Zero || pUpdateProfiles == IntPtr.Zero)
            {
                Console.Error.WriteLine("Required profile API exports (DeleteProfile/UpdateProfiles) missing from hooks library.");
                return ExitError;
            }

            var deleteProfile = Marshal.GetDelegateForFunctionPointer<DeleteProfileDelegate>(pDeleteProfile);
            var updateProfiles = Marshal.GetDelegateForFunctionPointer<UpdateProfilesDelegate>(pUpdateProfiles);
            EnumProfilesDelegate enumProfiles = pEnumProfiles != IntPtr.Zero
                ? Marshal.GetDelegateForFunctionPointer<EnumProfilesDelegate>(pEnumProfiles)
                : null;

            try
            {
                // 1. Delete application profile through native RTSS SDK export
                deleteProfile(targetExe);

                // 2. Refresh RTSS profile subsystem
                updateProfiles();

                // 3. Notify RTSS GUI
                NotifyRtssGui();

                // 4. Ensure disk file is removed if DeleteProfile didn't unlink it directly
                if (File.Exists(profileConfigPath))
                {
                    try
                    {
                        File.Delete(profileConfigPath);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Warning deleting file '{profileConfigPath}': {ex.Message}");
                    }
                }

                // 5. Verify deletion from RTSS itself
                Boolean isStillInEnum = false;
                if (enumProfiles != null)
                {
                    isStillInEnum = IsProfileInRtssEnum(enumProfiles, targetExe);
                }

                Boolean fileStillExists = File.Exists(profileConfigPath);

                if (isStillInEnum || fileStillExists)
                {
                    Console.Error.WriteLine($"[ERROR] Profile '{targetExe}' verification failed: stillInEnum={isStillInEnum}, fileExists={fileStillExists}.");
                    return ExitError;
                }

                Console.WriteLine($"[SUCCESS] Profile '{targetExe}' deleted and verified.");
                return ExitSuccess;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EXCEPTION] Exception during profile deletion: {ex.Message}");
                return ExitError;
            }
        }

        private static Boolean IsProfileInRtssEnum(EnumProfilesDelegate enumProfiles, String targetExe)
        {
            if (enumProfiles == null)
            {
                return false;
            }

            try
            {
                var bufferSize = 8192u;
                var buffer = Marshal.AllocHGlobal((Int32)bufferSize);
                try
                {
                    var reqSize = enumProfiles(buffer, bufferSize);
                    if (reqSize > bufferSize)
                    {
                        Marshal.FreeHGlobal(buffer);
                        buffer = Marshal.AllocHGlobal((Int32)reqSize);
                        enumProfiles(buffer, reqSize);
                    }

                    var listStr = Marshal.PtrToStringAnsi(buffer);
                    if (String.IsNullOrEmpty(listStr))
                    {
                        return false;
                    }

                    var items = listStr.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var item in items)
                    {
                        if (String.Equals(item.Trim(), targetExe, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    return false;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                return false;
            }
        }

        private static IntPtr FindRtssWindow()
        {
            try
            {
                var hWnd = FindWindowA(null, "RTSS");
                if (hWnd == IntPtr.Zero)
                {
                    hWnd = FindWindowA(null, "RivaTunerStatisticsServer");
                }
                if (hWnd == IntPtr.Zero)
                {
                    hWnd = FindWindowA("WND_MAIN", null);
                }
                if (hWnd == IntPtr.Zero)
                {
                    hWnd = FindWindow(null, "RTSS");
                }
                if (hWnd == IntPtr.Zero)
                {
                    hWnd = FindWindow(null, "RivaTuner Statistics Server");
                }
                return hWnd;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static void NotifyRtssGuiTargeted()
        {
            try
            {
                var hWnd = FindRtssWindow();
                if (hWnd != IntPtr.Zero)
                {
                    PostMessage(hWnd, WM_RTSS_UPDATESETTINGS, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch
            {
                // Best-effort
            }
        }

        private static void NotifyRtssGui()
        {
            // For other flows, call targeted notification first
            NotifyRtssGuiTargeted();
        }

        private static Boolean IsRtssRunning()
        {
            try
            {
                // Cheap preliminary signal via shared memory (<0.01ms)
                try
                {
                    using (var mmf = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", MemoryMappedFileRights.Read))
                    {
                        return true;
                    }
                }
                catch (FileNotFoundException)
                {
                    // Fall through
                }
                catch
                {
                    // Fall through
                }

                var hWnd = FindRtssWindow();
                if (hWnd != IntPtr.Zero)
                {
                    return true;
                }

                // Fallback to process check only if window and shared memory check failed
                var processes = Process.GetProcessesByName("RTSS");
                return processes.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static String TryGetInstallDirectory()
        {
            try
            {
                var fromEnvironment = Environment.GetEnvironmentVariable("RTSS_INSTALL_DIR");
                if (!String.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment) && File.Exists(Path.Combine(fromEnvironment, "RTSS.exe")))
                {
                    return fromEnvironment.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }

                foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                {
                    foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    {
                        using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                        {
                            foreach (var keyPath in new[]
                            {
                                @"SOFTWARE\Unwinder\RTSS",
                                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RivaTuner Statistics Server_is1"
                            })
                            using (var key = baseKey.OpenSubKey(keyPath))
                            {
                                foreach (var valueName in new[] { "InstallPath", "InstallDir", "InstallLocation", "DisplayIcon", "UninstallString" })
                                {
                                    var rawPath = key?.GetValue(valueName) as String;
                                    var path = NormalizeInstallPath(rawPath);
                                    if (!String.IsNullOrEmpty(path) && Directory.Exists(path) && File.Exists(Path.Combine(path, "RTSS.exe")))
                                    {
                                        return path;
                                    }
                                }
                            }
                        }
                    }
                }

                foreach (var root in new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                })
                {
                    var candidate = String.IsNullOrEmpty(root)
                        ? null
                        : Path.Combine(root, "RivaTuner Statistics Server");
                    if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "RTSS.exe")))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                // Ignored
            }

            return null;
        }

        private static String NormalizeInstallPath(String value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var path = value.Trim().Trim('"');
            var comma = path.IndexOf(',');
            if (comma > 0)
            {
                path = path.Substring(0, comma).Trim().Trim('"');
            }

            if (File.Exists(path))
            {
                path = Path.GetDirectoryName(path);
            }

            if (String.IsNullOrEmpty(path) || !Directory.Exists(path) || !File.Exists(Path.Combine(path, "RTSS.exe")))
            {
                return null;
            }

            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static Boolean TryNormalizePropertyName(String rawProp, out String canonicalProp)
        {
            canonicalProp = null;
            if (String.IsNullOrWhiteSpace(rawProp))
            {
                return false;
            }

            var trimmed = rawProp.Trim();
            if (String.Equals(trimmed, "EnableOSD", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "OSD", StringComparison.OrdinalIgnoreCase))
            {
                canonicalProp = "EnableOSD";
                return true;
            }

            if (String.Equals(trimmed, "AppDetectionLevel", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "HookLevel", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "DetectionLevel", StringComparison.OrdinalIgnoreCase))
            {
                canonicalProp = "AppDetectionLevel";
                return true;
            }

            if (String.Equals(trimmed, "EnableFloatingInjectionAddress", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "StealthMode", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "Stealth", StringComparison.OrdinalIgnoreCase))
            {
                canonicalProp = "EnableFloatingInjectionAddress";
                return true;
            }

            if (String.Equals(trimmed, "FramerateLimit", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "Limit", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "FpsLimit", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(trimmed, "FpsCap", StringComparison.OrdinalIgnoreCase))
            {
                canonicalProp = "FramerateLimit";
                return true;
            }

            return false;
        }

        private static Boolean TryValidatePropertyValue(String canonicalProp, String rawVal, out UInt32 val)
        {
            val = 0;
            if (String.IsNullOrWhiteSpace(rawVal) || !UInt32.TryParse(rawVal.Trim(), out val))
            {
                return false;
            }

            switch (canonicalProp)
            {
                case "EnableOSD":
                    return val <= 1;

                case "AppDetectionLevel":
                    return val <= 3;

                case "EnableFloatingInjectionAddress":
                    return val <= 1;

                case "FramerateLimit":
                    return val <= 1000;

                default:
                    return false;
            }
        }

        private static Boolean ResolveProfileArgs(
            String rawProfile,
            String profilesDir,
            out String targetName,
            out String sdkProfileArg,
            out String profileConfigPath)
        {
            var trimmed = rawProfile?.Trim() ?? String.Empty;
            if (trimmed.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 4).Trim();
            }

            var isGlobal = String.IsNullOrWhiteSpace(trimmed) ||
                           String.Equals(trimmed, "Global", StringComparison.OrdinalIgnoreCase) ||
                           String.Equals(trimmed, "Global.exe", StringComparison.OrdinalIgnoreCase) ||
                           String.Equals(trimmed, "Global.cfg", StringComparison.OrdinalIgnoreCase);

            if (isGlobal)
            {
                targetName = "Global";
                sdkProfileArg = String.Empty;
                profileConfigPath = Path.Combine(profilesDir, "Global");
                if (!File.Exists(profileConfigPath))
                {
                    var cfgGlobal = Path.Combine(profilesDir, "Global.cfg");
                    if (File.Exists(cfgGlobal))
                    {
                        profileConfigPath = cfgGlobal;
                    }
                }
                return true;
            }

            if (trimmed.IndexOf('/') >= 0 ||
                trimmed.IndexOf('\\') >= 0 ||
                trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                trimmed == "." ||
                trimmed == "..")
            {
                targetName = null;
                sdkProfileArg = null;
                profileConfigPath = null;
                return false;
            }

            targetName = Path.GetFileName(trimmed);
            if (!targetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                targetName += ".exe";
            }

            sdkProfileArg = targetName;
            profileConfigPath = Path.Combine(profilesDir, $"{targetName}.cfg");
            return true;
        }

        private static void UpdateConfigFileProperty(String filePath, String canonicalProp, UInt32 value)
        {
            try
            {
                if (String.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    return;
                }

                String section;
                String key;
                String strValue = value.ToString();

                switch (canonicalProp)
                {
                    case "EnableOSD":
                        section = "OSD";
                        key = "EnableOSD";
                        break;

                    case "AppDetectionLevel":
                        section = "Hooking";
                        key = "HookLevel";
                        break;

                    case "EnableFloatingInjectionAddress":
                        section = "Hooking";
                        key = "EnableFloatingInjectionAddress";
                        break;

                    case "FramerateLimit":
                        section = "Framerate";
                        key = "Limit";
                        break;

                    default:
                        return;
                }

                UpdateConfigFileSetting(filePath, section, key, strValue);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning updating config file '{filePath}': {ex.Message}");
            }
        }

        private static void UpdateConfigFileSetting(String filePath, String section, String key, String strValue)
        {
            try
            {
                if (String.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    return;
                }

                var lines = new System.Collections.Generic.List<String>(File.ReadAllLines(filePath));
                var sectionIndex = -1;
                var keyIndex = -1;
                var inSection = false;

                for (int i = 0; i < lines.Count; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        var sec = trimmed.Substring(1, trimmed.Length - 2).Trim();
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

                    if (inSection)
                    {
                        var eq = trimmed.IndexOf('=');
                        if (eq > 0)
                        {
                            var k = trimmed.Substring(0, eq).Trim();
                            if (String.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                            {
                                keyIndex = i;
                                break;
                            }
                        }
                    }
                }

                var newLine = $"{key}={strValue}";
                if (keyIndex >= 0)
                {
                    lines[keyIndex] = newLine;
                }
                else if (sectionIndex >= 0)
                {
                    lines.Insert(sectionIndex + 1, newLine);
                }
                else
                {
                    lines.Add($"[{section}]");
                    lines.Add(newLine);
                }

                var tempPath = filePath + ".tmp." + Guid.NewGuid().ToString("N");
                File.WriteAllLines(tempPath, lines);
                File.Move(tempPath, filePath, true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning updating config file '{filePath}': {ex.Message}");
            }
        }

        private static Int32 HandleGetProp(String rawProfile, String rawProp)
        {
            if (!TryNormalizePropertyName(rawProp, out var canonicalProp))
            {
                Console.Error.WriteLine($"Unsupported or invalid property name: '{rawProp}'.");
                return ExitError;
            }

            var installDir = TryGetInstallDirectory();
            if (String.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
            {
                Console.Error.WriteLine("RTSS installation directory not found.");
                return ExitRTSSUnavailable;
            }

            if (!IsRtssRunning())
            {
                Console.Error.WriteLine("RTSS is not currently running.");
                return ExitRTSSUnavailable;
            }

            var profilesDir = Path.Combine(installDir, "Profiles");
            if (!ResolveProfileArgs(rawProfile, profilesDir, out var targetName, out var sdkProfileArg, out var profileConfigPath))
            {
                Console.Error.WriteLine($"Invalid profile name: '{rawProfile}'.");
                return ExitError;
            }

            var libraryName = Environment.Is64BitProcess ? HooksLibrary64 : HooksLibrary32;
            var hooksDllPath = Path.Combine(installDir, libraryName);
            if (!File.Exists(hooksDllPath))
            {
                Console.Error.WriteLine($"Hooks library not found at '{hooksDllPath}'.");
                return ExitRTSSUnavailable;
            }

            var hModule = LoadLibrary(hooksDllPath);
            if (hModule == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                Console.Error.WriteLine($"Failed to load '{hooksDllPath}', error {error}.");
                return ExitRTSSUnavailable;
            }

            var pLoadProfile = GetProcAddress(hModule, "LoadProfile");
            var pGetProfileProperty = GetProcAddress(hModule, "GetProfileProperty");

            if (pLoadProfile == IntPtr.Zero || pGetProfileProperty == IntPtr.Zero)
            {
                Console.Error.WriteLine("Required profile API exports missing from hooks library.");
                return ExitError;
            }

            var loadProfile = Marshal.GetDelegateForFunctionPointer<LoadProfileDelegate>(pLoadProfile);
            var getProfileProperty = Marshal.GetDelegateForFunctionPointer<GetProfilePropertyDelegate>(pGetProfileProperty);

            try
            {
                loadProfile(sdkProfileArg);

                var pData = Marshal.AllocHGlobal(sizeof(UInt32));
                try
                {
                    Marshal.WriteInt32(pData, 0);
                    var result = getProfileProperty(canonicalProp, pData, sizeof(UInt32));
                    if (result == 0)
                    {
                        Console.Error.WriteLine($"[ERROR] GetProfileProperty returned FALSE for '{canonicalProp}'.");
                        return ExitError;
                    }

                    var val = (UInt32)Marshal.ReadInt32(pData);
                    Console.WriteLine($"[SUCCESS] {val}");
                    return ExitSuccess;
                }
                finally
                {
                    Marshal.FreeHGlobal(pData);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EXCEPTION] Exception reading property '{canonicalProp}': {ex.Message}");
                return ExitError;
            }
        }

        private static Int32 HandleSetProp(String rawProfile, String rawProp, String rawVal)
        {
            if (!TryNormalizePropertyName(rawProp, out var canonicalProp))
            {
                Console.Error.WriteLine($"Unsupported or invalid property name: '{rawProp}'.");
                return ExitError;
            }

            if (!TryValidatePropertyValue(canonicalProp, rawVal, out var val))
            {
                Console.Error.WriteLine($"Invalid value '{rawVal}' for property '{canonicalProp}'.");
                return ExitError;
            }

            var installDir = TryGetInstallDirectory();
            if (String.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
            {
                Console.Error.WriteLine("RTSS installation directory not found.");
                return ExitRTSSUnavailable;
            }

            if (!IsRtssRunning())
            {
                Console.Error.WriteLine("RTSS is not currently running.");
                return ExitRTSSUnavailable;
            }

            var profilesDir = Path.Combine(installDir, "Profiles");
            if (!ResolveProfileArgs(rawProfile, profilesDir, out var targetName, out var sdkProfileArg, out var profileConfigPath))
            {
                Console.Error.WriteLine($"Invalid profile name: '{rawProfile}'.");
                return ExitError;
            }

            var libraryName = Environment.Is64BitProcess ? HooksLibrary64 : HooksLibrary32;
            var hooksDllPath = Path.Combine(installDir, libraryName);
            if (!File.Exists(hooksDllPath))
            {
                Console.Error.WriteLine($"Hooks library not found at '{hooksDllPath}'.");
                return ExitRTSSUnavailable;
            }

            var hModule = LoadLibrary(hooksDllPath);
            if (hModule == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                Console.Error.WriteLine($"Failed to load '{hooksDllPath}', error {error}.");
                return ExitRTSSUnavailable;
            }

            var pLoadProfile = GetProcAddress(hModule, "LoadProfile");
            var pSaveProfile = GetProcAddress(hModule, "SaveProfile");
            var pSetProfileProperty = GetProcAddress(hModule, "SetProfileProperty");
            var pGetProfileProperty = GetProcAddress(hModule, "GetProfileProperty");
            var pUpdateProfiles = GetProcAddress(hModule, "UpdateProfiles");

            if (pLoadProfile == IntPtr.Zero ||
                pSaveProfile == IntPtr.Zero ||
                pSetProfileProperty == IntPtr.Zero ||
                pGetProfileProperty == IntPtr.Zero ||
                pUpdateProfiles == IntPtr.Zero)
            {
                Console.Error.WriteLine("Required profile API exports missing from hooks library.");
                return ExitError;
            }

            var loadProfile = Marshal.GetDelegateForFunctionPointer<LoadProfileDelegate>(pLoadProfile);
            var saveProfile = Marshal.GetDelegateForFunctionPointer<SaveProfileDelegate>(pSaveProfile);
            var setProfileProperty = Marshal.GetDelegateForFunctionPointer<SetProfilePropertyDelegate>(pSetProfileProperty);
            var getProfileProperty = Marshal.GetDelegateForFunctionPointer<GetProfilePropertyDelegate>(pGetProfileProperty);
            var updateProfiles = Marshal.GetDelegateForFunctionPointer<UpdateProfilesDelegate>(pUpdateProfiles);

            try
            {
                // 1. Load profile into RTSS hooks context
                loadProfile(sdkProfileArg);

                // 2. Set profile property
                var pData = Marshal.AllocHGlobal(sizeof(UInt32));
                try
                {
                    Marshal.WriteInt32(pData, (Int32)val);
                    var setResult = setProfileProperty(canonicalProp, pData, sizeof(UInt32));
                    if (setResult == 0)
                    {
                        Console.Error.WriteLine($"[ERROR] SetProfileProperty failed for '{canonicalProp}'.");
                        return ExitError;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pData);
                }

                // 3. Save profile through RTSS SDK
                saveProfile(sdkProfileArg);

                // 4. Force all hooked applications and runtime to update
                updateProfiles();

                // 5. Notify RTSS GUI
                NotifyRtssGui();

                // 6. Read-back verification from RTSS memory
                var pVerify = Marshal.AllocHGlobal(sizeof(UInt32));
                UInt32 readVal = 0;
                try
                {
                    Marshal.WriteInt32(pVerify, unchecked((Int32)0xDEADBEEF));
                    var getResult = getProfileProperty(canonicalProp, pVerify, sizeof(UInt32));
                    if (getResult == 0)
                    {
                        Console.Error.WriteLine($"[ERROR] Read-back GetProfileProperty failed for '{canonicalProp}'.");
                        return ExitError;
                    }
                    readVal = (UInt32)Marshal.ReadInt32(pVerify);
                }
                finally
                {
                    Marshal.FreeHGlobal(pVerify);
                }

                if (readVal != val)
                {
                    Console.Error.WriteLine($"[ERROR] Read-back mismatch for '{canonicalProp}': expected {val}, read {readVal}.");
                    return ExitError;
                }

                // 7. Synchronize config file on disk
                UpdateConfigFileProperty(profileConfigPath, canonicalProp, readVal);

                Console.WriteLine($"[SUCCESS] {canonicalProp}={readVal}");
                return ExitSuccess;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EXCEPTION] Exception setting property '{canonicalProp}': {ex.Message}");
                return ExitError;
            }
        }
    }
}
