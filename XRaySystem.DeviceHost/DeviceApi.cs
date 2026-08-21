using XRaySystem.Contracts;
using XRaySystem.Infrastructure.Storage;

namespace XRaySystem.DeviceHost;

public static class DeviceApi
{
    public static IEndpointRouteBuilder MapDeviceApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(DeviceApiRoutes.Devices, (DeviceCoordinator coordinator) =>
            Results.Ok(new[] { coordinator.GetState() }))
            .RequireAuthorization(DeviceAuthorizationPolicies.Viewer);

        endpoints.MapGet(DeviceApiRoutes.Device, (string deviceId, DeviceCoordinator coordinator) =>
            coordinator.Matches(deviceId)
                ? Results.Ok(coordinator.GetState())
                : Results.NotFound())
            .RequireAuthorization(DeviceAuthorizationPolicies.Viewer);

        endpoints.MapPost(DeviceApiRoutes.Connect,
            (string deviceId, DeviceCommandRequest request, HttpContext context, DeviceCoordinator coordinator,
                CommandLeaseManager lease, ILoggerFactory logs, CancellationToken cancellationToken) =>
                ExecuteAsync(deviceId, request.CommandId, "connect", context, coordinator, lease, logs,
                    ct => coordinator.ConnectAsync(request, ct), cancellationToken))
            .RequireAuthorization(DeviceAuthorizationPolicies.Operator);

        endpoints.MapPost(DeviceApiRoutes.Disconnect,
            (string deviceId, DeviceCommandRequest request, HttpContext context, DeviceCoordinator coordinator,
                CommandLeaseManager lease, ILoggerFactory logs, CancellationToken cancellationToken) =>
                ExecuteAsync(deviceId, request.CommandId, "disconnect", context, coordinator, lease, logs,
                    ct => coordinator.DisconnectAsync(request, ct), cancellationToken, releaseLease: true))
            .RequireAuthorization(DeviceAuthorizationPolicies.Operator);

        endpoints.MapPost(DeviceApiRoutes.StartLive,
            (string deviceId, DeviceCommandRequest request, HttpContext context, DeviceCoordinator coordinator,
                CommandLeaseManager lease, ILoggerFactory logs, CancellationToken cancellationToken) =>
                ExecuteAsync(deviceId, request.CommandId, "live.start", context, coordinator, lease, logs,
                    ct => coordinator.StartLiveAsync(request, ct), cancellationToken))
            .RequireAuthorization(DeviceAuthorizationPolicies.Operator);

        endpoints.MapPost(DeviceApiRoutes.StopLive,
            (string deviceId, DeviceCommandRequest request, HttpContext context, DeviceCoordinator coordinator,
                CommandLeaseManager lease, ILoggerFactory logs, CancellationToken cancellationToken) =>
                ExecuteAsync(deviceId, request.CommandId, "live.stop", context, coordinator, lease, logs,
                    ct => coordinator.StopLiveAsync(request, ct), cancellationToken))
            .RequireAuthorization(DeviceAuthorizationPolicies.Operator);

        endpoints.MapPost(DeviceApiRoutes.Parameters,
            (string deviceId, UpdateAcquisitionParametersRequest request, HttpContext context, DeviceCoordinator coordinator,
                CommandLeaseManager lease, ILoggerFactory logs, CancellationToken cancellationToken) =>
                ExecuteAsync(deviceId, request.CommandId, "parameters", context, coordinator, lease, logs,
                    ct => coordinator.UpdateParametersAsync(request, ct), cancellationToken))
            .RequireAuthorization(DeviceAuthorizationPolicies.Operator);

        endpoints.MapPost(DeviceApiRoutes.Snap,
            (string deviceId, DeviceCommandRequest request, HttpContext context, DeviceCoordinator coordinator,
                CommandLeaseManager lease, ILoggerFactory logs, CancellationToken cancellationToken) =>
                ExecuteAsync(deviceId, request.CommandId, "snap", context, coordinator, lease, logs,
                    ct => coordinator.SnapAsync(request, ct), cancellationToken))
            .RequireAuthorization(DeviceAuthorizationPolicies.Operator);

        endpoints.MapGet(DeviceApiRoutes.Images,
            (string deviceId, DeviceCoordinator coordinator, RemoteImageBuffer imageBuffer) =>
                coordinator.Matches(deviceId)
                    ? Results.Ok(imageBuffer.GetMetadata())
                    : Results.NotFound())
            .RequireAuthorization(DeviceAuthorizationPolicies.Viewer);

