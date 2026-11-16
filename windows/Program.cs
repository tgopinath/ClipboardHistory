// Clipboard History for Windows — a system tray clipboard manager.
// Mirrors the macOS app: background recording, search, pin, copy, Copy All, newest-first order.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ClipboardHistory;

// MARK: - Native

static class Native
{
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// Hidden message-only window: clipboard change notifications and the global hot key.
sealed class Watcher : NativeWindow, IDisposable
{
    public event Action? ClipboardChanged;
    public event Action? HotKeyPressed;

    public Watcher()
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
        Native.AddClipboardFormatListener(Handle);
        // Ctrl+Alt+H ("history"). Win+V is Windows' own clipboard and Ctrl+Alt+V is Office's Paste Special.
        Native.RegisterHotKey(Handle, 1, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.H);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_CLIPBOARDUPDATE) ClipboardChanged?.Invoke();
        else if (m.Msg == Native.WM_HOTKEY) HotKeyPressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Native.RemoveClipboardFormatListener(Handle);
        Native.UnregisterHotKey(Handle, 1);
        DestroyHandle();
    }
}

// MARK: - Model

sealed class ClipItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "text"; // "text" or "image"
    public string? Text { get; set; }
    public string? ImageFile { get; set; }
    public DateTime Date { get; set; }
    public bool Pinned { get; set; }
    [JsonIgnore] public bool IsImage => Kind == "image";
}

sealed class AppSettings
{
    public int Limit { get; set; } = 200;
    public bool AutoPaste { get; set; }
    public bool DidFirstLaunch { get; set; }
}

// MARK: - Store (keeps history on disk)

sealed class ClipStore
{
    public static readonly int[] Limits = { 50, 100, 200, 500, 1000 };
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public List<ClipItem> Items { get; private set; } = new();
    public AppSettings Settings { get; private set; } = new();
    public bool Paused { get; set; }
    public event Action? Changed;
    public event Action? Recorded;

    readonly string dir, historyFile, settingsFile, imagesDir;
    readonly Dictionary<string, Image> thumbs = new();
    uint ownSequence; // clipboard sequence number of our own writes, so picks aren't recorded

