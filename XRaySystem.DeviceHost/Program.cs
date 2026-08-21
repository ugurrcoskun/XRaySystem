using Microsoft.AspNetCore.Authentication;
using XRaySystem.DeviceHost;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Infrastructure.Sensors;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddSignalR();
builder.Services.AddOptions<DeviceSecurityOptions>()
    .Bind(builder.Configuration.GetSection(DeviceSecurityOptions.SectionName))
    .Validate(options => options.IsValid(out _), "DeviceHost güvenlik yapılandırması geçersiz.")
    .ValidateOnStart();
builder.Services
    .AddAuthentication(DeviceApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DeviceApiKeyAuthenticationHandler>(
        DeviceApiKeyAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(DeviceAuthorizationPolicies.Viewer,
        policy => policy.RequireRole(DeviceRoles.Viewer, DeviceRoles.Operator, DeviceRoles.Admin));
    options.AddPolicy(DeviceAuthorizationPolicies.Operator,
        policy => policy.RequireRole(DeviceRoles.Operator, DeviceRoles.Admin));
    options.AddPolicy(DeviceAuthorizationPolicies.Admin,
        policy => policy.RequireRole(DeviceRoles.Admin));
});
builder.Services.AddSingleton(
    new DeviceHostOptions(DeviceHostOptions.DefaultMockDeviceId, IsSimulator: true));
var defaultBuffer = RemoteImageBufferOptions.Default;
builder.Services.AddSingleton(new RemoteImageBufferOptions(
    builder.Configuration.GetValue<int?>("RemoteImageBuffer:ImageLimit") ?? defaultBuffer.ImageLimit,
    builder.Configuration.GetValue<long?>("RemoteImageBuffer:MaxBytes") ?? defaultBuffer.MaxBytes,
    builder.Configuration.GetValue<double?>("RemoteImageBuffer:TimeToLiveMinutes") is { } ttlMinutes
        ? TimeSpan.FromMinutes(ttlMinutes)
        : defaultBuffer.TimeToLive));
builder.Services.AddSingleton<RemoteImageBuffer>();
builder.Services.AddSingleton<LatestLiveFrameStore>();
builder.Services.AddSingleton(provider => new CommandLeaseManager(TimeSpan.FromSeconds(
    provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DeviceSecurityOptions>>()
        .Value.CommandLeaseSeconds)));
builder.Services.AddSingleton<IDeviceEventPublisher, SignalRDeviceEventPublisher>();
builder.Services.AddSingleton<ISensorService, MockXRaySensorService>();
builder.Services.AddSingleton<DeviceCoordinator>();
builder.Services.AddHostedService<SensorEventRelay>();

var app = builder.Build();

var security = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DeviceSecurityOptions>>().Value;
var requireHttps = security.RequireHttps;
app.Use(async (context, next) =>
{
    if (requireHttps && !context.Request.IsHttps)
    {
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "https_required",
            message = "DeviceHost bu ortamda yalnız HTTPS bağlantısı kabul eder."
        });
        return;
    }

    await next();
});

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/healthz");
app.MapGet("/", () => Results.Ok(new
{
    service = "XRaySystem.DeviceHost",
    mode = "development-mock",
    deviceId = DeviceHostOptions.DefaultMockDeviceId
}));
app.MapDeviceApi();
app.MapHub<DeviceEventsHub>(XRaySystem.Contracts.DeviceApiRoutes.EventsHub)
    .RequireAuthorization(DeviceAuthorizationPolicies.Viewer);

app.Run();

public partial class Program;
