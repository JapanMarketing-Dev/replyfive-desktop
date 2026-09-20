using System.Runtime.InteropServices;
using System.Text;

namespace ReplyFive.Desktop.Platform.Windows;

/// <summary>Win32 の呼び出し口。UI Automation で届かないところ（前面ウインドウ・クリップボード・貼り付けキー・
/// ショートカット登録・画面の取り込み・署名検証）だけを持つ。失敗は戻り値で返し、例外を上へ投げない。</summary>
internal static class Native
{
    // MARK: - ウインドウ

    internal const int SW_RESTORE = 9;
    internal const uint ASFW_ANY = 0xFFFFFFFF;

    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(uint dwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] internal static extern int GetWindowTextLengthW(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly System.Drawing.Rectangle ToRectangle() => System.Drawing.Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    /// <summary>ウインドウの表題。取れなければ null。</summary>
    internal static string? WindowText(IntPtr hWnd)
    {
        try
        {
            var n = GetWindowTextLengthW(hWnd);
            if (n <= 0) return null;
            var sb = new StringBuilder(n + 2);
            var got = GetWindowTextW(hWnd, sb, sb.Capacity);
            return got > 0 ? sb.ToString() : null;
        }
        catch (Exception) { return null; }
    }

    internal static System.Drawing.Rectangle WindowRect(IntPtr hWnd)
        => IsWindow(hWnd) && GetWindowRect(hWnd, out var r) ? r.ToRectangle() : System.Drawing.Rectangle.Empty;

    // MARK: - プロセス

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandleW(string? lpModuleName);

    /// <summary>プロセスの実行ファイルのパス。権限が無ければ null。</summary>
    internal static string? ProcessImagePath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var size = (uint)1024;
            var sb = new StringBuilder((int)size);
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        catch (Exception) { return null; }
        finally { CloseHandle(h); }
    }

    // MARK: - 利用者名・パッケージ

    const int NameDisplay = 3;
    const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetUserNameExW(int NameFormat, StringBuilder lpNameBuffer, ref uint lpnSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);

    /// <summary>ドメイン／ローカルの表示名（「遠藤 巧巳」）。取れなければアカウント名。</summary>
    internal static string DisplayUserName()
    {
        try
        {
            var size = (uint)256;
            var sb = new StringBuilder((int)size);
            if (GetUserNameExW(NameDisplay, sb, ref size) && sb.Length > 0) return sb.ToString();
        }
        catch (Exception) { }
        return Environment.UserName;
    }

    /// <summary>MSIX（ストア）配布かどうか。パッケージ下では URL スキーム・自動起動・自己更新を OS 側に任せる。</summary>
    internal static bool IsPackaged()
    {
        try
        {
            var len = (uint)0;
            return GetCurrentPackageFullName(ref len, null) != APPMODEL_ERROR_NO_PACKAGE;
        }
        catch (Exception) { return false; }
    }

    // MARK: - クリップボード

    internal const uint CF_UNICODETEXT = 13;
    const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] internal static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] internal static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>他のアプリがクリップボードを掴んでいることがあるので、200ms まで待って開く。</summary>
    static bool OpenClipboardWithRetry()
    {
        for (var i = 0; i < 20; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    internal static void ClipboardWrite(string text)
    {
        if (!OpenClipboardWithRetry()) return;
        var hMem = IntPtr.Zero;
        try
        {
            if (!EmptyClipboard()) return;
            var bytes = (UIntPtr)(ulong)((text.Length + 1) * 2);
            hMem = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (hMem == IntPtr.Zero) return;
            var p = GlobalLock(hMem);
            if (p == IntPtr.Zero) return;
            try { Marshal.Copy(text.ToCharArray(), 0, p, text.Length); Marshal.WriteInt16(p, text.Length * 2, 0); }
            finally { GlobalUnlock(hMem); }
            // 成功したら所有権は OS へ移る（解放しない）
            if (SetClipboardData(CF_UNICODETEXT, hMem) != IntPtr.Zero) hMem = IntPtr.Zero;
        }
        catch (Exception) { }
        finally
        {
            if (hMem != IntPtr.Zero) GlobalFree(hMem);
            CloseClipboard();
        }
    }

    internal static string? ClipboardRead()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        if (!OpenClipboardWithRetry()) return null;
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { GlobalUnlock(h); }
        }
        catch (Exception) { return null; }
        finally { CloseClipboard(); }
    }

    // MARK: - キー送出（貼り付けだけ。送信キーは決して送らない）

    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_V = 0x56;
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };

    /// <summary>Ctrl+V だけを送る。Enter・送信のキーは送らない（守る制約）。</summary>
    internal static bool SendCtrlV()
    {
        try
        {
            INPUT[] seq = [Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true)];
            return SendInput((uint)seq.Length, seq, Marshal.SizeOf<INPUT>()) == (uint)seq.Length;
        }
        catch (Exception) { return false; }
    }

    // MARK: - ショートカット（メッセージ専用ウインドウ）

    internal const uint WM_HOTKEY = 0x0312;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_DESTROY = 0x0002;
    internal static readonly IntPtr HWND_MESSAGE = new(-3);

    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;
    internal const uint MOD_NOREPEAT = 0x4000;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr DispatchMessageW(ref MSG lpMsg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern void PostQuitMessage(int nExitCode);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // MARK: - 画面の取り込み（付録BP・BU）

    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int nIndex);
    internal const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    internal static System.Drawing.Rectangle VirtualScreen()
        => new(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN), GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    // MARK: - Authenticode の検証（付録CD）

    static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    const uint WTD_UI_NONE = 2;
    const uint WTD_REVOKE_NONE = 0;
    const uint WTD_CHOICE_FILE = 1;
    const uint WTD_STATEACTION_VERIFY = 1;
    const uint WTD_STATEACTION_CLOSE = 2;
    const uint WTD_SAFER_FLAG = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

    /// <summary>Authenticode の署名と証明書の連鎖が有効か。UI は出さない。</summary>
    internal static bool VerifyAuthenticode(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        var pData = IntPtr.Zero;
        var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_SAFER_FLAG,
            };
            pData = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            Marshal.StructureToPtr(data, pData, false);
            var result = WinVerifyTrust(IntPtr.Zero, ref action, pData);
            // 取得した状態は必ず閉じる（ハンドルの取りこぼしを避ける）
            var close = Marshal.PtrToStructure<WINTRUST_DATA>(pData);
            close.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(close, pData, false);
            WinVerifyTrust(IntPtr.Zero, ref action, pData);
            return result == 0;
        }
        catch (Exception) { return false; }
        finally
        {
            if (pData != IntPtr.Zero) { Marshal.DestroyStructure<WINTRUST_DATA>(pData); Marshal.FreeHGlobal(pData); }
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}
