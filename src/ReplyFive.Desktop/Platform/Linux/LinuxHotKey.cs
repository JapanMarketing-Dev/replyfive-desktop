using System.Runtime.InteropServices;
using Avalonia.Input;
using ReplyFive.Desktop.Services;
using Tmds.DBus.Protocol;

namespace ReplyFive.Desktop.Platform.Linux;

/// <summary>グローバルショートカット。XDG Desktop Portal の GlobalShortcuts（Wayland・X11 とも、KDE / GNOME）を優先し、無ければ X11 の XGrabKey。</summary>
public sealed partial class LinuxPlatform
{
    public IHotKeyRegistration? RegisterHotKey(HotKeyCombo combo, Action handler)
    {
        var portal = PortalHotKey.TryCreate(combo, handler);
        if (portal is not null) return portal;
        if (!IsWayland || Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 })
        {
            var x = X11HotKey.TryCreate(combo, handler);
            if (x is not null) return x;
        }
        return new FailedHotKey();
    }

    sealed class FailedHotKey : IHotKeyRegistration { public bool Registered => false; public void Dispose() { } }
}

/// <summary>org.freedesktop.portal.GlobalShortcuts。CreateSession → BindShortcuts → Activated。GNOME 50 以降は先に host Registry.Register で app_id を名乗る。</summary>
public sealed class PortalHotKey : IHotKeyRegistration
{
    const string Portal = "org.freedesktop.portal.Desktop";
    const string PortalPath = "/org/freedesktop/portal/desktop";
    const string IShortcuts = "org.freedesktop.portal.GlobalShortcuts";
    const string IRequest = "org.freedesktop.portal.Request";
    const string AppId = LinuxPlatform.AppId;
    const string ShortcutId = "open-panel";

    public bool Registered { get; private set; }
    DBusConnection? conn;
    IDisposable? activated;
    string? sessionHandle;

    public static PortalHotKey? TryCreate(HotKeyCombo combo, Action handler)
    {
        var h = new PortalHotKey();
        try
        {
            var task = h.SetupAsync(combo, handler);
            if (!task.Wait(4000) || !task.Result) { h.Dispose(); return null; }
            return h;
        }
        catch (Exception e) { Diag.Log("portal hotkey failed " + e.GetType().Name); h.Dispose(); return null; }
    }

    async Task<bool> SetupAsync(HotKeyCombo combo, Action handler)
    {
        conn = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("no session bus"));
        await conn.ConnectAsync().ConfigureAwait(false);
        // GNOME 50 / xdg-desktop-portal 1.20：サンドボックス外のアプリは app_id を登録しないと拒否される
        try { await conn.CallMethodAsync(BuildRegister(conn)).ConfigureAwait(false); }
        catch (Exception) { }

