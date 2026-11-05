namespace Loupedeck.RTSSPerformancePlugin.Services
{
    using System;

    public interface ILimitingFactorService
    {
        /// <summary>Load at or above which a component is considered the limiting factor.</summary>
        Double SaturationThreshold { get; set; }

        /// <summary>Hint for the latest sensor reading, or null when neither component is saturated.</summary>
        String GetHint();
    }

    /// <summary>
    /// Decides whether the GPU or the CPU is the bottleneck, for the single-character badge
    /// drawn on the FPS key. Currently always returns null as sensor data is unavailable.
    /// </summary>
    public sealed class LimitingFactorService : ILimitingFactorService
    {
        /// <summary>Badge shown when the GPU is saturated.</summary>
        public const String GpuHint = "G";

        /// <summary>Badge shown when the CPU is saturated.</summary>
        public const String CpuHint = "C";

        public const Double DefaultSaturationThreshold = 90.0;

        public Double SaturationThreshold { get; set; } = DefaultSaturationThreshold;

        public LimitingFactorService()
        {
        }

        /// <summary>
        /// Returns "G" when GPU load is at or above 90%, otherwise "C" when CPU load is at or
        /// above 90%, otherwise null. The GPU is checked first, so a fully saturated system
        /// reports as GPU-limited. A NaN reading never satisfies the comparison and so is
        /// treated as not saturated.
        /// </summary>
        public static String GetHint(Double gpuUsage, Double cpuUsage) =>
            GetHint(gpuUsage, cpuUsage, DefaultSaturationThreshold);

        public static String GetHint(Double gpuUsage, Double cpuUsage, Double saturationThreshold)
        {
            if (gpuUsage >= saturationThreshold)
            {
                return GpuHint;
            }

            return cpuUsage >= saturationThreshold ? CpuHint : null;
        }

        /// <summary>
        /// Always returns null; no sensor source is available.
        /// </summary>
        public String GetHint()
        {
            return null;
        }
    }
}
