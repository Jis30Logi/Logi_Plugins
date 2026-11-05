namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;

    public enum ProfileHelperStatus
    {
        Success = 0,
        AlreadyExists = 1,
        RTSSUnavailable = 2,
        Error = 3,
        Timeout = 4,
        HelperNotFound = 5
    }

    public sealed class ProfileHelperResult
    {
        public ProfileHelperResult(ProfileHelperStatus status, String output, String error, Int32 exitCode = -1)
        {
            this.Status = status;
            this.Output = output ?? String.Empty;
            this.Error = error ?? String.Empty;
            this.ExitCode = exitCode;
        }

        public ProfileHelperStatus Status { get; }
        public String Output { get; }
        public String Error { get; }
        public Int32 ExitCode { get; }

        public Boolean IsSuccess => this.Status == ProfileHelperStatus.Success;
        public Boolean IsAlreadyExists => this.Status == ProfileHelperStatus.AlreadyExists;

        public static ProfileHelperResult Success(String output = "") =>
            new ProfileHelperResult(ProfileHelperStatus.Success, output, null, 0);

        public static ProfileHelperResult AlreadyExists(String output = "") =>
            new ProfileHelperResult(ProfileHelperStatus.AlreadyExists, output, null, 1);

        public static ProfileHelperResult RTSSUnavailable(String error = "") =>
            new ProfileHelperResult(ProfileHelperStatus.RTSSUnavailable, null, error, 2);

        public static ProfileHelperResult ErrorResult(String error = "", Int32 exitCode = 3) =>
            new ProfileHelperResult(ProfileHelperStatus.Error, null, error, exitCode);

        public static ProfileHelperResult TimeoutResult(String error = "Timed out") =>
            new ProfileHelperResult(ProfileHelperStatus.Timeout, null, error, -1);

        public static ProfileHelperResult NotFound(String error = "Helper executable not found") =>
            new ProfileHelperResult(ProfileHelperStatus.HelperNotFound, null, error, -1);
    }

    public interface IRTSSProfileHelperRunner
    {
        ProfileHelperResult RunAddProfile(String executableName);

        ProfileHelperResult RunSelectProfile(String profileName);

        ProfileHelperResult RunDeleteProfile(String profileName);

        ProfileHelperResult RunSetProperty(String profileName, String propertyName, UInt32 value);

        ProfileHelperResult RunGetProperty(String profileName, String propertyName, out UInt32 value);

        ProfileHelperResult RunLaunchOrFocus();

        ProfileHelperResult RunSetStartWithWindows(Boolean enable);
    }
}
