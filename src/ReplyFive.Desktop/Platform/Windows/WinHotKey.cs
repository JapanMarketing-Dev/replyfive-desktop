using Avalonia.Input;
using ReplyFive.Desktop.Services;

namespace ReplyFive.Desktop.Platform.Windows;

/// <summary>全体ショートカット。専用スレッドにメッセージ専用ウインドウ（HWND_MESSAGE）を作り、そこへ WM_HOTKEY を受ける。
/// 呼び出し側（App）がハンドラを UI スレッドへ渡し直すので、ここでは受けたまま呼ぶ。</summary>
internal sealed class WinHotKey : IHotKeyRegistration
{
    const int HotKeyId = 0x5F01;

    readonly Action handler;
    readonly Thread thread;
    readonly ManualResetEventSlim ready = new(false);
    IntPtr window;
    volatile bool registered;
    volatile bool disposed;
    Native.WndProcDelegate? wndProc;   // GC に回収させない
    readonly string className = "ReplyFiveHotKey_" + Guid.NewGuid().ToString("N");

    public bool Registered => registered;

    internal WinHotKey(HotKeyCombo combo, Action handler)
    {
        this.handler = handler;
        thread = new Thread(() => Loop(combo)) { IsBackground = true, Name = "ReplyFive hotkey" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(3000);
    }

    void Loop(HotKeyCombo combo)
    {
        try
        {
            wndProc = WndProc;
            var instance = Native.GetModuleHandleW(null);
            var cls = new Native.WNDCLASSEXW
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.WNDCLASSEXW>(),
                lpfnWndProc = wndProc,
                hInstance = instance,
                lpszClassName = className,
            };
            if (Native.RegisterClassExW(ref cls) == 0) { ready.Set(); return; }
            window = Native.CreateWindowExW(0, className, null, 0, 0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
            if (window == IntPtr.Zero) { Native.UnregisterClassW(className, instance); ready.Set(); return; }

            var (modifiers, vk) = Map(combo);
            registered = vk != 0 && Native.RegisterHotKey(window, HotKeyId, modifiers | Native.MOD_NOREPEAT, vk);
            ready.Set();
            if (!registered) { Cleanup(instance); return; }

            while (!disposed)
            {
                var got = Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
                if (got is 0 or -1) break;   // WM_QUIT または失敗
                if (msg.message == Native.WM_HOTKEY && (int)msg.wParam == HotKeyId)
                {
                    try { handler(); }
                    catch (Exception e) { Crash.Exception(e); }
                    continue;
                }
                Native.DispatchMessageW(ref msg);
            }
            Cleanup(instance);
        }
        catch (Exception e)
        {
            Crash.Exception(e);
            ready.Set();
        }
    }

    IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_CLOSE) { Native.DestroyWindow(hWnd); return IntPtr.Zero; }
        if (msg == Native.WM_DESTROY) { Native.PostQuitMessage(0); return IntPtr.Zero; }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    void Cleanup(IntPtr instance)
    {
        if (window != IntPtr.Zero)
        {
            if (registered) Native.UnregisterHotKey(window, HotKeyId);
            if (Native.IsWindow(window)) Native.DestroyWindow(window);
            window = IntPtr.Zero;
        }
        registered = false;
        Native.UnregisterClassW(className, instance);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var w = window;
        if (w != IntPtr.Zero) Native.PostMessageW(w, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        try { thread.Join(1000); } catch (Exception) { }
        ready.Dispose();
    }

    // MARK: - キーの対応づけ

    /// <summary>Avalonia のキーと修飾キーを Win32 の仮想キーコードへ。対応が無ければ vk = 0（登録しない）。</summary>
    internal static (uint Modifiers, uint Vk) Map(HotKeyCombo combo)
    {
        uint mods = 0;
        if (combo.Modifiers.HasFlag(KeyModifiers.Control)) mods |= Native.MOD_CONTROL;
        if (combo.Modifiers.HasFlag(KeyModifiers.Alt)) mods |= Native.MOD_ALT;
        if (combo.Modifiers.HasFlag(KeyModifiers.Shift)) mods |= Native.MOD_SHIFT;
        if (combo.Modifiers.HasFlag(KeyModifiers.Meta)) mods |= Native.MOD_WIN;
        return (mods, VirtualKey(combo.Key));
    }

    internal static uint VirtualKey(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return (uint)('A' + (key - Key.A));
        if (key >= Key.D0 && key <= Key.D9) return (uint)('0' + (key - Key.D0));
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return (uint)(0x60 + (key - Key.NumPad0));
        if (key >= Key.F1 && key <= Key.F24) return (uint)(0x70 + (key - Key.F1));
        return key switch
        {
            Key.Space => 0x20,
            Key.Return => 0x0D,
            Key.Tab => 0x09,
            Key.Back => 0x08,
            Key.Delete => 0x2E,
            Key.Insert => 0x2D,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.Multiply => 0x6A,
            Key.Add => 0x6B,
            Key.Subtract => 0x6D,
            Key.Decimal => 0x6E,
            Key.Divide => 0x6F,
            Key.OemSemicolon => 0xBA,
            Key.OemPlus => 0xBB,
            Key.OemComma => 0xBC,
            Key.OemMinus => 0xBD,
            Key.OemPeriod => 0xBE,
            Key.OemQuestion => 0xBF,
            Key.OemTilde => 0xC0,
            Key.OemOpenBrackets => 0xDB,
            Key.OemPipe => 0xDC,
            Key.OemCloseBrackets => 0xDD,
            Key.OemQuotes => 0xDE,
            Key.OemBackslash => 0xE2,
            _ => 0,
        };
    }
}
