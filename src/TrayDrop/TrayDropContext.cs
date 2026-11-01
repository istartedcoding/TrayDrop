using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using TrayDrop.Core;

namespace TrayDrop;

internal sealed class TrayDropContext : ApplicationContext
{
    private readonly TransferState _state = new();
    private readonly LocalTransferServer _server;
    private readonly FileLoggerProvider _logProvider;
    private readonly ILogger _logger;
    private readonly SaveLocationStore _locations;
    private readonly DropPanel _panel;
    private readonly Control _dispatcher = new();
    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly RegisteredWaitHandle _activationWait;
    private volatile bool _stopping;
    private bool _disposed;
    private string? _preferredAddress;

    public TrayDropContext(ServerOptions options, EventWaitHandle activate, SaveLocationStore locations)
    {
        _locations = locations;
        _logProvider = new FileLoggerProvider(options.DataDirectory);
        _logger = _logProvider.CreateLogger("TrayDrop");
        _server = new LocalTransferServer(_state, options, _logProvider);
        _panel = new DropPanel(
            _state, _logger, ShowConnection, OpenInbox, CopyConnectionLink, ShowSaveLocation,
            () => _server.Uploads.ReceivedDirectory);
        _ = _dispatcher.Handle;
        _icon = NativeTheme.CreateTrayIcon();
        _menu = new ContextMenuStrip();
        _menu.Items.Add("Open TrayDrop", null, (_, _) => _panel.ShowAtTray());
        _menu.Items.Add("Connect a device", null, (_, _) => ShowConnection());
        _menu.Items.Add("Copy Wi-Fi link", null, (_, _) => CopyConnectionLink());
        _menu.Items.Add("Open received files", null, (_, _) => OpenInbox());
        _menu.Items.Add("Save location...", null, (_, _) => ShowSaveLocation());
        _menu.Items.Add("Open log folder", null, (_, _) => OpenFolder(Path.GetDirectoryName(_logProvider.LogPath)!));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Clear shared links", null, (_, _) =>
        {
            if (MessageBox.Show("Remove all shared download links?\n\nOriginal and received files will not be deleted.",
                "TrayDrop", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _state.ClearSharedFiles();
            _panel.RefreshState();
        });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _tray = new NotifyIcon
        {
            Icon = _icon, ContextMenuStrip = _menu, Text = "TrayDrop | Local file transfer", Visible = false
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _panel.ShowAtTray(); };
        _state.DevicePaired += OnDevicePaired;
        _state.FileReceived += OnFileReceived;
        _logProvider.WriteFailed += OnLogFailure;
        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            activate, (_, _) => PostToUi(_panel.ShowAtTray), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Start()
    {
        _server.StartAsync().GetAwaiter().GetResult();
        _tray.Visible = true;
        _tray.ShowBalloonTip(3000, "TrayDrop is ready",
            "Click the tray icon to connect a phone and drop files.", ToolTipIcon.Info);
    }

    public void ReportError(Exception exception, string message)
    {
        _logger.LogError(exception, "{Message}", message);
        MessageBox.Show(message + "\n\n" + exception.Message + "\n\nLog: " + _logProvider.LogPath,
            "TrayDrop", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void ShowConnection()
    {
        _panel.ShowAtTray();
        using var form = new ConnectDeviceForm(
            _state, _server.Port, _preferredAddress, CopyText,
            address => _preferredAddress = address.Address.ToString());
        form.ShowDialog(_panel);
        _panel.RefreshState();
    }

    private void CopyConnectionLink()
    {
        var addresses = NetworkDiscovery.GetAddresses();
        var address = addresses.FirstOrDefault(a => a.Address.ToString() == _preferredAddress) ??
                      addresses.FirstOrDefault();
        if (address is null)
        {
            ShowConnection();
            return;
        }
        if (CopyText(address.ConnectionUrl(_server.Port, _state.PairingCode)))
            _tray.ShowBalloonTip(2000, "Connection link copied",
                "Open it on a phone on the same Wi-Fi, or use Connect a device to scan a QR code.", ToolTipIcon.Info);
    }

    private bool CopyText(string text)
    {
        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 100);
            return true;
        }
        catch (ExternalException exception)
        {
            ReportError(exception, "The clipboard is busy. Please try copying the link again.");
            return false;
        }
    }

    private void OpenInbox() => OpenFolder(_server.Uploads.ReceivedDirectory);

    private void ShowSaveLocation()
    {
        _panel.ShowAtTray();
        using var form = new SaveLocationForm(
            _server.Uploads.ReceivedDirectory, _locations.DefaultDirectory, _locations.IsDefault, ApplySaveLocation);
        form.ShowDialog(_panel);
        _panel.RefreshState();
    }

    private bool ApplySaveLocation(string? customDirectory)
    {
        try
        {
            var directory = UploadService.PrepareReceivedDirectory(customDirectory ?? _locations.DefaultDirectory);
            _locations.Save(customDirectory is null ? null : directory);
            _server.Uploads.SetReceivedDirectory(directory);
            _logger.LogInformation("Updated the received-file save location.");
            _panel.RefreshState();
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ReportError(exception, "This save location could not be used. Your previous location was kept.");
            return false;
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception exception)
        {
            ReportError(exception, "Windows could not open this folder.");
        }
    }

    private void OnDevicePaired(DeviceInfo device) => PostToUi(() =>
    {
        _panel.RefreshState();
        _tray.ShowBalloonTip(3000, "Device connected",
            device.Name + " is ready. Select it in TrayDrop to send files.", ToolTipIcon.Info);
    });

    private void OnFileReceived(ReceivedFileInfo file) => PostToUi(() =>
    {
        _panel.RefreshState();
        _tray.ShowBalloonTip(3000, "File received",
            file.Name + " from " + file.DeviceName + " was saved to your inbox.", ToolTipIcon.Info);
    });

    private void OnLogFailure(Exception exception) => PostToUi(() =>
        _tray.ShowBalloonTip(5000, "TrayDrop cannot write its log",
            "Check disk space and permissions. " + exception.Message, ToolTipIcon.Error));

    private void PostToUi(Action action)
    {
        if (_stopping)
            return;
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (!_stopping)
                    action();
            });
        }
        catch (InvalidOperationException) when (_stopping)
        {
            _logger.LogDebug("A notification was cancelled during shutdown.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _stopping = true;
            _activationWait.Unregister(null);
            _state.DevicePaired -= OnDevicePaired;
            _state.FileReceived -= OnFileReceived;
            _logProvider.WriteFailed -= OnLogFailure;
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _icon.Dispose();
            _panel.PermitExit = true;
            _panel.Dispose();
            _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _dispatcher.Dispose();
            _logProvider.Dispose();
        }
        base.Dispose(disposing);
    }
}
