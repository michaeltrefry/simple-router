using Microsoft.AspNetCore.DataProtection;

namespace SimpleRouter;

/// <summary>Encrypts secrets at rest with ASP.NET Data Protection.</summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("SimpleRouter.Secrets.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
