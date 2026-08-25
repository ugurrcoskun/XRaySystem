using System.Collections.Concurrent;
using XRaySystem.Domain.Entities;
using XRaySystem.Domain.Interfaces;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Sensors;

namespace XRaySystem.Tests;

public sealed class CoordinatedHardwareSensorServiceTests
{
    [Fact]
    public async Task Snap_routes_generator_and_detector_settings_and_stops_exposure_before_readout()
    {
        var trace = new TraceLog();
        var generator = new TestGenerator(trace);
        var detector = new TestDetector(trace);
        using var service = CreateService(generator, detector);
        service.Parameters = new AcquisitionParameters(82, 2460, 1);
        await service.ConnectAsync();

        var measurement = await service.SnapAsync(new SnapRequest(1, SnapFrameMode.Low));

        Assert.Equal(new GeneratorTechnique(82, 2460), generator.AppliedTechnique);
        Assert.Equal(new DetectorSnapSettings(1, SnapFrameMode.Low), detector.SnapSettings);
        Assert.Equal(SnapFrameMode.Low, measurement.Frame!.SnapFrameMode);
        Assert.Equal(MeasurementProvenance.Unknown, measurement.Readings.VoltageProvenance);
        Assert.Equal(82, measurement.RequestedParameters.VoltageKv);
        Assert.Equal(2460, measurement.RequestedParameters.PowerW);
        Assert.Equal(AcquisitionCoordinatorState.Frozen, service.CoordinatorState);
        Assert.Equal(DeviceStatus.Frozen, service.CurrentStatus);
        Assert.False(generator.IsExposureActive);
        AssertOrdered(trace,
            "detector.configure.snap",
            "generator.apply",
            "detector.arm",
            "detector.armed",
            "generator.prepare",
            "generator.ready",
            "detector.snap.start",
            "generator.exposure.start",
            "generator.exposure.stop",
            "generator.exposure.stopped",
            "detector.frame.wait",
            "detector.buffer.copy",
            "detector.buffer.release",
            "detector.stop");
    }

    [Fact]
    public async Task Repeated_snap_cycles_release_every_buffer_and_return_to_xray_off()
    {
        const int cycleCount = 25;
        var trace = new TraceLog();
        var generator = new TestGenerator(trace);
        var detector = new TestDetector(trace);
        using var service = CreateService(generator, detector);
        service.Parameters = new AcquisitionParameters(70, 3500, 1);
        await service.ConnectAsync();
        var acquisitionIds = new HashSet<Guid>();

        for (var cycle = 0; cycle < cycleCount; cycle++)
        {
            var mode = cycle % 2 == 0 ? SnapFrameMode.Low : SnapFrameMode.Raw;
            var measurement = await service.SnapAsync(new SnapRequest(1, mode));

            Assert.True(acquisitionIds.Add(measurement.AcquisitionId));
            Assert.Equal(mode, measurement.SnapFrameMode);
            Assert.Equal(DeviceStatus.Frozen, service.CurrentStatus);
            Assert.False(service.HardwareState.XRayActive);
            Assert.False(service.HardwareState.DetectorAcquiring);
            Assert.Null(service.HardwareState.ActiveAcquisitionId);
        }

        Assert.Equal(cycleCount, generator.ExposureStartCount);
        Assert.True(generator.ExposureStopCount >= cycleCount);
        Assert.Equal(cycleCount, trace.Items.Count(item => item == "detector.buffer.release"));
        Assert.Equal(cycleCount, trace.Items.Count(item => item == "detector.stop"));
    }

