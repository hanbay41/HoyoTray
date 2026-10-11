using System.Drawing.Drawing2D;

namespace HoyoTray;

/// <summary>Shared fonts / DPI scale for the custom menu.</summary>
class MenuStyle
{
    public Theme T = Theme.Current();
    public float Scale = 1;
    public readonly Font Title = new("Segoe UI Semibold", 9.5f);
    public readonly Font Body = new("Segoe UI", 9f);
    public readonly Font Small = new("Segoe UI", 8.25f);
    public readonly Font Value = new("Segoe UI Semibold", 13f);
    public readonly Font Glyph = MakeGlyphFont();
    public int Width => S(290);

    public int S(float px) => (int)Math.Round(px * Scale);

    static Font MakeGlyphFont()
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            var f = new Font(name, 10.5f);
            if (f.Name == name) return f;
            f.Dispose();
        }
        return new Font("Segoe UI Symbol", 10.5f);
    }

    public static void Text(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags = 0) =>
        TextRenderer.DrawText(g, s, f, r, c, flags | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

    public static Size Measure(string s, Font f) =>
        TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
}

class MenuRenderer(MenuStyle st) : ToolStripRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) =>
        e.Graphics.Clear(st.T.Back);

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(st.T.Border);
        var r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    public static void Apply(ContextMenuStrip menu, MenuStyle st)
    {
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        menu.Renderer = new MenuRenderer(st);
    }

    /// <summary>
    /// The menu reserves extra space on the right for image/arrow margins it doesn't need here;
    /// size it ourselves so both side gutters match.
    /// </summary>
    public static void FitWidth(ContextMenuStrip menu, MenuStyle st)
    {
        menu.AutoSize = true;
        int height = menu.GetPreferredSize(Size.Empty).Height;
        menu.AutoSize = false;
        menu.Size = new Size(st.Width + menu.Padding.Left * 2, height);
    }
}

/// <summary>Non-clickable card: game name, big value, progress bar, footer line.</summary>
class StatItem(MenuStyle st) : ToolStripItem
{
    public string Game = "", Account = "", Label = "", Footer = "", Side = "";
    public int Current, Max;
    public Color Accent;
    public bool SideDone;
    public string? Warning;

    public override bool CanSelect => false;

    public override Size GetPreferredSize(Size constrainingSize) =>
        new(st.Width, st.S(Warning == null ? 96 : 116));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var t = st.T;
        int pad = st.S(14), w = Width - pad * 2, y = st.S(10);

        // Header: colored dot + game, account on the right
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var dot = new SolidBrush(Accent))
            g.FillEllipse(dot, pad, y + st.S(5), st.S(8), st.S(8));
        MenuStyle.Text(g, Game, st.Title, t.Text, new Rectangle(pad + st.S(14), y, w, st.S(20)));
        MenuStyle.Text(g, Account, st.Small, t.Dim, new Rectangle(pad, y + st.S(2), w, st.S(18)), TextFormatFlags.Right);
        y += st.S(24);

        // Label left, "142 / 200" right
        var maxText = $" / {Max}";
        var maxSize = MenuStyle.Measure(maxText, st.Body);
        var valSize = MenuStyle.Measure(Current.ToString(), st.Value);
        int valH = st.S(26);
        MenuStyle.Text(g, Label, st.Body, t.Dim, new Rectangle(pad, y + st.S(7), w, st.S(18)));
        MenuStyle.Text(g, maxText, st.Body, t.Dim, new Rectangle(pad + w - maxSize.Width, y + st.S(7), maxSize.Width, st.S(18)));
        bool full = Current >= Max;
        MenuStyle.Text(g, Current.ToString(), st.Value, full ? Accent : t.Text,
            new Rectangle(pad + w - maxSize.Width - valSize.Width, y, valSize.Width, valH));
        y += valH + st.S(4);

        // Progress bar
        var track = new RectangleF(pad, y, w, st.S(6));
        using (var tp = Theme.Rounded(track, track.Height / 2))
        using (var tb = new SolidBrush(t.Track))
            g.FillPath(tb, tp);
        float frac = Max > 0 ? Math.Clamp(Current / (float)Max, 0, 1) : 0;
        if (frac > 0)
        {
            var fill = new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * frac), track.Height);
            using var fp = Theme.Rounded(fill, fill.Height / 2);
            using var fb = new SolidBrush(Accent);
            g.FillPath(fb, fp);
        }
        y += st.S(14);

        // Footer: "Full in 7h 44m · 23:40" left, side stat right
        MenuStyle.Text(g, Footer, st.Small, full ? Accent : t.Dim, new Rectangle(pad, y, w, st.S(18)));
        MenuStyle.Text(g, Side, st.Small, SideDone ? Theme.Good : t.Dim, new Rectangle(pad, y, w, st.S(18)), TextFormatFlags.Right);

        if (Warning != null)
        {
            y += st.S(20);
            MenuStyle.Text(g, "⚠  " + Warning, st.Small, Theme.Warn, new Rectangle(pad, y, w, st.S(18)));
        }
    }
}

