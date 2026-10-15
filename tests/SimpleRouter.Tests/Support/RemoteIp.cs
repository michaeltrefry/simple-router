using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace SimpleRouter.Tests.Support;

/// <summary>
/// Lets a test present a request as coming from another machine: ahead of the router's own middleware, the
/// connection's remote address is replaced by the one in <see cref="Header"/>, so no test depends on the runner's
/// network interfaces.
/// </summary>
public sealed class RemoteIp : IStartupFilter
{
    public const string Header = "X-Test-Remote-Ip";

    public static void Register(IServiceCollection services) => services.AddSingleton<IStartupFilter, RemoteIp>();

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (IPAddress.TryParse(context.Request.Headers[Header].ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }
            return nextMiddleware(context);
        });
        next(app);
    };
}
