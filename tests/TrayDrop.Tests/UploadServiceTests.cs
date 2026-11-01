using TrayDrop.Core;

namespace TrayDrop.Tests;

[TestClass]
public sealed class UploadServiceTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("../outside.txt")]
    [DataRow("..\\outside.txt")]
    [DataRow("C:\\outside.txt")]
    [DataRow("CON.txt")]
    [DataRow("AUX")]
    [DataRow("NUL.bin")]
    [DataRow("LPT9.log")]
    [DataRow("COM\u00b9.txt")]
    [DataRow("CONIN$")]
    [DataRow("bad?.txt")]
    [DataRow("bad|.txt")]
    [DataRow("trailing.")]
    [DataRow("trailing ")]
    [DataRow("line\nbreak.txt")]
    public void UnsafeWindowsFilenamesAreRejected(string filename) =>
        Assert.Throws<ArgumentException>(() => SafeFileName.Validate(filename));

    [TestMethod]
    [DataRow("photo.jpg")]
    [DataRow("report 2026.pdf")]
    [DataRow(".gitignore")]
    [DataRow("\u6587\u4ef6.txt")]
    public void OrdinaryAndInternationalNamesArePreserved(string filename) =>
        Assert.AreEqual(filename, SafeFileName.Validate(filename));

    [TestMethod]
    public async Task UploadsAreByteExactAndNeverOverwrite()
    {
        using var directory = new TestDirectory();
        var state = new TransferState();
        var pair = state.Pair(state.PairingCode, "Phone");
        Assert.IsTrue(state.TryAuthenticate(pair.Token, out var device));
        var uploads = new UploadService(state, directory.Path);
        var bytes = Enumerable.Range(0, 1024 * 1024 + 19).Select(n => (byte)(n % 251)).ToArray();
        using var firstSource = new MemoryStream(bytes);
        var first = await uploads.SaveAsync(device, "photo.bin", firstSource, bytes.Length);
        using var secondSource = new MemoryStream([1, 2, 3]);
        var second = await uploads.SaveAsync(device, "photo.bin", secondSource, 3);
        Assert.AreEqual("photo.bin", first.Name);
        Assert.AreEqual("photo (1).bin", second.Name);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(first.Path));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(second.Path));
        Assert.HasCount(2, state.GetReceivedFiles());
        Assert.AreEqual(pair.Id, first.DeviceId);
        Assert.AreEqual("Phone", first.DeviceName);
        Assert.HasCount(0, Directory.GetFiles(Path.Combine(uploads.ReceivedDirectory, UploadService.StagingFolderName)));
    }

    [TestMethod]
    public async Task EmptyFilesAreValidAndIncompleteFilesAreRemoved()
    {
        using var directory = new TestDirectory();
        var state = new TransferState();
        var pair = state.Pair(state.PairingCode, "Phone");
        Assert.IsTrue(state.TryAuthenticate(pair.Token, out var device));
        var uploads = new UploadService(state, directory.Path);
        using var empty = new MemoryStream();
        var result = await uploads.SaveAsync(device, "empty.txt", empty, 0);
        Assert.AreEqual(0L, new FileInfo(result.Path).Length);
        using var incomplete = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => uploads.SaveAsync(device, "broken.txt", incomplete, 10));
        Assert.IsFalse(File.Exists(Path.Combine(uploads.ReceivedDirectory, "broken.txt")));
        Assert.HasCount(0, Directory.GetFiles(Path.Combine(uploads.ReceivedDirectory, UploadService.StagingFolderName)));
        Assert.HasCount(1, state.GetReceivedFiles());
    }

    [TestMethod]
    public async Task CancellationDoesNotLeaveAReceivedOrPartialFile()
    {
        using var directory = new TestDirectory();
        var state = new TransferState();
        var pair = state.Pair(state.PairingCode, "Phone");
        Assert.IsTrue(state.TryAuthenticate(pair.Token, out var device));
        var uploads = new UploadService(state, directory.Path);
        using var source = new MemoryStream(new byte[1024]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            uploads.SaveAsync(device, "cancelled.bin", source, 1024, cancellation.Token));
        Assert.HasCount(0, Directory.GetFiles(uploads.ReceivedDirectory));
        Assert.HasCount(0, Directory.GetFiles(Path.Combine(uploads.ReceivedDirectory, UploadService.StagingFolderName)));
        Assert.HasCount(0, state.GetReceivedFiles());
    }

    [TestMethod]
    public async Task SimultaneousUploadsWithSameNameHaveDistinctDestinations()
    {
        using var directory = new TestDirectory();
        var state = new TransferState();
        var pair = state.Pair(state.PairingCode, "Phone");
        Assert.IsTrue(state.TryAuthenticate(pair.Token, out var device));
        var uploads = new UploadService(state, directory.Path);
        var results = await Task.WhenAll(Enumerable.Range(1, 6).Select(async number =>
        {
            using var source = new MemoryStream([(byte)number]);
            return await uploads.SaveAsync(device, "same.dat", source, 1);
        }));
        Assert.AreEqual(6, results.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var result in results)
            Assert.AreEqual(1L, new FileInfo(result.Path).Length);
        Assert.HasCount(0, Directory.GetFiles(Path.Combine(uploads.ReceivedDirectory, UploadService.StagingFolderName)));
    }

    [TestMethod]
    public void OverlongFilenameIsRejected() =>
        Assert.Throws<ArgumentException>(() => SafeFileName.Validate(new string('a', 256)));

    [TestMethod]
    public async Task ConfiguredFolderIsUsedWithoutMovingExistingFiles()
    {
        using var directory = new TestDirectory();
        var state = new TransferState();
        var pair = state.Pair(state.PairingCode, "Phone");
        Assert.IsTrue(state.TryAuthenticate(pair.Token, out var device));
        var firstFolder = directory.FilePath("Downloads");
        var secondFolder = directory.FilePath("Another folder");
        var uploads = new UploadService(state, directory.Path, receivedDirectory: firstFolder);
        using var firstSource = new MemoryStream([1, 2]);
        var first = await uploads.SaveAsync(device, "first.bin", firstSource, 2);
        uploads.SetReceivedDirectory(UploadService.PrepareReceivedDirectory(secondFolder));
        using var secondSource = new MemoryStream([3, 4]);
        var second = await uploads.SaveAsync(device, "second.bin", secondSource, 2);
        Assert.AreEqual(Path.Combine(firstFolder, "first.bin"), first.Path);
        Assert.AreEqual(Path.Combine(secondFolder, "second.bin"), second.Path);
        Assert.IsTrue(File.Exists(first.Path));
        Assert.IsFalse(Directory.Exists(directory.FilePath("Received")));
        Assert.AreEqual(FileAttributes.Hidden,
            File.GetAttributes(Path.Combine(secondFolder, UploadService.StagingFolderName)) & FileAttributes.Hidden);
    }

    [TestMethod]
    public async Task ChangingFolderMidUploadOnlyAffectsNewUploads()
    {
        using var directory = new TestDirectory();
        var state = new TransferState();
        var pair = state.Pair(state.PairingCode, "Phone");
        Assert.IsTrue(state.TryAuthenticate(pair.Token, out var device));
        var firstFolder = directory.FilePath("Downloads");
        var secondFolder = directory.FilePath("Another folder");
        var uploads = new UploadService(state, directory.Path, receivedDirectory: firstFolder);
        using var source = new PausingStream([7, 8, 9]);
        var pending = uploads.SaveAsync(device, "pending.bin", source, 3);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        uploads.SetReceivedDirectory(UploadService.PrepareReceivedDirectory(secondFolder));
        source.Continue.TrySetResult();
        var completed = await pending;
        Assert.AreEqual(Path.Combine(firstFolder, "pending.bin"), completed.Path);
        CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(completed.Path));
        Assert.HasCount(0, Directory.GetFiles(Path.Combine(firstFolder, UploadService.StagingFolderName)));
        Assert.AreEqual(secondFolder, uploads.ReceivedDirectory);
    }

    private sealed class PausingStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _source = new(bytes);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            return await _source.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _source.Dispose();
            base.Dispose(disposing);
        }
    }
}
