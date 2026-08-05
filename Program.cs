using System.Runtime.InteropServices;

namespace HiraganaSwitcher;

/// <summary>
/// Keeps the Japanese IME conversion mode "sticky" across application / focus
/// switches.
///
/// Problem: with per-window IME conversion mode, the mode you chose in app A
/// (Hiragana or Katakana) does not follow you to app B. App B comes up in its
/// own default -- often Alphanumeric ("A"), but sometimes Hiragana ("あ") --
/// which is wrong if you were using Katakana ("ア").
///
/// Model:
///  * There is one global "sticky" mode: Hiragana or full-width Katakana.
///  * On every focus/app switch we IMPOSE the sticky mode on the newly focused
///    window whenever its current mode differs (covers Alphanumeric AND a
///    wrong native mode such as Hiragana-when-you-wanted-Katakana).
///  * We only LEARN a new sticky mode from a deliberate change you make while
///    staying in a window -- never from an app's default-on-focus. This is what
///    stops Katakana from being wiped the moment you land on a Hiragana app.
/// </summary>
internal static class Program
{
    // ---- WinEvent hook interop ----
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    // ---- low-level keyboard hook (to detect deliberate user input) ----
    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private static LowLevelKeyboardProc _keyProcRef = null!;
    private static IntPtr _hookKeyboard;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    // Keep the delegate alive for the process lifetime so the GC does not
    // collect the thunk the OS calls back into.
    private static WinEventDelegate _procRef = null!;
    private static IntPtr _hookForeground;
    private static IntPtr _hookFocus;

    // ---- conversion-mode bits ----
    private const int NATIVE = ImeInterop.IME_CMODE_NATIVE;     // 0x01
    private const int KATAKANA = ImeInterop.IME_CMODE_KATAKANA;   // 0x02
    private const int FULLSHAPE = ImeInterop.IME_CMODE_FULLSHAPE;  // 0x08
    private const int ROMAN = ImeInterop.IME_CMODE_ROMAN;      // 0x10

    // ---- global sticky state ----
    // Whether the sticky mode is Katakana (vs Hiragana), and the ROMAN
    // (romaji-input) bit we have seen, so we preserve the user's kana/romaji
    // typing preference when imposing.
    private static bool _stickyKatakana;
    private static int _stickyRoman = ROMAN; // default to romaji input
    private static bool _enabled = true;

    // After a switch we IMPOSE the sticky mode on the new window for this long.
    // It must outlast an app's delayed focus-in IME restore (Discord/Settings
    // re-assert their own mode ~1-2 s after gaining focus). Once it passes we
    // stop imposing and adopt whatever mode the window is in -- so you can
    // always change the mode yourself, by keyboard OR mouse.
    private const long ImposeMs = 2500;
    private static long _imposeUntil;

    // A keyboard-driven change lets you override *early*, before ImposeMs is up.
    // (It is only an accelerator now -- learning does not depend on it, so a
    // mouse-driven IME change or an elevated app still works.)
    private const long KeyRecentMs = 900;
    private static long _lastKeyTime = -100000;

    // Once a change is allowed through in the current visit we stop imposing for
    // the rest of it. Reset on the next switch.
    private static bool _userOverride;

    // Debounce for learning: a differing native mode must persist a couple of
    // ticks before we treat it as the new sticky mode.
    private static int _observeConv = -1;
    private static int _observeCount;

    private static System.Windows.Forms.Timer _timer = null!;
    private const int TickMs = 80;

