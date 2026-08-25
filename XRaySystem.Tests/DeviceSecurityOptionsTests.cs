using XRaySystem.DeviceHost;

namespace XRaySystem.Tests;

public sealed class DeviceSecurityOptionsTests
{
    [Fact]
    public void Development_mode_allows_authentication_to_be_disabled_without_keys()
    {
        var options = new DeviceSecurityOptions
        {
            AuthenticationEnabled = false,
            ApiKeys = []
        };

        Assert.True(options.IsValid(out var error), error);
    }

    [Fact]
    public void Production_authentication_requires_a_credential()
    {
        var options = new DeviceSecurityOptions
        {
            AuthenticationEnabled = true,
            ApiKeys = []
        };

        Assert.False(options.IsValid(out var error));
        Assert.Contains("en az bir", error);
    }

    [Theory]
    [InlineData(DeviceRoles.Viewer)]
    [InlineData(DeviceRoles.Operator)]
    [InlineData(DeviceRoles.Admin)]
    [InlineData("operator")]
    public void Supported_roles_are_accepted_case_insensitively(string role)
    {
        var options = CreateOptions(role);

        Assert.True(options.IsValid(out var error), error);
    }

    [Fact]
    public void Unknown_role_is_rejected()
    {
        var options = CreateOptions("PowerUser");

        Assert.False(options.IsValid(out var error));
        Assert.Contains("Geçersiz rol", error);
    }

    [Fact]
    public void Short_or_duplicate_keys_are_rejected()
    {
        var shortKey = CreateOptions("Viewer", "short-key");
        Assert.False(shortKey.IsValid(out _));

        var duplicateKey = new string('k', 32);
        var duplicate = CreateOptions("Viewer", duplicateKey);
        duplicate.ApiKeys.Add(new DeviceApiKeyCredential
        {
            Key = duplicateKey,
            Subject = "viewer-02",
            Role = DeviceRoles.Viewer
        });
        Assert.False(duplicate.IsValid(out var error));
        Assert.Contains("birden fazla", error);
    }

    [Fact]
    public void Credential_lookup_accepts_only_the_exact_key()
    {
        var options = CreateOptions(DeviceRoles.Operator);

        var credential = options.FindCredential(new string('k', 32));

        Assert.NotNull(credential);
        Assert.Equal("test-user", credential.Subject);
        Assert.Null(options.FindCredential(new string('x', 32)));
        Assert.Null(options.FindCredential(null));
    }

    [Theory]
    [InlineData("user\nspoof")]
    [InlineData("")]
    public void Invalid_or_log_injection_subject_is_rejected(string subject)
    {
        var options = CreateOptions(DeviceRoles.Operator);
        options.ApiKeys[0].Subject = subject;

        Assert.False(options.IsValid(out _));
    }

    private static DeviceSecurityOptions CreateOptions(string role, string? key = null) => new()
    {
        AuthenticationEnabled = true,
        ApiKeys =
        [
            new DeviceApiKeyCredential
            {
                Key = key ?? new string('k', 32),
                Subject = "test-user",
                Role = role
            }
        ]
    };
}
