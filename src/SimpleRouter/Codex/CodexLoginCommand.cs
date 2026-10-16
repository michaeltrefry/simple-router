using Microsoft.Extensions.Options;
using SimpleRouter.Routing;

namespace SimpleRouter.Codex;

/// <summary>Interactive <c>login codex</c>: show the device code, poll until it is approved, store the tokens.</summary>
public sealed class CodexLoginCommand(CodexOAuthClient oauth, CodexCredentialStore store, ModelDiscovery discovery, IOptions<RouterOptions> options, TimeProvider time)
{
    public async Task<int> RunAsync(TextWriter output, CancellationToken ct = default)
    {
        try
        {
            var device = await oauth.StartDeviceAsync(ct);
            await output.WriteLineAsync($"Open {device.VerificationUrl} in your browser, sign in to ChatGPT and enter this code:");
            await output.WriteLineAsync(device.UserCode);
            await output.FlushAsync(ct);

            if (!await CompleteAsync(device, ct))
            {
                await output.WriteLineAsync($"Codex login failed: {NotEnteredInTime}");
                return 1;
            }
        }
        catch (CodexOAuthException e)
        {
            await output.WriteLineAsync($"Codex login failed: {e.Message}");
            return 1;
        }
        await output.WriteLineAsync("Codex subscription saved.");
        await discovery.ReportRefreshAsync(ProviderKind.Codex, output, ct);
        return 0;
    }

    public const string NotEnteredInTime = "the device code was not entered in time.";

    /// <summary>Polls until <paramref name="device"/> is approved, then stores the tokens; false when it was not entered in time.</summary>
    public async Task<bool> CompleteAsync(DeviceCode device, CancellationToken ct = default)
    {
        if (await WaitForApprovalAsync(device, ct) is not { } authorization)
        {
            return false;
        }
        await store.SaveLoginAsync(await oauth.ExchangeAsync(authorization, ct), ct);
        return true;
    }

    private async Task<DeviceAuthorization?> WaitForApprovalAsync(DeviceCode device, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + options.Value.Codex.DeviceCodeTimeout;
        while (time.GetUtcNow() < deadline)
        {
            try
            {
                if (await oauth.PollDeviceAsync(device, ct) is { } authorization)
                {
                    return authorization;
                }
            }
            catch (CodexOAuthException e) when (e.Status == 0)
            {
                // A transient network failure; keep polling until the deadline.
            }
            await Task.Delay(device.Interval, time, ct);
        }
        return null;
    }
}