    public ClipStore()
    {
        dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardHistory");
        imagesDir = Path.Combine(dir, "images");
        historyFile = Path.Combine(dir, "history.json");
        settingsFile = Path.Combine(dir, "settings.json");
        Directory.CreateDirectory(imagesDir);
        try { Items = JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(historyFile)) ?? new(); } catch { }
        try { Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsFile)) ?? new(); } catch { }
    }

    public int Limit
    {
        get => Settings.Limit;
        set { Settings.Limit = value; SaveSettings(); Trim(); Save(); }
    }

    public bool AutoPaste
    {
        get => Settings.AutoPaste;
        set { Settings.AutoPaste = value; SaveSettings(); }
    }

    public int UnpinnedCount => Items.Count(i => !i.Pinned);

    public static bool LaunchAtStartup
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("ClipboardHistory") != null;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("ClipboardHistory", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("ClipboardHistory", false);
        }
    }

    /// Turn on start with Windows the first time the app runs, so history keeps recording after a restart.
    public bool FirstLaunch()
    {
        if (Settings.DidFirstLaunch) return false;
        Settings.DidFirstLaunch = true;
        SaveSettings();
        try { LaunchAtStartup = true; } catch { }
        return true;
    }

    public void OnClipboardUpdate()
    {
        if (Native.GetClipboardSequenceNumber() == ownSequence || Paused) return;
        IDataObject? data;
        try { data = Clipboard.GetDataObject(); } catch { return; }
        if (data == null || ShouldIgnore(data)) return;

        try
        {
            if (data.GetDataPresent(DataFormats.UnicodeText) &&
                data.GetData(DataFormats.UnicodeText) is string text && !string.IsNullOrWhiteSpace(text))
            {
                AddText(text);
                Recorded?.Invoke();
            }
            else if (data.GetDataPresent(DataFormats.Bitmap, true) && data.GetData(DataFormats.Bitmap, true) is Image img)
            {
                using var ms = new MemoryStream();
                img.Save(ms, ImageFormat.Png);
                AddImage(ms.ToArray());
                Recorded?.Invoke();
            }
        }
        catch { }
    }

    // Password managers mark secrets with these formats.
    static bool ShouldIgnore(IDataObject d)
    {
        if (d.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || d.GetDataPresent("Clipboard Viewer Ignore"))
            return true;
        if (d.GetDataPresent("CanIncludeInClipboardHistory") && d.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4)
        {
            var b = new byte[4];
            ms.Position = 0;
            ms.Read(b, 0, 4);
            if (BitConverter.ToInt32(b, 0) == 0) return true;
        }
        return false;
    }

    void AddText(string s)
    {
        var existing = Items.FirstOrDefault(i => !i.IsImage && i.Text == s);
        if (existing != null) { Items.Remove(existing); existing.Date = DateTime.Now; Items.Insert(0, existing); }
        else Items.Insert(0, new ClipItem { Kind = "text", Text = s, Date = DateTime.Now });
        Trim(); Save();
    }

    void AddImage(byte[] png)
    {
        var name = Convert.ToHexString(SHA256.HashData(png))[..32].ToLowerInvariant() + ".png";
        var existing = Items.FirstOrDefault(i => i.ImageFile == name);
        if (existing != null) { Items.Remove(existing); existing.Date = DateTime.Now; Items.Insert(0, existing); }
        else
        {
            File.WriteAllBytes(Path.Combine(imagesDir, name), png);
            Items.Insert(0, new ClipItem { Kind = "image", ImageFile = name, Date = DateTime.Now });
        }
        Trim(); Save();
    }

    /// Puts an item back on the clipboard. It keeps its original time and place in the list.
    public void Copy(ClipItem item)
    {
        var obj = new DataObject();
        if (item.IsImage) { var img = LoadImage(item); if (img == null) return; obj.SetData(DataFormats.Bitmap, true, img); }
        else obj.SetData(DataFormats.UnicodeText, item.Text ?? "");
        SetClipboard(obj);
    }

    /// All text joined by new lines, plus the newest image (Windows holds one image at a time).
    public void CopyAll(List<ClipItem> list)
    {
        if (list.Count == 0) return;
        var obj = new DataObject();
        var text = string.Join("\r\n", list.Where(i => !i.IsImage).Select(i => i.Text));
        if (text.Length > 0) obj.SetData(DataFormats.UnicodeText, text);
        var firstImage = list.FirstOrDefault(i => i.IsImage);
        if (firstImage != null && LoadImage(firstImage) is Image img) obj.SetData(DataFormats.Bitmap, true, img);
        SetClipboard(obj);
    }

    void SetClipboard(DataObject obj)
    {
        try { Clipboard.SetDataObject(obj, true, 10, 50); } catch { }
        ownSequence = Native.GetClipboardSequenceNumber(); // don't record our own write as a new copy
    }

    public void TogglePin(ClipItem item) { item.Pinned = !item.Pinned; Trim(); Save(); }

    public void Delete(ClipItem item)
    {
        Items.Remove(item);
        RemoveFileIfUnused(item);
        Save();
    }

    public void ClearUnpinned()
    {
        var removed = Items.Where(i => !i.Pinned).ToList();
        Items.RemoveAll(i => !i.Pinned);
        removed.ForEach(RemoveFileIfUnused);
        Save();
    }

    public Image? LoadImage(ClipItem item)
    {
        if (item.ImageFile == null) return null;
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(imagesDir, item.ImageFile));
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            return new Bitmap(img);
        }
        catch { return null; }
    }

    public Image? Thumbnail(ClipItem item)
    {
        if (item.ImageFile == null) return null;
        if (thumbs.TryGetValue(item.ImageFile, out var cached)) return cached;
        var img = LoadImage(item);
        if (img != null) thumbs[item.ImageFile] = img;
        return img;
    }

    /// Pinned items never count against the limit; the oldest unpinned ones drop off.
    void Trim()
    {
        var unpinned = 0;
        var dropped = new List<ClipItem>();
        foreach (var item in Items)
            if (!item.Pinned && ++unpinned > Limit) dropped.Add(item);
        if (dropped.Count == 0) return;
        Items = Items.Except(dropped).ToList();
        dropped.ForEach(RemoveFileIfUnused);
    }

    void RemoveFileIfUnused(ClipItem item)
    {
        var name = item.ImageFile;
        if (name == null || Items.Any(i => i.ImageFile == name)) return;
        try { File.Delete(Path.Combine(imagesDir, name)); } catch { }
        if (thumbs.Remove(name, out var img)) img.Dispose();
    }

    void Save()
    {
        try { File.WriteAllText(historyFile, JsonSerializer.Serialize(Items)); } catch { }
        Changed?.Invoke();
    }

    void SaveSettings()
    {
        try { File.WriteAllText(settingsFile, JsonSerializer.Serialize(Settings)); } catch { }
    }
}