    private static NotifyIcon _tray = null!;
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "hiragana-switcher.log");

    [STAThread]
    private static void Main(string[] args)
    {
        bool headless = args.Contains("--headless") || args.Contains("--console");

        ApplicationConfiguration.Initialize();

        _procRef = OnWinEvent;
        _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _procRef, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _hookFocus = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS,
            IntPtr.Zero, _procRef, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (_hookForeground == IntPtr.Zero && _hookFocus == IntPtr.Zero)
        {
            Log("FATAL: could not install WinEvent hooks.");
            return;
        }

        _keyProcRef = OnKey;
        _hookKeyboard = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProcRef, GetModuleHandle(null), 0);

        _timer = new System.Windows.Forms.Timer { Interval = TickMs };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        if (!headless)
            SetupTray();

        Log($"=== HiraganaSwitcher started (headless={headless}) ===");
        Log("Default sticky mode: Hiragana. Impose-on-switch, learn-on-deliberate-change.");

        ArmEnforce();

        Application.Run();

        Cleanup();
    }

    private static void SetupTray()
    {
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "HiraganaSwitcher (enabled)",
        };
        var menu = new ContextMenuStrip();
        var toggle = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        toggle.CheckedChanged += (_, _) =>
        {
            _enabled = toggle.Checked;
            UpdateTrayText();
            Log($"-> {(_enabled ? "ENABLED" : "PAUSED")} by user.");
        };
        menu.Items.Add(toggle);
        menu.Items.Add("Open log", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogPath) { UseShellExecute = true }); }
            catch { }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        _tray.ContextMenuStrip = menu;
        UpdateTrayText();
    }

    // Called by the OS on every foreground / focus change.
    private static void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint eventThread, uint eventTime)
    {
        // idObject == 0 (OBJID_WINDOW) filters caret/child noise on focus events.
        if (eventType == EVENT_OBJECT_FOCUS && idObject != 0) return;
        ArmEnforce();
        Tick(); // act immediately, don't wait for the next timer tick
    }

    private static void ArmEnforce()
    {
        _imposeUntil = Environment.TickCount64 + ImposeMs;
        _userOverride = false;
        _observeConv = -1;
        _observeCount = 0;
    }

    private static IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                _lastKeyTime = Environment.TickCount64;
        }
        return CallNextHookEx(_hookKeyboard, nCode, wParam, lParam);
    }

    // ---- mode helpers ----
    // 0 = Alphanumeric, 1 = Hiragana, 2 = Katakana.
    private static int Canon(bool open, int conv)
    {
        if (!open || (conv & NATIVE) == 0) return 0;
        return (conv & KATAKANA) != 0 ? 2 : 1;
    }

    // The full-width conversion-mode value for the sticky mode, preserving the
    // romaji/kana input preference.
    private static int StickyConv()
    {
        int bits = NATIVE | FULLSHAPE | (_stickyKatakana ? KATAKANA : 0);
        return bits | (_stickyRoman & ROMAN);
    }

    private static void Tick()
    {
        if (!_enabled) return;

        var hwnd = ImeInterop.FocusedWindow();
        if (hwnd == IntPtr.Zero) return;
        if (!ImeInterop.IsJapaneseInput(hwnd)) return;
        if (!ImeInterop.TryGetState(hwnd, out bool open, out int conv)) return;

        var cur = Canon(open, conv);
        var want = _stickyKatakana ? 2 : 1;
        var now = Environment.TickCount64;

        if (cur == want) { _observeConv = -1; _observeCount = 0; return; }

        // The window differs from the sticky mode. Decide: impose, or adopt.
        var locked = now < _imposeUntil && !_userOverride;
        var recentKey = (now - _lastKeyTime) < KeyRecentMs;

        if (locked && !recentKey)
        {
            // Still in the post-switch window and no deliberate keyboard change
            // behind this -> it's the app's default/late revert. Impose sticky
            // (covers Alphanumeric AND a wrong native mode such as Hiragana).
            var target = NATIVE | FULLSHAPE | (_stickyKatakana ? KATAKANA : 0)
                         | ((conv | _stickyRoman) & ROMAN);
            if (ImeInterop.SetState(hwnd, target))
                Log($"IMPOSE [{ImeInterop.ForegroundProcessName(hwnd)}] {Name(cur)} -> {Name(want)} " +
                    $"(conv=0x{target:X}) title='{Trunc(ImeInterop.WindowTitle(ImeInterop.GetForegroundWindow()))}'");
            _observeConv = -1;
            _observeCount = 0;
            return;
        }

        // Past the impose window, or an early keyboard override: this is your
        // choice. Stop imposing for the rest of this visit and adopt it.
        _userOverride = true;

        if (cur == 0)
        {
            // You went to Alphanumeric to type ASCII -- leave it, don't learn.
            _observeConv = -1;
            _observeCount = 0;
            return;
        }

        // A native mode (Hiragana/Katakana) -> learn it as the new sticky mode.
        if (conv == _observeConv) _observeCount++;
        else { _observeConv = conv; _observeCount = 1; }

        if (_observeCount < 2) return;
        _stickyKatakana = cur == 2;
        _stickyRoman = conv & ROMAN;
        _observeConv = -1;
        _observeCount = 0;
        
        Log($"LEARN sticky mode is now {Name(cur)} (from {ImeInterop.ForegroundProcessName(hwnd)}, conv=0x{conv:X})");
        
        UpdateTrayText();
    }

    private static string Name(int canon) => canon switch { 0 => "Alphanumeric", 2 => "Katakana", _ => "Hiragana" };

    private static void UpdateTrayText()
    {
        if (_tray == null) return;
        _tray.Text = $"HiraganaSwitcher ({(_enabled ? "on" : "paused")}) - sticky: {(_stickyKatakana ? "Katakana" : "Hiragana")}";
    }

    private static string Trunc(string s) => s.Length > 40 ? s[..40] + "…" : s;

    private static readonly object _logLock = new();
    private static void Log(string msg)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff}  {msg}";
        Console.WriteLine(line);
        try
        {
            lock (_logLock)
                File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { }
    }

    private static void Cleanup()
    {
        if (_hookForeground != IntPtr.Zero) UnhookWinEvent(_hookForeground);
        if (_hookFocus != IntPtr.Zero) UnhookWinEvent(_hookFocus);
        if (_hookKeyboard != IntPtr.Zero) UnhookWindowsHookEx(_hookKeyboard);
        _tray?.Dispose();
        Log("=== HiraganaSwitcher stopped ===");
    }
}
