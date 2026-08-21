using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using XRaySystem.Contracts;

namespace XRaySystem.DeviceHost;

public sealed class DeviceApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "XRayApiKey";

    private readonly DeviceSecurityOptions _security;

    public DeviceApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<DeviceSecurityOptions> security)
        : base(options, logger, encoder)
    {
        _security = security.Value;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!_security.AuthenticationEnabled)
            return Task.FromResult(Success("development-local", DeviceRoles.Admin));

        if (!Request.Headers.TryGetValue(DeviceProtocolHeaders.ApiKey, out var values) || values.Count != 1)
            return Task.FromResult(AuthenticateResult.NoResult());

        var presented = values[0];
        if (string.IsNullOrWhiteSpace(presented))
            return Task.FromResult(AuthenticateResult.Fail("Geçersiz API anahtarı."));

        var credential = _security.FindCredential(presented);
        if (credential is not null)
            return Task.FromResult(Success(credential.Subject.Trim(), NormalizeRole(credential.Role)));

        return Task.FromResult(AuthenticateResult.Fail("Geçersiz API anahtarı."));
    }

    private AuthenticateResult Success(string subject, string role)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, subject),
            new Claim(ClaimTypes.Name, subject),
            new Claim(ClaimTypes.Role, role)
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private static string NormalizeRole(string role) =>
        DeviceRoles.All.First(value => value.Equals(role, StringComparison.OrdinalIgnoreCase));
}