        var token = "rf" + Environment.ProcessId + "_" + Random.Shared.Next(1_000_000);
        var sessionToken = token + "s";
        var response = new TaskCompletionSource<(uint code, Dictionary<string, VariantValue> results)>();
        using var watch = await conn.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Interface = IRequest, Member = "Response" },
            (Message m, object? _) =>
            {
                var path = m.PathAsString ?? "";
                var r = m.GetBodyReader();
                var code = r.ReadUInt32();
                var results = new Dictionary<string, VariantValue>();
                var end = r.ReadDictionaryStart();
                while (r.HasNext(end)) { r.AlignStruct(); results[r.ReadString()] = r.ReadVariantValue(); }
                return (path, code, results);
            },
            (Notification<(string path, uint code, Dictionary<string, VariantValue> results)> n) =>
            {
                if (!n.HasValue || !n.Value.path.EndsWith("/" + token, StringComparison.Ordinal) && !n.Value.path.EndsWith("/" + token + "b", StringComparison.Ordinal)) return;
                response.TrySetResult((n.Value.code, n.Value.results));
            }, false, ObserverFlags.None, null).ConfigureAwait(false);

        await conn.CallMethodAsync(BuildCreateSession(conn, token, sessionToken)).ConfigureAwait(false);
        var created = await WaitAsync(response.Task, 3000).ConfigureAwait(false);
        if (created is null || created.Value.code != 0 || !created.Value.results.TryGetValue("session_handle", out var sh)) return false;
        sessionHandle = sh.Type == VariantValueType.ObjectPath ? sh.GetObjectPathAsString() : sh.GetString();

        // Activated を先に受け、その後 BindShortcuts
        activated = await conn.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Interface = IShortcuts, Member = "Activated" },
            (Message m, object? _) => { var r = m.GetBodyReader(); var session = r.ReadObjectPathAsString(); var id = r.ReadString(); return (session, id); },
            (Notification<(string session, string id)> n) => { if (n.HasValue && n.Value.session == sessionHandle && n.Value.id == ShortcutId) handler(); }, false, ObserverFlags.None, null).ConfigureAwait(false);

        response = new TaskCompletionSource<(uint, Dictionary<string, VariantValue>)>();
        var bindToken = token + "b";
        await conn.CallMethodAsync(BuildBind(conn, sessionHandle, combo, bindToken)).ConfigureAwait(false);
        // ポータルは利用者の確認ダイアログを出すことがあるので長めに待つ
        var bound = await WaitAsync(response.Task, 60_000).ConfigureAwait(false);
        Registered = bound is { code: 0 };
        Diag.Log("portal hotkey registered=" + Registered);
        return Registered;
    }

    static MessageBuffer BuildRegister(DBusConnection conn)
    {
        using var w = conn.GetMessageWriter();
        w.WriteMethodCallHeader(Portal, PortalPath, "org.freedesktop.host.portal.Registry", "Register", "sa{sv}");
        w.WriteString(AppId);
        var d = w.WriteDictionaryStart(); w.WriteDictionaryEnd(d);
        return w.CreateMessage();
    }

    static MessageBuffer BuildCreateSession(DBusConnection conn, string token, string sessionToken)
    {
        using var w = conn.GetMessageWriter();
        w.WriteMethodCallHeader(Portal, PortalPath, IShortcuts, "CreateSession", "a{sv}");
        var d = w.WriteDictionaryStart();
        w.WriteDictionaryEntryStart(); w.WriteString("handle_token"); w.WriteVariantString(token);
        w.WriteDictionaryEntryStart(); w.WriteString("session_handle_token"); w.WriteVariantString(sessionToken);
        w.WriteDictionaryEnd(d);
        return w.CreateMessage();
    }

    static MessageBuffer BuildBind(DBusConnection conn, string sessionHandle, HotKeyCombo combo, string bindToken)
    {
        using var w = conn.GetMessageWriter();
        w.WriteMethodCallHeader(Portal, PortalPath, IShortcuts, "BindShortcuts", "oa(sa{sv})sa{sv}");
        w.WriteObjectPath(sessionHandle);
        var arr = w.WriteArrayStart(DBusType.Struct);
        w.WriteStructureStart();
        w.WriteString(ShortcutId);
        var sd = w.WriteDictionaryStart();
        w.WriteDictionaryEntryStart(); w.WriteString("description"); w.WriteVariantString("Open ReplyFive");
        w.WriteDictionaryEntryStart(); w.WriteString("preferred_trigger"); w.WriteVariantString(Trigger(combo));
        w.WriteDictionaryEnd(sd);
        w.WriteArrayEnd(arr);
        w.WriteString(""); // parent_window
        var od = w.WriteDictionaryStart();
        w.WriteDictionaryEntryStart(); w.WriteString("handle_token"); w.WriteVariantString(bindToken);
        w.WriteDictionaryEnd(od);
        return w.CreateMessage();
    }

    static MessageBuffer BuildClose(DBusConnection conn, string sessionHandle)
    {
        using var w = conn.GetMessageWriter();
        w.WriteMethodCallHeader(Portal, sessionHandle, "org.freedesktop.portal.Session", "Close", null);
        return w.CreateMessage();
    }

    static async Task<T?> WaitAsync<T>(Task<T> task, int ms) where T : struct
    {
        var done = await Task.WhenAny(task, Task.Delay(ms)).ConfigureAwait(false);
        return done == task ? await task.ConfigureAwait(false) : null;
    }

    /// <summary>ポータルの shortcut 記法："CTRL+SHIFT+r"（修飾は大文字、キーは xkb のキー名）。</summary>
    public static string Trigger(HotKeyCombo c)
    {
        var parts = new List<string>();
        if (c.Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("CTRL");
        if (c.Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("ALT");
        if (c.Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("SHIFT");
        if (c.Modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("LOGO");
        parts.Add(X11HotKey.KeysymName(c.Key));
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        activated?.Dispose();
        if (conn is not null && sessionHandle is not null)
        {
            try { conn.TrySendMessage(BuildClose(conn, sessionHandle)); } catch (Exception) { }
        }
        conn?.Dispose();
    }
}

/// <summary>X11 の XGrabKey。NumLock / CapsLock の併用も掴む。専用スレッドで XPending を見張る。</summary>
public sealed class X11HotKey : IHotKeyRegistration
{
    public bool Registered { get; private set; }
    IntPtr display;
    IntPtr root;
    int keycode;
    uint mods;
    volatile bool stop;
    Thread? thread;
    static readonly uint[] lockMasks = [0, LockMask, Mod2Mask, LockMask | Mod2Mask];
    const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16, Mod4Mask = 64;
    const int KeyPress = 2, GrabModeAsync = 1;
    const long KeyPressMask = 1L << 0;

    public static X11HotKey? TryCreate(HotKeyCombo combo, Action handler)
    {
        try
        {
            var h = new X11HotKey();
            h.display = XOpenDisplay(IntPtr.Zero);
            if (h.display == IntPtr.Zero) return null;
            h.root = XDefaultRootWindow(h.display);
            var keysym = XStringToKeysym(KeysymName(combo.Key));
            if (keysym == 0) { XCloseDisplay(h.display); return null; }
            h.keycode = XKeysymToKeycode(h.display, keysym);
            if (h.keycode == 0) { XCloseDisplay(h.display); return null; }
            h.mods = 0;
            if (combo.Modifiers.HasFlag(KeyModifiers.Control)) h.mods |= ControlMask;
            if (combo.Modifiers.HasFlag(KeyModifiers.Shift)) h.mods |= ShiftMask;
            if (combo.Modifiers.HasFlag(KeyModifiers.Alt)) h.mods |= Mod1Mask;
            if (combo.Modifiers.HasFlag(KeyModifiers.Meta)) h.mods |= Mod4Mask;
            XSetErrorHandler(errorHandler);
            foreach (var lm in lockMasks) XGrabKey(h.display, h.keycode, h.mods | lm, h.root, true, GrabModeAsync, GrabModeAsync);
            XSelectInput(h.display, h.root, KeyPressMask);
            XSync(h.display, false);
            h.Registered = !grabFailed;
            grabFailed = false;
            h.thread = new Thread(() => h.Loop(handler)) { IsBackground = true, Name = "replyfive-x11-hotkey" };
            h.thread.Start();
            Diag.Log("x11 hotkey registered=" + h.Registered);
            return h;
        }
        catch (DllNotFoundException) { return null; }
        catch (Exception e) { Diag.Log("x11 hotkey failed " + e.GetType().Name); return null; }
    }

    static volatile bool grabFailed;
    static readonly XErrorHandler errorHandler = (_, _) => { grabFailed = true; return 0; };

    void Loop(Action handler)
    {
        var buffer = new byte[192];
        while (!stop)
        {
            try
            {
                while (!stop && XPending(display) > 0)
                {
                    XNextEvent(display, buffer);
                    var type = BitConverter.ToInt32(buffer, 0);
                    if (type != KeyPress) continue;
                    var state = BitConverter.ToUInt32(buffer, 80);
                    var code = BitConverter.ToUInt32(buffer, 84);
                    if (code == (uint)keycode && (state & (ShiftMask | ControlMask | Mod1Mask | Mod4Mask)) == mods) handler();
                }
            }
            catch (Exception) { }
            Thread.Sleep(25);
        }
    }

    public void Dispose()
    {
        stop = true;
        try
        {
            if (display != IntPtr.Zero)
            {
                foreach (var lm in lockMasks) XUngrabKey(display, keycode, mods | lm, root);
                XSync(display, false);
                thread?.Join(500);
                XCloseDisplay(display);
                display = IntPtr.Zero;
            }
        }
        catch (Exception) { }
    }

    /// <summary>Avalonia の Key → X11 keysym 名（xkb の名前。ポータルの記法にも使う）。</summary>
    public static string KeysymName(Key k)
    {
        if (k >= Key.A && k <= Key.Z) return k.ToString().ToLowerInvariant();
        if (k >= Key.D0 && k <= Key.D9) return ((int)k - (int)Key.D0).ToString();
        if (k >= Key.F1 && k <= Key.F24) return k.ToString();
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return "KP_" + ((int)k - (int)Key.NumPad0);
        return k switch
        {
            Key.Space => "space", Key.Return => "Return", Key.Tab => "Tab", Key.Back => "BackSpace", Key.Delete => "Delete", Key.Insert => "Insert",
            Key.Left => "Left", Key.Right => "Right", Key.Up => "Up", Key.Down => "Down", Key.Home => "Home", Key.End => "End", Key.PageUp => "Prior", Key.PageDown => "Next",
            Key.OemComma => "comma", Key.OemPeriod => "period", Key.OemMinus => "minus", Key.OemPlus => "equal", Key.OemQuestion => "slash", Key.OemSemicolon => "semicolon",
            Key.OemQuotes => "apostrophe", Key.OemOpenBrackets => "bracketleft", Key.OemCloseBrackets => "bracketright", Key.OemPipe => "backslash", Key.OemTilde => "grave",
            _ => k.ToString(),
        };
    }

    delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);
    const string Lib = "libX11.so.6";
    [DllImport(Lib)] static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(Lib)] static extern int XCloseDisplay(IntPtr display);
    [DllImport(Lib)] static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(Lib)] static extern ulong XStringToKeysym(string name);
    [DllImport(Lib)] static extern byte XKeysymToKeycode(IntPtr display, ulong keysym);
    [DllImport(Lib)] static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow, bool ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(Lib)] static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);
    [DllImport(Lib)] static extern int XSelectInput(IntPtr display, IntPtr window, long mask);
    [DllImport(Lib)] static extern int XPending(IntPtr display);
    [DllImport(Lib)] static extern int XNextEvent(IntPtr display, byte[] eventReturn);
    [DllImport(Lib)] static extern int XSync(IntPtr display, bool discard);
    [DllImport(Lib)] static extern IntPtr XSetErrorHandler(XErrorHandler handler);
}
