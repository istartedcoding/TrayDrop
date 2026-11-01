using QRCoder;
using TrayDrop.Core;

namespace TrayDrop;

internal sealed class ConnectDeviceForm : Form
{
    private readonly TransferState _state;
    private readonly int _port;
    private readonly Func<string, bool> _copy;
    private readonly Action<LanAddress> _selectAddress;
    private readonly ComboBox _adapters = new()
    {
        Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = Padding.Empty
    };
    private readonly PictureBox _qr = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.CenterImage };
    private readonly Label _noNetwork = NativeTheme.Label(
        "No local IPv4 address.\nConnect your PC to Wi-Fi or Ethernet, then click Refresh.", 10, muted: true);
    private readonly TextBox _url = new()
    {
        ReadOnly = true, Multiline = true, WordWrap = true, Dock = DockStyle.Fill,
        BackColor = NativeTheme.Surface, ForeColor = NativeTheme.Text, BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Consolas", 9), Margin = new Padding(0, 4, 0, 8)
    };
    private readonly Button _copyButton;
    private string? _preferredAddress;

    public ConnectDeviceForm(
        TransferState state, int port, string? preferredAddress,
        Func<string, bool> copy, Action<LanAddress> selectAddress)
    {
        _state = state;
        _port = port;
        _preferredAddress = preferredAddress;
        _copy = copy;
        _selectAddress = selectAddress;
        Text = "Connect a device | TrayDrop";
        BackColor = NativeTheme.Background;
        ForeColor = NativeTheme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(420, 580);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 7 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int[] heights = [32, 44, 36, 252, 68, 42, 58];
        foreach (var height in heights)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        Controls.Add(root);
        root.Controls.Add(NativeTheme.Label("Bring your phone closer.", 18, true), 0, 0);
        root.Controls.Add(NativeTheme.Label("Same Wi-Fi. Scan this code with your camera.\nNo mobile app or account needed.", 9, muted: true), 0, 1);
        var adapterRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        adapterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        adapterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        _adapters.BackColor = NativeTheme.Surface;
        _adapters.ForeColor = NativeTheme.Text;
        adapterRow.Controls.Add(_adapters, 0, 0);
        var refresh = NativeTheme.Button("Refresh", RefreshAdapters);
        refresh.Margin = new Padding(8, 0, 0, 0);
        adapterRow.Controls.Add(refresh, 1, 0);
        root.Controls.Add(adapterRow, 0, 2);
        var qrSlot = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8), BackColor = NativeTheme.Surface };
        qrSlot.Controls.Add(_qr);
        _noNetwork.TextAlign = ContentAlignment.MiddleCenter;
        qrSlot.Controls.Add(_noNetwork);
        root.Controls.Add(qrSlot, 0, 3);
        root.Controls.Add(_url, 0, 4);
        _copyButton = NativeTheme.Button("Copy private connection link", CopyLink, true);
        root.Controls.Add(_copyButton, 0, 5);
        root.Controls.Add(NativeTheme.Label(
            "Anyone with this link can connect. Use trusted Wi-Fi.\nAllow TrayDrop through Windows Firewall on Private networks only.",
            8.5f, muted: true), 0, 6);
        _adapters.SelectedIndexChanged += (_, _) => UpdateCode();
        RefreshAdapters();
    }

    private void RefreshAdapters()
    {
        var addresses = NetworkDiscovery.GetAddresses();
        _adapters.Items.Clear();
        foreach (var address in addresses)
            _adapters.Items.Add(address);
        var index = addresses.ToList().FindIndex(a => a.Address.ToString() == _preferredAddress);
        if (addresses.Count > 0)
            _adapters.SelectedIndex = index < 0 ? 0 : index;
        _adapters.Enabled = addresses.Count > 0;
        UpdateCode();
    }

    private void CopyLink()
    {
        if (_copy(_url.Text))
            _copyButton.Text = "Link copied";
    }

    private void UpdateCode()
    {
        _qr.Image?.Dispose();
        _qr.Image = null;
        if (_adapters.SelectedItem is not LanAddress address)
        {
            _noNetwork.Visible = true;
            _qr.Visible = false;
            _url.Text = "";
            _copyButton.Enabled = false;
            return;
        }
        _preferredAddress = address.Address.ToString();
        _selectAddress(address);
        _url.Text = address.ConnectionUrl(_port, _state.PairingCode);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(_url.Text, QRCodeGenerator.ECCLevel.M);
        using var code = new QRCode(data);
        _qr.Image = code.GetGraphic(4, Color.FromArgb(36, 36, 36), Color.White, true);
        _qr.Visible = true;
        _noNetwork.Visible = false;
        _copyButton.Enabled = true;
        _copyButton.Text = "Copy private connection link";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _qr.Image?.Dispose();
        base.Dispose(disposing);
    }
}
