using System.Text;
using Tmds.DBus.Protocol;

namespace ReplyFive.Desktop.Platform.Linux;

/// <summary>AT-SPI2（Linux のアクセシビリティバス）の薄いクライアント。セッションバスで org.a11y.Bus からアクセシビリティバスの住所を取り、そこへつなぐ。
/// 読むのは会話のペインだけ（付録BQ）。本文はログに出さない。</summary>
public sealed class AtSpi : IDisposable
{
    public readonly record struct Ref(string Bus, string Path)
    {
        public bool IsNull => Path is "/org/a11y/atspi/null" or "";
    }

    public const string Registry = "org.a11y.atspi.Registry";
    public const string RootPath = "/org/a11y/atspi/accessible/root";
    const string IAccessible = "org.a11y.atspi.Accessible";
    const string IComponent = "org.a11y.atspi.Component";
    const string IText = "org.a11y.atspi.Text";
    const string IEditableText = "org.a11y.atspi.EditableText";
    const string ICollection = "org.a11y.atspi.Collection";
    const string IProperties = "org.freedesktop.DBus.Properties";

    // AtspiStateType（at-spi2-core の atspi-constants.h）
    public const int StateActive = 1, StateEditable = 7, StateFocusable = 11, StateFocused = 12, StateShowing = 25, StateVisible = 30, StateDefunct = 6;
    // AtspiRole（at-spi2-core 2.52 の Atspi.Role から取得した実値。順序は enum 定義どおりで、推測で足さない）
    public const uint RoleDialog = 16, RoleFrame = 23, RoleLabel = 29, RoleList = 31, RoleListItem = 32, RoleMenu = 33, RoleMenuBar = 34, RoleMenuItem = 35, RolePanel = 39, RolePasswordText = 40,
        RolePushButton = 43, RoleButton = 43, RoleScrollBar = 48, RoleScrollPane = 49, RoleTableCell = 56, RoleTerminal = 60, RoleText = 61, RoleToggleButton = 62, RoleToolBar = 63,
        RoleViewport = 68, RoleWindow = 69, RoleHeader = 71, RoleParagraph = 73, RoleApplication = 75, RoleEntry = 79, RoleTextBox = 79, RoleDocumentFrame = 82, RoleHeading = 83, RoleSection = 85,
        RoleLink = 88, RoleTableRow = 90, RoleDocumentText = 94, RoleDocumentWeb = 95, RoleComment = 97, RoleListBox = 98, RoleArticle = 109, RoleStatic = 116;

    DBusConnection? session;
    DBusConnection? bus;