    [Fact]
    public async Task Generator_ready_timeout_prevents_exposure_and_runs_safe_stop()
    {
        var trace = new TraceLog();
        var generator = new TestGenerator(trace) { BlockReady = true };
        var detector = new TestDetector(trace);
        using var service = CreateService(generator, detector, timeoutMs: 35);
        service.Parameters = new AcquisitionParameters(70, 3500, 1);
        await service.ConnectAsync();

        var error = await Assert.ThrowsAsync<AcquisitionTimeoutException>(() =>
            service.SnapAsync(new SnapRequest(1, SnapFrameMode.Raw)));

        Assert.Equal("generator.ready", error.Stage);
        Assert.Equal(0, generator.ExposureStartCount);
        Assert.True(generator.ExposureStopCount >= 1);
        Assert.False(generator.IsExposureActive);
        Assert.Equal(DeviceStatus.Error, service.CurrentStatus);
    }

    [Fact]
    public async Task Detector_arm_timeout_prevents_generator_prepare_and_exposure()
    {
        var trace = new TraceLog();
        var generator = new TestGenerator(trace);
        var detector = new TestDetector(trace) { BlockArmed = true };
        using var service = CreateService(generator, detector, timeoutMs: 35);
        service.Parameters = new AcquisitionParameters(70, 3500, 1);
        await service.ConnectAsync();

        var error = await Assert.ThrowsAsync<AcquisitionTimeoutException>(() =>
            service.SnapAsync(new SnapRequest(1, SnapFrameMode.Low)));

        Assert.Equal("detector.arm", error.Stage);
        Assert.DoesNotContain("generator.prepare", trace.Items);
        Assert.DoesNotContain("generator.exposure.start", trace.Items);
        Assert.False(generator.IsExposureActive);
    }

    [Fact]
    public async Task Frame_ready_timeout_occurs_only_after_generator_is_confirmed_off()
    {
        var trace = new TraceLog();
        var generator = new TestGenerator(trace);
        var detector = new TestDetector(trace) { BlockFrame = true };
        using var service = CreateService(generator, detector, timeoutMs: 35);
        service.Parameters = new AcquisitionParameters(70, 3500, 1);
        await service.ConnectAsync();

        var error = await Assert.ThrowsAsync<AcquisitionTimeoutException>(() =>
            service.SnapAsync(new SnapRequest(1, SnapFrameMode.Raw)));

        Assert.Equal("detector.frame-ready", error.Stage);
        Assert.Equal(1, generator.ExposureStartCount);
        Assert.False(generator.IsExposureActive);
        Assert.True(trace.IndexOf("generator.exposure.stopped") < trace.IndexOf("detector.frame.wait"));
        Assert.DoesNotContain("detector.buffer.copy", trace.Items);
    }

    [Fact]
    public async Task Invalid_buffer_is_rejected_and_vendor_token_is_still_released()
    {
        var trace = new TraceLog();
        var generator = new TestGenerator(trace);
        var detector = new TestDetector(trace) { ReturnInvalidPixelCount = true };
        using var service = CreateService(generator, detector);
        service.Parameters = new AcquisitionParameters(70, 3500, 1);
        await service.ConnectAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SnapAsync(new SnapRequest(1, SnapFrameMode.Raw)));

