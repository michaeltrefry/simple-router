using SimpleRouter.Routing;

namespace SimpleRouter.Claude;

/// <summary>Interactive <c>login claude</c>: open the authorize URL, accept the pasted <c>code#state</c>, store tokens.</summary>
public sealed class ClaudeLoginCommand(ClaudeOAuthClient oauth, ClaudeCredentialStore store, ModelDiscovery discovery)
{
    public async Task<int> RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        var login = oauth.StartLogin();
        await output.WriteLineAsync("Open this URL in your browser and sign in to Claude:");
        await output.WriteLineAsync(login.AuthorizeUrl);
        await output.WriteAsync("Paste the authorization code: ");
        await output.FlushAsync(ct);

        var pasted = await input.ReadLineAsync(ct);
        try
        {
            await CompleteAsync(login, pasted, ct);
        }
        catch (Exception e) when (e is ArgumentException or ClaudeOAuthException)
        {
            await output.WriteLineAsync($"Claude login failed: {e.Message}");
            return 1;
        }
        await output.WriteLineAsync("Claude subscription saved.");
        await discovery.ReportRefreshAsync(ProviderKind.Claude, output, ct);
        return 0;
    }

    public async Task CompleteAsync(PkceLogin login, string? pasted, CancellationToken ct = default)
    {
        var parts = (pasted ?? "").Trim().Split('#', 2);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            throw new ArgumentException("Expected the code#state value shown by Claude.");
        }
        if (parts[1] != login.State)
        {
            throw new ArgumentException("The authorization state did not match this login attempt.");
        }

        var tokens = await oauth.ExchangeCodeAsync(parts[0], login, ct);
        await store.SaveAsync(tokens, ct);
    }
}
