using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;
using SimpleRouter.Routing;

namespace SimpleRouter.RouterCom;

/// <summary>Stores one protected Router.com key independently of OpenRouter.</summary>
public sealed class RouterComCredentialStore(IDbContextFactory<RouterDbContext> dbFactory, SecretProtector protector)
    : ApiKeyCredentialStore(dbFactory, protector, ProviderKind.RouterCom);
