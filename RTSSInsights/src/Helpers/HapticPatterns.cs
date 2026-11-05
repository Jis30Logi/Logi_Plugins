namespace Loupedeck.RTSSPerformancePlugin.Helpers
{
    using System;

    /// <summary>
    /// Named haptic feedback patterns used to confirm preset writes and health transitions.
    /// </summary>
    public static class HapticPatterns
    {
        public enum Pattern
        {
            None = 0,

            /// <summary>Double buzz when a write or shared-memory read fails.</summary>
            OperationFailed = 1,

            /// <summary>Soft pulse when FPS health drops to degraded.</summary>
            HealthDegraded = 4,

            /// <summary>Soft pulse when FPS health recovers.</summary>
            HealthRecovered = 5,

            /// <summary>Gentle pulse marking a drop into the yellow tier.</summary>
            SoftPulse = 6,

            /// <summary>Insistent pulse marking a drop into the red tier.</summary>
            AlertPulse = 7,
        }

        private static readonly Object Sync = new Object();

        private static Plugin _plugin;

        private static Boolean _isEnabled = true;

        /// <summary>
        /// Device sink for haptic playback. The plugin API exposes no general-purpose haptic
        /// channel, so the host wires this up to whatever the attached device supports; until
        /// then playback is silently dropped.
        /// </summary>
        public static Action<Pattern> Sink { get; set; }

        /// <summary>Plugin passed to <see cref="Init"/>, for sinks that need it.</summary>
        public static Plugin Plugin
        {
            get
            {
                lock (Sync)
                {
                    return _plugin;
                }
            }
        }

        public static void Init(Plugin plugin)
        {
            lock (Sync)
            {
                _plugin = plugin;
            }
        }

        /// <summary>Plays a pattern. Never throws: haptics are cosmetic and run on event paths.</summary>
        public static void Play(Pattern pattern)
        {
            if (pattern == Pattern.None)
            {
                return;
            }

            Action<Pattern> sink;

            lock (Sync)
            {
                if (!_isEnabled)
                {
                    return;
                }

                sink = Sink;
            }

            if (sink == null)
            {
                return;
            }

            try
            {
                sink(pattern);
            }
            catch (Exception)
            {
                // A failed buzz must never take down the caller's event handler.
            }
        }

        public static void PlayForHealthChange(Boolean isHealthy) =>
            Play(isHealthy ? Pattern.HealthRecovered : Pattern.HealthDegraded);

        public static void SetEnabled(Boolean enabled)
        {
            lock (Sync)
            {
                _isEnabled = enabled;
            }
        }
    }
}
