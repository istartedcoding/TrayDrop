using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using TrayDrop.Core;

namespace TrayDrop.Tests;

[TestClass]
public sealed class WindowsInteractionTests
{
    [TestMethod]
    public async Task FileDragEventsQueueFilesForTheSelectedDevice()
    {
        await RunStaAsync(() =>
        {
            using var directory = new TestDirectory();
            var path = directory.FilePath("dragged.txt");
            File.WriteAllText(path, "dragged file bytes");
            var state = new TransferState();
            var device = state.Pair(state.PairingCode, "Phone");
            using var panel = CreatePanel(state, directory.Path);
            var drop = panel.Controls[0].Controls.OfType<FileDropZone>().Single();
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            var arguments = new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
            RaiseDragEvent(drop, "OnDragEnter", arguments);
            Assert.AreEqual(DragDropEffects.Copy, arguments.Effect);
            RaiseDragEvent(drop, "OnDragDrop", arguments);
            var shared = state.GetSharedFiles().Single();
            Assert.AreEqual(device.Id, shared.DeviceId);
            Assert.AreEqual("dragged.txt", shared.Name);
            Assert.AreEqual(18L, shared.Size);
            Assert.IsTrue(File.Exists(path));
        });
    }

    [TestMethod]
    public async Task NonFileDragsAreNotAccepted()
    {
        await RunStaAsync(() =>
        {
            var called = false;
            using var drop = new FileDropZone(() => { }, _ => called = true);
            var arguments = new DragEventArgs(
                new DataObject(DataFormats.Text, "not a file"), 0, 0, 0,
                DragDropEffects.Copy, DragDropEffects.None);
            RaiseDragEvent(drop, "OnDragEnter", arguments);
            RaiseDragEvent(drop, "OnDragDrop", arguments);
            Assert.AreEqual(DragDropEffects.None, arguments.Effect);
            Assert.IsFalse(called);
        });
    }

    [TestMethod]
    public async Task PairingStillWorksWhenThePanelHasNoWindowHandle()
    {
        await RunStaAsync(() =>
        {
            using var directory = new TestDirectory();
            using var activate = new EventWaitHandle(false, EventResetMode.AutoReset);
            var settings = new SaveLocationStore(directory.Path, directory.FilePath("Downloads"));
            using var context = new TrayDropContext(
                new ServerOptions(directory.Path, 0, IPAddress.Loopback) { ReceiveDirectory = settings.CurrentDirectory },
                activate, settings);
            context.Start();
            var panelField = typeof(TrayDropContext).GetField("_panel", BindingFlags.Instance | BindingFlags.NonPublic)
                             ?? throw new InvalidOperationException("The context's panel field was not found.");
            var serverField = typeof(TrayDropContext).GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)
                              ?? throw new InvalidOperationException("The context's server field was not found.");
            var stateField = typeof(TrayDropContext).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)
                             ?? throw new InvalidOperationException("The context's state field was not found.");
            if (panelField.GetValue(context) is not DropPanel panel ||
                serverField.GetValue(context) is not LocalTransferServer server ||
                stateField.GetValue(context) is not TransferState state)
                throw new InvalidOperationException("The context's services had unexpected types.");
            _ = panel.Handle;
            var destroy = typeof(Control).GetMethod("DestroyHandle", BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException("The native handle destruction method was not found.");
            destroy.Invoke(panel, null);
            Assert.IsFalse(panel.IsHandleCreated);
            using var client = new HttpClient { BaseAddress = server.LoopbackUrl };
            using var response = client.PostAsJsonAsync("/api/pair",
                new { code = state.PairingCode, name = "Phone while hidden" }).GetAwaiter().GetResult();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Application.DoEvents();
            Assert.HasCount(1, state.GetDevices());
        });
    }

    private static DropPanel CreatePanel(TransferState state, string folder) =>
        new(state, NullLogger.Instance, () => { }, () => { }, () => { }, () => { }, () => folder);

    private static void RaiseDragEvent(Control control, string name, DragEventArgs arguments)
    {
        var method = typeof(Control).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("The native drag event method was not found.");
        method.Invoke(control, [arguments]);
    }

    private static Task RunStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
