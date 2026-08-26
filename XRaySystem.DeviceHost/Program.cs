using Microsoft.AspNetCore.Authentication;
using XRaySystem.DeviceHost;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Sensors;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

var sharedOperatorKey = Environment.GetEnvironmentVariable("XRAYSYSTEM_DEVICEHOST_API_KEY");
if (!string.IsNullOrWhiteSpace(sharedOperatorKey) &&
    !builder.Configuration.GetSection("Security:ApiKeys").GetChildren().Any())
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Security:ApiKeys:0:Key"] = sharedOperatorKey.Trim(),
        ["Security:ApiKeys:0:Subject"] = "device-operator",
        ["Security:ApiKeys:0:Role"] = DeviceRoles.Operator
    });
}

builder.Services.AddHealthChecks()
    .AddCheck<DeviceHostStorageReadinessCheck>(
        "devicehost_durable_storage",
        tags: ["ready"]);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // Only a same-machine reverse proxy (for example cloudflared) may assert
    // the external HTTPS scheme. Network clients cannot spoof this header.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
    options.ForwardLimit = 1;
});
builder.Services.AddSignalR();
var apiRateLimitPermitCount = builder.Configuration.GetValue<int?>("RequestSecurity:RateLimitPermitCount") ?? 120;
var apiRateLimitWindowSeconds = builder.Configuration.GetValue<int?>("RequestSecurity:RateLimitWindowSeconds") ?? 60;
var maximumApiRequestBodyBytes = builder.Configuration.GetValue<long?>("RequestSecurity:MaximumApiRequestBodyBytes") ?? 65_536;
if (apiRateLimitPermitCount <= 0 || apiRateLimitWindowSeconds <= 0 || maximumApiRequestBodyBytes <= 0)
    throw new InvalidOperationException("DeviceHost request security limitleri pozitif olmalıdır.");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var subject = context.User.Identity?.IsAuthenticated == true
            ? context.User.Identity.Name
            : null;
        var partitionKey = !string.IsNullOrWhiteSpace(subject)
            ? "subject:" + subject
            : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = apiRateLimitPermitCount,
                Window = TimeSpan.FromSeconds(apiRateLimitWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new
            {
                type = "about:blank",
                title = "Çok fazla istek.",
                status = StatusCodes.Status429TooManyRequests,
                detail = "İstek sınırı aşıldı; kısa süre sonra yeniden deneyin."
            },
            cancellationToken);
    };
});
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
var remoteImageBufferOptions = new RemoteImageBufferOptions(
    builder.Configuration.GetValue<int?>("RemoteImageBuffer:ImageLimit") ?? defaultBuffer.ImageLimit,
    builder.Configuration.GetValue<long?>("RemoteImageBuffer:MaxBytes") ?? defaultBuffer.MaxBytes,
    builder.Configuration.GetValue<double?>("RemoteImageBuffer:TimeToLiveMinutes") is { } ttlMinutes
        ? TimeSpan.FromMinutes(ttlMinutes)
        : defaultBuffer.TimeToLive);
remoteImageBufferOptions.EnsureValid();
builder.Services.AddSingleton(remoteImageBufferOptions);
builder.Services.AddSingleton<RemoteImageBuffer>();
var configuredAcquisitionFolder = builder.Configuration["AcquisitionStorage:BaseFolder"];
var acquisitionFolder = string.IsNullOrWhiteSpace(configuredAcquisitionFolder)
    ? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XRaySystem",
        "DeviceHost",
        "Acquisitions")
    : Path.GetFullPath(configuredAcquisitionFolder, builder.Environment.ContentRootPath);
var acquisitionArchiveOptions = new DeviceAcquisitionArchiveOptions(
    builder.Configuration.GetValue<bool?>("AcquisitionStorage:Enabled") ?? true,
    acquisitionFolder,
    builder.Configuration.GetValue<bool?>("AcquisitionStorage:RequireSignedMetadata") ?? true,
    Environment.GetEnvironmentVariable("XRAYSYSTEM_ACQUISITION_INTEGRITY_KEY")
        ?? builder.Configuration["AcquisitionStorage:IntegrityKey"],
    builder.Configuration.GetValue<bool?>("AcquisitionStorage:QuarantineIncompleteArtifacts") ?? true,
    builder.Configuration.GetValue<double?>("AcquisitionStorage:IncompleteArtifactGracePeriodMinutes") ?? 10);
builder.Services.AddSingleton(acquisitionArchiveOptions);
builder.Services.AddSingleton<DeviceAcquisitionArchive>();
builder.Services.AddSingleton<IDeviceAcquisitionArchive>(provider =>
    provider.GetRequiredService<DeviceAcquisitionArchive>());
builder.Services.AddSingleton<IDeviceAcquisitionCatalog>(provider =>
    provider.GetRequiredService<IDeviceAcquisitionArchive>());
