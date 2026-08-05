using System.Runtime.InteropServices;
using System.Text;

namespace HiraganaSwitcher;

/// <summary>
/// Thin P/Invoke wrapper around the Win32 IMM / IME-control messages used to
/// read and write the conversion mode ("あ" Hiragana / "ア" Katakana / "A"
/// alphanumeric) of whatever window currently has focus.
///
/// The Microsoft Japanese IME still honours the classic WM_IME_CONTROL
/// messages even though it is a TSF text service, so we can query and drive it
/// cross-process by talking to each window's *default IME window*
/// (ImmGetDefaultIMEWnd).
/// </summary>
internal static class ImeInterop
{
    // --- window / thread / layout ---------------------------------------
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    public delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern uint QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder text, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    // --- IMM -------------------------------------------------------------
    [DllImport("imm32.dll")]
    public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public int left, top, right, bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO gti);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    // --- constants -------------------------------------------------------
    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_GETCONVERSIONMODE = 0x0001;
    private const int IMC_SETCONVERSIONMODE = 0x0002;
    private const int IMC_GETOPENSTATUS = 0x0005;
    private const int IMC_SETOPENSTATUS = 0x0006;

    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SendTimeoutMs = 200;

    // Conversion-mode flags.
    public const int IME_CMODE_NATIVE = 0x0001; // Hiragana/Katakana (as opposed to alphanumeric)
    public const int IME_CMODE_KATAKANA = 0x0002; // requires NATIVE
    public const int IME_CMODE_FULLSHAPE = 0x0008;
    public const int IME_CMODE_ROMAN = 0x0010;

    // Sensible default for a Japanese user typing romaji -> Hiragana.
    public const int HiraganaMode = IME_CMODE_NATIVE | IME_CMODE_FULLSHAPE | IME_CMODE_ROMAN; // 0x19

    public const uint LANG_JAPANESE = 0x0411;

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static string ClassOf(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Modern (UWP/WinUI) apps such as Settings, Windows Search and Start are
    /// hosted inside an "ApplicationFrameWindow" whose real text-input surface
    /// is a child "Windows.UI.Core.CoreWindow" living on a different thread.
    /// The IME must be driven through that child, not the frame. For classic
    /// Win32 windows this returns the window unchanged.
    /// </summary>
    public static IntPtr ResolveInputTarget(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return hWnd;
        if (ClassOf(hWnd) != "ApplicationFrameWindow") return hWnd;
        IntPtr core = IntPtr.Zero;
        EnumChildWindows(hWnd, (h, _) =>
        {
            if (ClassOf(h) == "Windows.UI.Core.CoreWindow") { core = h; return false; }
            return true;
        }, IntPtr.Zero);
        return core != IntPtr.Zero ? core : hWnd;
    }

    /// <summary>
    /// The window that actually owns keyboard focus right now, which is where
    /// the IME really lives. This reaches inside modern app hosts: for the
    /// WinUI3 Notepad it returns the RichEdit control, for a Chromium browser
    /// the render/widget window, etc. -- unlike the top-level foreground window,
    /// whose thread may not be the input thread at all. Falls back to the
    /// foreground window when focus info is unavailable.
    /// </summary>
    public static IntPtr FocusedWindow()
    {
        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(0, ref gti))
        {
            if (gti.hwndFocus != IntPtr.Zero) return ResolveInputTarget(gti.hwndFocus);
            if (gti.hwndActive != IntPtr.Zero) return ResolveInputTarget(gti.hwndActive);
        }
        return ResolveInputTarget(GetForegroundWindow());
    }

    /// <summary>Language id (low word of the HKL) of the layout active in the given window's thread.</summary>
    public static uint GetInputLanguage(IntPtr hWnd)
    {
        uint tid = GetWindowThreadProcessId(hWnd, out _);
        IntPtr hkl = GetKeyboardLayout(tid);
        return (uint)(hkl.ToInt64() & 0xFFFF);
    }

    public static bool IsJapaneseInput(IntPtr hWnd) => GetInputLanguage(ResolveInputTarget(hWnd)) == LANG_JAPANESE;

    private static bool TrySend(IntPtr imeWnd, int cmd, IntPtr value, out int result)
    {
        result = 0;
        if (imeWnd == IntPtr.Zero) return false;
        IntPtr r = SendMessageTimeout(imeWnd, WM_IME_CONTROL, (IntPtr)cmd, value,
            SMTO_ABORTIFHUNG, SendTimeoutMs, out IntPtr res);
        if (r == IntPtr.Zero) return false; // timed out / failed
        result = res.ToInt32();
        return true;
    }

    /// <summary>Reads the IME open status + conversion mode for a window. Returns false if it cannot be queried.</summary>
    public static bool TryGetState(IntPtr hWnd, out bool open, out int conversion)
    {
        open = false;
        conversion = 0;
        hWnd = ResolveInputTarget(hWnd);
        IntPtr imeWnd = ImmGetDefaultIMEWnd(hWnd);
        if (imeWnd == IntPtr.Zero) return false;
        if (!TrySend(imeWnd, IMC_GETOPENSTATUS, IntPtr.Zero, out int o)) return false;
        if (!TrySend(imeWnd, IMC_GETCONVERSIONMODE, IntPtr.Zero, out int c)) return false;
        open = o != 0;
        conversion = c;
        return true;
    }

    /// <summary>Forces the window's IME open and into the given conversion mode.</summary>
    public static bool SetState(IntPtr hWnd, int conversion)
    {
        hWnd = ResolveInputTarget(hWnd);
        IntPtr imeWnd = ImmGetDefaultIMEWnd(hWnd);
        if (imeWnd == IntPtr.Zero) return false;
        bool ok = TrySend(imeWnd, IMC_SETOPENSTATUS, (IntPtr)1, out _);
        ok &= TrySend(imeWnd, IMC_SETCONVERSIONMODE, (IntPtr)conversion, out _);
        return ok;
    }

    /// <summary>Classifies a raw (open, conversion) pair into a human-readable IME mode.</summary>
    public static ImeMode Classify(bool open, int conversion)
    {
        if (!open) return ImeMode.Alphanumeric;
        if ((conversion & IME_CMODE_NATIVE) == 0) return ImeMode.Alphanumeric;
        return (conversion & IME_CMODE_KATAKANA) != 0 ? ImeMode.Katakana : ImeMode.Hiragana;
    }

    public static string ForegroundProcessName(IntPtr hWnd)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "?";
            try
            {
                var sb = new StringBuilder(1024);
                uint size = 1024;
                if (QueryFullProcessImageName(h, 0, sb, ref size) != 0)
                    return Path.GetFileName(sb.ToString());
            }
            finally { CloseHandle(h); }
        }
        catch { /* best-effort */ }
        return "?";
    }

    public static string WindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}

internal enum ImeMode
{
    Alphanumeric,
    Hiragana,
    Katakana,
}