    /// <summary>接続する。アクセシビリティバスが無ければ null。</summary>
    public static async Task<AtSpi?> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            var a = new AtSpi();
            a.session = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("no session bus"));
            await a.session.ConnectAsync().ConfigureAwait(false);
            var address = await a.session.CallMethodAsync(Build(a.session, "org.a11y.Bus", "/org/a11y/bus", "org.a11y.Bus", "GetAddress", null, null), (m, _) => m.GetBodyReader().ReadString()).ConfigureAwait(false);
            if (string.IsNullOrEmpty(address)) return null;
            a.bus = new DBusConnection(address);
            await a.bus.ConnectAsync().ConfigureAwait(false);
            return a;
        }
        catch (Exception e) { LogThrottled("atspi connect failed " + e.GetType().Name + ": " + e.Message); return null; }
    }

    public void Dispose() { bus?.Dispose(); session?.Dispose(); }

    // MARK: - 有効化（org.a11y.Status.IsEnabled）

    /// <summary>デスクトップのアクセシビリティ機能が有効か。無効だと Chromium 系は木を出さない。</summary>
    public static async Task<bool?> IsEnabledAsync()
    {
        try
        {
            using var s = new DBusConnection(DBusAddress.Session ?? "");
            await s.ConnectAsync().ConfigureAwait(false);
            var msg = Build(s, "org.a11y.Bus", "/org/a11y/bus", IProperties, "Get", "ss", (ref MessageWriter w) => { w.WriteString("org.a11y.Status"); w.WriteString("IsEnabled"); });
            return await s.CallMethodAsync(msg, (m, _) => m.GetBodyReader().ReadVariantValue().GetBool()).ConfigureAwait(false);
        }
        catch (Exception e) { LogThrottled("a11y status failed " + e.GetType().Name + ": " + e.Message); return null; }
    }

    /// <summary>有効にする（org.a11y.Status.IsEnabled = true）。永続化は gsettings（GNOME）にも書く。</summary>
    public static async Task<bool> EnableAsync()
    {
        try
        {
            using var s = new DBusConnection(DBusAddress.Session ?? "");
            await s.ConnectAsync().ConfigureAwait(false);
            var msg = Build(s, "org.a11y.Bus", "/org/a11y/bus", IProperties, "Set", "ssv", (ref MessageWriter w) => { w.WriteString("org.a11y.Status"); w.WriteString("IsEnabled"); w.WriteVariantBool(true); });
            await s.CallMethodAsync(msg).ConfigureAwait(false);
            if (LinuxPlatform.Which("gsettings") is { } gs) LinuxPlatform.Run(gs, "set", "org.gnome.desktop.interface", "toolkit-accessibility", "true");
            return true;
        }
        catch (Exception e) { Services.Diag.Log("a11y enable failed " + e.GetType().Name + ": " + e.Message); return false; }
    }

    // 0.5 秒ごとの読み取りで同じ失敗が続いてもログを埋めないよう、同じ文言は 60 秒に 1 回だけ書く
    static readonly Dictionary<string, long> lastLogged = new();
    static void LogThrottled(string line)
    {
        lock (lastLogged)
        {
            var now = Environment.TickCount64;
            if (lastLogged.TryGetValue(line, out var t) && now - t < 60_000) return;
            lastLogged[line] = now;
        }
        Services.Diag.Log(line);
    }

    // MARK: - 呼び出しの共通部分

    /// <summary>MessageWriter は ref struct なので await をまたげない。ここで同期的に組み立てる。</summary>
    public delegate void WriteArgs(ref MessageWriter w);

    static MessageBuffer Build(DBusConnection conn, string destination, string path, string iface, string member, string? signature, WriteArgs? args)
    {
        var w = conn.GetMessageWriter();
        try
        {
            w.WriteMethodCallHeader(destination, path, iface, member, signature);
            args?.Invoke(ref w);
            return w.CreateMessage();
        }
        finally { w.Dispose(); }
    }

    async Task<T> Call<T>(Ref r, string iface, string member, string? signature, WriteArgs? args, MessageValueReader<T> reader, int timeoutMs = 400)
    {
        var b = bus ?? throw new InvalidOperationException("not connected");
        var task = b.CallMethodAsync(Build(b, r.Bus, r.Path, iface, member, signature, args), reader);
        var done = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (done != task) throw new TimeoutException(member);
        return await task.ConfigureAwait(false);
    }

    static List<Ref> ReadRefs(Message m, object? _)
    {
        var reader = m.GetBodyReader();
        var list = new List<Ref>();
        var end = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(end))
        {
            reader.AlignStruct();
            var busName = reader.ReadString();
            var path = reader.ReadObjectPathAsString();
            list.Add(new Ref(busName, path));
        }
        return list;
    }

    static Ref ReadRef(Message m, object? _)
    {
        var reader = m.GetBodyReader();
        reader.AlignStruct();
        return new Ref(reader.ReadString(), reader.ReadObjectPathAsString());
    }

    static ulong ReadStates(Message m, object? _)
    {
        var reader = m.GetBodyReader();
        var end = reader.ReadArrayStart(DBusType.UInt32);
        ulong states = 0; var i = 0;
        while (reader.HasNext(end)) { var v = reader.ReadUInt32(); if (i < 2) states |= (ulong)v << (32 * i); i++; }
        return states;
    }

    public static bool Has(ulong states, int state) => (states & (1UL << state)) != 0;

    // MARK: - Accessible

    public Task<List<Ref>> Children(Ref r) => Call(r, IAccessible, "GetChildren", null, null, ReadRefs);
    public Task<Ref> Parent(Ref r) => Property(r, IAccessible, "Parent", (m, _) => { var v = m.GetBodyReader().ReadVariantValue(); return new Ref(v.GetItem(0).GetString(), v.GetItem(1).GetObjectPathAsString()); });
    public Task<string> Name(Ref r) => Property(r, IAccessible, "Name", (m, _) => m.GetBodyReader().ReadVariantValue().GetString());
    public Task<uint> Role(Ref r) => Call(r, IAccessible, "GetRole", null, null, (m, _) => m.GetBodyReader().ReadUInt32());
    public Task<ulong> States(Ref r) => Call(r, IAccessible, "GetState", null, null, ReadStates);
    public Task<List<string>> Interfaces(Ref r) => Call(r, IAccessible, "GetInterfaces", null, null, (m, _) =>
    {
        var reader = m.GetBodyReader(); var list = new List<string>();
        var end = reader.ReadArrayStart(DBusType.String);
        while (reader.HasNext(end)) list.Add(reader.ReadString());
        return list;
    });
    public Task<Ref> Application(Ref r) => Call(r, IAccessible, "GetApplication", null, null, ReadRef);

    Task<T> Property<T>(Ref r, string iface, string name, MessageValueReader<T> reader)
        => Call(r, IProperties, "Get", "ss", (ref MessageWriter w) => { w.WriteString(iface); w.WriteString(name); }, reader);

    // MARK: - Component

    public readonly record struct Rect(int X, int Y, int W, int H)
    {
        public int Right => X + W; public int Bottom => Y + H;
        public bool Contains(Rect o) => o.X >= X && o.Y >= Y && o.Right <= Right && o.Bottom <= Bottom;
        public bool Intersects(Rect o) => o.X < Right && X < o.Right && o.Y < Bottom && Y < o.Bottom;
    }

    /// <summary>画面座標（coord type 0 = screen）の枠。</summary>
    public Task<Rect> Extents(Ref r) => Call(r, IComponent, "GetExtents", "u", (ref MessageWriter w) => w.WriteUInt32(0), (m, _) =>
    {
        var reader = m.GetBodyReader(); reader.AlignStruct();
        return new Rect(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
    });

    public Task<bool> GrabFocus(Ref r) => Call(r, IComponent, "GrabFocus", null, null, (m, _) => m.GetBodyReader().ReadBool());

    // MARK: - Text / EditableText

    public Task<int> CharacterCount(Ref r) => Property(r, IText, "CharacterCount", (m, _) => m.GetBodyReader().ReadVariantValue().GetInt32());
    public Task<int> CaretOffset(Ref r) => Property(r, IText, "CaretOffset", (m, _) => m.GetBodyReader().ReadVariantValue().GetInt32());
    public Task<string> GetText(Ref r, int start = 0, int end = -1) => Call(r, IText, "GetText", "ii", (ref MessageWriter w) => { w.WriteInt32(start); w.WriteInt32(end); }, (m, _) => m.GetBodyReader().ReadString());
    public Task<(int start, int end)> Selection(Ref r) => Call(r, IText, "GetSelection", "i", (ref MessageWriter w) => w.WriteInt32(0), (m, _) => { var rd = m.GetBodyReader(); return (rd.ReadInt32(), rd.ReadInt32()); });
    /// <summary>length は ATK の契約どおり UTF-8 のバイト数（文字数を渡すと GTK は先頭の数文字しか入れない）。Qt は文字数として text.left(length) に使うので、バイト数なら全文が入る。</summary>
    public Task<bool> InsertText(Ref r, int position, string text) => Call(r, IEditableText, "InsertText", "isi", (ref MessageWriter w) => { w.WriteInt32(position); w.WriteString(text); w.WriteInt32(Encoding.UTF8.GetByteCount(text)); }, (m, _) => m.GetBodyReader().ReadBool(), 1500);
    public Task<bool> SetTextContents(Ref r, string text) => Call(r, IEditableText, "SetTextContents", "s", (ref MessageWriter w) => w.WriteString(text), (m, _) => m.GetBodyReader().ReadBool(), 1500);

    // MARK: - Collection：状態で子孫を検索（1 往復）

    /// <summary>指定の状態をすべて持つ子孫（Collection が無いアプリでは例外）。</summary>
    public Task<List<Ref>> MatchStates(Ref root, int[] states, int count = 5)
        => Call(root, ICollection, "GetMatches", "(aiia{ss}iaiiasib)uib", (ref MessageWriter w) =>
        {
            ulong bits = 0; foreach (var s in states) bits |= 1UL << s;
            w.WriteStructureStart();
            w.WriteArray(new[] { (int)(bits & 0xFFFFFFFF), (int)(bits >> 32) }); // states
            w.WriteInt32(1); // MATCH_ALL
            var dict = w.WriteDictionaryStart(); w.WriteDictionaryEnd(dict); // attributes（a{ss} は空）
            w.WriteInt32(0); // MATCH_INVALID
            w.WriteArray(new int[] { 0, 0, 0, 0 }); // roles
            w.WriteInt32(0);
            w.WriteArray(Array.Empty<string>()); // interfaces
            w.WriteInt32(0);
            w.WriteBool(false); // invert
            w.WriteUInt32(0); // SORT_ORDER_INVALID
            w.WriteInt32(count);
            w.WriteBool(true); // traverse
        }, ReadRefs, 800);

    // MARK: - 上位：前面のウインドウと焦点

    /// <summary>登録済みアプリの一覧（デスクトップの子）。</summary>
    public Task<List<Ref>> Applications() => Children(new Ref(Registry, RootPath));

    /// <summary>ACTIVE 状態のトップレベルウインドウとそのアプリ。</summary>
    public async Task<(Ref app, Ref window, string appName, string title)?> ActiveWindow(int budgetMs = 300)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        foreach (var app in await Applications().ConfigureAwait(false))
        {
            if (Environment.TickCount64 > deadline) break;
            if (app.IsNull) continue;
            List<Ref> windows;
            try { windows = await Children(app).ConfigureAwait(false); } catch (Exception) { continue; }
            foreach (var win in windows)
            {
                if (win.IsNull) continue;
                try
                {
                    var st = await States(win).ConfigureAwait(false);
                    if (!Has(st, StateActive)) continue;
                    var appName = await Name(app).ConfigureAwait(false);
                    var title = await Name(win).ConfigureAwait(false);
                    return (app, win, appName, title);
                }
                catch (Exception) { }
            }
        }
        // ACTIVE を出すウインドウが無い（ウインドウマネージャが無い／状態を伝えないアプリ）ときは、焦点要素を持つ表示中ウインドウを前面とみなす。
        // Collection を持たないアプリ（GTK3 等）では窓ごとに木を辿る（要素数と時間で打ち切る）
        var apps = await Applications().ConfigureAwait(false);
        var withFocus = 0;
        var fallbackDeadline = deadline + budgetMs;
        foreach (var app in apps)
        {
            if (Environment.TickCount64 > fallbackDeadline) { Services.Diag.LogIfChanged("active window: budget exhausted"); break; }
            if (app.IsNull) continue;
            try
            {
                foreach (var win in await Children(app).ConfigureAwait(false))
                {
                    if (win.IsNull || Environment.TickCount64 > fallbackDeadline) continue;
                    var st = await States(win).ConfigureAwait(false);
                    if (!Has(st, StateShowing)) continue;
                    if (await FocusedIn(app, win, (int)Math.Max(50, fallbackDeadline - Environment.TickCount64)).ConfigureAwait(false) is null) continue;
                    withFocus++;
                    return (app, win, await Name(app).ConfigureAwait(false), await Name(win).ConfigureAwait(false));
                }
            }
            catch (Exception e) { LogThrottled("active window fallback failed " + e.GetType().Name + ": " + e.Message); }
        }
        Services.Diag.LogIfChanged($"active window: none apps={apps.Count} with_focus={withFocus}");
        return null;
    }

    /// <summary>アプリ内の焦点要素。Collection があれば 1 往復、無ければ木を辿る（上限つき）。</summary>
    public Task<Ref?> Focused(Ref app, Ref window, int budgetMs = 300) => FocusedIn(app, window, budgetMs);

    async Task<Ref?> FocusedIn(Ref app, Ref window, int budgetMs)
    {
        try
        {
            var m = await MatchStates(app, [StateFocused], 3).ConfigureAwait(false);
            if (m.Count > 0) return m[0];
        }
        catch (Exception) { } // Collection を持たないアプリ（GTK3 等）は木を辿る
        var deadline = Environment.TickCount64 + budgetMs;
        var stack = new Stack<Ref>(); stack.Push(window);
        var visited = 0;
        while (stack.Count > 0 && visited < 800 && Environment.TickCount64 < deadline)
        {
            var r = stack.Pop(); visited++;
            try
            {
                var st = await States(r).ConfigureAwait(false);
                if (Has(st, StateFocused)) return r;
                if (!Has(st, StateShowing) && r != window) continue;
                var kids = await Children(r).ConfigureAwait(false);
                for (var i = kids.Count - 1; i >= 0; i--) if (!kids[i].IsNull) stack.Push(kids[i]);
            }
            catch (Exception) { }
        }
        return null;
    }
}
