using System.Drawing.Drawing2D;

namespace HoyoTray;

/// <summary>
/// Pick which games/accounts are shown and which tray icon to use.
/// Edits a copy of the settings; the caller reads <see cref="Result"/> after OK.
/// </summary>
class SettingsForm : Form
{
    readonly Theme t = Theme.Current();
    readonly float k;
    readonly Font bold = new("Segoe UI Semibold", 10f);
    readonly Font body = new("Segoe UI", 9.5f);
    readonly Font small = new("Segoe UI", 8.5f);
    readonly Font header = new("Segoe UI Semibold", 11.5f);

    public Settings Result { get; }

    int S(float px) => (int)Math.Round(px * k);

    public SettingsForm(Settings settings, IReadOnlyDictionary<Game, List<GameAccount>> accounts)
    {
        Result = settings.Clone();
        k = DeviceDpi / 96f;

        Text = "HoyoTray Settings";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = t.Back;
        ForeColor = t.Text;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        if (t.Dark) HandleCreated += (_, _) => Theme.DarkTitleBar(Handle);

        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(S(20), S(14), S(20), S(18)),
        };
        Controls.Add(flow);

        flow.Controls.Add(Header("Games & accounts", first: true));
        foreach (var g in Game.All)
        {
            var list = accounts.TryGetValue(g, out var l) ? l : [];
            var gameRow = new ToggleRow(this, g.Name, list.Count == 0 ? "No account on this HoYoLAB login" : null,
                Result.GameEnabled(g), bold, accent: g.Accent);
            flow.Controls.Add(gameRow);

            var rows = new List<ToggleRow>();
            foreach (var a in list)
            {
                var row = new ToggleRow(this, a.Nickname, $"UID {a.Uid}  ·  Lv {a.Level}  ·  {HoyoApi.RegionName(a.Region)}",
                    Result.AccountShown(g, a, list), body, indent: S(22)) { Enabled = gameRow.Checked };
                row.Changed += () => Result.Accounts[Settings.Key(g, a)] = row.Checked;
                rows.Add(row);
                flow.Controls.Add(row);
            }
            gameRow.Changed += () =>
            {
                if (gameRow.Checked) Result.DisabledGames.Remove(g.Key);
                else Result.DisabledGames.Add(g.Key);
                foreach (var r in rows) r.Enabled = gameRow.Checked;
            };
        }

        flow.Controls.Add(Header("Tray icon"));
        var tiles = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var tileList = new List<IconTile>();
        foreach (var (key, name) in Theme.IconChoices)
        {
            var tile = new IconTile(this, key, name, Result.Icon == key);
            tile.Click += (_, _) =>
            {
                Result.Icon = key;
                foreach (var other in tileList) other.Selected = other == tile;
            };
            tileList.Add(tile);
            tiles.Controls.Add(tile);
        }
        flow.Controls.Add(tiles);

