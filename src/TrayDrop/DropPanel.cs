using System.ComponentModel;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using TrayDrop.Core;

namespace TrayDrop;

internal sealed class DropPanel : Form
{
    private sealed record DeviceItem(DeviceInfo Device)
    {
        public override string ToString() => Device.Name + (Device.IsOnline ? "  |  Online" : "  |  Offline");
    }

    private sealed record SharedItem(SharedFileInfo File)
    {
        public override string ToString() => File.Name + "  |  " + NativeTheme.FileSize(File.Size) +
                                            (File.DownloadRequests > 0 ? $"  |  {File.DownloadRequests} requested" : "");
    }

    private readonly TransferState _state;
    private readonly ILogger _logger;
    private readonly ListBox _devices = CreateListBox();
    private readonly ListBox _sharedFiles = CreateListBox();
    private readonly Label _emptyDevices = NativeTheme.Label("No connected devices yet. Connect a phone above.", 9, muted: true);
    private readonly Label _network = NativeTheme.Label("", 8.5f, muted: true);
    private readonly Label _status = NativeTheme.Label("Select a device, then drop files here.", 8.5f, muted: true);
    private readonly Label _sharedHeading = NativeTheme.Label("SHARED FILES", 8, true, true);
    private readonly Label _lastReceived = NativeTheme.Label("Phone uploads are saved in your local inbox.", 8, muted: true);
    private readonly Button _removeShare;
    private readonly Button _forgetDevice;
    private readonly Button _saveLocation;
    private readonly Func<string> _getSaveLocation;
    private readonly ToolTip _saveLocationTip = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 2000 };
    private string _deviceSignature = "";
    private string _shareSignature = "";
    private bool _networkError;

    public DropPanel(
        TransferState state, ILogger logger, Action connect, Action openInbox, Action copyLink,
        Action changeSaveLocation, Func<string> getSaveLocation)
    {
        _state = state;
        _logger = logger;
        _getSaveLocation = getSaveLocation;
        _devices.AccessibleName = "Receiving devices";
        _sharedFiles.AccessibleName = "Shared files";
        Text = "TrayDrop";
        Name = "TrayDropPanel";
        AccessibleName = "TrayDrop file transfer panel";
        BackColor = NativeTheme.Background;
        ForeColor = NativeTheme.Text;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(408, 652);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 14,
            BackColor = NativeTheme.Background, AutoScroll = true
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int[] heights = [52, 40, 28, 24, 64, 128, 32, 24, 72, 32, 40, 24, 32, 20];
        foreach (var height in heights)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        Controls.Add(root);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        var title = NativeTheme.Label("TrayDrop", 20, true);
        header.Controls.Add(title, 0, 0);
        var close = NativeTheme.Button("x", Hide);
        close.AccessibleName = "Hide to tray";
        close.Margin = new Padding(0, 4, 0, 12);
        header.Controls.Add(close, 1, 0);
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(NativeTheme.Button("Connect a device", connect, true), 0, 1);
        root.Controls.Add(_network, 0, 2);
        root.Controls.Add(NativeTheme.Label("SEND TO", 8, true, true), 0, 3);
        var deviceSlot = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = NativeTheme.Surface };
        deviceSlot.Controls.Add(_devices);
        _emptyDevices.Padding = new Padding(8);
        deviceSlot.Controls.Add(_emptyDevices);
        root.Controls.Add(deviceSlot, 0, 4);
        root.Controls.Add(new FileDropZone(ChooseFiles, QueueFiles), 0, 5);
        root.Controls.Add(_status, 0, 6);
        root.Controls.Add(_sharedHeading, 0, 7);
        root.Controls.Add(_sharedFiles, 0, 8);
        _removeShare = NativeTheme.Button("Remove share", RemoveSelectedShare);
        _forgetDevice = NativeTheme.Button("Forget device", ForgetSelectedDevice);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 4, 0, 0) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _removeShare.Margin = new Padding(0, 0, 4, 0);
        _forgetDevice.Margin = new Padding(4, 0, 0, 0);
        actions.Controls.Add(_removeShare, 0, 0);
        actions.Controls.Add(_forgetDevice, 1, 0);
        root.Controls.Add(actions, 0, 9);
        var inbox = NativeTheme.Button("Open received files", openInbox);
        inbox.Margin = new Padding(0, 8, 0, 0);
        root.Controls.Add(inbox, 0, 10);
        root.Controls.Add(_lastReceived, 0, 11);
        _saveLocation = NativeTheme.Button("Save location...", changeSaveLocation);
        _saveLocation.AccessibleName = "Change received-file save location";
        _saveLocation.Font = new Font("Segoe UI", 8.5f);
        _saveLocation.FlatAppearance.BorderSize = 0;
        _saveLocation.BackColor = NativeTheme.Background;
        _saveLocation.ForeColor = NativeTheme.Muted;
        root.Controls.Add(_saveLocation, 0, 12);
        var footer = NativeTheme.Button("Copy Wi-Fi link", copyLink);
        footer.FlatAppearance.BorderSize = 0;
        footer.BackColor = NativeTheme.Background;
        footer.ForeColor = NativeTheme.Accent;
        footer.Font = new Font("Segoe UI", 8);
        root.Controls.Add(footer, 0, 13);
        _devices.DrawItem += DrawDevice;
        _devices.SelectedIndexChanged += (_, _) =>
        {
            _shareSignature = "";
            RefreshShares();
            _forgetDevice.Enabled = SelectedDevice is not null;
        };
        _sharedFiles.SelectedIndexChanged += (_, _) => _removeShare.Enabled = _sharedFiles.SelectedItem is SharedItem;
        _refreshTimer.Tick += (_, _) => RefreshState();
        _refreshTimer.Start();
        RefreshState();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool PermitExit { get; set; }
    private DeviceInfo? SelectedDevice => (_devices.SelectedItem as DeviceItem)?.Device;

    public void ShowAtTray()
    {
        RefreshState();
        var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var inset = (int)(12 * DeviceDpi / 96f);
        Height = Math.Min(Height, workArea.Height - inset * 2);
        Location = new Point(workArea.Right - Width - inset, workArea.Bottom - Height - inset);
        Show();
        BringToFront();
        Activate();
    }

    public void RefreshState()
    {
        var devices = _state.GetDevices();
        var signature = string.Join("|", devices.Select(d => $"{d.Id}:{d.Name}:{d.IsOnline}"));
        if (_deviceSignature != signature)
        {
            var selectedId = SelectedDevice?.Id;
            _devices.BeginUpdate();
            _devices.Items.Clear();
            foreach (var device in devices)
                _devices.Items.Add(new DeviceItem(device));
            _devices.EndUpdate();
            var selected = devices.ToList().FindIndex(d => d.Id == selectedId);
            if (selected < 0)
                selected = devices.ToList().FindIndex(d => d.IsOnline);
            _devices.SelectedIndex = selected;
            _deviceSignature = signature;
        }
        _devices.Visible = devices.Count > 0;
        _emptyDevices.Visible = devices.Count == 0;
        _forgetDevice.Enabled = SelectedDevice is not null;
        RefreshShares();
        var received = _state.GetReceivedFiles().FirstOrDefault();
        _lastReceived.Text = received is null
            ? "Phone uploads are saved in your local inbox."
            : $"Received: {received.Name} from {received.DeviceName}";
        var saveLocation = _getSaveLocation();
        _saveLocation.Text = $"Save to: {new DirectoryInfo(saveLocation).Name} (change)";
        _saveLocationTip.SetToolTip(_saveLocation, saveLocation);
        try
        {
            var networks = NetworkDiscovery.GetAddresses();
            _network.Text = networks.Count > 0
                ? $"Local network: {networks[0].Address}  |  No cloud"
                : "No Wi-Fi or Ethernet address. Connect to a local network.";
            _networkError = false;
        }
        catch (NetworkInformationException exception)
        {
            if (!_networkError)
                _logger.LogWarning(exception, "Could not read network adapter information.");
            _networkError = true;
            _network.Text = "Network information unavailable. Refresh the connection dialog.";
        }
    }

    private void RefreshShares()
    {
        var device = SelectedDevice;
        var files = device is null ? [] : _state.GetSharedFiles(device.Id);
        _sharedHeading.Text = $"SHARED FILES ({files.Count})";
        var signature = string.Join("|", files.Select(f => $"{f.Id}:{f.DownloadRequests}"));
        if (signature != _shareSignature || (files.Count == 0 && _sharedFiles.Items.Count > 0))
        {
            var selectedId = (_sharedFiles.SelectedItem as SharedItem)?.File.Id;
            _sharedFiles.BeginUpdate();
            _sharedFiles.Items.Clear();
            foreach (var file in files)
                _sharedFiles.Items.Add(new SharedItem(file));
            _sharedFiles.EndUpdate();
            _sharedFiles.SelectedIndex = files.ToList().FindIndex(f => f.Id == selectedId);
            _shareSignature = signature;
        }
        _removeShare.Enabled = _sharedFiles.SelectedItem is SharedItem;
    }

    private void ChooseFiles()
    {
        if (!EnsureOnlineDevice())
            return;
        using var dialog = new OpenFileDialog
        {
            Title = "Choose files to send", Multiselect = true, CheckFileExists = true,
            Filter = "All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            QueueFiles(dialog.FileNames);
    }

    private bool EnsureOnlineDevice()
    {
        if (SelectedDevice?.IsOnline == true)
            return true;
        MessageBox.Show(this,
            SelectedDevice is null ? "Connect a phone, then select it before dropping files." :
                "This device is offline. Keep its phone page open, then try again.",
            "TrayDrop", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private void QueueFiles(string[] paths)
    {
        if (!EnsureOnlineDevice() || SelectedDevice is not { } device)
            return;
        var errors = new List<string>();
        var count = 0;
        foreach (var path in paths)
        {
            try
            {
                _state.ShareFile(device.Id, path);
                count++;
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning(exception, "Could not queue a file for sharing.");
                errors.Add(Path.GetFileName(path) + ": " + exception.Message);
            }
        }
        _status.Text = count > 0 ? $"{count} file(s) ready on {device.Name}." : "No files were shared.";
        _status.ForeColor = count > 0 ? NativeTheme.Success : NativeTheme.Muted;
        RefreshShares();
        if (errors.Count > 0)
            MessageBox.Show(this, string.Join("\n\n", errors.Take(6)) +
                (errors.Count > 6 ? $"\n\nAnd {errors.Count - 6} more errors. See the app log." : ""),
                "Some files could not be shared", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void RemoveSelectedShare()
    {
        if (_sharedFiles.SelectedItem is not SharedItem item)
            return;
        _state.RevokeFile(item.File.Id);
        _status.Text = "Download link removed. Your original file was not deleted.";
        RefreshShares();
    }

    private void ForgetSelectedDevice()
    {
        if (SelectedDevice is not { } device)
            return;
        if (MessageBox.Show(this,
            $"Disconnect {device.Name} and remove its shared download links?\n\nReceived files will not be deleted.",
            "Forget device", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _state.RemoveDevice(device.Id);
        RefreshState();
    }

    private static ListBox CreateListBox() => new()
    {
        Dock = DockStyle.Fill, BackColor = NativeTheme.Surface, ForeColor = NativeTheme.Text,
        BorderStyle = BorderStyle.None, Margin = Padding.Empty, IntegralHeight = false,
        Font = new Font("Segoe UI", 9), DrawMode = DrawMode.Normal
    };

    private void DrawDevice(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _devices.Items[e.Index] is not DeviceItem item)
            return;
        e.DrawBackground();
        TextRenderer.DrawText(e.Graphics, item.ToString(), e.Font,
            new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height),
            item.Device.IsOnline ? NativeTheme.Text : NativeTheme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _devices.DrawMode = DrawMode.OwnerDrawFixed;
        _devices.ItemHeight = (int)(28 * DeviceDpi / 96f);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!PermitExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
            _saveLocationTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
