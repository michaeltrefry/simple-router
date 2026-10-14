using SimpleRouter.Claude;
using SimpleRouter.Codex;
using SimpleRouter.Gateway;
using SimpleRouter.Routing;

namespace SimpleRouter.Cli;

/// <summary>Non-server commands: <c>login claude</c>, <c>login codex</c>, <c>key show</c> and <c>models refresh</c>.</summary>
public static class CommandLine
{
    private const string Usage = "Usage: simple-router [login claude | login codex | key show | models refresh] [--SimpleRouter:Setting=value ...]";

    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, Action<IServiceCollection>? configureServices = null)
    {
        var command = string.Join(' ', args.TakeWhile(a => !a.StartsWith('-')));
        var hostArgs = args.SkipWhile(a => !a.StartsWith('-')).ToArray();
        if (command is not ("login claude" or "login codex" or "key show" or "models refresh"))
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }

        var app = RouterApp.Build(hostArgs, configureServices);
        await RouterApp.InitializeAsync(app);
        if (command == "key show")
        {
            await output.WriteLineAsync(await app.Services.GetRequiredService<GatewayKeyStore>().GetAsync());
            return 0;
        }
        if (command == "models refresh")
        {
            return await app.Services.GetRequiredService<ModelDiscovery>().ReportRefreshLoggedInAsync(output) ? 0 : 1;
        }
        return command == "login codex"
            ? await app.Services.GetRequiredService<CodexLoginCommand>().RunAsync(output)
            : await app.Services.GetRequiredService<ClaudeLoginCommand>().RunAsync(input, output);
    }
}
