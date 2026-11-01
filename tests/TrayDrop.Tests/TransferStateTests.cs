using System.Net;
using TrayDrop.Core;

namespace TrayDrop.Tests;

[TestClass]
public sealed class TransferStateTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }

    [TestMethod]
    public void PairingRequiresCurrentSecretAndValidName()
    {
        var state = new TransferState();
        Assert.Throws<UnauthorizedAccessException>(() => state.Pair("incorrect", "Phone"));
        Assert.Throws<ArgumentException>(() => state.Pair(state.PairingCode, " "));
        Assert.Throws<ArgumentException>(() => state.Pair(state.PairingCode, new string('a', 49)));
        Assert.Throws<ArgumentException>(() => state.Pair(state.PairingCode, "Phone\nname"));
        var pair = state.Pair(state.PairingCode, "  My phone  ");
        Assert.AreEqual(64, pair.Token.Length);
        Assert.AreNotEqual(state.PairingCode, pair.Token);
        Assert.AreEqual("My phone", pair.Name);
        Assert.IsTrue(state.TryAuthenticate(pair.Token, out var device));
        Assert.AreEqual(pair.Id, device.Id);
        Assert.IsFalse(state.TryAuthenticate(new string('0', 64), out _));
        Assert.IsFalse(state.TryAuthenticate(null, out _));
    }

    [TestMethod]
    public void OnlineThresholdIsExactAndHeartbeatRestoresDevice()
    {
        var clock = new ManualClock();
        var state = new TransferState(clock);
        var pair = state.Pair(state.PairingCode, "Phone");
        clock.Advance(TimeSpan.FromSeconds(45));
        Assert.IsTrue(state.GetDevices().Single().IsOnline);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.IsFalse(state.GetDevices().Single().IsOnline);
        using var directory = new TestDirectory();
        File.WriteAllText(directory.FilePath("note.txt"), "hello");
        Assert.Throws<InvalidOperationException>(() => state.ShareFile(pair.Id, directory.FilePath("note.txt")));
        Assert.IsTrue(state.TryAuthenticate(pair.Token, out _));
        Assert.IsTrue(state.GetDevices().Single().IsOnline);
        state.ShareFile(pair.Id, directory.FilePath("note.txt"));
        Assert.HasCount(1, state.GetSharedFiles());
    }

    [TestMethod]
    public void FilesAreIsolatedAndRevocationInvalidatesCapabilities()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("note.txt");
        File.WriteAllText(path, "hello");
        var state = new TransferState();
        var first = state.Pair(state.PairingCode, "First phone");
        var second = state.Pair(state.PairingCode, "Second phone");
        var share = state.ShareFile(first.Id, path);
        var firstState = state.GetPhoneState(first.Id, "Test PC");
        Assert.HasCount(1, firstState.Files);
        Assert.HasCount(0, state.GetPhoneState(second.Id, "Test PC").Files);
        var key = firstState.Files.Single().DownloadUrl.Split("?key=")[1];
        Assert.Throws<InvalidDownloadException>(() => state.OpenDownload(share.Id, "wrong"));
        using (var stream = state.OpenDownload(share.Id, key).Stream)
            Assert.AreEqual(5L, stream.Length);
        Assert.IsTrue(state.RevokeFile(share.Id));
        Assert.Throws<InvalidDownloadException>(() => state.OpenDownload(share.Id, key));
        var next = state.ShareFile(first.Id, path);
        var nextKey = state.GetPhoneState(first.Id, "Test PC").Files.Single().DownloadUrl.Split("?key=")[1];
        Assert.IsTrue(state.RemoveDevice(first.Id));
        Assert.IsFalse(state.TryAuthenticate(first.Token, out _));
        Assert.Throws<InvalidDownloadException>(() => state.OpenDownload(next.Id, nextKey));
        Assert.HasCount(1, state.GetDevices());
    }

    [TestMethod]
    public void OriginalFileChangesAndRemovalAreExplicitErrors()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("original.txt");
        File.WriteAllText(path, "before");
        var state = new TransferState();
        var device = state.Pair(state.PairingCode, "Phone");
        var share = state.ShareFile(device.Id, path);
        var key = state.GetPhoneState(device.Id, "PC").Files.Single().DownloadUrl.Split("?key=")[1];
        File.WriteAllText(path, "after");
        Assert.Throws<SharedFileChangedException>(() => state.OpenDownload(share.Id, key));
        File.Delete(path);
        Assert.Throws<FileNotFoundException>(() => state.OpenDownload(share.Id, key));
        Assert.Throws<FileNotFoundException>(() => state.ShareFile(device.Id, path));
        Assert.Throws<ArgumentException>(() => state.ShareFile(device.Id, directory.Path));
    }

    [TestMethod]
    public void NetworkPolicyOnlyAllowsLocalAddressesAndKnownHosts()
    {
        LanAddress[] networks = [
            new("Wi-Fi", IPAddress.Parse("192.168.12.5"), IPAddress.Parse("255.255.255.0"))
        ];
        Assert.IsTrue(NetworkDiscovery.IsAllowedClient(IPAddress.Loopback, networks));
        Assert.IsTrue(NetworkDiscovery.IsAllowedClient(IPAddress.Parse("192.168.12.100"), networks));
        Assert.IsTrue(NetworkDiscovery.IsAllowedClient(IPAddress.Parse("::ffff:192.168.12.100"), networks));
        Assert.IsFalse(NetworkDiscovery.IsAllowedClient(IPAddress.Parse("192.168.13.1"), networks));
        Assert.IsFalse(NetworkDiscovery.IsAllowedClient(IPAddress.Parse("203.0.113.1"), networks));
        Assert.IsFalse(NetworkDiscovery.IsAllowedClient(IPAddress.Parse("fe80::1"), networks));
        Assert.IsFalse(NetworkDiscovery.IsAllowedClient(null, networks));
        Assert.IsTrue(NetworkDiscovery.IsAllowedHost("localhost", networks));
        Assert.IsTrue(NetworkDiscovery.IsAllowedHost("192.168.12.5", networks));
        Assert.IsFalse(NetworkDiscovery.IsAllowedHost("evil.invalid", networks));
        Assert.IsFalse(NetworkDiscovery.IsAllowedHost("192.168.12.100", networks));
    }
}
