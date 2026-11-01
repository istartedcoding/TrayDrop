namespace TrayDrop;

internal sealed class SaveLocationForm : Form
{
    private readonly string _defaultDirectory;
    private readonly Func<string?, bool> _save;
    private bool _useDefault;
    private readonly TextBox _path = new()
    {
        ReadOnly = true, Multiline = true, WordWrap = true, Dock = DockStyle.Fill,
        BackColor = NativeTheme.Surface, ForeColor = NativeTheme.Text, BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Consolas", 9), Margin = new Padding(0, 8, 0, 12),
        AccessibleName = "Received files folder"
    };

    public SaveLocationForm(
        string currentDirectory, string defaultDirectory, bool isDefault, Func<string?, bool> save)
    {
        _defaultDirectory = defaultDirectory;
        _save = save;
        _useDefault = isDefault;
        _path.Text = currentDirectory;
        Text = "Save location | TrayDrop";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = NativeTheme.Background;
        ForeColor = NativeTheme.Text;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(500, 312);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 6
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int[] heights = [36, 32, 68, 36, 56, 36];
        foreach (var height in heights)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        Controls.Add(root);
        root.Controls.Add(NativeTheme.Label("Where should received files go?", 16, true), 0, 0);
        root.Controls.Add(NativeTheme.Label("Default: your Windows Downloads folder.", 9, muted: true), 0, 1);
        root.Controls.Add(_path, 0, 2);
        var choose = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        choose.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        choose.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var browse = NativeTheme.Button("Browse...", Browse);
        browse.Margin = new Padding(0, 0, 4, 0);
        var reset = NativeTheme.Button("Use Downloads", () =>
        {
            _path.Text = _defaultDirectory;
            _useDefault = true;
        });
        reset.Margin = new Padding(4, 0, 0, 0);
        choose.Controls.Add(browse, 0, 0);
        choose.Controls.Add(reset, 1, 0);
        root.Controls.Add(choose, 0, 3);
        root.Controls.Add(NativeTheme.Label(
            "Changes are remembered and apply to new received files only.\nExisting files are not moved. Sent originals stay where they are.",
            8.5f, muted: true), 0, 4);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var cancel = NativeTheme.Button("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); });
        cancel.Margin = new Padding(0, 0, 4, 0);
        var apply = NativeTheme.Button("Save", () =>
        {
            if (!_save(_useDefault ? null : _path.Text))
                return;
            DialogResult = DialogResult.OK;
            Close();
        }, true);
        apply.Margin = new Padding(4, 0, 0, 0);
        actions.Controls.Add(cancel, 0, 0);
        actions.Controls.Add(apply, 1, 0);
        root.Controls.Add(actions, 0, 5);
        CancelButton = cancel;
        AcceptButton = apply;
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where received files are saved",
            UseDescriptionForTitle = true,
            SelectedPath = _path.Text,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _path.Text = dialog.SelectedPath;
        _useDefault = false;
    }
}