// MARK: - Theme

static class Theme
{
    public static readonly Color Background = Color.FromArgb(249, 249, 251);
    public static readonly Color Text = Color.FromArgb(28, 28, 32);
    public static readonly Color Secondary = Color.FromArgb(110, 112, 122);
    public static readonly Color Tertiary = Color.FromArgb(160, 162, 170);
    public static readonly Color Divider = Color.FromArgb(226, 227, 232);
    public static readonly Color Accent = Color.FromArgb(91, 76, 245);
    public static readonly Color Selected = Color.FromArgb(225, 222, 253);
    public static readonly Color Pin = Color.FromArgb(240, 140, 20);
    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8f);
    public static readonly Font SmallBold = new("Segoe UI Semibold", 8f);
    public static readonly Font Icons = new("Segoe MDL2 Assets", 10f);
    public static readonly Font IconsSmall = new("Segoe MDL2 Assets", 7.5f);
    public const string CopyGlyph = "", PinGlyph = "", UnpinGlyph = "", DeleteGlyph = "",
        SearchGlyph = "", SettingsGlyph = "", ClipGlyph = "";

    /// Always rounds down, so an older item never shows a smaller time than a newer one.
    public static string Ago(DateTime date, DateTime now)
    {
        var secs = Math.Max(0, (int)(now - date).TotalSeconds);
        if (secs < 10) return "just now";
        if (secs < 60) return $"{secs} sec ago";
        if (secs < 3600) return $"{secs / 60} min ago";
        if (secs < 86400) return $"{secs / 3600} hr ago";
        return date.ToString("d MMM, h:mm tt");
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// MARK: - History list (custom drawn)

sealed class ClipList : Control
{
    sealed class Entry
    {
        public ClipItem? Item;
        public string? Header;
        public int Index; // position among items
        public Rectangle Rect; // in content coordinates
    }

    readonly ClipStore store;
    readonly List<Entry> entries = new();
    List<ClipItem> visible = new();
    int scroll, contentHeight, hover = -1;
    public int Selected;
    public DateTime Now = DateTime.Now;

    public event Action<ClipItem>? Pick;

    public ClipList(ClipStore store)
    {
        this.store = store;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = false;
        BackColor = Theme.Background;
    }

    float S => DeviceDpi / 96f;
    int Px(float v) => (int)Math.Round(v * S);

    public List<ClipItem> Visible => visible;
    public ClipItem? SelectedItem => Selected >= 0 && Selected < visible.Count ? visible[Selected] : null;

    public void SetItems(List<ClipItem> items, bool resetScroll)
    {
        visible = items;
        if (resetScroll) { scroll = 0; Selected = 0; }
        Selected = Math.Clamp(Selected, 0, Math.Max(0, visible.Count - 1));
        Layout2();
        Invalidate();
    }

    void Layout2()
    {
        entries.Clear();
        var pinned = visible.Count(i => i.Pinned);
        int y = Px(6), w = ClientSize.Width - Px(12);
        for (var i = 0; i < visible.Count; i++)
        {
            if (pinned > 0 && (i == 0 || i == pinned))
            {
                var hh = Px(i == 0 ? 22 : 28);
                entries.Add(new Entry { Header = i == 0 ? "Pinned" : "Recent", Rect = new Rectangle(Px(6), y, w, hh) });
                y += hh;
            }
            var h = Px(visible[i].IsImage ? 64 : 52);
            entries.Add(new Entry { Item = visible[i], Index = i, Rect = new Rectangle(Px(6), y, w, h) });
            y += h + Px(2);
        }
        contentHeight = y + Px(4);
        ClampScroll();
    }

    void ClampScroll() => scroll = Math.Clamp(scroll, 0, Math.Max(0, contentHeight - ClientSize.Height));

    protected override void OnResize(EventArgs e) { base.OnResize(e); Layout2(); }

    public void MoveSelection(int delta)
    {
        if (visible.Count == 0) return;
        Selected = Math.Clamp(Selected + delta, 0, visible.Count - 1);
        EnsureVisible(Selected);
        Invalidate();
    }

    void EnsureVisible(int index)
    {
        var e = entries.FirstOrDefault(x => x.Item != null && x.Index == index);
        if (e == null) return;
        if (e.Rect.Top - Px(6) < scroll) scroll = e.Rect.Top - Px(6);
        else if (e.Rect.Bottom + Px(6) > scroll + ClientSize.Height) scroll = e.Rect.Bottom + Px(6) - ClientSize.Height;
        ClampScroll();
    }

    Rectangle ToView(Rectangle r) => new(r.X, r.Y - scroll, r.Width, r.Height);

    (Rectangle copy, Rectangle pin, Rectangle del) IconRects(Rectangle r)
    {
        int size = Px(28), y = r.Y + (r.Height - size) / 2, right = r.Right - Px(6);
        var del = new Rectangle(right - size, y, size, size);
        var pin = new Rectangle(del.X - size, y, size, size);
        var copy = new Rectangle(pin.X - size, y, size, size);
        return (copy, pin, del);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        if (visible.Count == 0)
        {
            var empty = store.Items.Count == 0 ? "Copy something and it will show up here." : "No matches";
            TextRenderer.DrawText(g, empty, Theme.Body, ClientRectangle, Theme.Secondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        for (var n = 0; n < entries.Count; n++)
        {
            var entry = entries[n];
            var r = ToView(entry.Rect);
            if (r.Bottom < 0 || r.Top > ClientSize.Height) continue;

            if (entry.Header != null)
            {
                TextRenderer.DrawText(g, entry.Header, Theme.SmallBold, new Rectangle(r.X + Px(8), r.Y, r.Width, r.Height),
                    Theme.Secondary, TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.SingleLine);
                continue;
            }

            var item = entry.Item!;
            if (entry.Index == Selected)
            {
                using var path = Theme.Rounded(r, Px(6));
                using var brush = new SolidBrush(Theme.Selected);
                g.FillPath(brush, path);
            }

            var rightWidth = Px(96);
            var content = new Rectangle(r.X + Px(10), r.Y + Px(6), r.Width - rightWidth - Px(14), r.Height - Px(12));

            if (item.IsImage)
            {
                var img = store.Thumbnail(item);
                if (img != null)
                {
                    // Size the thumbnail to the image so it sits flush left, in line with text rows.
                    var fit = Math.Min(Px(120) / (float)Math.Max(img.Width, 1), Px(48) / (float)Math.Max(img.Height, 1));
                    var tw = Math.Max(Px(12), (int)(img.Width * fit));
                    var th = Math.Max(Px(12), (int)(img.Height * fit));
                    var tr = new Rectangle(content.X, r.Y + (r.Height - th) / 2, tw, th);
                    g.DrawImage(img, tr);
                    using var pen = new Pen(Theme.Divider);
                    g.DrawRectangle(pen, tr);
                    TextRenderer.DrawText(g, $"{img.Width}×{img.Height}", Theme.Small,
                        new Rectangle(tr.Right + Px(8), r.Y, Px(100), r.Height), Theme.Secondary,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                }
            }
            else
            {
                var text = string.Join(" ↵ ", (item.Text ?? "").Split('\n')
                    .Select(l => l.Trim()).Where(l => l.Length > 0));
                const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
                // Centre one or two lines vertically (GDI only centres single lines itself).
                var h = Math.Min(content.Height, TextRenderer.MeasureText(g, text, Theme.Body, new Size(content.Width, int.MaxValue), flags).Height);
                var tr = new Rectangle(content.X, content.Y + (content.Height - h) / 2, content.Width, h);
                TextRenderer.DrawText(g, text, Theme.Body, tr, Theme.Text, flags);
            }

            if (n == hover)
            {
                var (copy, pin, del) = IconRects(r);
                DrawGlyph(g, Theme.CopyGlyph, copy);
                DrawGlyph(g, item.Pinned ? Theme.UnpinGlyph : Theme.PinGlyph, pin);
                DrawGlyph(g, Theme.DeleteGlyph, del);
            }
            else
            {
                var right = new Rectangle(r.Right - rightWidth - Px(4), r.Y + Px(8), rightWidth, r.Height - Px(16));
                if (entry.Index < 9)
                    TextRenderer.DrawText(g, $"Ctrl+{entry.Index + 1}", Theme.Small, right, Theme.Tertiary,
                        TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.SingleLine);
                var time = Theme.Ago(item.Date, Now);
                TextRenderer.DrawText(g, time, Theme.Small, right, Theme.Secondary,
                    TextFormatFlags.Right | TextFormatFlags.Bottom | TextFormatFlags.SingleLine);
                if (item.Pinned)
                {
                    var tw = TextRenderer.MeasureText(time, Theme.Small).Width;
                    var pr = new Rectangle(right.Right - tw - Px(16), right.Bottom - Px(16), Px(14), Px(16));
                    TextRenderer.DrawText(g, Theme.PinGlyph, Theme.IconsSmall, pr, Theme.Pin,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }

        // Slim scroll indicator.
        if (contentHeight > ClientSize.Height)
        {
            var h = Math.Max(Px(24), ClientSize.Height * ClientSize.Height / contentHeight);
            var y = (ClientSize.Height - h) * scroll / Math.Max(1, contentHeight - ClientSize.Height);
            using var path = Theme.Rounded(new RectangleF(ClientSize.Width - Px(5), y + Px(2), Px(3), h - Px(4)), Px(1.5f));
            using var brush = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
            g.FillPath(brush, path);
        }
    }

    void DrawGlyph(Graphics g, string glyph, Rectangle r) =>
        TextRenderer.DrawText(g, glyph, Theme.Icons, r, Theme.Secondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

    int EntryAt(Point p)
    {
        var y = p.Y + scroll;
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].Item != null && entries[i].Rect.Contains(p.X, y)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var n = EntryAt(e.Location);
        if (n == hover) return;
        hover = n;
        if (n >= 0) Selected = entries[n].Index;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        scroll -= e.Delta * Px(48) / 120;
        ClampScroll();
        hover = EntryAt(PointToClient(Cursor.Position));
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        var n = EntryAt(e.Location);
        if (n < 0) return;
        var item = entries[n].Item!;
        var (copy, pin, del) = IconRects(ToView(entries[n].Rect));
        if (pin.Contains(e.Location)) store.TogglePin(item);
        else if (del.Contains(e.Location)) store.Delete(item);
        else Pick?.Invoke(item); // the copy icon and the row itself both copy
    }
}

// MARK: - Popup window

sealed class PopupForm : Form
{
    readonly ClipStore store;
    readonly TextBox search = new();
    readonly Button copyAll = new();
    readonly Label count = new();
    readonly Label hint = new();
    readonly Button gear = new();
    readonly ContextMenuStrip gearMenu = new();
    readonly ClipList list;
    readonly System.Windows.Forms.Timer clock = new() { Interval = 10_000 };
    IntPtr previousWindow;
    public DateTime LastHidden { get; private set; }

    public PopupForm(ClipStore store)
    {
        this.store = store;
        list = new ClipList(store) { Dock = DockStyle.Fill };
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Background;
        KeyPreview = true;
        Text = "Clipboard History";

        var s = DeviceDpi / 96f;
        int Px(float v) => (int)Math.Round(v * s);
        ClientSize = new Size(Px(380), Px(500));

        // Header: search + Copy All.
        var header = new Panel { Dock = DockStyle.Top, Height = Px(46), Padding = new Padding(Px(12), Px(10), Px(10), Px(8)) };
        var glyph = new Label { Text = Theme.SearchGlyph, Font = Theme.Icons, ForeColor = Theme.Secondary, Dock = DockStyle.Left, Width = Px(24), TextAlign = ContentAlignment.MiddleLeft };
        search.BorderStyle = BorderStyle.None;
        search.Font = new Font("Segoe UI", 10.5f);
        search.PlaceholderText = "Search clipboard history";
        search.BackColor = Theme.Background;
        search.Dock = DockStyle.Fill;
        var searchHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, Px(4), Px(6), 0) };
        searchHost.Controls.Add(search);
        copyAll.Text = "Copy All";
        copyAll.Font = Theme.Small;
        copyAll.FlatStyle = FlatStyle.Flat;
        copyAll.FlatAppearance.BorderColor = Theme.Divider;
        copyAll.BackColor = Color.White;
        copyAll.Dock = DockStyle.Right;
        copyAll.Width = Px(78);
        copyAll.Cursor = Cursors.Hand;
        copyAll.Click += (_, _) => CopyAllVisible();
        header.Controls.Add(searchHost);
        header.Controls.Add(copyAll);
        header.Controls.Add(glyph);

        // Footer: count, hints, settings.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = Px(36), Padding = new Padding(Px(12), 0, Px(6), 0) };
        count.Font = Theme.Small; count.ForeColor = Theme.Secondary; count.Dock = DockStyle.Left; count.AutoSize = false;
        count.Width = Px(120); count.TextAlign = ContentAlignment.MiddleLeft;
        hint.Text = "Enter copy · Ctrl+1–9 quick pick";
        hint.Font = Theme.Small; hint.ForeColor = Theme.Tertiary; hint.Dock = DockStyle.Fill; hint.TextAlign = ContentAlignment.MiddleRight;
        gear.Text = Theme.SettingsGlyph; gear.Font = Theme.Icons; gear.ForeColor = Theme.Secondary;
        gear.FlatStyle = FlatStyle.Flat; gear.FlatAppearance.BorderSize = 0; gear.Dock = DockStyle.Right; gear.Width = Px(32);
        gear.Cursor = Cursors.Hand;
        gear.Click += (_, _) => { BuildGearMenu(); gearMenu.Show(gear, new Point(0, 0), ToolStripDropDownDirection.AboveLeft); };
        footer.Controls.Add(hint);
        footer.Controls.Add(gear);
        footer.Controls.Add(count);

        Controls.Add(list);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Divider });
        Controls.Add(header);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Divider });
        Controls.Add(footer);

        search.TextChanged += (_, _) => Reload(resetScroll: true);
        list.Pick += PickItem;
        store.Changed += () => { if (Visible) Reload(resetScroll: false); };
        clock.Tick += (_, _) => { list.Now = DateTime.Now; list.Invalidate(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW: no Alt+Tab entry
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var round = 2; // Windows 11 rounded corners
        Native.DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    /// Pinned items first, then everything else — each group strictly newest first.
    List<ClipItem> Filtered()
    {
        var q = search.Text.Trim();
        var matched = store.Items
            .Where(i => q.Length == 0 || (i.Text ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.Date).ToList();
        return matched.Where(i => i.Pinned).Concat(matched.Where(i => !i.Pinned)).ToList();
    }

    void Reload(bool resetScroll)
    {
        list.SetItems(Filtered(), resetScroll);
        count.Text = $"{store.UnpinnedCount} of {store.Limit}" + (store.Paused ? "  · Paused" : "");
        var filtered = search.Text.Trim().Length > 0;
        copyAll.Text = filtered ? $"Copy {list.Visible.Count}" : "Copy All";
        copyAll.Enabled = list.Visible.Count > 0;
    }

    public void Toggle()
    {
        if (Visible) { HidePopup(); return; }
        if ((DateTime.Now - LastHidden).TotalMilliseconds < 250) return; // the tray click that just closed it
        previousWindow = Native.GetForegroundWindow();
        search.Text = "";
        list.Now = DateTime.Now;
        Reload(resetScroll: true);
        PlaceNearTray();
        Show();
        Activate();
        Native.SetForegroundWindow(Handle);
        search.Focus();
        clock.Start();
    }

    void PlaceNearTray()
    {
        var area = System.Windows.Forms.Screen.FromPoint(Cursor.Position).WorkingArea;
        var margin = (int)(12 * DeviceDpi / 96f);
        var x = Math.Clamp(Cursor.Position.X - Width / 2, area.Left + margin, area.Right - Width - margin);
        // Sit against whichever edge the taskbar is on (bottom by default).
        var y = Cursor.Position.Y > area.Top + area.Height / 2 ? area.Bottom - Height - margin : area.Top + margin;
        Location = new Point(x, y);
    }

    void HidePopup(bool returnFocus = false)
    {
        clock.Stop();
        Hide();
        LastHidden = DateTime.Now;
        if (returnFocus && previousWindow != IntPtr.Zero) Native.SetForegroundWindow(previousWindow);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible && !gearMenu.Visible) HidePopup();
    }

    void PickItem(ClipItem item)
    {
        store.Copy(item);
        HidePopup(returnFocus: true);
        if (store.AutoPaste) PasteSoon();
    }

    void CopyAllVisible()
    {
        store.CopyAll(list.Visible);
        HidePopup(returnFocus: true);
    }

    static void PasteSoon()
    {
        var t = new System.Windows.Forms.Timer { Interval = 120 };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); SendKeys.SendWait("^v"); };
        t.Start();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Down: list.MoveSelection(1); return true;
            case Keys.Up: list.MoveSelection(-1); return true;
            case Keys.Enter:
                if (list.SelectedItem is ClipItem pick) PickItem(pick);
                return true;
            case Keys.Escape: HidePopup(returnFocus: true); return true;
            case Keys.Control | Keys.Shift | Keys.C: CopyAllVisible(); return true;
            case Keys.Control | Keys.P:
                if (list.SelectedItem is ClipItem pin) store.TogglePin(pin);
                return true;
            case Keys.Control | Keys.Delete:
                if (list.SelectedItem is ClipItem del) store.Delete(del);
                return true;
        }
        var key = keyData & Keys.KeyCode;
        if ((keyData & Keys.Modifiers) == Keys.Control &&
            ((key >= Keys.D1 && key <= Keys.D9) || (key >= Keys.NumPad1 && key <= Keys.NumPad9)))
        {
            var n = key >= Keys.NumPad1 ? key - Keys.NumPad1 : key - Keys.D1;
            if (n < list.Visible.Count) PickItem(list.Visible[n]);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void BuildGearMenu()
    {
        gearMenu.Items.Clear();
        var keep = new ToolStripMenuItem("Keep last");
        foreach (var limit in ClipStore.Limits)
        {
            var l = limit;
            keep.DropDownItems.Add(new ToolStripMenuItem($"{l} items", null, (_, _) => { store.Limit = l; Reload(false); })
                { Checked = store.Limit == l });
        }
        gearMenu.Items.Add(keep);
        gearMenu.Items.Add(new ToolStripMenuItem("Paste automatically after picking", null,
            (_, _) => store.AutoPaste = !store.AutoPaste) { Checked = store.AutoPaste });
        gearMenu.Items.Add(new ToolStripMenuItem("Start with Windows", null,
            (_, _) => { try { ClipStore.LaunchAtStartup = !ClipStore.LaunchAtStartup; } catch { } }) { Checked = ClipStore.LaunchAtStartup });
        gearMenu.Items.Add(new ToolStripMenuItem("Pause recording", null,
            (_, _) => { store.Paused = !store.Paused; Reload(false); }) { Checked = store.Paused });
        gearMenu.Items.Add(new ToolStripSeparator());
        gearMenu.Items.Add(new ToolStripMenuItem("Clear history (keeps pinned)…", null, (_, _) => ConfirmClear()));
        gearMenu.Items.Add(new ToolStripSeparator());
        gearMenu.Items.Add(new ToolStripMenuItem("Quit Clipboard History", null, (_, _) => Application.Exit()));
    }

    public void ConfirmClear()
    {
        HidePopup();
        var result = MessageBox.Show(
            $"This removes {store.UnpinnedCount} items. Pinned items are kept. This can't be undone.",
            "Clear clipboard history?", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (result == DialogResult.OK) store.ClearUnpinned();
    }
}

// MARK: - Tray app

sealed class TrayApp : ApplicationContext
{
    readonly ClipStore store = new();
    readonly Watcher watcher = new();
    readonly NotifyIcon tray = new();
    readonly PopupForm popup;
    readonly Icon icon, dimIcon;
    readonly System.Windows.Forms.Timer pulse = new() { Interval = 350 };

    public TrayApp()
    {
        popup = new PopupForm(store);
        popup.CreateControl();
        _ = popup.Handle;

        using (var s = typeof(TrayApp).Assembly.GetManifestResourceStream("ClipboardHistory.tray.ico")!)
            icon = new Icon(s, SystemInformation.SmallIconSize);
        dimIcon = Dim(icon);

        tray.Icon = icon;
        tray.Text = "Clipboard History (Ctrl+Alt+H)";
        tray.Visible = true;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) popup.Toggle(); };
        tray.ContextMenuStrip = BuildTrayMenu();

        watcher.ClipboardChanged += store.OnClipboardUpdate;
        watcher.HotKeyPressed += popup.Toggle;
        store.Recorded += Pulse;
        Application.ApplicationExit += (_, _) => tray.Visible = false; // don't leave a ghost tray icon
        pulse.Tick += (_, _) => { pulse.Stop(); tray.Icon = icon; };

        if (store.FirstLaunch())
            tray.ShowBalloonTip(6000, "Clipboard History is running",
                "It saves everything you copy. Press Ctrl+Alt+H, or click the tray icon, to open your history.", ToolTipIcon.Info);
    }

    ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        var pause = new ToolStripMenuItem("Pause Recording");
        menu.Opening += (_, _) => pause.Text = store.Paused ? "Resume Recording" : "Pause Recording";
        menu.Items.Add(new ToolStripMenuItem("Open History  (Ctrl+Alt+H)", null, (_, _) => popup.Toggle()));
        pause.Click += (_, _) => store.Paused = !store.Paused;
        menu.Items.Add(pause);
        menu.Items.Add(new ToolStripMenuItem("Clear History…", null, (_, _) => popup.ConfirmClear()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit Clipboard History", null, (_, _) => ExitThread()));
        return menu;
    }

    /// Brief dim-and-return of the tray icon to show a copy was saved.
    void Pulse()
    {
        tray.Icon = dimIcon;
        pulse.Stop();
        pulse.Start();
    }

    static Icon Dim(Icon src)
    {
        using var bmp = src.ToBitmap();
        var faded = new Bitmap(bmp.Width, bmp.Height);
        using (var g = Graphics.FromImage(faded))
        {
            var attrs = new ImageAttributes();
            attrs.SetColorMatrix(new ColorMatrix { Matrix33 = 0.3f });
            g.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attrs);
        }
        return Icon.FromHandle(faded.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        tray.Visible = false;
        tray.Dispose();
        watcher.Dispose();
        base.ExitThreadCore();
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "ClipboardHistory.SingleInstance", out var firstInstance);
        if (!firstInstance) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
