using Microsoft.Extensions.Options;

namespace SimpleRouter.Codex;

/// <summary>Interactive <c>login codex</c>: show the device code, poll until it is approved, store the tokens.</summary>
public sealed class CodexLoginCommand(CodexOAuthClient oauth, CodexCredentialStore store, IOptions<RouterOptions> options, TimeProvider time)
{
    public async Task<int> RunAsync(TextWriter output, CancellationToken ct = default)
    {
        try
        {
            var device = await oauth.StartDeviceAsync(ct);
            await output.WriteLineAsync($"Open {device.VerificationUrl} in your browser, sign in to ChatGPT and enter this code:");
            await output.WriteLineAsync(device.UserCode);
            await output.FlushAsync(ct);

            var authorization = await WaitForApprovalAsync(device, ct);
            if (authorization is null)
            {
                await output.WriteLineAsync("Codex login failed: the device code was not entered in time.");
                return 1;
            }
            await store.SaveAsync(await oauth.ExchangeAsync(authorization, ct), ct);
        }
        catch (CodexOAuthException e)
        {
            await output.WriteLineAsync($"Codex login failed: {e.Message}");
            return 1;
        }
        await output.WriteLineAsync("Codex subscription saved.");
        return 0;
    }

    private async Task<DeviceAuthorization?> WaitForApprovalAsync(DeviceCode device, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + options.Value.Codex.DeviceCodeTimeout;
        while (time.GetUtcNow() < deadline)
        {
            if (await oauth.PollDeviceAsync(device, ct) is { } authorization)
            {
                return authorization;
            }
            await Task.Delay(device.Interval, time, ct);
        }
        return null;
    }
}