        var done = new Button
        {
            Text = "Done",
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Resin,
            ForeColor = Color.White,
            Font = bold,
            Size = new Size(S(100), S(34)),
            Margin = new Padding(0, S(18), 0, 0),
            Cursor = Cursors.Hand,
        };
        done.FlatAppearance.BorderSize = 0;
        done.FlatAppearance.BorderColor = Theme.Resin; // default-button outline would otherwise be white
        flow.Controls.Add(done);
        AcceptButton = done;
        CancelButton = done;
    }

    Label Header(string text, bool first = false) => new()
    {
        Text = text,
        UseMnemonic = false,
        Font = header,
        ForeColor = t.Text,
        AutoSize = true,
        Margin = new Padding(0, first ? 0 : S(18), 0, S(6)),
    };

    /// <summary>A row with a title, optional dim subtitle and a toggle switch on the right.</summary>
    class ToggleRow : Control
    {
        readonly SettingsForm f;
        readonly string? sub;
        readonly Font font;
        readonly Color? accent;
        readonly int indent;
        bool isChecked, hover;

        public event Action? Changed;

        public bool Checked
        {
            get => isChecked;
            set { isChecked = value; Invalidate(); }
        }

        public ToggleRow(SettingsForm f, string text, string? sub, bool isChecked, Font font, Color? accent = null, int indent = 0)
        {
            this.f = f;
            this.sub = sub;
            this.font = font;
            this.accent = accent;
            this.indent = indent;
            this.isChecked = isChecked;
            Text = text;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Size = new Size(f.S(440), f.S(sub == null ? 36 : 46));
            Margin = new Padding(0, 0, 0, f.S(2));
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnClick(EventArgs e)
        {
            Checked = !Checked;
            Changed?.Invoke();
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var t = f.t;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(t.Back);

            if (hover && Enabled)
            {
                using var p = Theme.Rounded(new RectangleF(indent, 0, Width - indent - 1, Height - 1), f.S(6));
                using var b = new SolidBrush(t.Hover);
                g.FillPath(b, p);
            }

            int x = indent + f.S(10);
            var textColor = Enabled ? t.Text : t.Dim;
            if (accent is Color a)
            {
                using var dot = new SolidBrush(Enabled ? a : t.Dim);
                g.FillEllipse(dot, x, Height / 2f - f.S(4), f.S(8), f.S(8));
                x += f.S(16);
            }

            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            int textW = Width - x - f.S(64);
            if (sub == null)
                TextRenderer.DrawText(g, Text, font, new Rectangle(x, 0, textW, Height), textColor, flags | TextFormatFlags.VerticalCenter);
            else
            {
                TextRenderer.DrawText(g, Text, font, new Rectangle(x, f.S(5), textW, f.S(20)), textColor, flags);
                TextRenderer.DrawText(g, sub, f.small, new Rectangle(x, f.S(25), textW, f.S(18)), t.Dim, flags);
            }

            // Toggle switch
            var r = new RectangleF(Width - f.S(52), (Height - f.S(20)) / 2f, f.S(40), f.S(20));
            using var path = Theme.Rounded(r, r.Height / 2);
            bool on = Checked && Enabled;
            if (on)
            {
                using var b = new SolidBrush(Theme.Resin);
                g.FillPath(b, path);
            }
            else
            {
                using var pen = new Pen(t.Dim, f.S(1.2f));
                g.DrawPath(pen, path);
            }
            float kn = r.Height - f.S(8);
            float kx = Checked ? r.Right - f.S(4) - kn : r.X + f.S(4);
            using var knob = new SolidBrush(on ? Color.White : t.Dim);
            g.FillEllipse(knob, kx, r.Y + f.S(4), kn, kn);
        }
    }

    /// <summary>Clickable icon preview with a label; highlighted when selected.</summary>
    class IconTile : Control
    {
        readonly SettingsForm f;
        readonly Bitmap art;
        bool selected, hover;

        public bool Selected
        {
            get => selected;
            set { selected = value; Invalidate(); }
        }

        public IconTile(SettingsForm f, string key, string name, bool selected)
        {
            this.f = f;
            this.selected = selected;
            art = Theme.LoadArt(key);
            Text = name;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Size = new Size(f.S(140), f.S(112));
            Margin = new Padding(0, 0, f.S(10), 0);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) art.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var t = f.t;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(t.Back);

            var box = new RectangleF(f.S(1), f.S(1), Width - f.S(2), Height - f.S(2));
            using (var p = Theme.Rounded(box, f.S(8)))
            {
                using var b = new SolidBrush(selected || hover ? t.Hover : t.Back);
                g.FillPath(b, p);
                using var pen = new Pen(selected ? Theme.Resin : t.Border, f.S(selected ? 2 : 1));
                g.DrawPath(pen, p);
            }

            int s = f.S(56);
            g.DrawImage(art, (Width - s) / 2, f.S(12), s, s);
            TextRenderer.DrawText(g, Text, f.small, new Rectangle(0, f.S(78), Width, f.S(22)),
                selected ? t.Text : t.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }
    }
}
