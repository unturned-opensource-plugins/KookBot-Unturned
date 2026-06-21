using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class EventSubscriptionRegistryTests
{
    [Fact]
    public void TryRegister_rolls_back_prior_subscriptions_when_later_subscription_fails()
    {
        var calls = new List<string>();
        var subscriptions = new[]
        {
            new EventSubscription("first", () => calls.Add("subscribe:first"), () => calls.Add("unsubscribe:first")),
            new EventSubscription("second", () => throw new InvalidOperationException("boom"), () => calls.Add("unsubscribe:second"))
        };

        var registered = EventSubscriptionRegistry.TryRegister(subscriptions, _ => { });

        Assert.False(registered);
        Assert.Equal(new[] { "subscribe:first", "unsubscribe:first" }, calls);
    }
}
