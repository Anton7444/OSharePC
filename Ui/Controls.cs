using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CatShareSender.Ui;

/// <summary>Segoe MDL2 Assets glyph code points (Windows 10/11 system font).</summary>
internal static class Glyphs
{
    public const string Send = "\uE724";
    public const string Recent = "\uE823";      // clock — Log tab
    public const string Sync = "\uE72C";
    public const string Download = "\uE896";
    public const string Add = "\uE710";
    public const string Delete = "\uE74D";
    public const string Cancel = "\uE894";
    public const string CheckMark = "\uE73E";
    public const string Error = "\uE783";
    public const string Warning = "\uE7BA";
    public const string Info = "\uE946";
    public const string Sun = "\uE706";
    public const string Moon = "\uE708";
    public const string CellPhone = "\uE8EA";
    public const string Wifi = "\uE701";
    public const string OpenFile = "\uE8E5";
    public const string OpenFolder = "\uE838";
    public const string Document = "\uE8A5";
}

/// <summary>Base for owner-drawn, theme-aware controls (double buffered, transparent corners).</summary>
public abstract class ThemedControl : Control
{
    protected ThemedControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                 | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected int Dpi(int value) => (int)Math.Round(value * DeviceDpi / 96f);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Changed += OnThemeChanged;
        OnThemeChanged();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Theme.Changed -= OnThemeChanged;
        base.OnHandleDestroyed(e);
    }

    protected virtual void OnThemeChanged() => Invalidate();
}

/// <summary>Plain panel that follows the theme surface color.</summary>
public class ThemedPanel : Panel
{
    /// <summary>Use the elevated card color instead of the scaffold surface.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CardBackground { get; set; }

    public ThemedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Changed += OnThemeChanged;
        OnThemeChanged();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Theme.Changed -= OnThemeChanged;
        base.OnHandleDestroyed(e);
    }

    protected virtual void OnThemeChanged() =>
        BackColor = CardBackground ? Theme.Scheme.CardColor : Theme.Scheme.Surface;
}

/// <summary>Container that paints no background of its own (for layering on cards).</summary>
public sealed class TransparentPanel : ThemedControl
{
}

/// <summary>FlowLayoutPanel that follows the theme (used for the horizontal thumbnail strip).</summary>
public sealed class ThemedFlow : FlowLayoutPanel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool OnCard { get; set; } = true;

    public ThemedFlow()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        WrapContents = false;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Changed += OnThemeChanged;
        OnThemeChanged();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Theme.Changed -= OnThemeChanged;
        base.OnHandleDestroyed(e);
    }

    private void OnThemeChanged() => BackColor = OnCard ? Theme.Scheme.CardColor : Theme.Scheme.Surface;
}

