namespace Loupedeck.RTSSPerformancePlugin.Helpers
{
    using System;

    /// <summary>
    /// Subscribes an action to <see cref="RTSSPerformancePlugin.ServicesReady"/> and, race-free,
    /// makes sure the handler runs at least once even if the services were already up before the
    /// action asked.
    ///
    /// The host does not guarantee that action.OnLoad() runs after Plugin.Load(): on-device, an
    /// action's OnLoad ran and its own EnsureServicesBound found <see cref="RTSSPerformancePlugin.Instance"/>
    /// still null 129ms before Plugin.Load() started. Nothing else was ever going to re-trigger
    /// that binding — a poll-tick subscription is itself part of what a successful bind sets up —
    /// so the action was stuck on its initial static render for the rest of the session.
    ///
    /// Subscribing before checking readiness (rather than the reverse) is what makes this race
    /// free: whichever order the event fire and the readiness check land in, at least one of them
    /// observes the ready state and invokes the handler. Both landing on the ready side calls the
    /// handler twice, which every caller's handler (EnsureServicesBound + a diff-checked
    /// notification) already tolerates.
    /// </summary>
    public static class ActionReadiness
    {
        public static void SubscribeAndCatchUp(EventHandler handler)
        {
            if (handler == null)
            {
                return;
            }

            RTSSPerformancePlugin.ServicesReady += handler;

            if (RTSSPerformancePlugin.Instance != null)
            {
                handler(null, EventArgs.Empty);
            }
        }

        public static void Unsubscribe(EventHandler handler)
        {
            if (handler != null)
            {
                RTSSPerformancePlugin.ServicesReady -= handler;
            }
        }
    }
}
