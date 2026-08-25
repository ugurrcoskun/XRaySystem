using XRaySystem.Contracts;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Storage;

namespace XRaySystem.DeviceHost;

public static class DeviceApi
{
    public static IEndpointRouteBuilder MapDeviceApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(DeviceApiRoutes.Protocol, (DeviceCoordinator coordinator) =>
            Results.Ok(coordinator.GetProtocol()))
            .RequireAuthorization(DeviceAuthorizationPolicies.Viewer);

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

        endpoints.MapPost(DeviceApiRoutes.StartGrab,
            (string deviceId, DeviceCommandRequest request, HttpContext context, DeviceCoordinator coordinator,
                CommandLeaseManager lease, ILoggerFactory logs, CancellationToken cancellationToken) =>
                ExecuteAsync(deviceId, request.CommandId, "grab.start", context, coordinator, lease, logs,
                    ct => coordinator.StartGrabAsync(request, ct), cancellationToken))
            .RequireAuthorization(DeviceAuthorizationPolicies.Operator);

        endpoints.MapPost(DeviceApiRoutes.StopLive,
            (string deviceId, DeviceCommandRequest request, HttpContext context, DeviceCoordinator coordinator,
                CommandLeaseManager lease, ILoggerFactory logs, CancellationToken cancellationToken) =>
                ExecuteAsync(deviceId, request.CommandId, "live.stop", context, coordinator, lease, logs,
                    ct => coordinator.StopLiveAsync(request, ct), cancellationToken))
            .RequireAuthorization(DeviceAuthorizationPolicies.Operator);

        endpoints.MapPost(DeviceApiRoutes.FreezeGrab,
            (string deviceId, DeviceCommandRequest request, HttpContext context, DeviceCoordinator coordinator,
                CommandLeaseManager lease, ILoggerFactory logs, CancellationToken cancellationToken) =>
                ExecuteAsync(deviceId, request.CommandId, "grab.freeze", context, coordinator, lease, logs,
                    ct => coordinator.FreezeGrabAsync(request, ct), cancellationToken))
            .RequireAuthorization(DeviceAuthorizationPolicies.Operator);

        endpoints.MapPost(DeviceApiRoutes.Abort,
            (string deviceId, DeviceCommandRequest request, HttpContext context, DeviceCoordinator coordinator,
                CommandLeaseManager lease, ILoggerFactory logs, CancellationToken cancellationToken) =>
                ExecuteAsync(deviceId, request.CommandId, "acquisition.abort", context, coordinator, lease, logs,
                    ct => coordinator.AbortAsync(request, ct), cancellationToken))
            .RequireAuthorization(DeviceAuthorizationPolicies.Operator);

        endpoints.MapPost(DeviceApiRoutes.Parameters,
            (string deviceId, UpdateAcquisitionParametersRequest request, HttpContext context, DeviceCoordinator coordinator,
                CommandLeaseManager lease, ILoggerFactory logs, CancellationToken cancellationToken) =>
                ExecuteAsync(deviceId, request.CommandId, "parameters", context, coordinator, lease, logs,
                    ct => coordinator.UpdateParametersAsync(request, ct), cancellationToken))
            .RequireAuthorization(DeviceAuthorizationPolicies.Operator);

