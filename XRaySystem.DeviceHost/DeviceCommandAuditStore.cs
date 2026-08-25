using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XRaySystem.DeviceHost;

public sealed record DeviceCommandAuditOptions(
    bool Enabled,
    string FilePath,
    bool RequireSignedEntries = false,
    string? IntegrityKey = null,
    long MaximumFileBytes = 64L * 1024 * 1024)
{
    public void EnsureValid()
    {
        if (!Enabled) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(FilePath);
        var fullPath = Path.GetFullPath(FilePath);
        if (string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Audit hedefi bir disk kökü olamaz.", nameof(FilePath));
        if (RequireSignedEntries && string.IsNullOrWhiteSpace(IntegrityKey))
            throw new ArgumentException("İmzalı audit kayıtları için ayrı integrity key zorunludur.", nameof(IntegrityKey));
        if (!string.IsNullOrWhiteSpace(IntegrityKey) && IntegrityKey.Trim().Length < 32)
            throw new ArgumentException("Audit integrity key en az 32 karakter olmalıdır.", nameof(IntegrityKey));
        if (MaximumFileBytes is < 256 or > 10L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumFileBytes));
    }
}

public sealed record DeviceCommandAuditEvent(
    DateTimeOffset TimestampUtc,
    string DeviceId,
    string Subject,
    string Role,
    string ClientId,
    Guid CommandId,
    string Operation,
    string Outcome,
    int? HttpStatus,
    string? ErrorCode);

public sealed class AuditPersistenceException : IOException
{
    public AuditPersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IDeviceCommandAuditStore
{
    bool WasCommandObservedBeforeStartup(Guid commandId);
    Task AppendAsync(DeviceCommandAuditEvent auditEvent, CancellationToken cancellationToken = default);
}

public sealed class HashChainedJsonlDeviceAuditStore : IDeviceCommandAuditStore
{
    private const int SchemaVersion = 1;
    private const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DeviceCommandAuditOptions _options;
    private readonly byte[]? _integrityKey;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly HashSet<Guid> _commandsObservedBeforeStartup = [];
    private long _lastSequence;
    private string _lastEntryHash = GenesisHash;

    public HashChainedJsonlDeviceAuditStore(DeviceCommandAuditOptions options)
    {
        options.EnsureValid();
        _options = options;
        _integrityKey = string.IsNullOrWhiteSpace(options.IntegrityKey)
            ? null
            : SHA256.HashData(Encoding.UTF8.GetBytes(options.IntegrityKey.Trim()));
        if (!options.Enabled) return;

        var directory = Path.GetDirectoryName(Path.GetFullPath(options.FilePath))
            ?? throw new ArgumentException("Audit dosya klasörü belirlenemedi.", nameof(options));
        Directory.CreateDirectory(directory);
        VerifyExistingChain();
        if (File.Exists(options.FilePath) && new FileInfo(options.FilePath).Length > options.MaximumFileBytes)
            throw new InvalidDataException("Audit dosyası yapılandırılmış maksimum boyutu aşmış.");
        VerifyWritable(directory);
    }