        Assert.False(generator.IsExposureActive);
        Assert.Contains("detector.buffer.release", trace.Items);
        Assert.True(trace.IndexOf("detector.buffer.copy") < trace.IndexOf("detector.buffer.release"));
    }

    [Fact]
    public async Task Wrong_acquisition_notification_is_rejected_before_buffer_copy_and_released()
    {
        var trace = new TraceLog();
        var generator = new TestGenerator(trace);
        var detector = new TestDetector(trace) { ReturnWrongAcquisitionId = true };
        using var service = CreateService(generator, detector);
        service.Parameters = new AcquisitionParameters(70, 3500, 1);
        await service.ConnectAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SnapAsync(new SnapRequest(1, SnapFrameMode.Raw)));

        Assert.False(generator.IsExposureActive);
        Assert.DoesNotContain("detector.buffer.copy", trace.Items);
        Assert.Contains("detector.buffer.release", trace.Items);
    }

    [Fact]
    public async Task Detector_stop_timeout_is_bounded_after_generator_has_stopped()
    {
        var trace = new TraceLog();
        var generator = new TestGenerator(trace);
        var detector = new TestDetector(trace) { BlockStop = true };
        using var service = CreateService(generator, detector, timeoutMs: 35);
        service.Parameters = new AcquisitionParameters(70, 3500, 1);
        await service.ConnectAsync();

        var startedAt = DateTime.UtcNow;
        var error = await Assert.ThrowsAsync<AcquisitionTimeoutException>(() =>
            service.SnapAsync(new SnapRequest(1, SnapFrameMode.Low)));

        Assert.Equal("detector.stop", error.Stage);
        Assert.True(DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(1));
        Assert.False(generator.IsExposureActive);
        Assert.Equal(DeviceStatus.Error, service.CurrentStatus);
    }

    [Fact]
    public async Task Freeze_orders_detector_freeze_then_generator_off_then_detector_stop()
    {
        var trace = new TraceLog();
        var generator = new TestGenerator(trace);
        var detector = new TestDetector(trace) { BlockFrame = true };
        using var service = CreateService(generator, detector);
        service.Parameters = new AcquisitionParameters(70, 3500, 10);
        await service.ConnectAsync();
        await service.StartCaptureAsync();
        Assert.True(generator.IsExposureActive);
        Assert.True(service.HardwareState.XRayActive);
        Assert.True(service.HardwareState.DetectorAcquiring);
        Assert.True(service.HardwareState.IsVerified);
        Assert.NotNull(service.HardwareState.ActiveAcquisitionId);

        await service.StopCaptureAsync();

        Assert.False(generator.IsExposureActive);
        Assert.Equal(DeviceStatus.Frozen, service.CurrentStatus);
        Assert.False(service.HardwareState.XRayActive);
        Assert.False(service.HardwareState.DetectorAcquiring);
        Assert.Null(service.HardwareState.ActiveAcquisitionId);
        AssertOrdered(trace,
            "detector.freeze",
            "generator.exposure.stop",
            "generator.exposure.stopped",
            "detector.stop");
    }

    [Fact]
    public async Task Snap_during_grab_completes_atomic_freeze_and_stop_before_snap_configuration()
    {
        var trace = new TraceLog();
        var generator = new TestGenerator(trace);
        var detector = new TestDetector(trace) { BlockFrame = true };
        using var service = CreateService(generator, detector);
        service.Parameters = new AcquisitionParameters(70, 3500, 1);
        await service.ConnectAsync();
        await service.StartCaptureAsync();
        Assert.Equal(DeviceStatus.Grabbing, service.CurrentStatus);

        detector.BlockFrame = false;
        var measurement = await service.SnapAsync(new SnapRequest(1, SnapFrameMode.Low));

        Assert.NotNull(measurement.Frame);
        Assert.Equal(AcquisitionOperation.Snap, measurement.Frame.Operation);
        AssertOrdered(trace,
            "detector.grab.start",
            "detector.freeze",
            "generator.exposure.stop",
            "generator.exposure.stopped",
            "detector.stop",
            "detector.configure.snap",
            "detector.arm",
            "detector.snap.start",
            "generator.exposure.start");
    }

    [Fact]
    public async Task Abort_preempts_active_exposure_and_uses_generator_first_safety_order()
    {
        var trace = new TraceLog();
        var generator = new TestGenerator(trace);
        var detector = new TestDetector(trace);
        using var service = CreateService(generator, detector);
        service.Parameters = new AcquisitionParameters(70, 3500, 1000);
        await service.ConnectAsync();
        var snap = service.SnapAsync(new SnapRequest(1000, SnapFrameMode.Raw));
        await generator.ExposureStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await service.AbortAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snap);

        Assert.False(generator.IsExposureActive);
        Assert.Equal(DeviceStatus.Connected, service.CurrentStatus);
        var finalGeneratorStop = trace.LastIndexOf("generator.exposure.stop");
        var finalDetectorStop = trace.LastIndexOf("detector.stop");
        Assert.True(finalGeneratorStop >= 0 && finalGeneratorStop < finalDetectorStop);
    }

    [Fact]
    public async Task Grab_watchdog_aborts_radiation_and_clears_the_active_session()
    {
        var trace = new TraceLog();
        var generator = new TestGenerator(trace);
        var detector = new TestDetector(trace) { BlockFrame = true };
        using var service = CreateService(generator, detector, maxGrabMs: 40);
        service.Parameters = new AcquisitionParameters(70, 3500, 10);
        await service.ConnectAsync();
        await service.StartCaptureAsync();

        for (var attempt = 0; attempt < 100 && service.CurrentStatus != DeviceStatus.Error; attempt++)
            await Task.Delay(10);

        Assert.Equal(DeviceStatus.Error, service.CurrentStatus);
        Assert.Equal(AcquisitionCoordinatorState.Faulted, service.CoordinatorState);
        Assert.False(generator.IsExposureActive);
        Assert.False(service.HardwareState.DetectorAcquiring);
        Assert.Null(service.HardwareState.ActiveAcquisitionId);
        Assert.True(trace.IndexOf("generator.exposure.stop") < trace.IndexOf("detector.freeze"));
    }

    [Fact]
    public async Task Unsupported_snap_mode_is_rejected_before_generator_preparation()
    {
        var trace = new TraceLog();
        var generator = new TestGenerator(trace);
        var detector = new TestDetector(trace) { SupportRaw = false };
        using var service = CreateService(generator, detector);
        await service.ConnectAsync();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.SnapAsync(new SnapRequest(1, SnapFrameMode.Raw)));

        Assert.DoesNotContain("generator.apply", trace.Items);
        Assert.DoesNotContain("generator.prepare", trace.Items);
        Assert.DoesNotContain("generator.exposure.start", trace.Items);
    }

    [Fact]
    public async Task Snap_publishes_coordinator_stage_changes_and_only_reports_verified_active_exposure()
    {
        var trace = new TraceLog();
        var generator = new TestGenerator(trace);
        var detector = new TestDetector(trace);
        using var service = CreateService(generator, detector);
        service.Parameters = new AcquisitionParameters(70, 3500, 1);
        var observed = new List<(AcquisitionCoordinatorState State, bool XRayActive)>();
        var hardwareObserved = new List<AcquisitionHardwareState>();
        service.StatusChanged += _ =>
            observed.Add((service.CoordinatorState, service.HardwareState.XRayActive));
        service.HardwareStateChanged += hardwareObserved.Add;
        await service.ConnectAsync();

        await service.SnapAsync(new SnapRequest(1, SnapFrameMode.Raw));

        Assert.Contains((AcquisitionCoordinatorState.Configuring, false), observed);
        Assert.Contains((AcquisitionCoordinatorState.Preparing, false), observed);
        Assert.Contains((AcquisitionCoordinatorState.Armed, false), observed);
        Assert.Contains((AcquisitionCoordinatorState.Exposing, true), observed);
        Assert.Contains((AcquisitionCoordinatorState.ReadingOut, false), observed);
        Assert.Equal((AcquisitionCoordinatorState.Frozen, false), observed[^1]);
        Assert.False(hardwareObserved[^1].XRayActive);
        Assert.Null(hardwareObserved[^1].ActiveAcquisitionId);
        Assert.True(
            observed.FindIndex(item => item.State == AcquisitionCoordinatorState.Exposing) <
            observed.FindIndex(item => item.State == AcquisitionCoordinatorState.ReadingOut));
    }

    private static CoordinatedHardwareSensorService CreateService(
        IXRayGenerator generator,
        IXRayDetector detector,
        int timeoutMs = 250,
        int maxGrabMs = 2000) =>
        new(generator, detector, new AcquisitionSafetyOptions(
            GeneratorReadyTimeout: TimeSpan.FromMilliseconds(timeoutMs),
            DetectorArmTimeout: TimeSpan.FromMilliseconds(timeoutMs),
            ExposureStartTimeout: TimeSpan.FromMilliseconds(timeoutMs),
            MaximumExposureDuration: TimeSpan.FromSeconds(2),
            MaximumGrabDuration: TimeSpan.FromMilliseconds(maxGrabMs),
            ExposureStopConfirmationTimeout: TimeSpan.FromMilliseconds(timeoutMs),
            DetectorStopTimeout: TimeSpan.FromMilliseconds(timeoutMs),
            FrameReadyTimeout: TimeSpan.FromMilliseconds(timeoutMs),
            BufferCopyTimeout: TimeSpan.FromMilliseconds(timeoutMs)));

    private static void AssertOrdered(TraceLog trace, params string[] operations)
    {
        var previous = -1;
        foreach (var operation in operations)
        {
            var current = trace.IndexOf(operation, previous + 1);
            Assert.True(current > previous, $"'{operation}' beklenen sırada yok. Trace: {string.Join(", ", trace.Items)}");
            previous = current;
        }
    }

    private sealed class TraceLog
    {
        private readonly ConcurrentQueue<string> _items = new();
        public IReadOnlyList<string> Items => _items.ToArray();
        public void Add(string value) => _items.Enqueue(value);
        public int IndexOf(string value, int start = 0)
        {
            var snapshot = Items;
            for (var index = Math.Max(0, start); index < snapshot.Count; index++)
                if (snapshot[index] == value) return index;
            return -1;
        }
        public int LastIndexOf(string value)
        {
            var snapshot = Items;
            for (var index = snapshot.Count - 1; index >= 0; index--)
                if (snapshot[index] == value) return index;
            return -1;
        }
    }

    private sealed class TestGenerator(TraceLog trace) : IXRayGenerator
    {
        public bool IsConnected { get; private set; }
        public bool IsExposureActive { get; private set; }
        public bool BlockReady { get; init; }
        public GeneratorTechnique? AppliedTechnique { get; private set; }
        public int ExposureStartCount { get; private set; }
        public int ExposureStopCount { get; private set; }
        public TaskCompletionSource<bool> ExposureStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            trace.Add("generator.connect");
            IsConnected = true;
            return Task.CompletedTask;
        }
        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            trace.Add("generator.disconnect");
            IsExposureActive = false;
            IsConnected = false;
            return Task.CompletedTask;
        }
        public Task ApplyTechniqueAsync(GeneratorTechnique technique, CancellationToken cancellationToken)
        {
            trace.Add("generator.apply");
            AppliedTechnique = technique;
            return Task.CompletedTask;
        }
        public Task PrepareAsync(CancellationToken cancellationToken)
        {
            trace.Add("generator.prepare");
            return Task.CompletedTask;
        }
        public async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            trace.Add("generator.ready");
            if (BlockReady) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public Task StartExposureAsync(Guid acquisitionId, CancellationToken cancellationToken)
        {
            trace.Add("generator.exposure.start");
            ExposureStartCount++;
            IsExposureActive = true;
            ExposureStarted.TrySetResult(true);
            return Task.CompletedTask;
        }
        public Task StopExposureAsync(CancellationToken cancellationToken)
        {
            trace.Add("generator.exposure.stop");
            ExposureStopCount++;
            IsExposureActive = false;
            return Task.CompletedTask;
        }
        public Task WaitUntilExposureStoppedAsync(CancellationToken cancellationToken)
        {
            trace.Add("generator.exposure.stopped");
            if (IsExposureActive) throw new InvalidOperationException("Exposure aktif.");
            return Task.CompletedTask;
        }
        public Task<GeneratorTelemetry> ReadTelemetryAsync(CancellationToken cancellationToken)
        {
            trace.Add("generator.telemetry");
            var technique = AppliedTechnique ?? new GeneratorTechnique(70, 3500);
            return Task.FromResult(new GeneratorTelemetry(
                technique.VoltageKv,
                technique.PowerW / technique.VoltageKv,
                1,
                0));
        }
    }

    private sealed class TestDetector(TraceLog trace) : IXRayDetector
    {
        private Guid _acquisitionId;
        public bool IsConnected { get; private set; }
        public bool IsAcquiring { get; private set; }
        public bool BlockFrame { get; set; }
        public bool BlockArmed { get; init; }
        public bool BlockStop { get; init; }
        public bool ReturnInvalidPixelCount { get; init; }
        public bool ReturnWrongAcquisitionId { get; init; }
        public bool SupportRaw { get; init; } = true;
        public DetectorSnapSettings? SnapSettings { get; private set; }

        public bool SupportsSnapMode(SnapFrameMode frameMode) =>
            frameMode == SnapFrameMode.Low || SupportRaw;
        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            trace.Add("detector.connect");
            IsConnected = true;
            return Task.CompletedTask;
        }
        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            trace.Add("detector.disconnect");
            IsAcquiring = false;
            IsConnected = false;
            return Task.CompletedTask;
        }
        public Task ConfigureSnapAsync(Guid acquisitionId, DetectorSnapSettings settings, CancellationToken cancellationToken)
        {
            trace.Add("detector.configure.snap");
            _acquisitionId = acquisitionId;
            SnapSettings = settings;
            return Task.CompletedTask;
        }
        public Task ConfigureGrabAsync(Guid grabSessionId, DetectorGrabSettings settings, CancellationToken cancellationToken)
        {
            trace.Add("detector.configure.grab");
            _acquisitionId = grabSessionId;
            return Task.CompletedTask;
        }
        public Task ArmAsync(Guid acquisitionId, CancellationToken cancellationToken)
        {
            trace.Add("detector.arm");
            return Task.CompletedTask;
        }
        public async Task WaitUntilArmedAsync(Guid acquisitionId, CancellationToken cancellationToken)
        {
            trace.Add("detector.armed");
            if (BlockArmed) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public Task StartSnapAsync(Guid acquisitionId, CancellationToken cancellationToken)
        {
            trace.Add("detector.snap.start");
            IsAcquiring = true;
            return Task.CompletedTask;
        }
        public Task StartGrabAsync(Guid grabSessionId, CancellationToken cancellationToken)
        {
            trace.Add("detector.grab.start");
            IsAcquiring = true;
            return Task.CompletedTask;
        }
        public Task FreezeAsync(Guid grabSessionId, CancellationToken cancellationToken)
        {
            trace.Add("detector.freeze");
            IsAcquiring = false;
            return Task.CompletedTask;
        }
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            trace.Add("detector.stop");
            IsAcquiring = false;
            if (BlockStop) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public async Task<DetectorFrameToken> WaitForFrameAsync(CancellationToken cancellationToken)
        {
            trace.Add("detector.frame.wait");
            if (BlockFrame) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new DetectorFrameToken(
                Guid.NewGuid(),
                ReturnWrongAcquisitionId ? Guid.NewGuid() : _acquisitionId,
                1,
                2,
                2,
                4,
                Gray16PixelFormat.Gray16LittleEndian,
                "test-detector");
        }
        public Task<ushort[]> CopyFrameAsync(DetectorFrameToken token, CancellationToken cancellationToken)
        {
            trace.Add("detector.buffer.copy");
            return Task.FromResult(ReturnInvalidPixelCount
                ? new ushort[] { 1, 2, 3 }
                : new ushort[] { 1, 2, 3, 4 });
        }
        public Task ReleaseFrameAsync(DetectorFrameToken token, CancellationToken cancellationToken)
        {
            trace.Add("detector.buffer.release");
            return Task.CompletedTask;
        }
    }
}
