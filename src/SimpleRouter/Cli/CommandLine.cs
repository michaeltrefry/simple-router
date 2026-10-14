using SimpleRouter.Claude;
using SimpleRouter.Gateway;

namespace SimpleRouter.Cli;

/// <summary>Non-server commands: <c>login claude</c> and <c>key show</c>.</summary>
public static class CommandLine
{
    private const string Usage = "Usage: simple-router [login claude | key show] [--SimpleRouter:Setting=value ...]";

    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, Action<IServiceCollection>? configureServices = null)
    {
        var command = string.Join(' ', args.TakeWhile(a => !a.StartsWith('-')));
        var hostArgs = args.SkipWhile(a => !a.StartsWith('-')).ToArray();
        if (command is not ("login claude" or "key show"))
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
        return await app.Services.GetRequiredService<ClaudeLoginCommand>().RunAsync(input, output);
    }
}
