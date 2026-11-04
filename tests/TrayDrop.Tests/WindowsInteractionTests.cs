using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using TrayDrop.Core;

namespace TrayDrop.Tests;

[TestClass]
[DoNotParallelize]
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
            var panel = GetContextField<DropPanel>(context, "_panel");
            var server = GetContextField<LocalTransferServer>(context, "_server");
            var state = GetContextField<TransferState>(context, "_state");
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

    [TestMethod]
    public async Task ClosingTheVisiblePanelHidesItAndTrayReopenKeepsTheSameWindow()
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
            var menu = GetContextField<ContextMenuStrip>(context, "_menu");
            var open = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Open TrayDrop");
            open.PerformClick();
            var panel = GetContextField<DropPanel>(context, "_panel");
            Assert.IsTrue(panel.Visible);
            panel.Close();
            Assert.IsFalse(panel.IsDisposed);
            Assert.IsFalse(panel.Visible);
            open.PerformClick();
            Assert.AreSame(panel, GetContextField<DropPanel>(context, "_panel"));
            Assert.IsTrue(panel.Visible);
        });
    }

    [TestMethod]
    public async Task TrayClickRecreatesADisposedPanelWithoutLosingTheServerOrSharedFiles()
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
            var state = GetContextField<TransferState>(context, "_state");
            var server = GetContextField<LocalTransferServer>(context, "_server");
            var device = state.Pair(state.PairingCode, "Phone");
            var path = directory.FilePath("still-shared.txt");
            File.WriteAllText(path, "shared before reopening");
            state.ShareFile(device.Id, path);
            Application.DoEvents();
            var original = GetContextField<DropPanel>(context, "_panel");
            Assert.IsFalse(original.IsHandleCreated);
            original.Close();
            Assert.IsTrue(original.IsDisposed);
            var tray = GetContextField<NotifyIcon>(context, "_tray");
            var mouseUp = typeof(NotifyIcon).GetMethod("WmMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException("The native tray-click method was not found.");
            mouseUp.Invoke(tray, [MouseButtons.Left]);
            var reopened = GetContextField<DropPanel>(context, "_panel");
            Assert.AreNotSame(original, reopened);
            Assert.IsFalse(reopened.IsDisposed);
            Assert.IsTrue(reopened.Visible);
            Assert.AreSame(state, GetContextField<TransferState>(context, "_state"));
            Assert.AreSame(server, GetContextField<LocalTransferServer>(context, "_server"));
            Assert.AreEqual(settings.CurrentDirectory, server.Uploads.ReceivedDirectory);
            using var client = new HttpClient { BaseAddress = server.LoopbackUrl };
            client.DefaultRequestHeaders.Authorization = new("Bearer", device.Token);
            using var response = client.GetAsync("/api/state").GetAwaiter().GetResult();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var phone = response.Content.ReadFromJsonAsync<PhoneState>().GetAwaiter().GetResult();
            Assert.IsNotNull(phone);
            Assert.AreEqual(device.Id, phone.Device.Id);
            Assert.HasCount(1, phone.Files);
            Assert.AreEqual("still-shared.txt", phone.Files[0].Name);
            Assert.IsTrue(File.Exists(path));
        });
    }

    [TestMethod]
    public async Task ActivationRecreatesADisposedPanel()
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
            var original = GetContextField<DropPanel>(context, "_panel");
            original.Close();
            Assert.IsTrue(original.IsDisposed);
            activate.Set();
            PumpUntil(() =>
            {
                var panel = GetContextField<DropPanel>(context, "_panel");
                return !ReferenceEquals(original, panel) && !panel.IsDisposed && panel.Visible;
            });
        });
    }

    [TestMethod]
    public async Task PairingRecreatesADisposedPanelWithoutOpeningAWindow()
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
            var original = GetContextField<DropPanel>(context, "_panel");
            original.Close();
            Assert.IsTrue(original.IsDisposed);
            var state = GetContextField<TransferState>(context, "_state");
            state.Pair(state.PairingCode, "Phone after closing");
            PumpUntil(() =>
            {
                var panel = GetContextField<DropPanel>(context, "_panel");
                return !ReferenceEquals(original, panel) && !panel.IsDisposed;
            });
            var recreated = GetContextField<DropPanel>(context, "_panel");
            Assert.IsFalse(recreated.Visible);
            Assert.IsFalse(recreated.IsHandleCreated);
            Assert.HasCount(1, state.GetDevices());
        });
    }

    [TestMethod]
    [DataRow("Connect a device", typeof(ConnectDeviceForm))]
    [DataRow("Save location...", typeof(SaveLocationForm))]
    public async Task TrayDialogsRecreateADisposedOwner(string menuText, Type dialogType)
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
            var original = GetContextField<DropPanel>(context, "_panel");
            original.Close();
            Assert.IsTrue(original.IsDisposed);
            var observed = false;
            using var timer = new System.Windows.Forms.Timer { Interval = 25 };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.OfType<Form>()
                    .SingleOrDefault(form => form.GetType() == dialogType);
                if (dialog is null)
                    return;
                observed = true;
                timer.Stop();
                dialog.Close();
            };
            timer.Start();
            var menu = GetContextField<ContextMenuStrip>(context, "_menu");
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == menuText).PerformClick();
            Assert.IsTrue(observed, "The requested tray dialog did not open.");
            var reopened = GetContextField<DropPanel>(context, "_panel");
            Assert.AreNotSame(original, reopened);
            Assert.IsFalse(reopened.IsDisposed);
            Assert.IsTrue(reopened.Visible);
        });
    }

    [TestMethod]
    public async Task UploadNotificationRecreatesADisposedPanelWithoutOpeningAWindow()
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
            var state = GetContextField<TransferState>(context, "_state");
            var server = GetContextField<LocalTransferServer>(context, "_server");
            var device = state.Pair(state.PairingCode, "Uploading phone");
            Application.DoEvents();
            var original = GetContextField<DropPanel>(context, "_panel");
            original.Close();
            Assert.IsTrue(original.IsDisposed);
            using var client = new HttpClient { BaseAddress = server.LoopbackUrl };
            client.DefaultRequestHeaders.Authorization = new("Bearer", device.Token);
            const string contents = "received while the panel was closed";
            using var body = new StringContent(contents);
            using var response = client.PostAsync("/api/uploads?name=received-after-close.txt", body)
                .GetAwaiter().GetResult();
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            PumpUntil(() =>
            {
                var panel = GetContextField<DropPanel>(context, "_panel");
                return !ReferenceEquals(original, panel) && !panel.IsDisposed;
            });
            Assert.IsFalse(GetContextField<DropPanel>(context, "_panel").Visible);
            var received = state.GetReceivedFiles().Single();
            Assert.AreEqual(Path.Combine(settings.CurrentDirectory, "received-after-close.txt"), received.Path);
            Assert.AreEqual(contents, File.ReadAllText(received.Path));
        });
    }

    private static DropPanel CreatePanel(TransferState state, string folder) =>
        new(state, NullLogger.Instance, () => { }, () => { }, () => { }, () => { }, () => folder);

    private static T GetContextField<T>(TrayDropContext context, string name) where T : class
    {
        var field = typeof(TrayDropContext).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(context) is T value
            ? value
            : throw new InvalidOperationException($"The context's {name} field was not found or had an unexpected type.");
    }

    private static void PumpUntil(Func<bool> condition)
    {
        Exception? error = null;
        ThreadExceptionEventHandler handler = (_, arguments) => error = arguments.Exception;
        Application.ThreadException += handler;
        try
        {
            var completed = SpinWait.SpinUntil(() =>
            {
                Application.DoEvents();
                return error is not null || condition();
            }, TimeSpan.FromSeconds(5));
            Assert.IsNull(error, $"A dispatched UI action failed: {error}");
            Assert.IsTrue(completed, "The expected UI action did not complete.");
        }
        finally
        {
            Application.ThreadException -= handler;
        }
    }

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
            var autoInstall = WindowsFormsSynchronizationContext.AutoInstall;
            // These tests pump native messages manually instead of running Application.Run.
            WindowsFormsSynchronizationContext.AutoInstall = false;
            Exception? error = null;
            try
            {
                action();
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                WindowsFormsSynchronizationContext.AutoInstall = autoInstall;
            }
            if (error is null)
                completion.SetResult();
            else
                completion.SetException(error);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
