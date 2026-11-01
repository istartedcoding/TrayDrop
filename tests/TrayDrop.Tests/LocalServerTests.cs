using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using TrayDrop.Core;

namespace TrayDrop.Tests;

[TestClass]
public sealed class LocalServerTests
{
    private static LocalTransferServer CreateServer(TransferState state, TestDirectory directory) =>
        new(state, new ServerOptions(directory.Path, 0, IPAddress.Loopback) { ComputerName = "Test PC" },
            networkProvider: () => []);

    private static async Task<PairingResult> PairAsync(HttpClient client, TransferState state, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/pair", new { code = state.PairingCode, name });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PairingResult>()
               ?? throw new AssertFailedException("Missing pairing response.");
    }

    [TestMethod]
    public async Task RealHttpTransfersWorkInBothDirectionsWithDeviceIsolation()
    {
        using var directory = new TestDirectory();
        var state = new TransferState();
        await using var server = CreateServer(state, directory);
        await server.StartAsync();
        using var first = new HttpClient { BaseAddress = server.LoopbackUrl };
        using var second = new HttpClient { BaseAddress = server.LoopbackUrl };
        var firstPair = await PairAsync(first, state, "First phone");
        var secondPair = await PairAsync(second, state, "Second phone");
        first.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firstPair.Token);
        second.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondPair.Token);
        var originalBytes = Enumerable.Range(0, 2 * 1024 * 1024 + 7).Select(n => (byte)(n % 253)).ToArray();
        var original = directory.FilePath("desktop.bin");
        await File.WriteAllBytesAsync(original, originalBytes);
        state.ShareFile(firstPair.Id, original);
        var firstState = await first.GetFromJsonAsync<PhoneState>("/api/state");
        var secondState = await second.GetFromJsonAsync<PhoneState>("/api/state");
        Assert.IsNotNull(firstState);
        Assert.IsNotNull(secondState);
        Assert.AreEqual("Test PC", firstState.ComputerName);
        Assert.HasCount(1, firstState.Files);
        Assert.HasCount(0, secondState.Files);
        using var download = await first.GetAsync(firstState.Files.Single().DownloadUrl);
        Assert.AreEqual(HttpStatusCode.OK, download.StatusCode);
        Assert.AreEqual("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        CollectionAssert.AreEqual(originalBytes, await download.Content.ReadAsByteArrayAsync());
        using var content = new ByteArrayContent([0, 1, 2, 3, 250, 255]);
        using var upload = await first.PostAsync("/api/uploads?name=phone.bin", content);
        Assert.AreEqual(HttpStatusCode.Created, upload.StatusCode);
        var received = state.GetReceivedFiles().Single();
        Assert.AreEqual(firstPair.Id, received.DeviceId);
        CollectionAssert.AreEqual(new byte[] { 0, 1, 2, 3, 250, 255 }, await File.ReadAllBytesAsync(received.Path));
    }

    [TestMethod]
    public async Task DownloadsSupportHeadAndByteRangesWithoutBufferingWholeFile()
    {
        using var directory = new TestDirectory();
        var state = new TransferState();
        await using var server = CreateServer(state, directory);
        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = server.LoopbackUrl };
        var pair = await PairAsync(client, state, "Phone");
        var file = directory.FilePath("range.bin");
        await File.WriteAllBytesAsync(file, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
        var share = state.ShareFile(pair.Id, file);
        var link = state.GetPhoneState(pair.Id, "PC").Files.Single().DownloadUrl;
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, link);
        using var head = await client.SendAsync(headRequest);
        Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
        Assert.AreEqual(10L, head.Content.Headers.ContentLength);
        Assert.HasCount(0, await head.Content.ReadAsByteArrayAsync());
        Assert.AreEqual(0, state.GetSharedFiles().Single().DownloadRequests);
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, link);
        rangeRequest.Headers.Range = new RangeHeaderValue(2, 5);
        using var range = await client.SendAsync(rangeRequest);
        Assert.AreEqual(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.AreEqual("bytes 2-5/10", range.Content.Headers.ContentRange?.ToString());
        CollectionAssert.AreEqual(new byte[] { 2, 3, 4, 5 }, await range.Content.ReadAsByteArrayAsync());
        Assert.AreEqual(1, state.GetSharedFiles().Single().DownloadRequests);
        state.RevokeFile(share.Id);
        using var revoked = await client.GetAsync(link);
        Assert.AreEqual(HttpStatusCode.NotFound, revoked.StatusCode);
    }

    [TestMethod]
    public async Task AuthenticationOriginAndHostAreEnforced()
    {
        using var directory = new TestDirectory();
        var state = new TransferState();
        await using var server = CreateServer(state, directory);
        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = server.LoopbackUrl };
        using var missingSession = await client.GetAsync("/api/state");
        Assert.AreEqual(HttpStatusCode.Unauthorized, missingSession.StatusCode);
        using var unauthenticatedUpload = await client.PostAsync(
            "/api/uploads?name=blocked.txt", new StringContent("secret"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthenticatedUpload.StatusCode);
        using var invalidPair = await client.PostAsJsonAsync("/api/pair", new { code = "wrong", name = "Phone" });
        Assert.AreEqual(HttpStatusCode.Unauthorized, invalidPair.StatusCode);
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/pair");
        crossOrigin.Headers.Add("Origin", "https://evil.invalid");
        crossOrigin.Content = JsonContent.Create(new { code = state.PairingCode, name = "Phone" });
        using var originResponse = await client.SendAsync(crossOrigin);
        Assert.AreEqual(HttpStatusCode.Forbidden, originResponse.StatusCode);
        using var badHost = new HttpRequestMessage(HttpMethod.Get, "/");
        badHost.Headers.Host = "evil.invalid";
        using var hostResponse = await client.SendAsync(badHost);
        Assert.AreEqual(HttpStatusCode.Forbidden, hostResponse.StatusCode);
        Assert.HasCount(0, state.GetDevices());
        Assert.HasCount(0, Directory.GetFiles(server.Uploads.ReceivedDirectory));
    }

    [TestMethod]
    public async Task InvalidUploadNamesAndMalformedJsonProduceExplicitProblems()
    {
        using var directory = new TestDirectory();
        var state = new TransferState();
        await using var server = CreateServer(state, directory);
        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = server.LoopbackUrl };
        var pair = await PairAsync(client, state, "Phone");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pair.Token);
        using var invalidUpload = await client.PostAsync(
            "/api/uploads?name=" + Uri.EscapeDataString("..\\outside.txt"), new StringContent("blocked"));
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidUpload.StatusCode);
        StringAssert.Contains(await invalidUpload.Content.ReadAsStringAsync(), "Upload rejected");
        using var badJson = await client.PostAsync("/api/pair", new StringContent("{invalid", Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.BadRequest, badJson.StatusCode);
        StringAssert.Contains(await badJson.Content.ReadAsStringAsync(), "Invalid request");
        Assert.HasCount(0, Directory.GetFiles(server.Uploads.ReceivedDirectory));
        Assert.HasCount(0, Directory.GetFiles(Path.Combine(server.Uploads.ReceivedDirectory, UploadService.StagingFolderName)));
    }

    [TestMethod]
    public async Task RenameAndDisconnectRevokeTheSessionAndItsFiles()
    {
        using var directory = new TestDirectory();
        var state = new TransferState();
        await using var server = CreateServer(state, directory);
        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = server.LoopbackUrl };
        var pair = await PairAsync(client, state, "Phone");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pair.Token);
        var path = directory.FilePath("file.txt");
        await File.WriteAllTextAsync(path, "hello");
        state.ShareFile(pair.Id, path);
        var link = state.GetPhoneState(pair.Id, "PC").Files.Single().DownloadUrl;
        using var rename = await client.PatchAsJsonAsync("/api/device", new { name = "My renamed phone" });
        Assert.AreEqual(HttpStatusCode.OK, rename.StatusCode);
        Assert.AreEqual("My renamed phone", state.GetDevices().Single().Name);
        using var disconnect = await client.DeleteAsync("/api/device");
        Assert.AreEqual(HttpStatusCode.NoContent, disconnect.StatusCode);
        using var oldSession = await client.GetAsync("/api/state");
        using var oldDownload = await client.GetAsync(link);
        Assert.AreEqual(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, oldDownload.StatusCode);
    }

    [TestMethod]
    public async Task PageIsSelfContainedAndUsesRequiredThemeAndPrivacyHeaders()
    {
        using var directory = new TestDirectory();
        var state = new TransferState();
        await using var server = CreateServer(state, directory);
        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = server.LoopbackUrl };
        using var response = await client.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "--cp-accent: #b11f4b");
        StringAssert.Contains(html, "scoutTheme");
        Assert.IsFalse(html.Contains("src=\"https://", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.AreEqual("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        StringAssert.Contains(response.Headers.GetValues("Content-Security-Policy").Single(), "frame-ancestors 'none'");
    }

    [TestMethod]
    public async Task UploadsAboveKestrelsDefaultLimitStillStreamSuccessfully()
    {
        using var directory = new TestDirectory();
        var state = new TransferState();
        await using var server = CreateServer(state, directory);
        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = server.LoopbackUrl };
        var pair = await PairAsync(client, state, "Phone");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pair.Token);
        var sourcePath = directory.FilePath("large-source.bin");
        const int length = 32 * 1024 * 1024 + 1;
        await using (var output = File.Create(sourcePath))
            output.SetLength(length);
        await using var source = File.OpenRead(sourcePath);
        using var content = new StreamContent(source);
        using var response = await client.PostAsync("/api/uploads?name=large-upload.bin", content);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual((long)length, state.GetReceivedFiles().Single().Size);
        Assert.AreEqual((long)length, new FileInfo(state.GetReceivedFiles().Single().Path).Length);
    }

    [TestMethod]
    public async Task JsonRequestsHaveABoundedSizeAndPairingAttemptsAreRateLimited()
    {
        using var directory = new TestDirectory();
        var state = new TransferState();
        await using var server = CreateServer(state, directory);
        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = server.LoopbackUrl };
        using var large = await client.PostAsJsonAsync("/api/pair",
            new { code = state.PairingCode, name = new string('a', 70 * 1024) });
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        for (var index = 0; index < 11; index++)
        {
            using var rejected = await client.PostAsJsonAsync("/api/pair", new { code = "wrong", name = "Phone" });
            Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        using var limited = await client.PostAsJsonAsync("/api/pair", new { code = "wrong", name = "Phone" });
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        StringAssert.Contains(await limited.Content.ReadAsStringAsync(), "Wait one minute");
    }

    [TestMethod]
    public async Task ServerUsesAndUpdatesTheConfiguredReceivedFolder()
    {
        using var directory = new TestDirectory();
        var state = new TransferState();
        var downloads = directory.FilePath("Downloads");
        await using var server = new LocalTransferServer(state,
            new ServerOptions(directory.Path, 0, IPAddress.Loopback) { ReceiveDirectory = downloads },
            networkProvider: () => []);
        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = server.LoopbackUrl };
        var pair = await PairAsync(client, state, "Phone");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pair.Token);
        using var first = await client.PostAsync("/api/uploads?name=first.txt", new StringContent("first"));
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        var changed = directory.FilePath("Changed");
        server.Uploads.SetReceivedDirectory(UploadService.PrepareReceivedDirectory(changed));
        using var second = await client.PostAsync("/api/uploads?name=second.txt", new StringContent("second"));
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
        Assert.AreEqual("first", await File.ReadAllTextAsync(Path.Combine(downloads, "first.txt")));
        Assert.AreEqual("second", await File.ReadAllTextAsync(Path.Combine(changed, "second.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(changed, "first.txt")));
        Assert.IsFalse(Directory.Exists(directory.FilePath("Received")));
    }
}
