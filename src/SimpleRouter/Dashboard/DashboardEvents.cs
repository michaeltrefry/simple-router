namespace SimpleRouter.Dashboard;

/// <summary>In-process signal that routing state the dashboard shows (usage, pins, cooldowns) has changed.</summary>
public sealed class DashboardEvents
{
    public event Action? Changed;

    /// <summary>Called on the request path, so a failing subscriber is skipped rather than failing the request.</summary>
    public void Publish()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception)
            {
                // A dashboard circuit torn down mid-publish; it no longer needs the update.
            }
        }
    }
}
