namespace SimpleRouter.Tests;

public class RouterOptionsTests
{
    [Fact]
    public void Local_health_checks_default_to_five_minutes()
    {
        Assert.Equal(300, new RouterOptions().Local.HealthCheckIntervalSeconds);
    }
}
