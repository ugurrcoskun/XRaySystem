using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using XRaySystem.Infrastructure.Persistence;

namespace XRaySystem.Mobile.Services;

public sealed class DatabaseInitializationState
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _gate = new();
    private Task? _initializationTask;
    private int _isReady;
    private Exception? _failure;

    public DatabaseInitializationState(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public bool IsReady => Volatile.Read(ref _isReady) != 0;
    public bool HasFailed => Failure is not null;
    public Exception? Failure => Volatile.Read(ref _failure);

    public Task EnsureInitializedAsync()
    {
        lock (_gate)
            return _initializationTask ??= Task.Run(InitializeCore);
    }

    private void InitializeCore()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<XRayDbContext>();
            LegacyDatabaseBaseliner.Prepare(db);
            db.Database.Migrate();
            MarkReady();
        }
        catch (Exception ex)
        {
            MarkFailed(ex);
        }
    }

    private void MarkReady()
    {
        Volatile.Write(ref _failure, null);
        Volatile.Write(ref _isReady, 1);
    }

    private void MarkFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Volatile.Write(ref _isReady, 0);
        Volatile.Write(ref _failure, exception);
    }
}
