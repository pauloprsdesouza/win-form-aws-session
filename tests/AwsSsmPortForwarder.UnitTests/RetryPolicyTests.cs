using AwsSsmPortForwarder.Core.Models;
using AwsSsmPortForwarder.Core.Services;
using FluentAssertions;

namespace AwsSsmPortForwarder.UnitTests;

public class RetryPolicyTests
{
    [Fact]
    public void Default_max_retries_is_three()
    {
        ConnectionOrchestrator.DefaultMaxRetries.Should().Be(3);
        new SessionView { Mapping = new PortMapping(1, 1) }.MaxRetries.Should().Be(3);
    }

    [Fact]
    public void Session_state_includes_reconnecting()
    {
        Enum.GetNames<SessionState>().Should().Contain("Reconnecting");
    }

    [Fact]
    public void Progress_update_carries_busy_and_percent()
    {
        var update = new ProgressUpdate { Message = "Reconnecting", IsBusy = true, Percent = 33 };
        update.IsBusy.Should().BeTrue();
        update.Percent.Should().Be(33);
    }
}
