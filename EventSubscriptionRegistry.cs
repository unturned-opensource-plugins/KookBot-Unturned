using System;
using System.Collections.Generic;
using System.Linq;

namespace Emqo.KookBot_Unturned
{
    internal sealed class EventSubscription
    {
        public EventSubscription(string name, Action subscribe, Action unsubscribe)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "unknown" : name;
            Subscribe = subscribe ?? throw new ArgumentNullException(nameof(subscribe));
            Unsubscribe = unsubscribe ?? throw new ArgumentNullException(nameof(unsubscribe));
        }

        public string Name { get; }
        public Action Subscribe { get; }
        public Action Unsubscribe { get; }
    }

    internal static class EventSubscriptionRegistry
    {
        internal static bool TryRegister(IEnumerable<EventSubscription> subscriptions, Action<string> logError)
        {
            var registered = new List<EventSubscription>();

            try
            {
                foreach (var subscription in subscriptions ?? Enumerable.Empty<EventSubscription>())
                {
                    subscription.Subscribe();
                    registered.Add(subscription);
                }

                return true;
            }
            catch (Exception ex)
            {
                logError?.Invoke($"Error registering events: {ex.Message}");
                Rollback(registered, logError);
                return false;
            }
        }

        internal static void Unregister(IEnumerable<EventSubscription> subscriptions, Action<string> logError)
        {
            foreach (var subscription in (subscriptions ?? Enumerable.Empty<EventSubscription>()).Reverse())
            {
                try
                {
                    subscription.Unsubscribe();
                }
                catch (Exception ex)
                {
                    logError?.Invoke($"Error unregistering {subscription.Name}: {ex.Message}");
                }
            }
        }

        private static void Rollback(IEnumerable<EventSubscription> subscriptions, Action<string> logError)
        {
            foreach (var subscription in subscriptions.Reverse())
            {
                try
                {
                    subscription.Unsubscribe();
                }
                catch (Exception ex)
                {
                    logError?.Invoke($"Error rolling back {subscription.Name}: {ex.Message}");
                }
            }
        }
    }
}