    public async Task AppendAsync(
        DeviceCommandAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (!_options.Enabled) return;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = new AuditPayload(
                SchemaVersion,
                checked(_lastSequence + 1),
                auditEvent.TimestampUtc.ToUniversalTime(),
                auditEvent.DeviceId,
                auditEvent.Subject,
                auditEvent.Role,
                auditEvent.ClientId,
                auditEvent.CommandId,
                auditEvent.Operation,
                auditEvent.Outcome,
                auditEvent.HttpStatus,
                auditEvent.ErrorCode,
                _lastEntryHash);
            ValidatePayload(payload);
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            var entryHash = Convert.ToHexString(SHA256.HashData(payloadBytes));
            var hmac = _integrityKey is null
                ? null
                : Convert.ToHexString(HMACSHA256.HashData(_integrityKey, Convert.FromHexString(entryHash)));
            var envelope = new AuditEnvelope(payload, entryHash, hmac);
            var line = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

            var currentLength = File.Exists(_options.FilePath)
                ? new FileInfo(_options.FilePath).Length
                : 0;
            if (line.LongLength + 1 > _options.MaximumFileBytes - currentLength)
                throw new AuditPersistenceException(
                    "Komut audit dosyası güvenli boyut sınırına ulaştı; rotation/archive gereklidir.",
                    new IOException("Audit storage capacity limit reached."));

            try
            {
                using var stream = new FileStream(
                    _options.FilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 16 * 1024,
                    FileOptions.WriteThrough);
                await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AuditPersistenceException("Komut audit kaydı kalıcı olarak yazılamadı.", ex);
            }

            _lastSequence = payload.Sequence;
            _lastEntryHash = entryHash;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public bool WasCommandObservedBeforeStartup(Guid commandId)
    {
        if (commandId == Guid.Empty) return false;
        return _commandsObservedBeforeStartup.Contains(commandId);
    }

    private void VerifyExistingChain()
    {
        if (!File.Exists(_options.FilePath)) return;
        var expectedSequence = 1L;
        var previousHash = GenesisHash;
        foreach (var line in File.ReadLines(_options.FilePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            AuditEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<AuditEnvelope>(line, JsonOptions)
                    ?? throw new InvalidDataException("Audit satırı boş.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Audit JSONL dosyası bozuk.", ex);
            }
            ValidatePayload(envelope.Payload);
            _commandsObservedBeforeStartup.Add(envelope.Payload.CommandId);
            if (envelope.Payload.Sequence != expectedSequence ||
                !string.Equals(envelope.Payload.PreviousEntryHash, previousHash, StringComparison.Ordinal))
                throw new InvalidDataException("Audit sequence/hash zinciri kesilmiş.");
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.Payload, JsonOptions);
            var expectedHash = Convert.ToHexString(SHA256.HashData(payloadBytes));
            if (!FixedTimeHexEquals(expectedHash, envelope.EntryHash))
                throw new InvalidDataException("Audit entry hash doğrulaması başarısız.");
            VerifyHmac(envelope.EntryHash, envelope.IntegrityHmacSha256);
            expectedSequence++;
            previousHash = envelope.EntryHash;
        }
        _lastSequence = expectedSequence - 1;
        _lastEntryHash = previousHash;
    }

    private void VerifyHmac(string entryHash, string? hmacHex)
    {
        if (string.IsNullOrWhiteSpace(hmacHex))
        {
            if (_options.RequireSignedEntries)
                throw new InvalidDataException("İmzalı audit kaydı zorunlu fakat HMAC bulunamadı.");
            return;
        }
        if (_integrityKey is null)
            throw new InvalidDataException("Audit HMAC doğrulama anahtarı bulunamadı.");
        var expected = HMACSHA256.HashData(_integrityKey, Convert.FromHexString(entryHash));
        byte[] actual;
        try { actual = Convert.FromHexString(hmacHex); }
        catch (FormatException ex) { throw new InvalidDataException("Audit HMAC biçimi geçersiz.", ex); }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidDataException("Audit HMAC doğrulaması başarısız.");
    }

    private static bool FixedTimeHexEquals(string expectedHex, string actualHex)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHex),
                Convert.FromHexString(actualHex));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void ValidatePayload(AuditPayload payload)
    {
        if (payload.SchemaVersion != SchemaVersion || payload.Sequence <= 0 ||
            payload.CommandId == Guid.Empty || string.IsNullOrWhiteSpace(payload.DeviceId) ||
            string.IsNullOrWhiteSpace(payload.Subject) || string.IsNullOrWhiteSpace(payload.Role) ||
            string.IsNullOrWhiteSpace(payload.ClientId) || string.IsNullOrWhiteSpace(payload.Operation) ||
            string.IsNullOrWhiteSpace(payload.Outcome))
            throw new InvalidDataException("Audit kayıt alanları geçersiz.");
    }

    private static void VerifyWritable(string directory)
    {
        var path = Path.Combine(directory, $".audit-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
            stream.WriteByte(0x41);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed record AuditPayload(
        int SchemaVersion,
        long Sequence,
        DateTimeOffset TimestampUtc,
        string DeviceId,
        string Subject,
        string Role,
        string ClientId,
        Guid CommandId,
        string Operation,
        string Outcome,
        int? HttpStatus,
        string? ErrorCode,
        string PreviousEntryHash);

    private sealed record AuditEnvelope(
        AuditPayload Payload,
        string EntryHash,
        string? IntegrityHmacSha256);
}

public sealed class DeviceAuditStartupService(IDeviceCommandAuditStore auditStore) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = auditStore;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
