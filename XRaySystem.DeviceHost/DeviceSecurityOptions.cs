using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace XRaySystem.DeviceHost;

public sealed class DeviceSecurityOptions
{
    public const string SectionName = "Security";

    public bool RequireHttps { get; set; } = true;
    public bool AuthenticationEnabled { get; set; } = true;
    public double CommandLeaseSeconds { get; set; } = 30;
    public List<DeviceApiKeyCredential> ApiKeys { get; set; } = [];

    public bool IsValid(out string error)
    {
        error = string.Empty;
        if (CommandLeaseSeconds is <= 0 or > 3600)
        {
            error = "Security:CommandLeaseSeconds 0 ile 3600 saniye arasında olmalıdır.";
            return false;
        }

        if (!AuthenticationEnabled) return true;
        if (ApiKeys.Count == 0)
        {
            error = "Authentication açıkken en az bir Security:ApiKeys kaydı tanımlanmalıdır.";
            return false;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var credential in ApiKeys)
        {
            if (string.IsNullOrWhiteSpace(credential.Key) || credential.Key.Length < 32)
            {
                error = "Her API anahtarı en az 32 karakter olmalıdır.";
                return false;
            }

            if (!keys.Add(credential.Key))
            {
                error = "Aynı API anahtarı birden fazla kimliğe atanamaz.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(credential.Subject) ||
                credential.Subject.Length > 128 ||
                credential.Subject.Any(char.IsControl))
            {
                error = "Her API anahtarının Subject değeri 1-128 görünür karakter olmalıdır.";
                return false;
            }

            if (!DeviceRoles.All.Contains(credential.Role, StringComparer.OrdinalIgnoreCase))
            {
                error = $"Geçersiz rol '{credential.Role}'. İzin verilen roller: {string.Join(", ", DeviceRoles.All)}.";
                return false;
            }
        }

        return true;
    }

    public DeviceApiKeyCredential? FindCredential(string? presentedKey)
    {
        if (string.IsNullOrWhiteSpace(presentedKey)) return null;

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));
        foreach (var credential in ApiKeys)
        {
            var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(credential.Key));
            if (CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash))
                return credential;
        }

        return null;
    }
}

public sealed class DeviceApiKeyCredential
{
    public string Key { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Role { get; set; } = DeviceRoles.Viewer;
}

public sealed class DeviceSecurityOptionsValidator : IValidateOptions<DeviceSecurityOptions>
{
    public ValidateOptionsResult Validate(string? name, DeviceSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.IsValid(out var error)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(error);
    }
}

public static class DeviceRoles
{
    public const string Viewer = "Viewer";
    public const string Operator = "Operator";
    public const string Admin = "Admin";

    public static readonly string[] All = [Viewer, Operator, Admin];
}

public static class DeviceAuthorizationPolicies
{
    public const string Viewer = "DeviceViewer";
    public const string Operator = "DeviceOperator";
    public const string Admin = "DeviceAdmin";
}
