using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.Infrastructure.Sensors;

/// <summary>
/// DeviceHost hardware-boundary simulator. It models generator ownership and
/// exposure state; it is not a claim about any vendor generator protocol.
/// </summary>
public sealed class MockXRayGenerator : IXRayGenerator
{
    private readonly object _gate = new();
    private GeneratorTechnique _technique = new(70, 3500);
    private bool _isConnected;
    private bool _isPrepared;
    private bool _isExposureActive;

    public bool IsConnected { get { lock (_gate) return _isConnected; } }
    public bool IsExposureActive { get { lock (_gate) return _isExposureActive; } }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _isConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _isExposureActive = false;
            _isPrepared = false;
            _isConnected = false;
        }
        return Task.CompletedTask;
    }

    public Task ApplyTechniqueAsync(GeneratorTechnique technique, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(technique);
        technique.EnsureValid();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureConnected();
            if (_isExposureActive)
                throw new InvalidOperationException("Exposure aktifken generator tekniği değiştirilemez.");
            _technique = technique;
        }
        return Task.CompletedTask;
    }

    public Task PrepareAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureConnected();
            _isPrepared = true;
        }
        return Task.CompletedTask;
    }

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureConnected();
            if (!_isPrepared)
                throw new InvalidOperationException("Generator prepare edilmedi.");
        }
        return Task.CompletedTask;
    }

    public Task StartExposureAsync(Guid acquisitionId, CancellationToken cancellationToken)
    {
        if (acquisitionId == Guid.Empty)
            throw new ArgumentException("Acquisition ID boş olamaz.", nameof(acquisitionId));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureConnected();
            if (!_isPrepared)
                throw new InvalidOperationException("Generator hazır değil.");
            _isExposureActive = true;
        }
        return Task.CompletedTask;
    }

    public Task StopExposureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _isExposureActive = false;
        return Task.CompletedTask;
    }

    public Task WaitUntilExposureStoppedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_isExposureActive)
                throw new InvalidOperationException("Generator exposure hâlâ aktif.");
        }
        return Task.CompletedTask;
    }

    public Task<GeneratorTelemetry> ReadTelemetryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureConnected();
            return Task.FromResult(new GeneratorTelemetry(
                _technique.VoltageKv,
                _technique.PowerW / _technique.VoltageKv,
                XRayIntensity: _technique.PowerW / 1000.0,
                ExposureTimeMs: 0,
                VoltageProvenance: MeasurementProvenance.AppliedTarget,
                TubeCurrentProvenance: MeasurementProvenance.Derived,
                ExposureTimeProvenance: MeasurementProvenance.AppliedTarget));
        }
    }

    private void EnsureConnected()
    {
        if (!_isConnected)
            throw new InvalidOperationException("Generator bağlı değil.");
    }
}
