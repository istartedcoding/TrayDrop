using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TrayDrop.Core;

public static class SafeFileName
{
    private static readonly char[] InvalidCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static string Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." ||
            name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(InvalidCharacters) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException("Use a valid Windows filename, without paths, trailing dots, or special characters.");

        var stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                                stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             (char.IsAsciiDigit(stem[3]) || stem[3] is '\u00b9' or '\u00b2' or '\u00b3')))
            throw new ArgumentException("This filename is reserved by Windows. Rename the file and try again.");

        return name;
    }

    internal static string WithSuffix(string name, int number)
    {
        var suffix = $" ({number})";
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var available = 255 - suffix.Length - extension.Length;
        if (available < 1 || stem.Length == 0)
            return name[..Math.Min(name.Length, 255 - suffix.Length)] + suffix;
        return stem[..Math.Min(stem.Length, available)] + suffix + extension;
    }
}

public sealed class UploadService
{
    public const string StagingFolderName = ".traydrop-staging";
    private readonly TransferState _state;
    private string _receivedDirectory;
    private readonly object _moveGate = new();
    private readonly ILogger<UploadService> _logger;

    public UploadService(
        TransferState state, string dataDirectory, ILogger<UploadService>? logger = null,
        string? receivedDirectory = null)
    {
        _state = state;
        _logger = logger ?? NullLogger<UploadService>.Instance;
        _receivedDirectory = PrepareReceivedDirectory(
            receivedDirectory ?? Path.Combine(Path.GetFullPath(dataDirectory), "Received"));
    }

    public string ReceivedDirectory => Volatile.Read(ref _receivedDirectory);

    public void SetReceivedDirectory(string directory) =>
        Volatile.Write(ref _receivedDirectory, SaveLocationStore.NormalizeDirectory(directory));

    public static string PrepareReceivedDirectory(string directory)
    {
        var normalized = SaveLocationStore.NormalizeDirectory(directory);
        Directory.CreateDirectory(normalized);
        var staging = Path.Combine(normalized, StagingFolderName);
        Directory.CreateDirectory(staging);
        File.SetAttributes(staging, File.GetAttributes(staging) | FileAttributes.Hidden);
        var probe = Path.Combine(staging, $"probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        finally
        {
            if (File.Exists(probe))
                File.Delete(probe);
        }
        return normalized;
    }

    public async Task<ReceivedFileInfo> SaveAsync(
        DeviceInfo device, string? name, Stream source, long? expectedLength,
        CancellationToken cancellationToken = default)
    {
        var filename = SafeFileName.Validate(name);
        if (expectedLength < 0)
            throw new ArgumentException("The upload length cannot be negative.");

        var receivedDirectory = ReceivedDirectory;
        var stagingDirectory = Path.Combine(receivedDirectory, StagingFolderName);
        Directory.CreateDirectory(stagingDirectory);
        var temporaryPath = Path.Combine(stagingDirectory, $"upload-{Guid.NewGuid():N}.part");
        try
        {
            long length;
            await using (var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                length = output.Length;
            }

            if (expectedLength is not null && length != expectedLength.Value)
                throw new InvalidDataException("The upload was incomplete. Please send the file again.");

            cancellationToken.ThrowIfCancellationRequested();
            string destination;
            lock (_moveGate)
            {
                var number = 0;
                while (true)
                {
                    destination = Path.Combine(
                        receivedDirectory, number == 0 ? filename : SafeFileName.WithSuffix(filename, number));
                    try
                    {
                        File.Move(temporaryPath, destination, overwrite: false);
                        break;
                    }
                    catch (IOException) when (File.Exists(destination))
                    {
                        number = checked(number + 1);
                    }
                }
            }
            return _state.RecordReceivedFile(device, destination, length);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(exception, "Could not remove a partial upload from the staging directory.");
            }
        }
    }
}