var configuredAuditPath = builder.Configuration["AuditStorage:FilePath"];
var auditPath = string.IsNullOrWhiteSpace(configuredAuditPath)
    ? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XRaySystem",
        "DeviceHost",
        "Audit",
        "device-audit.jsonl")
    : Path.GetFullPath(configuredAuditPath, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(new DeviceCommandAuditOptions(
    builder.Configuration.GetValue<bool?>("AuditStorage:Enabled") ?? true,
    auditPath,
    builder.Configuration.GetValue<bool?>("AuditStorage:RequireSignedEntries") ?? true,
    Environment.GetEnvironmentVariable("XRAYSYSTEM_AUDIT_INTEGRITY_KEY")
        ?? builder.Configuration["AuditStorage:IntegrityKey"],
    builder.Configuration.GetValue<long?>("AuditStorage:MaximumFileBytes") ?? 64L * 1024 * 1024));
builder.Services.AddSingleton<IDeviceCommandAuditStore, HashChainedJsonlDeviceAuditStore>();
builder.Services.AddSingleton<LatestLiveFrameStore>();
builder.Services.AddSingleton(provider => new CommandLeaseManager(TimeSpan.FromSeconds(
    provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DeviceSecurityOptions>>()
        .Value.CommandLeaseSeconds)));
builder.Services.AddSingleton<IDeviceEventPublisher, SignalRDeviceEventPublisher>();
builder.Services.AddSingleton<IXRayGenerator, MockXRayGenerator>();
builder.Services.AddSingleton<IXRayDetector, MockXRayDetector>();
var defaultSafety = AcquisitionSafetyOptions.Default;
var safety = new AcquisitionSafetyOptions(
    TimeSpan.FromMilliseconds(builder.Configuration.GetValue<double?>("AcquisitionSafety:GeneratorReadyTimeoutMs") ?? defaultSafety.GeneratorReadyTimeout.TotalMilliseconds),
    TimeSpan.FromMilliseconds(builder.Configuration.GetValue<double?>("AcquisitionSafety:DetectorArmTimeoutMs") ?? defaultSafety.DetectorArmTimeout.TotalMilliseconds),
    TimeSpan.FromMilliseconds(builder.Configuration.GetValue<double?>("AcquisitionSafety:ExposureStartTimeoutMs") ?? defaultSafety.ExposureStartTimeout.TotalMilliseconds),
    TimeSpan.FromMilliseconds(builder.Configuration.GetValue<double?>("AcquisitionSafety:MaximumExposureDurationMs") ?? defaultSafety.MaximumExposureDuration.TotalMilliseconds),
    TimeSpan.FromMilliseconds(builder.Configuration.GetValue<double?>("AcquisitionSafety:MaximumGrabDurationMs") ?? defaultSafety.MaximumGrabDuration.TotalMilliseconds),
    TimeSpan.FromMilliseconds(builder.Configuration.GetValue<double?>("AcquisitionSafety:ExposureStopConfirmationTimeoutMs") ?? defaultSafety.ExposureStopConfirmationTimeout.TotalMilliseconds),
    TimeSpan.FromMilliseconds(builder.Configuration.GetValue<double?>("AcquisitionSafety:DetectorStopTimeoutMs") ?? defaultSafety.DetectorStopTimeout.TotalMilliseconds),
    TimeSpan.FromMilliseconds(builder.Configuration.GetValue<double?>("AcquisitionSafety:FrameReadyTimeoutMs") ?? defaultSafety.FrameReadyTimeout.TotalMilliseconds),
    TimeSpan.FromMilliseconds(builder.Configuration.GetValue<double?>("AcquisitionSafety:BufferCopyTimeoutMs") ?? defaultSafety.BufferCopyTimeout.TotalMilliseconds));
safety.EnsureValid();
builder.Services.AddSingleton(safety);
builder.Services.AddSingleton<IAcquisitionCoordinator, CoordinatedHardwareSensorService>();
builder.Services.AddSingleton<ISensorService>(provider =>
    provider.GetRequiredService<IAcquisitionCoordinator>());
builder.Services.AddSingleton<DeviceCoordinator>();
builder.Services.AddHostedService<DeviceAuditStartupService>();
builder.Services.AddHostedService<DeviceArchiveRecoveryService>();
builder.Services.AddHostedService<SensorEventRelay>();

var app = builder.Build();

app.UseForwardedHeaders();

var security = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DeviceSecurityOptions>>().Value;
var requireHttps = security.RequireHttps;
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        context.Request.ContentLength is { } contentLength &&
        contentLength > maximumApiRequestBodyBytes)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "request_body_too_large",
            message = "API istek gövdesi izin verilen sınırı aşıyor."
        });
        return;
    }

    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }

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
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/readyz", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
app.MapGet("/", () => Results.Ok(new
{
    service = "XRaySystem.DeviceHost",
    protocolVersion = XRaySystem.Contracts.DeviceProtocolVersion.Current,
    role = "pc-side-device-owner",
    deviceId = DeviceHostOptions.DefaultMockDeviceId,
    acquisitionOperations = new[] { "Snap", "Grab", "Freeze", "Abort" },
    snapFrameModes = new[] { "Raw", "Low" }
}));
app.MapDeviceApi();
app.MapHub<DeviceEventsHub>(XRaySystem.Contracts.DeviceApiRoutes.EventsHub)
    .RequireAuthorization(DeviceAuthorizationPolicies.Viewer);

app.Run();

public partial class Program;
