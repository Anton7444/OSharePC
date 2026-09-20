using System.Runtime.InteropServices;

namespace OShareSender.Ui;

public enum DialogKind { Info, Error, Success }

/// <summary>Styled modal dialogs replacing the stock gray MessageBox, with a Material look:
/// rounded surface, tinted icon badge and a filled primary button.</summary>
public static class AppDialog
{
    public static DialogResult Show(Form? owner, string title, string message, DialogKind kind)
    {
        using var dlg = new DialogForm(title, message, kind);
        return owner is null ? dlg.ShowDialog() : dlg.ShowDialog(owner);
    }

    public static DialogResult Info(Form? owner, string title, string message) => Show(owner, title, message, DialogKind.Info);

    public static DialogResult Error(Form? owner, string title, string message) => Show(owner, title, message, DialogKind.Error);

    public static bool Confirm(Form? owner, string title, string message) => MessageBox.Show(owner, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private sealed class DialogForm : Form
    {
        private const int CS_DROPSHADOW = 0x20000;

        private readonly string _message;
        private readonly DialogKind _kind;
        private Rectangle _okRect;
        private bool _okHover;

        public DialogForm(string title, string message, DialogKind kind)
        {
            _message = message;
            _kind = kind;
            Text = title;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;
            KeyDown += (_, e) =>
            {
                if (e.KeyCode is Keys.Escape or Keys.Enter) Close();
            };
            Size = ComputeSize(message);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateOkRect();
        }

        private void UpdateOkRect() => _okRect = new Rectangle(Width - 24 - 96, Height - 22 - 34, 96, 34);

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UpdateOkRect();
            if (Owner is null)
            {
                var screen = Screen.FromPoint(Cursor.Position).Bounds;
                Location = new Point(screen.Left + (screen.Width - Width) / 2,
                    screen.Top + Math.Max(0, (screen.Height - Height) / 3));
            }
            ApplyRoundedRegion();
        }

        private void ApplyRoundedRegion()
        {
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = Gfx.Rounded(rect, Theme.RadiusDialog);
            Region?.Dispose();
            Region = new Region(path);
        }

        private Size ComputeSize(string message)
        {
            const int width = 380;
            var msgFont = Theme.Font(9.5f);
            var msgHeight = TextRenderer.MeasureText(message, msgFont,
                new Size(width - 100, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            var bodyHeight = Math.Max(48, msgHeight);
            var height = 32 + bodyHeight + 62 + 22;
            var size = new Size(width, height);
            _okRect = new Rectangle(width - 24 - 96, height - 22 - 34, 96, 34);
            return size;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var scheme = Theme.Scheme;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            Gfx.FillRounded(g, scheme.SurfaceContainerHigh, rect, Theme.RadiusDialog);

            var (badgeBg, badgeFg, glyph) = _kind switch
            {
                DialogKind.Error => (scheme.ErrorContainer, scheme.OnErrorContainer, Glyphs.Error),
                DialogKind.Success => (scheme.PrimaryContainer, scheme.OnPrimaryContainer, Glyphs.CheckMark),
                _ => (scheme.PrimaryContainer, scheme.OnPrimaryContainer, Glyphs.Info),
            };
            var badge = new Rectangle(24, 30, 48, 48);
            Gfx.FillRounded(g, badgeBg, badge, badge.Height / 2);
            Gfx.DrawGlyph(g, glyph, Theme.IconFont(16f), badgeFg, new Rectangle(badge.X + 12, badge.Y + 12, 24, 24));

            var textX = badge.Right + 16;
            var textW = Width - textX - 24;
            TextRenderer.DrawText(g, Text, Theme.SemiFont(11.5f),
                new Rectangle(textX, 32, textW, 24), scheme.OnSurface,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, _message, Theme.Font(9.5f),
                new Rectangle(textX, 58, textW, Height - 58 - 72), scheme.OnSurfaceVariant,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

            UpdateOkRect();
            Gfx.FillRounded(g, _kind == DialogKind.Error ? scheme.Error : scheme.Primary, _okRect, _okRect.Height / 2);
            if (_okHover) Gfx.FillRounded(g, scheme.Hover, _okRect, _okRect.Height / 2);
            TextRenderer.DrawText(g, Lang.T("Btn.OK"), Theme.SemiFont(10f), _okRect,
                _kind == DialogKind.Error ? scheme.OnError : scheme.OnPrimary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hover = _okRect.Contains(e.Location);
            if (hover != _okHover)
            {
                _okHover = hover;
                Cursor = hover ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (_okRect.Contains(e.Location)) { DialogResult = DialogResult.OK; Close(); }
        }
    }
}