/// <summary>Thin divider line, full menu width.</summary>
class SepItem(MenuStyle st) : ToolStripItem
{
    public override bool CanSelect => false;
    public override Size GetPreferredSize(Size constrainingSize) => new(st.Width, st.S(9));

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(st.T.Separator);
        int y = Height / 2;
        e.Graphics.DrawLine(pen, st.S(10), y, Width - st.S(10), y);
    }
}

/// <summary>Small non-clickable text line (status / warnings).</summary>
class TextItem(MenuStyle st, string text, Color? color = null) : ToolStripItem
{
    public override bool CanSelect => false;
    public override Size GetPreferredSize(Size constrainingSize) => new(st.Width, st.S(26));

    protected override void OnPaint(PaintEventArgs e) =>
        MenuStyle.Text(e.Graphics, text, st.Small, color ?? st.T.Dim,
            new Rectangle(st.S(14), 0, Width - st.S(28), Height), TextFormatFlags.VerticalCenter);
}

/// <summary>Clickable row with an icon glyph and an optional toggle switch.</summary>
class ActionItem : ToolStripButton
{
    readonly MenuStyle st;
    readonly string glyph;
    public bool? Toggle;

    public ActionItem(MenuStyle st, string glyph, string text, EventHandler onClick) : base(text, null, onClick)
    {
        this.st = st;
        this.glyph = glyph;
    }

    public override Size GetPreferredSize(Size constrainingSize) => new(st.Width, st.S(34));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var t = st.T;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (Selected)
        {
            using var p = Theme.Rounded(new RectangleF(st.S(2), st.S(2), Width - st.S(4), Height - st.S(4)), st.S(5));
            using var b = new SolidBrush(t.Hover);
            g.FillPath(b, p);
        }

        MenuStyle.Text(g, glyph, st.Glyph, t.Text, new Rectangle(st.S(14), 0, st.S(20), Height), TextFormatFlags.VerticalCenter);
        MenuStyle.Text(g, Text ?? "", st.Body, t.Text, new Rectangle(st.S(44), 0, Width - st.S(100), Height), TextFormatFlags.VerticalCenter);

        if (Toggle is bool on)
        {
            var r = new RectangleF(Width - st.S(52), (Height - st.S(18)) / 2f, st.S(36), st.S(18));
            using var path = Theme.Rounded(r, r.Height / 2);
            if (on)
            {
                using var b = new SolidBrush(Theme.Resin);
                g.FillPath(b, path);
            }
            else
            {
                using var pen = new Pen(t.Dim, st.S(1.2f));
                g.DrawPath(pen, path);
            }
            float k = r.Height - st.S(8);
            float kx = on ? r.Right - st.S(4) - k : r.X + st.S(4);
            using var knob = new SolidBrush(on ? Color.White : t.Dim);
            g.FillEllipse(knob, kx, r.Y + st.S(4), k, k);
        }
    }
}
