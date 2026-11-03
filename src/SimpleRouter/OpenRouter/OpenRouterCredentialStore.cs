using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;
using SimpleRouter.Routing;

namespace SimpleRouter.OpenRouter;

/// <summary>Stores one protected API key shared by all OpenRouter catalog entries.</summary>
public sealed class OpenRouterCredentialStore(IDbContextFactory<RouterDbContext> dbFactory, SecretProtector protector)
    : ApiKeyCredentialStore(dbFactory, protector, ProviderKind.OpenRouter);
