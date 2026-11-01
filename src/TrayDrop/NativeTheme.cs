using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace TrayDrop;

internal static class NativeTheme
{
    public static Color Background { get; } = Color.FromArgb(41, 41, 41);
    public static Color Surface { get; } = Color.FromArgb(46, 46, 46);
    public static Color Border { get; } = Color.FromArgb(71, 71, 71);
    public static Color Text { get; } = Color.FromArgb(222, 222, 222);
    public static Color Muted { get; } = Color.FromArgb(145, 145, 145);
    public static Color Accent { get; } = Color.FromArgb(253, 142, 161);
    public static Color AccentForeground { get; } = Color.FromArgb(26, 26, 26);
    public static Color Success { get; } = Color.FromArgb(74, 222, 128);

    public static Button Button(string text, Action click, bool primary = false)
    {
        var button = new Button
        {
            Text = text, AccessibleName = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Surface, ForeColor = primary ? AccentForeground : Text,
            UseVisualStyleBackColor = false, Margin = Padding.Empty, Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), TabStop = true
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(251, 123, 145) : Border;
        button.Click += (_, _) => click();
        return button;
    }

    public static Label Label(string text, float size = 9, bool bold = false, bool muted = false) => new()
    {
        Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty,
        ForeColor = muted ? Muted : Text, AutoEllipsis = true,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
    };

    public static string FileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return size.ToString("0.#", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var background = new SolidBrush(Accent);
        graphics.FillRoundedRectangle(background, new RectangleF(1, 1, 30, 30), new SizeF(8, 8));
        using var pen = new Pen(AccentForeground, 2.8f)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
        };
        graphics.DrawLines(pen, [new PointF(8, 13), new PointF(8, 24), new PointF(24, 24), new PointF(24, 13)]);
        graphics.DrawLine(pen, 16, 7, 16, 19);
        graphics.DrawLines(pen, [new PointF(11, 12), new PointF(16, 7), new PointF(21, 12)]);
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}

internal sealed class FileDropZone : Control
{
    private bool _dragging;

    public FileDropZone(Action chooseFiles, Action<string[]> dropFiles)
    {
        AllowDrop = true;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = "Drop files here, or choose files";
        BackColor = NativeTheme.Surface;
        Cursor = Cursors.Hand;
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 8, 0, 0);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Click += (_, _) => chooseFiles();
        KeyDown += (_, e) => { if (e.KeyCode is Keys.Enter or Keys.Space) chooseFiles(); };
        DragEnter += (_, e) =>
        {
            _dragging = e.Data?.GetDataPresent(DataFormats.FileDrop) == true;
            e.Effect = _dragging ? DragDropEffects.Copy : DragDropEffects.None;
            Invalidate();
        };
        DragOver += (_, e) => e.Effect = _dragging ? DragDropEffects.Copy : DragDropEffects.None;
        DragLeave += (_, _) => { _dragging = false; Invalidate(); };
        DragDrop += (_, e) =>
        {
            _dragging = false;
            Invalidate();
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
                dropFiles(paths);
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var scale = DeviceDpi / 96f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(_dragging ? NativeTheme.Accent : NativeTheme.Border, scale) { DashStyle = DashStyle.Dash };
        e.Graphics.DrawRectangle(border, 1, 1, Width - 3, Height - 3);
        var middle = Width / 2f;
        var top = 20 * scale;
        using var arrow = new Pen(NativeTheme.Accent, 2 * scale) { LineJoin = LineJoin.Round };
        e.Graphics.DrawLine(arrow, middle, top, middle, top + 22 * scale);
        e.Graphics.DrawLines(arrow, [
            new PointF(middle - 7 * scale, top + 7 * scale), new PointF(middle, top),
            new PointF(middle + 7 * scale, top + 7 * scale)
        ]);
        using var titleFont = new Font("Segoe UI", 13, FontStyle.Bold);
        using var noteFont = new Font("Segoe UI", 9);
        TextRenderer.DrawText(e.Graphics, _dragging ? "Release to share" : "Drop files here", titleFont,
            new Rectangle(0, (int)(top + 31 * scale), Width, (int)(28 * scale)), NativeTheme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics, "or click to choose files", noteFont,
            new Rectangle(0, (int)(top + 59 * scale), Width, (int)(22 * scale)), NativeTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused)
            ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(5, 5, Width - 11, Height - 11));
    }
}
