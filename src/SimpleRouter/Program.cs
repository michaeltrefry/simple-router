using SimpleRouter;
using SimpleRouter.Cli;

if (args.Length > 0 && !args[0].StartsWith('-'))
{
    return await CommandLine.RunAsync(args, Console.In, Console.Out);
}

var app = RouterApp.Build(args);
await RouterApp.InitializeAsync(app);
await app.RunAsync();
return 0;