internal static class Gfx
{
    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, Color color, Rectangle r, int radius)
    {
        using var path = Rounded(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void DrawRounded(Graphics g, Color color, float width, Rectangle r, int radius)
    {
        using var path = Rounded(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    public static void DrawDashedRounded(Graphics g, Color color, Rectangle r, int radius)
    {
        using var path = Rounded(r, radius);
        using var pen = new Pen(color, 1.4f) { DashStyle = DashStyle.Dash, DashPattern = [4f, 3f] };
        g.DrawPath(pen, path);
    }

    /// <summary>Draws a Segoe MDL2 glyph centered in a rect, optionally rotated.</summary>
    public static void DrawGlyph(Graphics g, string glyph, Font font, Color color, Rectangle rect, float angleDeg = 0)
    {
        var state = g.Save();
        try
        {
            var center = new PointF(rect.Left + rect.Width / 2f, rect.Top + rect.Height / 2f);
            if (angleDeg != 0)
            {
                g.TranslateTransform(center.X, center.Y);
                g.RotateTransform(angleDeg);
                g.TranslateTransform(-center.X, -center.Y);
            }
            using var brush = new SolidBrush(color);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(glyph, font, brush, rect, fmt);
        }
        finally
        {
            g.Restore(state);
        }
    }
}

/// <summary>Static text label that follows the theme (avoids unthemed WinForms Labels).</summary>
public sealed class TextBlock : ThemedControl
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Semibold { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Dim { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float TextSize { get; set; } = 10f;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Wrap { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TextAlignment Align { get; set; } = TextAlignment.Left;

    public enum TextAlignment { Left, Center }

    public TextBlock(string text, float size, bool semibold = false, bool dim = false)
    {
        Text = text;
        TextSize = size;
        Semibold = semibold;
        Dim = dim;
        TabStop = false;
    }

    private Font Effective => Semibold ? Theme.SemiFont(TextSize) : Theme.Font(TextSize);

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var scheme = Theme.Scheme;
        var color = Dim ? scheme.OnSurfaceVariant : scheme.OnSurface;
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        if (Wrap) flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        var rect = new Rectangle(0, 0, Width, Height);
        if (Align == TextAlignment.Center)
            TextRenderer.DrawText(e.Graphics, Text, Effective, rect, color,
                flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        else
            TextRenderer.DrawText(e.Graphics, Text, Effective, rect, color,
                flags | TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding);
    }
}

/// <summary>Extended left navigation rail with the app header, tab pills, theme toggle and language picker.</summary>
public sealed class NavRail : ThemedControl
{
    private static readonly string[] ItemGlyphs = [Glyphs.Send, Glyphs.Download, Glyphs.Recent];
    private static readonly string[] ItemKeys = ["Nav.Send", "Nav.Receive", "Nav.Log"];

    private const int ItemHeight = 50;
    private const int TopPad = 88;

    private int _hoverItem = -1;
    private bool _toggleHover;
    private bool _langHover;

    private int _selectedIndex;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value) return;
            _selectedIndex = value;
            Invalidate();
        }
    }

    public event Action<int>? SelectedIndexChanged;

    public NavRail()
    {
        Width = 192;
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    private Rectangle ItemRect(int i) => new(12, TopPad + i * (ItemHeight + 6), Width - 24, ItemHeight);

    private Rectangle ToggleRect => new(18, Height - 90, 44, 44);
    private Rectangle LangRect => new(18, Height - 46, 44, 30);

    protected override void OnThemeChanged()
    {
        BackColor = Theme.Scheme.CardColor;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var oldItem = _hoverItem;
        var oldToggle = _toggleHover;
        var oldLang = _langHover;
        _hoverItem = -1;
        _toggleHover = false;
        _langHover = false;
        for (var i = 0; i < ItemGlyphs.Length; i++)
            if (ItemRect(i).Contains(e.Location)) { _hoverItem = i; break; }
        if (ToggleRect.Contains(e.Location)) _toggleHover = true;
        if (LangRect.Contains(e.Location)) _langHover = true;
        if (oldItem != _hoverItem || oldToggle != _toggleHover || oldLang != _langHover) Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverItem != -1 || _toggleHover || _langHover)
        {
            _hoverItem = -1;
            _toggleHover = false;
            _langHover = false;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (ToggleRect.Contains(e.Location))
        {
            Theme.Toggle();
            return;
        }
        if (LangRect.Contains(e.Location))
        {
            Lang.Cycle();
            Lang.Save();
            Invalidate();
            return;
        }
        for (var i = 0; i < ItemGlyphs.Length; i++)
        {
            if (!ItemRect(i).Contains(e.Location)) continue;
            if (SelectedIndex != i)
            {
                SelectedIndex = i;
                Invalidate();
                SelectedIndexChanged?.Invoke(i);
            }
            return;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var scheme = Theme.Scheme;
        g.Clear(scheme.CardColor);

        using (var headerBrush = new SolidBrush(scheme.OnSurface))
            g.DrawString("OsharePC", Theme.HeaderFont, headerBrush, 20, 26);

        for (var i = 0; i < ItemGlyphs.Length; i++)
        {
            var rect = ItemRect(i);
            var selected = i == SelectedIndex;
            if (selected)
                Gfx.FillRounded(g, scheme.SecondaryContainer, rect, rect.Height / 2);
            else if (i == _hoverItem)
                Gfx.FillRounded(g, scheme.SurfaceContainerHigh, rect, rect.Height / 2);

            var color = selected ? scheme.Primary : scheme.OnSurfaceVariant;
            var iconRect = new Rectangle(rect.X + 14, rect.Y + (rect.Height - 24) / 2, 24, 24);
            Gfx.DrawGlyph(g, ItemGlyphs[i], Theme.IconFont(15f), color, iconRect);

            var textRect = new Rectangle(rect.X + 46, rect.Y, rect.Width - 50, rect.Height);
            TextRenderer.DrawText(g, Lang.T(ItemKeys[i]), Theme.SemiFont(10.5f), textRect,
                selected ? scheme.OnSecondaryContainer : scheme.OnSurfaceVariant,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }

        // theme toggle (sun in dark mode, moon in light mode)
        var tRect = ToggleRect;
        if (_toggleHover) Gfx.FillRounded(g, scheme.SurfaceContainerHigh, tRect, tRect.Height / 2);
        Gfx.DrawGlyph(g, Theme.IsDark ? Glyphs.Sun : Glyphs.Moon, Theme.IconFont(13f),
            scheme.OnSurfaceVariant, new Rectangle(tRect.X + 10, tRect.Y + 10, 24, 24));

        // language picker
        var lRect = LangRect;
        if (_langHover) Gfx.FillRounded(g, scheme.SurfaceContainerHigh, lRect, lRect.Height / 2);
        TextRenderer.DrawText(g, Lang.PickerLabel, Theme.SemiFont(9f), lRect,
            scheme.OnSurfaceVariant,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

        TextRenderer.DrawText(g, $"v{Program.Version}", Theme.Font(7.5f),
            new Rectangle(20, Height - 16, 120, 14), Theme.Blend(scheme.OnSurfaceVariant, scheme.CardColor, 0.35),
            TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Stadium-shaped button: filled (primary), tonal, text or error variants.</summary>
public sealed class AppButton : ThemedControl
{
    public enum AppBtnStyle { Filled, Tonal, Text, Error }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Glyph { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public AppBtnStyle Style { get; set; } = AppBtnStyle.Filled;
    private bool _hover;
    private bool _down;

    public AppButton(string text, string? glyph, AppBtnStyle style, int width, int height = 40)
    {
        Text = text;
        Glyph = glyph;
        Style = style;
        Size = new Size(width, height);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var scheme = Theme.Scheme;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        Color bg, fg;
        switch (Style)
        {
            case AppBtnStyle.Tonal: bg = scheme.SecondaryContainer; fg = scheme.OnSecondaryContainer; break;
            case AppBtnStyle.Text: bg = Color.Transparent; fg = scheme.Primary; break;
            case AppBtnStyle.Error: bg = scheme.Error; fg = scheme.OnError; break;
            default: bg = scheme.Primary; fg = scheme.OnPrimary; break;
        }
        if (!Enabled)
        {
            bg = Style == AppBtnStyle.Text ? Color.Transparent : Theme.WithAlpha(scheme.OnSurface, 26);
            fg = Theme.Blend(scheme.OnSurface, scheme.Surface, 0.62);
        }
        if (bg != Color.Transparent)
            Gfx.FillRounded(g, bg, rect, rect.Height / 2);
        if (Enabled && (_hover || _down))
        {
            var overlay = _down && bg != Color.Transparent ? scheme.Pressed : scheme.Hover;
            var clip = g.Save();
            g.SetClip(Gfx.Rounded(rect, rect.Height / 2));
            Gfx.FillRounded(g, overlay, rect, rect.Height / 2);
            g.Restore(clip);
        }

        var textSize = TextRenderer.MeasureText(Text, Theme.SemiFont(10f));
        var font = Theme.SemiFont(10f);
        var glyphWidth = Glyph is null ? 0 : (int)(Height * 0.40) + 6;
        var contentW = textSize.Width + glyphWidth;
        var x = (Width - contentW) / 2;

        if (Glyph is not null)
        {
            var iconSize = (int)(Height * 0.40);
            var iconRect = new Rectangle(x, (Height - iconSize) / 2, iconSize, iconSize);
            Gfx.DrawGlyph(g, Glyph, Theme.IconFont(iconSize * 0.62f), fg, iconRect);
            x += glyphWidth;
        }
        var textRect = new Rectangle(x, 0, textSize.Width + 4, Height);
        TextRenderer.DrawText(g, Text, font, textRect, fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>Material card: rounded 10 surface with a hairline outline.</summary>
public sealed class Card : ThemedPanel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = Theme.RadiusMedium;

    protected override void OnThemeChanged() => Invalidate();

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 1 || Height <= 1) return;
        using var path = Gfx.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        Region = new Region(path);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var scheme = Theme.Scheme;
        e.Graphics.Clear(scheme.Surface);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Gfx.FillRounded(e.Graphics, scheme.CardColor, rect, CornerRadius);
        Gfx.DrawRounded(e.Graphics, scheme.OutlineVariant, 1f, rect, CornerRadius);
    }
}

/// <summary>Nearby-phone tile styled like LocalSend's DeviceListTile (rounded card, 46px icon,
/// name + badges; optional inline progress during a transfer).</summary>
public sealed class DeviceTile : ThemedControl
{
    public const int TileHeight = 84;

    public PhoneDevice Device = null!;
    private bool _selected;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
        }
    }
    public string? StatusText;
    public double? Progress;
    public event Action<DeviceTile>? TileClicked;

    private bool _hover;

    public DeviceTile(PhoneDevice device)
    {
        Device = device;
        Height = TileHeight;
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    public void UpdateDevice(PhoneDevice device)
    {
        Device = device;
        Invalidate();
    }

    public void SetProgress(string? status, double? progress)
    {
        StatusText = status;
        Progress = progress;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left) TileClicked?.Invoke(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var scheme = Theme.Scheme;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var bg = Theme.IsDark ? scheme.SecondaryContainer : scheme.CardColor;

        var stale = DateTimeOffset.Now - Device.LastSeen > TimeSpan.FromSeconds(8);
        var fade = stale ? 0.55 : 0.0;
        var surfaceForText = bg;

        if (Selected)
        {
            bg = Theme.Blend(bg, scheme.Primary, 0.10);
            surfaceForText = bg;
        }
        Gfx.FillRounded(g, bg, rect, Theme.RadiusMedium);
        if (_hover && !Selected)
            Gfx.FillRounded(g, scheme.Hover, rect, Theme.RadiusMedium);
        if (Selected)
            Gfx.DrawRounded(g, scheme.Primary, 2f, rect, Theme.RadiusMedium);

        var iconColor = Theme.Blend(Selected ? scheme.Primary : scheme.OnSurfaceVariant, surfaceForText, fade);
        var iconRect = new Rectangle(18, (Height - 46) / 2, 46, 46);
        Gfx.DrawGlyph(g, Glyphs.CellPhone, Theme.IconFont(26f), iconColor, iconRect);

        var textX = 80;
        var addressTail = Device.AddressStr.Length >= 5 ? Device.AddressStr[^5..] : Device.AddressStr;
        var name = string.IsNullOrWhiteSpace(Device.Name)
            ? string.Format(Lang.T("Device.Unknown"), addressTail)
            : Device.Name;
        var nameColor = Theme.Blend(scheme.OnSurface, surfaceForText, fade);
        TextRenderer.DrawText(g, name, Theme.SemiFont(12.5f),
            new Rectangle(textX, 12, Width - textX - 20, 26), nameColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (StatusText is not null)
        {
            var statusColor = StatusText.Contains("complete", StringComparison.OrdinalIgnoreCase)
                ? scheme.Primary : scheme.OnSurfaceVariant;
            TextRenderer.DrawText(g, StatusText, Theme.Font(9f),
                new Rectangle(textX, 42, Width - textX - 20, 18), statusColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            var barRect = new Rectangle(textX, 62, Width - textX - 24, 8);
            DrawBar(g, barRect);
            return;
        }

        // kind badge
        var badgeFont = Theme.SemiFont(8.5f);
        var badgeText = Device.KindLabel;
        var badgeSize = TextRenderer.MeasureText(badgeText, badgeFont);
        var badgeRect = new Rectangle(textX, 44, badgeSize.Width + 14, 20);
        var badgeBg = Theme.WithAlpha(scheme.Primary, Theme.IsDark ? 40 : 26);
        var badgeFg = Theme.Blend(scheme.Primary, surfaceForText, fade);
        Gfx.FillRounded(g, badgeBg, badgeRect, Theme.RadiusSmall);
        TextRenderer.DrawText(g, badgeText, badgeFont, badgeRect, badgeFg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

        var metaFont = Theme.Font(9f);
        var meta = $"RSSI {Device.Rssi}  ·  seen {SeenAgo()}";
        var metaColor = Theme.Blend(scheme.OnSurfaceVariant, surfaceForText, stale ? 0.55 : 0.15);
        TextRenderer.DrawText(g, meta, metaFont,
            new Rectangle(badgeRect.Right + 10, 44, Width - badgeRect.Right - 26, 20), metaColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void DrawBar(Graphics g, Rectangle rect)
    {
        var scheme = Theme.Scheme;
        Gfx.FillRounded(g, scheme.Track, rect, rect.Height / 2);
        var value = Math.Clamp(Progress ?? 0, 0, 1);
        if (value > 0.005)
        {
            var fillRect = new Rectangle(rect.X, rect.Y, Math.Max(rect.Height, (int)(rect.Width * value)), rect.Height);
            Gfx.FillRounded(g, scheme.Primary, fillRect, rect.Height / 2);
        }
    }

    private string SeenAgo()
    {
        var age = DateTimeOffset.Now - Device.LastSeen;
        if (age.TotalSeconds < 3) return Lang.T("Device.SeenNow");
        if (age.TotalSeconds < 60) return string.Format(Lang.T("Device.SeenSeconds"), (int)age.TotalSeconds);
        if (age.TotalMinutes < 60) return string.Format(Lang.T("Device.SeenMinutes"), (int)age.TotalMinutes);
        return string.Format(Lang.T("Device.SeenHours"), (int)age.TotalHours);
    }
}

/// <summary>Tile displaying a received file with size, time, sender, and action buttons.</summary>
public sealed class ReceivedFileTile : ThemedControl
{
    public const int TileHeight = 64;

    public string FilePath { get; }
    public string FileName { get; }
    public long SizeBytes { get; }
    public string SenderName { get; }
    public DateTime ReceivedAt { get; }

    public event Action<string>? OpenFileRequested;
    public event Action<string>? ShowInFolderRequested;

    private bool _hover;
    private bool _openHover;
    private bool _folderHover;
    private Rectangle _openRect;
    private Rectangle _folderRect;

    public ReceivedFileTile(string filePath, string senderName)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        SenderName = senderName;
        ReceivedAt = DateTime.Now;
        try { SizeBytes = new FileInfo(filePath).Length; } catch { }
        Height = TileHeight;
        Cursor = Cursors.Default;
        TabStop = false;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _openHover = false;
        _folderHover = false;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var oh = _openRect.Contains(e.Location);
        var fh = _folderRect.Contains(e.Location);
        if (oh != _openHover || fh != _folderHover)
        {
            _openHover = oh;
            _folderHover = fh;
            Cursor = (oh || fh) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (_openRect.Contains(e.Location))
        {
            OpenFileRequested?.Invoke(FilePath);
        }
        else if (_folderRect.Contains(e.Location))
        {
            ShowInFolderRequested?.Invoke(FilePath);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var scheme = Theme.Scheme;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var bg = Theme.IsDark ? scheme.SecondaryContainer : scheme.CardColor;

        Gfx.FillRounded(g, bg, rect, Theme.RadiusMedium);
        if (_hover)
            Gfx.FillRounded(g, scheme.Hover, rect, Theme.RadiusMedium);

        // File icon
        var iconRect = new Rectangle(14, (Height - 36) / 2, 36, 36);
        Gfx.DrawGlyph(g, Glyphs.Document, Theme.IconFont(22f), scheme.Primary, iconRect);

        // Action buttons on the right
        var btnW = 32;
        var btnH = 32;
        var btnY = (Height - btnH) / 2;
        _folderRect = new Rectangle(Width - 14 - btnW, btnY, btnW, btnH);
        _openRect = new Rectangle(_folderRect.Left - 8 - btnW, btnY, btnW, btnH);

        // Open button
        if (_openHover) Gfx.FillRounded(g, scheme.SurfaceContainerHigh, _openRect, Theme.RadiusSmall);
        Gfx.DrawGlyph(g, Glyphs.OpenFile, Theme.IconFont(14f), scheme.OnSurfaceVariant, _openRect);

        // Folder button
        if (_folderHover) Gfx.FillRounded(g, scheme.SurfaceContainerHigh, _folderRect, Theme.RadiusSmall);
        Gfx.DrawGlyph(g, Glyphs.OpenFolder, Theme.IconFont(14f), scheme.OnSurfaceVariant, _folderRect);

        // Text
        var textX = 60;
        var textW = Math.Max(20, _openRect.Left - textX - 8);

        TextRenderer.DrawText(g, FileName, Theme.SemiFont(10.5f),
            new Rectangle(textX, 12, textW, 20), scheme.OnSurface,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        var sub = $"{FormatSize(SizeBytes)} • {ReceivedAt:HH:mm} • {SenderName}";
        TextRenderer.DrawText(g, sub, Theme.Font(8.5f),
            new Rectangle(textX, 34, textW, 18), scheme.OnSurfaceVariant,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024f:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024f * 1024f):F1} MB",
        _ => $"{bytes / (1024f * 1024f * 1024f):F2} GB",
    };
}

/// <summary>One staged file: path + display info.</summary>
public sealed record FileEntry(string FullPath, string Name, long Size);

/// <summary>Rounded file thumbnail with name and a hover delete button, LocalSend-style.</summary>
public sealed class FileThumb : ThemedControl
{
    private const int ThumbSize = 56;

    public FileEntry Entry { get; }
    public event Action<FileThumb>? RemoveRequested;

    private Image? _image;
    private Icon? _icon;
    private bool _loadStarted;
    private bool _hover;
    private bool _hoverClose;

    public FileThumb(FileEntry entry)
    {
        Entry = entry;
        Size = new Size(70, 88);
        TabStop = false;
    }

    private static bool IsImageExt(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => true,
            _ => false,
        };
    }

    private void StartLoad()
    {
        _loadStarted = true;
        var path = Entry.FullPath;
        var isImage = IsImageExt(path);
        long len = 0;
        try { len = new FileInfo(path).Length; } catch { }
        if (isImage && len < 30_000_000)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var tmp = new Bitmap(fs);
                    var scale = Math.Min(1.0, 256.0 / Math.Max(tmp.Width, tmp.Height));
                    return (Image?)new Bitmap(tmp, Math.Max(1, (int)(tmp.Width * scale)), Math.Max(1, (int)(tmp.Height * scale)));
                }
                catch { return null; }
            }).ContinueWith(t =>
            {
                var image = t.Result;
                if (image is null) return;
                try
                {
                    if (IsDisposed || Disposing) { image.Dispose(); return; }
                    BeginInvoke(() =>
                    {
                        if (IsDisposed || Disposing) image.Dispose();
                        else { _image?.Dispose(); _image = image; Invalidate(); }
                    });
                }
                catch { image.Dispose(); }
            }, TaskScheduler.Default);
        }
        else
        {
            try
            {
                using var extracted = System.Drawing.Icon.ExtractAssociatedIcon(path);
                _icon = extracted is null ? null : (Icon)extracted.Clone();
            }
            catch { _icon = null; }
            Invalidate();
        }
    }

    private static Color CategoryColor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => FromHex("#43A047"),
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" => FromHex("#7E57C2"),
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" => FromHex("#EF6C00"),
            ".pdf" or ".doc" or ".docx" or ".txt" or ".md" => FromHex("#1E88E5"),
            ".apk" => FromHex("#388E3C"),
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => FromHex("#795548"),
            _ => FromHex("#607D8B"),
        };

    private static Color FromHex(string hex) => Theme.FromHex(hex);

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_loadStarted) StartLoad();
        var g = e.Graphics;
        var scheme = Theme.Scheme;
        var thumbRect = new Rectangle((Width - ThumbSize) / 2, 2, ThumbSize, ThumbSize);
        var path = Gfx.Rounded(thumbRect, Theme.RadiusMedium);
        using (path)
        {
            using (var bg = new SolidBrush(scheme.SurfaceVariant))
                g.FillPath(bg, path);

            if (_image is not null)
            {
                var scale = Math.Max(thumbRect.Width / (double)_image.Width, thumbRect.Height / (double)_image.Height);
                var srcW = (int)(thumbRect.Width / scale);
                var srcH = (int)(thumbRect.Height / scale);
                var srcRect = new Rectangle((_image.Width - srcW) / 2, (_image.Height - srcH) / 2, srcW, srcH);
                g.SetClip(path);
                g.DrawImage(_image, thumbRect, srcRect, GraphicsUnit.Pixel);
                g.ResetClip();
            }
            else if (_icon is not null)
            {
                var iconRect = new Rectangle(thumbRect.X + 12, thumbRect.Y + 12, 32, 32);
                g.DrawIcon(_icon, iconRect);
            }
            else
            {
                var ext = Path.GetExtension(Entry.Name);
                var label = ext.Length > 1 ? ext[1..] : Lang.T("File.Fallback");
                if (label.Length > 4) label = label[..4];
                Gfx.FillRounded(g, CategoryColor(Entry.FullPath), thumbRect, Theme.RadiusMedium);
                TextRenderer.DrawText(g, label.ToUpperInvariant(), Theme.SemiFont(9f), thumbRect, Color.White,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            }
        }

        if (_hover)
        {
            var closeRect = new Rectangle(thumbRect.Right - 20, thumbRect.Top - 4, 22, 22);
            Gfx.FillRounded(g, Theme.WithAlpha(scheme.OnSurface, 210), closeRect, closeRect.Height / 2);
            Gfx.DrawGlyph(g, Glyphs.Cancel, Theme.IconFont(8f), scheme.OnPrimary,
                new Rectangle(closeRect.X + 4, closeRect.Y + 4, 14, 14));
        }

        TextRenderer.DrawText(g, Entry.Name, Theme.Font(7.8f),
            new Rectangle(0, ThumbSize + 6, Width, 18), scheme.OnSurfaceVariant,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private Rectangle CloseHitRect => new((Width - ThumbSize) / 2 + ThumbSize - 20, -2, 22, 22);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var overClose = CloseHitRect.Contains(e.Location);
        if (overClose != _hoverClose)
        {
            _hoverClose = overClose;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _hoverClose = false;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && CloseHitRect.Contains(e.Location)) RemoveRequested?.Invoke(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _image?.Dispose(); _icon?.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        base.OnHandleDestroyed(e);
    }
}

/// <summary>10px-tall rounded linear progress bar (LocalSend CustomProgressBar).</summary>
public sealed class RoundedProgressBar : ThemedControl
{
    public double Value { get; private set; }

    public RoundedProgressBar() { Height = 10; TabStop = false; }

    public void SetValue(double value)
    {
        Value = Math.Clamp(value, 0, 1);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Gfx.FillRounded(g, Theme.Scheme.Track, rect, rect.Height / 2);
        if (Value > 0.005)
        {
            var fillRect = new Rectangle(rect.X, rect.Y,
                Math.Max(rect.Height, (int)(rect.Width * Value)), rect.Height);
            Gfx.FillRounded(g, Theme.Scheme.Primary, fillRect, rect.Height / 2);
        }
    }
}

/// <summary>Circular scan button with a Material-style indeterminate arc spinner.</summary>
public sealed class ScanButton : ThemedControl
{
    private bool _spinning;
    private bool _hover;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Diagnostics.Stopwatch _sw = new();

    public ScanButton()
    {
        Size = new Size(42, 42);
        Cursor = Cursors.Hand;
        TabStop = false;
        _timer = new System.Windows.Forms.Timer { Interval = 16 }; // ~60fps
        _timer.Tick += (_, _) => { if (_spinning) Invalidate(); };
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Spinning
    {
        get => _spinning;
        set
        {
            if (_spinning == value) return;
            _spinning = value;
            if (value) { _sw.Restart(); _timer.Start(); }
            else { _timer.Stop(); _sw.Reset(); }
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var scheme = Theme.Scheme;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Gfx.FillRounded(g, scheme.SecondaryContainer, rect, rect.Height / 2);
        if (_hover) Gfx.FillRounded(g, scheme.Hover, rect, rect.Height / 2);

        if (_spinning)
        {
            var t = _sw.Elapsed.TotalSeconds;
            // rotation: full circle every 1.4s
            var rotation = (float)(t * 257.0) % 360f;
            // sweep: oscillates between 60 and 270 degrees via sine wave (period ~1.6s)
            var sweep = 60f + 105f * (1f + (float)Math.Sin(t * Math.PI * 2.0 / 1.6));

            var cx = Width / 2f;
            var cy = Height / 2f;
            var r = 14f;
            var penWidth = 2.8f;
            using var trackPen = new Pen(Theme.WithAlpha(scheme.OnSurfaceVariant, 30), penWidth);
            trackPen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
            trackPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            g.DrawEllipse(trackPen, cx - r, cy - r, r * 2, r * 2);

            using var arcPen = new Pen(_hover ? scheme.OnSecondaryContainer : scheme.Primary, penWidth);
            arcPen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
            arcPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            g.DrawArc(arcPen, cx - r, cy - r, r * 2, r * 2, rotation, sweep);
        }
        else
        {
            Gfx.DrawGlyph(g, Glyphs.Sync, Theme.IconFont(16f), scheme.Primary,
                new Rectangle(8, 8, 26, 26), 0);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Empty-state selection area: dashed drop target with a browse button.
/// Everything is owner-drawn in OnPaint — no child controls — so z-order is fully controlled.</summary>
public sealed class DropZone : ThemedControl
{
    public event Action? BrowseRequested;

    private bool _btnHover;
    private bool _btnDown;
    private Rectangle _btnRect;

    public DropZone()
    {
        Cursor = Cursors.Hand;
        TabStop = false;
        Height = 182;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var over = _btnRect.Contains(e.Location);
        if (over != _btnHover) { _btnHover = over; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && _btnRect.Contains(e.Location)) { _btnDown = true; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_btnDown) { _btnDown = false; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left) BrowseRequested?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var scheme = Theme.Scheme;
        var w = Width;
        var h = Height;

        // dashed border
        Gfx.DrawDashedRounded(g, scheme.OutlineVariant,
            new Rectangle(Dpi(2), Dpi(2), w - Dpi(5), h - Dpi(5)), Dpi(Theme.RadiusMedium));

        // download icon
        var iconSize = Dpi(36);
        var iconRect = new Rectangle((w - iconSize) / 2, Dpi(8), iconSize, iconSize);
        Gfx.DrawGlyph(g, Glyphs.Download, Theme.IconFont(24f), scheme.OnSurfaceVariant, iconRect);

        // "Drag & drop files here"
        TextRenderer.DrawText(g, Lang.T("Drop.DragHere"), Theme.SemiFont(11f),
            new Rectangle(0, Dpi(48), w, Dpi(24)), scheme.OnSurface,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // "or browse your computer"
        TextRenderer.DrawText(g, Lang.T("Drop.OrBrowse"), Theme.Font(9f),
            new Rectangle(0, Dpi(70), w, Dpi(18)), scheme.OnSurfaceVariant,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // browse button (tonal style, owner-drawn)
        var btnW = Dpi(150);
        var btnH = Dpi(38);
        var btnY = Math.Max(h - btnH - Dpi(16), Dpi(108));
        _btnRect = new Rectangle((w - btnW) / 2, btnY, btnW, btnH);
        var btnBg = scheme.SecondaryContainer;
        if (!Enabled) btnBg = Theme.WithAlpha(scheme.OnSurface, 26);
        Gfx.FillRounded(g, btnBg, _btnRect, _btnRect.Height / 2);
        if (_btnHover && Enabled) Gfx.FillRounded(g, scheme.Hover, _btnRect, _btnRect.Height / 2);
        var btnFg = Enabled ? scheme.OnSecondaryContainer : Theme.Blend(scheme.OnSurface, scheme.Surface, 0.62);
        // icon + text inside button
        var glyphSize = Dpi(20);
        var glyphRect = new Rectangle(_btnRect.X + Dpi(12), _btnRect.Y + (_btnRect.Height - glyphSize) / 2, glyphSize, glyphSize);
        Gfx.DrawGlyph(g, Glyphs.OpenFile, Theme.IconFont(11f), btnFg, glyphRect);
        var textRect = new Rectangle(_btnRect.X + Dpi(34), _btnRect.Y, _btnRect.Width - Dpi(34), _btnRect.Height);
        TextRenderer.DrawText(g, Lang.T("Btn.BrowseFiles"), Theme.SemiFont(9.5f), textRect, btnFg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Full-area overlay shown while a drag is over the window ("Place items to share.").</summary>
public sealed class DropOverlay : ThemedControl
{
    public DropOverlay() { Visible = false; TabStop = false; }

    protected override void OnThemeChanged()
    {
        BackColor = Theme.Scheme.Surface;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var scheme = Theme.Scheme;
        var cy = Height * 0.42f;
        var iconRect = new Rectangle((Width - 120) / 2, (int)(cy - 70), 120, 120);
        Gfx.DrawGlyph(g, Glyphs.Download, Theme.IconFont(72f), scheme.Primary, iconRect);
        TextRenderer.DrawText(g, Lang.T("Drop.PlaceItems"), Theme.SemiFont(14f),
            new Rectangle(0, (int)(cy + 58), Width, 34), scheme.OnSurface,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Empty nearby-devices hint (dashed card with a wifi glyph).</summary>
public sealed class EmptyDeviceHint : ThemedControl
{
    public EmptyDeviceHint() { Height = 96; TabStop = false; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var scheme = Theme.Scheme;
        Gfx.DrawDashedRounded(g, scheme.OutlineVariant,
            new Rectangle(2, 2, Width - 5, Height - 5), Theme.RadiusMedium);

        Gfx.DrawGlyph(g, Glyphs.Wifi, Theme.IconFont(15f), scheme.OnSurfaceVariant,
            new Rectangle(26, (Height - 30) / 2, 30, 30));
        TextRenderer.DrawText(g, Lang.T("Hint.NoPhones"), Theme.SemiFont(10.5f),
            new Rectangle(70, Height / 2 - 24, Width - 90, 22), scheme.OnSurface,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, string.Format(Lang.T("Hint.OpenApp"), "\u4e92\u4f20"), Theme.Font(9f),
            new Rectangle(70, Height / 2 + 0, Width - 90, 20), scheme.OnSurfaceVariant,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Themed dropdown (replaces the unstylable native ComboBox): tonal pill face that
/// opens a themed context menu.</summary>
public sealed class AppDropdown : ThemedControl
{
    private string[] _items = [];
    private int _selectedIndex = -1;
    private bool _hover;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string[] Items
    {
        get => _items;
        set { _items = value; if (_selectedIndex >= value.Length) _selectedIndex = value.Length - 1; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value) return;
            _selectedIndex = value;
            Invalidate();
        }
    }

    public event Action<int>? SelectedIndexChanged;

    public AppDropdown(int width, int height = 40)
    {
        Size = new Size(width, height);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        ShowMenu();
    }

    private void ShowMenu()
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Renderer = new ThemedMenuRenderer(),
            Font = Theme.Font(9.5f),
        };
        menu.Closed += (_, _) => BeginInvoke(() => menu.Dispose());
        for (var i = 0; i < _items.Length; i++)
        {
            var index = i;
            var item = new ToolStripMenuItem(_items[i])
            {
                ForeColor = i == _selectedIndex ? Theme.Scheme.Primary : Theme.Scheme.OnSurface,
                Padding = new Padding(4, 2, 12, 2),
            };
            item.Click += (_, _) =>
            {
                SelectedIndex = index;
                SelectedIndexChanged?.Invoke(index);
            };
            menu.Items.Add(item);
        }
        menu.Show(this, new Point(0, Height + 4));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var scheme = Theme.Scheme;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Gfx.FillRounded(g, scheme.SecondaryContainer, rect, rect.Height / 2);
        if (_hover) Gfx.FillRounded(g, scheme.Hover, rect, rect.Height / 2);

        var text = _selectedIndex >= 0 && _selectedIndex < _items.Length ? _items[_selectedIndex] : "";
        var textRect = new Rectangle(16, 0, Width - 16 - 34, Height);
        TextRenderer.DrawText(g, text, Theme.SemiFont(9.5f), textRect, scheme.OnSecondaryContainer,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var chevron = "\uE70D"; // ChevronDown
        Gfx.DrawGlyph(g, chevron, Theme.IconFont(9f), scheme.OnSecondaryContainer,
            new Rectangle(Width - 30, (Height - 20) / 2, 20, 20));
    }
}

/// <summary>Context-menu color table driven by the app theme.</summary>
public sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
{
    public ThemedMenuRenderer() : base(new ThemedMenuColorTable()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var scheme = Theme.Scheme;
        var rect = new Rectangle(Point.Empty, e.Item.Size);
        if (e.Item.Selected)
        {
            using var b = new SolidBrush(scheme.SecondaryContainer);
            e.Graphics.FillRectangle(b, 1, 1, rect.Width - 2, rect.Height - 2);
        }
        if (e.Item is ToolStripMenuItem { Pressed: true })
        {
            using var b = new SolidBrush(scheme.SurfaceContainerHigh);
            e.Graphics.FillRectangle(b, 1, 1, rect.Width - 2, rect.Height - 2);
        }
    }
}

public sealed class ThemedMenuColorTable : ProfessionalColorTable
{
    private static Color S(Func<ThemeScheme, Color> pick) => pick(Theme.Scheme);

    public override Color ToolStripDropDownBackground => S(s => s.SurfaceContainerHigh);
    public override Color ImageMarginGradientBegin => S(s => s.SurfaceContainerHigh);
    public override Color ImageMarginGradientMiddle => S(s => s.SurfaceContainerHigh);
    public override Color ImageMarginGradientEnd => S(s => s.SurfaceContainerHigh);
    public override Color MenuBorder => S(s => s.OutlineVariant);
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => S(s => s.SecondaryContainer);
    public override Color MenuItemSelectedGradientBegin => S(s => s.SecondaryContainer);
    public override Color MenuItemSelectedGradientEnd => S(s => s.SecondaryContainer);
    public override Color MenuItemPressedGradientBegin => S(s => s.SurfaceContainerHigh);
    public override Color MenuItemPressedGradientEnd => S(s => s.SurfaceContainerHigh);
    public override Color SeparatorDark => S(s => s.OutlineVariant);
    public override Color SeparatorLight => S(s => s.OutlineVariant);
}

/// <summary>Bottom action bar of the Send tab (status, progress, mode, send).</summary>
public sealed class FooterBar : ThemedPanel
{
    public FooterBar() => CardBackground = true;

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(Theme.Scheme.OutlineVariant, 1f);
        e.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }
}

/// <summary>Runtime-drawn app icon (teal rounded square + white paper plane) used for the
/// window and tray; the compiled exe uses Assets/app.ico via ApplicationIcon.</summary>
public static class AppIcon
{
    private static Icon? _cached;

    public static Icon App => _cached ??= Create(32);

    public static Icon Create(int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, size - 1, size - 1);
            Gfx.FillRounded(g, Color.FromArgb(255, 0x00, 0x6A, 0x60), rect, (int)(size * 0.22));

            var sc = size * 0.55f / 22f;
            var offX = (size - 22f * sc) / 2f - 1f * sc;
            var offY = (size - 18f * sc) / 2f - 3f * sc;
            PointF P(float x, float y) => new(offX + x * sc, offY + y * sc);
            using var white = new SolidBrush(Color.White);
            g.FillPolygon(white,
            [
                P(2, 21), P(23, 12), P(2, 3), P(2, 10), P(17, 12), P(2, 14),
            ]);
        }
        var icon = Icon.FromHandle(bmp.GetHicon());
        return (Icon)icon.Clone();  // detach from the temp handle
    }
}
