using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace TrayDrop.Core;

public sealed record DeviceInfo(Guid Id, string Name, DateTimeOffset LastSeen, bool IsOnline);
public sealed record PairingResult(Guid Id, string Name, string Token);
public sealed record SharedFileInfo(
    Guid Id, Guid DeviceId, string Name, long Size, DateTimeOffset CreatedAt, int DownloadRequests);
public sealed record PhoneFileInfo(
    Guid Id, string Name, long Size, DateTimeOffset CreatedAt, int DownloadRequests, string DownloadUrl);
public sealed record PhoneState(string ComputerName, DeviceInfo Device, IReadOnlyList<PhoneFileInfo> Files);
public sealed record ReceivedFileInfo(
    Guid Id, Guid DeviceId, string DeviceName, string Name, long Size, string Path, DateTimeOffset ReceivedAt);
public sealed record OpenedDownload(FileStream Stream, string Name);

public sealed class InvalidDownloadException() : Exception("This download link is invalid or has been removed.");
public sealed class SharedFileChangedException() : IOException(
    "The original file has changed. Drop it in TrayDrop again to share the new version.");

public sealed class TransferState
{
    private sealed record DeviceSession(Guid Id, string Name, string TokenHash, DateTimeOffset LastSeen);
    private sealed record SharedFile(
        SharedFileInfo Info, string Path, DateTime LastWriteTimeUtc, string Key);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, DeviceSession> _devices = [];
    private readonly Dictionary<string, Guid> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, SharedFile> _files = [];
    private readonly List<ReceivedFileInfo> _received = [];
    private readonly TimeProvider _clock;

    public TransferState(TimeProvider? clock = null, TimeSpan? onlineTimeout = null)
    {
        _clock = clock ?? TimeProvider.System;
        OnlineTimeout = onlineTimeout ?? TimeSpan.FromSeconds(45);
    }

    public string PairingCode { get; } = NewToken();
    public TimeSpan OnlineTimeout { get; }
    public event Action<DeviceInfo>? DevicePaired;
    public event Action<ReceivedFileInfo>? FileReceived;

    public PairingResult Pair(string? code, string? name)
    {
        if (!SecretEquals(code, PairingCode))
            throw new UnauthorizedAccessException("Scan the current QR code or copy the connection link from TrayDrop.");

        var validatedName = ValidateName(name);
        var token = NewToken();
        var device = new DeviceSession(Guid.NewGuid(), validatedName, HashToken(token), _clock.GetUtcNow());
        lock (_gate)
        {
            _devices.Add(device.Id, device);
            _tokens.Add(device.TokenHash, device.Id);
        }

        DevicePaired?.Invoke(ToInfo(device));
        return new PairingResult(device.Id, device.Name, token);
    }

    public bool TryAuthenticate(string? token, [NotNullWhen(true)] out DeviceInfo? device)
    {
        device = null;
        if (token is null || token.Length != 64)
            return false;

        lock (_gate)
        {
            if (!_tokens.TryGetValue(HashToken(token), out var id) || !_devices.TryGetValue(id, out var session))
                return false;

            session = session with { LastSeen = _clock.GetUtcNow() };
            _devices[id] = session;
            device = ToInfo(session);
            return true;
        }
    }

    public IReadOnlyList<DeviceInfo> GetDevices()
    {
        lock (_gate)
            return _devices.Values.Select(ToInfo).OrderByDescending(d => d.IsOnline).ThenBy(d => d.Name).ToArray();
    }

    public DeviceInfo RenameDevice(Guid id, string? name)
    {
        var validatedName = ValidateName(name);
        lock (_gate)
        {
            var device = _devices[id] with { Name = validatedName };
            _devices[id] = device;
            return ToInfo(device);
        }
    }

    public bool RemoveDevice(Guid id)
    {
        lock (_gate)
        {
            if (!_devices.Remove(id, out var device))
                return false;

            _tokens.Remove(device.TokenHash);
            foreach (var fileId in _files.Where(f => f.Value.Info.DeviceId == id).Select(f => f.Key).ToArray())
                _files.Remove(fileId);
            return true;
        }
    }