        endpoints.MapGet(DeviceApiRoutes.Image,
            (Guid imageId, string? mode, RemoteImageBuffer imageBuffer) =>
                GetImage(imageId, mode, imageBuffer))
            .RequireAuthorization(DeviceAuthorizationPolicies.Viewer);

        endpoints.MapGet(DeviceApiRoutes.LatestLiveFrame,
            (string deviceId, long? afterSequence, HttpContext context,
                DeviceCoordinator coordinator, LatestLiveFrameStore latestLiveFrame) =>
                GetLatestLiveFrame(deviceId, afterSequence, context, coordinator, latestLiveFrame))
            .RequireAuthorization(DeviceAuthorizationPolicies.Viewer);

        return endpoints;
    }

    private static IResult GetLatestLiveFrame(
        string deviceId,
        long? afterSequence,
        HttpContext context,
        DeviceCoordinator coordinator,
        LatestLiveFrameStore latestLiveFrame)
    {
        if (!coordinator.Matches(deviceId)) return Results.NotFound();
        if (!latestLiveFrame.TryGetNewerThan(afterSequence ?? -1, out var frame) || frame is null)
            return Results.NoContent();

        context.Response.Headers["X-XRay-Sequence"] = frame.Telemetry.SequenceNumber.ToString();
        context.Response.Headers["X-XRay-Acquisition-Id"] = frame.Telemetry.AcquisitionId.ToString("D");
        context.Response.Headers["X-XRay-Width"] = frame.Telemetry.LowWidth.ToString();
        context.Response.Headers["X-XRay-Height"] = frame.Telemetry.LowHeight.ToString();
        return Results.File(frame.LowBmp, "image/bmp");
    }

    private static IResult GetImage(Guid imageId, string? mode, RemoteImageBuffer imageBuffer)
    {
        if (string.Equals(mode, "low", StringComparison.OrdinalIgnoreCase))
        {
            return imageBuffer.TryGetLow(imageId, out var lowBmp)
                ? Results.File(lowBmp.ToArray(), "image/bmp", $"{imageId:N}.bmp")
                : Results.NotFound();
        }

        if (string.Equals(mode, "raw", StringComparison.OrdinalIgnoreCase))
        {
            if (!imageBuffer.TryGetRaw(imageId, out var frame) || frame is null)
                return Results.NotFound();
            using var stream = new MemoryStream();
            Gray16RawFileCodec.Write(stream, frame);
            return Results.File(
                stream.ToArray(),
                "application/vnd.xraysystem.gray16",
                $"{imageId:N}.xrayraw");
        }

        return Results.Problem(
            "mode query değeri 'low' veya 'raw' olmalıdır.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> ExecuteAsync<T>(
        string deviceId,
        Guid commandId,
        string operationName,
        HttpContext context,
        DeviceCoordinator coordinator,
        CommandLeaseManager lease,
        ILoggerFactory loggerFactory,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        bool releaseLease = false)
    {
        if (!coordinator.Matches(deviceId)) return Results.NotFound();

        var clientId = context.Request.Headers[DeviceProtocolHeaders.ClientId].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(clientId))
            return Results.Problem(
                $"{DeviceProtocolHeaders.ClientId} başlığı zorunludur.",
                statusCode: StatusCodes.Status400BadRequest);
        if (!lease.TryAcquire(clientId, DateTimeOffset.UtcNow))
            return Results.Problem(
                "Cihaz başka bir operatör oturumu tarafından kontrol ediliyor.",
                statusCode: StatusCodes.Status423Locked);

        var audit = loggerFactory.CreateLogger("XRaySystem.DeviceAudit");
        var subject = context.User.Identity?.Name ?? "unknown";
        var role = context.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "unknown";

        try
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);
            audit.LogInformation(
                "Device command completed: DeviceId={DeviceId} Subject={Subject} Role={Role} ClientId={ClientId} CommandId={CommandId} Operation={Operation}",
                deviceId, subject, role, clientId, commandId, operationName);
            if (releaseLease) lease.Release(clientId);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            audit.LogWarning(ex,
                "Device command rejected: DeviceId={DeviceId} Subject={Subject} Role={Role} ClientId={ClientId} CommandId={CommandId} Operation={Operation}",
                deviceId, subject, role, clientId, commandId, operationName);
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            audit.LogWarning(ex,
                "Device command conflicted: DeviceId={DeviceId} Subject={Subject} Role={Role} ClientId={ClientId} CommandId={CommandId} Operation={Operation}",
                deviceId, subject, role, clientId, commandId, operationName);
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