        endpoints.MapPost(DeviceApiRoutes.Snap,
            (string deviceId, SnapCommandRequest request, HttpContext context, DeviceCoordinator coordinator,
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

        endpoints.MapGet(DeviceApiRoutes.LatestGrabFrame,
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
        if (commandId == Guid.Empty)
            return Results.Problem(
                detail: "commandId boş GUID olamaz.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "invalid_command_id"
                });
        try
        {
            CommandLeaseManager.ValidateClientId(clientId);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        var audit = loggerFactory.CreateLogger("XRaySystem.DeviceAudit");
        var durableAudit = context.RequestServices.GetRequiredService<IDeviceCommandAuditStore>();
        var subject = context.User.Identity?.Name ?? "unknown";
        var role = context.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "unknown";
        if (durableAudit.WasCommandObservedBeforeStartup(commandId))
        {
            try
            {
                await durableAudit.AppendAsync(
                    CreateAuditEvent(
                        deviceId, subject, role, clientId, commandId, operationName,
                        "replay_rejected", StatusCodes.Status409Conflict, "command_replay_after_restart"),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AuditPersistenceException or IOException or UnauthorizedAccessException)
            {
                audit.LogError(ex,
                    "Restart replay rejection could not be audited: DeviceId={DeviceId} Subject={Subject} ClientId={ClientId} CommandId={CommandId}",
                    deviceId, subject, clientId, commandId);
                return Results.Problem(
                    "Kalıcı komut audit kaydı kullanılamıyor; güvenlik nedeniyle komut çalıştırılmadı.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            audit.LogWarning(
                "Command replay from a previous host lifetime rejected: DeviceId={DeviceId} Subject={Subject} ClientId={ClientId} CommandId={CommandId} Operation={Operation}",
                deviceId, subject, clientId, commandId, operationName);
            return Results.Problem(
                detail: "Bu commandId önceki DeviceHost çalışma döneminde görülmüş; olası tekrar pozlamayı önlemek için komut çalıştırılmadı.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "command_replay_after_restart"
                });
        }

        try
        {
            if (!lease.TryAcquire(clientId, DateTimeOffset.UtcNow))
                return Results.Problem(
                    "Cihaz başka bir operatör oturumu tarafından kontrol ediliyor.",
                    statusCode: StatusCodes.Status423Locked);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            await durableAudit.AppendAsync(
                CreateAuditEvent(
                    deviceId, subject, role, clientId, commandId, operationName,
                    "started", null, null),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuditPersistenceException or IOException or UnauthorizedAccessException)
        {
            audit.LogError(ex,
                "Device command rejected because durable audit is unavailable: DeviceId={DeviceId} Subject={Subject} Role={Role} ClientId={ClientId} CommandId={CommandId} Operation={Operation}",
                deviceId, subject, role, clientId, commandId, operationName);
            return Results.Problem(
                "Kalıcı komut audit kaydı kullanılamıyor; güvenlik nedeniyle komut çalıştırılmadı.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);
            try
            {
                await durableAudit.AppendAsync(
                    CreateAuditEvent(
                        deviceId, subject, role, clientId, commandId, operationName,
                        "succeeded", StatusCodes.Status200OK, null),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AuditPersistenceException or IOException or UnauthorizedAccessException)
            {
                audit.LogError(ex,
                    "Device command completed but durable outcome audit failed: DeviceId={DeviceId} Subject={Subject} Role={Role} ClientId={ClientId} CommandId={CommandId} Operation={Operation}",
                    deviceId, subject, role, clientId, commandId, operationName);
                return Results.Problem(
                    "Cihaz komutu işlendi ancak sonuç audit kaydı doğrulanamadı; aynı commandId ile durum sorgulanmalıdır.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            audit.LogInformation(
                "Device command completed: DeviceId={DeviceId} Subject={Subject} Role={Role} ClientId={ClientId} CommandId={CommandId} Operation={Operation}",
                deviceId, subject, role, clientId, commandId, operationName);
            if (releaseLease) lease.Release(clientId);
            return Results.Ok(result);
        }
        catch (AcquisitionTimeoutException ex)
        {
            await TryAppendAuditOutcomeAsync(
                durableAudit, audit, deviceId, subject, role, clientId, commandId, operationName,
                "failed", StatusCodes.Status504GatewayTimeout, "acquisition_timeout").ConfigureAwait(false);
            audit.LogError(ex,
                "Device command timed out safely: DeviceId={DeviceId} Subject={Subject} Role={Role} ClientId={ClientId} CommandId={CommandId} Operation={Operation} Stage={Stage}",
                deviceId, subject, role, clientId, commandId, operationName, ex.Stage);
            return Results.Problem(
                $"Cihaz işlemi zaman aşımına uğradı: {ex.Stage}.",
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (InvalidDataException ex)
        {
            await TryAppendAuditOutcomeAsync(
                durableAudit, audit, deviceId, subject, role, clientId, commandId, operationName,
                "failed", StatusCodes.Status502BadGateway, "invalid_frame").ConfigureAwait(false);
            audit.LogError(ex,
                "Device returned invalid frame data: DeviceId={DeviceId} Subject={Subject} Role={Role} ClientId={ClientId} CommandId={CommandId} Operation={Operation}",
                deviceId, subject, role, clientId, commandId, operationName);
            return Results.Problem(
                "Cihaz geçersiz görüntü verisi döndürdü; acquisition güvenli durduruldu.",
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (AcquisitionPersistenceException ex)
        {
            await TryAppendAuditOutcomeAsync(
                durableAudit, audit, deviceId, subject, role, clientId, commandId, operationName,
                "failed", StatusCodes.Status507InsufficientStorage, "persistence_failed").ConfigureAwait(false);
            audit.LogError(ex,
                "Acquisition persistence failed: DeviceId={DeviceId} Subject={Subject} Role={Role} ClientId={ClientId} CommandId={CommandId} Operation={Operation}",
                deviceId, subject, role, clientId, commandId, operationName);
            return Results.Problem(
                "Görüntü cihaz bilgisayarında kalıcı olarak doğrulanamadı; SNAP başarılı sayılmadı.",
                statusCode: StatusCodes.Status507InsufficientStorage);
        }
        catch (OperationCanceledException)
        {
            await TryAppendAuditOutcomeAsync(
                durableAudit, audit, deviceId, subject, role, clientId, commandId, operationName,
                "cancelled", StatusCodes.Status408RequestTimeout, "command_cancelled").ConfigureAwait(false);
            audit.LogWarning(
                "Device command cancelled: DeviceId={DeviceId} Subject={Subject} Role={Role} ClientId={ClientId} CommandId={CommandId} Operation={Operation}",
                deviceId, subject, role, clientId, commandId, operationName);
            return Results.Problem(
                "Cihaz komutu iptal edildi.",
                statusCode: StatusCodes.Status408RequestTimeout);
        }
        catch (ArgumentException ex)
        {
            await TryAppendAuditOutcomeAsync(
                durableAudit, audit, deviceId, subject, role, clientId, commandId, operationName,
                "rejected", StatusCodes.Status400BadRequest, "invalid_argument").ConfigureAwait(false);
            audit.LogWarning(ex,
                "Device command rejected: DeviceId={DeviceId} Subject={Subject} Role={Role} ClientId={ClientId} CommandId={CommandId} Operation={Operation}",
                deviceId, subject, role, clientId, commandId, operationName);
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            await TryAppendAuditOutcomeAsync(
                durableAudit, audit, deviceId, subject, role, clientId, commandId, operationName,
                "rejected", StatusCodes.Status409Conflict, "invalid_operation").ConfigureAwait(false);
            audit.LogWarning(ex,
                "Device command conflicted: DeviceId={DeviceId} Subject={Subject} Role={Role} ClientId={ClientId} CommandId={CommandId} Operation={Operation}",
                deviceId, subject, role, clientId, commandId, operationName);
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception ex)
        {
            await TryAppendAuditOutcomeAsync(
                durableAudit, audit, deviceId, subject, role, clientId, commandId, operationName,
                "failed", StatusCodes.Status500InternalServerError, "command_failed").ConfigureAwait(false);
            audit.LogError(ex,
                "Device command failed: DeviceId={DeviceId} Subject={Subject} Role={Role} ClientId={ClientId} CommandId={CommandId} Operation={Operation}",
                deviceId, subject, role, clientId, commandId, operationName);
            return Results.Problem(
                "Cihaz komutu güvenli biçimde tamamlanamadı.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static DeviceCommandAuditEvent CreateAuditEvent(
        string deviceId,
        string subject,
        string role,
        string clientId,
        Guid commandId,
        string operation,
        string outcome,
        int? httpStatus,
        string? errorCode) => new(
            DateTimeOffset.UtcNow,
            deviceId,
            subject,
            role,
            clientId,
            commandId,
            operation,
            outcome,
            httpStatus,
            errorCode);

    private static async Task TryAppendAuditOutcomeAsync(
        IDeviceCommandAuditStore durableAudit,
        ILogger auditLogger,
        string deviceId,
        string subject,
        string role,
        string clientId,
        Guid commandId,
        string operation,
        string outcome,
        int httpStatus,
        string errorCode)
    {
        try
        {
            await durableAudit.AppendAsync(
                CreateAuditEvent(
                    deviceId, subject, role, clientId, commandId, operation,
                    outcome, httpStatus, errorCode),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuditPersistenceException or IOException or UnauthorizedAccessException)
        {
            auditLogger.LogError(ex,
                "Device command outcome audit failed: DeviceId={DeviceId} Subject={Subject} Role={Role} ClientId={ClientId} CommandId={CommandId} Operation={Operation} Outcome={Outcome}",
                deviceId, subject, role, clientId, commandId, operation, outcome);
        }
    }
}
