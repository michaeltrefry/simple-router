namespace SimpleRouter.Dashboard;

/// <summary>In-process signal that routing state the dashboard shows (usage, pins, cooldowns) has changed.</summary>
public sealed class DashboardEvents
{
    public event Action? Changed;

    /// <summary>
    /// Called on the request path, so subscribers run on the thread pool: a dashboard circuit would otherwise run its
    /// reload inline on the caller's thread. A failing subscriber is skipped.
    /// </summary>
    public void Publish()
    {
        if (Changed is not { } changed)
        {
            return;
        }
        ThreadPool.UnsafeQueueUserWorkItem(static handlers =>
        {
            foreach (var handler in handlers.GetInvocationList())
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
        }, changed, preferLocal: false);
    }
}
