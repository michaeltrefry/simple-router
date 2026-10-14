using Microsoft.AspNetCore.Components;

namespace SimpleRouter.Dashboard;

/// <summary>A settings form: runs an edit, shows a refused edit's reason, and asks the page to reload after a saved one.</summary>
public abstract class SettingsSection : ComponentBase
{
    [Parameter, EditorRequired] public SettingsSnapshot Snapshot { get; set; } = default!;

    /// <summary>Raised after a saved edit, so the page re-reads the snapshot every section shows.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    [Inject] protected CatalogSettings Catalog { get; set; } = default!;
    [Inject] private ILogger<SettingsSection> Logger { get; set; } = default!;

    protected string? Error { get; set; }
    protected string? Notice { get; set; }
    protected bool Busy { get; private set; }

    /// <returns>Whether the edit was saved.</returns>
    protected async Task<bool> RunAsync(Func<Task> edit, string? notice = null)
    {
        Error = null;
        Notice = null;
        Busy = true;
        try
        {
            await edit();
            Notice = notice;
            await OnChanged.InvokeAsync();
            return true;
        }
        catch (SettingsException e)
        {
            Error = e.Message;
            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Logger.LogWarning("A settings edit failed: {error}", e.GetType().Name);
            Error = "The change could not be saved; see the server log.";
            return false;
        }
        finally
        {
            Busy = false;
        }
    }
}
