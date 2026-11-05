namespace Loupedeck.RTSSPerformancePlugin.Helpers
{
    using System;
    using System.Collections.Concurrent;

    /// <summary>
    /// Thin wrapper over the Loupedeck plugin logger so call sites do not carry a Plugin reference.
    /// </summary>
    public static class PluginLog
    {
        private static PluginLogFile _logFile;

        public static void Init(PluginLogFile logFile) => _logFile = logFile;

        public static void Verbose(String message) => Write(log => log.Verbose(message));

        public static void Info(String message) => Write(log => log.Info(message));

        /// <summary>Minimum gap between repeats of the same throttled key.</summary>
        private static readonly TimeSpan ThrottleInterval = TimeSpan.FromSeconds(10);

        private static readonly ConcurrentDictionary<String, DateTime> LastWriteUtc =
            new ConcurrentDictionary<String, DateTime>(StringComparer.Ordinal);

        /// <summary>Last state token written under a key, for the transition-aware overload.</summary>
        private static readonly ConcurrentDictionary<String, String> LastStateToken =
            new ConcurrentDictionary<String, String>(StringComparer.Ordinal);

        /// <summary>
        /// Writes at most one line per <see cref="ThrottleInterval"/> for a given key.
        ///
        /// The value-flow trace runs on the 4Hz poll thread, where logging every hop unthrottled
        /// would produce tens of thousands of lines an hour and make the log useless for the
        /// problem it exists to diagnose.
        /// </summary>
        public static void InfoThrottled(String key, String message)
        {
            var now = DateTime.UtcNow;
            var due = false;

            LastWriteUtc.AddOrUpdate(
                key,
                _ =>
                {
                    due = true;
                    return now;
                },
                (_, previous) =>
                {
                    if (now - previous < ThrottleInterval)
                    {
                        return previous;
                    }

                    due = true;
                    return now;
                });

            if (due)
            {
                Info(message);
            }
        }

        /// <summary>
        /// Throttles steady state the same way, but always writes immediately when
        /// <paramref name="stateToken"/> differs from the token last seen under that key.
        ///
        /// Steady state is the thing worth throttling; a transition is the thing worth never
        /// missing. A purely time-based throttle silently drops the one line that explains a
        /// colour change whenever the change lands inside the interval — which is precisely when
        /// somebody is reading the log.
        /// </summary>
        public static void InfoThrottled(String key, String stateToken, String message)
        {
            var token = stateToken ?? String.Empty;
            var changed = false;

            LastStateToken.AddOrUpdate(
                key,
                _ =>
                {
                    changed = true;
                    return token;
                },
                (_, previous) =>
                {
                    if (String.Equals(previous, token, StringComparison.Ordinal))
                    {
                        return previous;
                    }

                    changed = true;
                    return token;
                });

            if (!changed)
            {
                InfoThrottled(key, message);
                return;
            }

            // Restart the time throttle, so a transition line is not immediately followed by a
            // steady-state repeat of itself.
            LastWriteUtc[key] = DateTime.UtcNow;

            Info(message);
        }

        public static void Warning(String message) => Write(log => log.Warning(message));

        public static void Warning(Exception exception, String message) =>
            Write(log => log.Warning(exception, message));

        public static void Error(String message) => Write(log => log.Error(message));

        public static void Error(Exception exception, String message) =>
            Write(log => log.Error(exception, message));

        /// <summary>
        /// Logging is best-effort: call sites include catch blocks and event handlers, so a
        /// missing or failing log file must never surface as an exception.
        /// </summary>
        private static void Write(Action<PluginLogFile> write)
        {
            var log = _logFile;

            if (log == null)
            {
                return;
            }

            try
            {
                write(log);
            }
            catch (Exception)
            {
            }
        }
    }
}