    public SharedFileInfo ShareFile(Guid deviceId, string path)
    {
        var file = new FileInfo(System.IO.Path.GetFullPath(path));
        if (Directory.Exists(file.FullName))
            throw new ArgumentException("Folders are not supported yet. Drop individual files instead.");
        if (!file.Exists)
            throw new FileNotFoundException("The file no longer exists.", file.FullName);

        var info = new SharedFileInfo(
            Guid.NewGuid(), deviceId, file.Name, file.Length, _clock.GetUtcNow(), 0);
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var device) || !ToInfo(device).IsOnline)
                throw new InvalidOperationException("This device is offline. Keep its phone page open and try again.");

            _files.Add(info.Id, new SharedFile(info, file.FullName, file.LastWriteTimeUtc, NewToken()));
        }
        return info;
    }

    public IReadOnlyList<SharedFileInfo> GetSharedFiles(Guid? deviceId = null)
    {
        lock (_gate)
            return _files.Values.Where(f => deviceId is null || f.Info.DeviceId == deviceId)
                .Select(f => f.Info).OrderByDescending(f => f.CreatedAt).ToArray();
    }

    public PhoneState GetPhoneState(Guid deviceId, string computerName)
    {
        lock (_gate)
        {
            var files = _files.Values.Where(f => f.Info.DeviceId == deviceId)
                .OrderByDescending(f => f.Info.CreatedAt)
                .Select(f => new PhoneFileInfo(
                    f.Info.Id, f.Info.Name, f.Info.Size, f.Info.CreatedAt, f.Info.DownloadRequests,
                    $"/api/downloads/{f.Info.Id:D}?key={f.Key}"))
                .ToArray();
            return new PhoneState(computerName, ToInfo(_devices[deviceId]), files);
        }
    }

    public OpenedDownload OpenDownload(Guid id, string? key)
    {
        SharedFile file;
        lock (_gate)
        {
            if (!_files.TryGetValue(id, out var found) || !SecretEquals(key, found.Key))
                throw new InvalidDownloadException();
            file = found;
        }

        var stream = new FileStream(
            file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var keepStream = false;
        try
        {
            if (stream.Length != file.Info.Size || File.GetLastWriteTimeUtc(file.Path) != file.LastWriteTimeUtc)
                throw new SharedFileChangedException();
            keepStream = true;
            return new OpenedDownload(stream, file.Info.Name);
        }
        finally
        {
            if (!keepStream)
                stream.Dispose();
        }
    }

    public void RecordDownloadRequest(Guid id)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(id, out var file))
                _files[id] = file with { Info = file.Info with { DownloadRequests = file.Info.DownloadRequests + 1 } };
        }
    }

    public bool RevokeFile(Guid id)
    {
        lock (_gate)
            return _files.Remove(id);
    }

    public void ClearSharedFiles()
    {
        lock (_gate)
            _files.Clear();
    }

    public IReadOnlyList<ReceivedFileInfo> GetReceivedFiles()
    {
        lock (_gate)
            return _received.AsEnumerable().Reverse().ToArray();
    }

    internal ReceivedFileInfo RecordReceivedFile(DeviceInfo device, string path, long size)
    {
        var info = new ReceivedFileInfo(
            Guid.NewGuid(), device.Id, device.Name, System.IO.Path.GetFileName(path), size, path, _clock.GetUtcNow());
        lock (_gate)
        {
            _received.Add(info);
            if (_received.Count > 100)
                _received.RemoveAt(0);
        }
        FileReceived?.Invoke(info);
        return info;
    }

    private DeviceInfo ToInfo(DeviceSession device) => new(
        device.Id, device.Name, device.LastSeen, _clock.GetUtcNow() - device.LastSeen <= OnlineTimeout);

    private static string ValidateName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 48 || trimmed.Any(char.IsControl))
            throw new ArgumentException("Use a device name with 1 to 48 characters and no control characters.");
        return trimmed;
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static bool SecretEquals(string? supplied, string expected) =>
        supplied is not null && supplied.Length == expected.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
}
