using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HiraganaSwitcher;
using ImeMode = HiraganaSwitcher.ImeMode;

namespace TestDriver;

// End-to-end test. Assumes HiraganaSwitcher is already running (headless).
// Spawns two separate "target" processes, each with the Japanese IME active and
// sitting in alphanumeric mode, then drives real foreground switches and
// asserts that the switcher restores the correct native mode.
internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int SW_SHOW = 5;
    private const int KatakanaMode = ImeInterop.IME_CMODE_NATIVE | ImeInterop.IME_CMODE_KATAKANA
                                   | ImeInterop.IME_CMODE_FULLSHAPE | ImeInterop.IME_CMODE_ROMAN; // 0x1B

    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_SETOPENSTATUS = 0x0006;
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    private static int _pass, _fail;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        string targetExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "TargetApp", "bin", "Release", "net10.0-windows", "TargetApp.exe"));
        if (!File.Exists(targetExe))
        {
            Console.WriteLine($"ERROR: TargetApp not found at {targetExe}");
            return 2;
        }

        Console.WriteLine("Launching two Japanese-IME target windows (A, B)...");
        var pA = Process.Start(new ProcessStartInfo(targetExe, "HSW_TARGET_A 80") { UseShellExecute = true });
        var pB = Process.Start(new ProcessStartInfo(targetExe, "HSW_TARGET_B 650") { UseShellExecute = true });
        Thread.Sleep(2500); // let both windows show + activate Japanese IME

        IntPtr a = FindWindowByTitle("HSW_TARGET_A");
        IntPtr b = FindWindowByTitle("HSW_TARGET_B");
        Console.WriteLine($"  A hwnd=0x{a.ToInt64():X}  B hwnd=0x{b.ToInt64():X}");
        if (a == IntPtr.Zero || b == IntPtr.Zero)
        {
            Console.WriteLine("ERROR: could not locate target windows.");
            KillQuiet(pA); KillQuiet(pB);
            return 2;
        }

        // Sanity: confirm both are seen as Japanese input.
        Report("A uses Japanese IME", ImeInterop.IsJapaneseInput(a), true);
        Report("B uses Japanese IME", ImeInterop.IsJapaneseInput(b), true);

        // Timing knobs matched to the switcher (ImposeMs=2500, KeyRecentMs=900).
        const int PastImpose = 2800; // dwell past the impose window (mouse-style change)
        const int KeySettle = 1600;  // within the impose window (keyboard override)
        const int Switch = 1500;     // wait for impose-on-switch to complete
        const int Learn = 700;       // wait for a change to be learned

        try
        {
            // --- T1: switching to an ALPHANUMERIC app imposes Hiragana (default sticky) ---
            SetAlphanumeric(a);
            Focus(b); Thread.Sleep(200);
            Focus(a); Thread.Sleep(Switch);
            AssertMode("T1 switch to alphanumeric app -> Hiragana", a, ImeMode.Hiragana);

            // --- T2: an app that comes up already in Hiragana stays Hiragana ---
            ImeInterop.SetState(b, ImeInterop.HiraganaMode);
            Focus(a); Thread.Sleep(200);
            Focus(b); Thread.Sleep(Switch);
            AssertMode("T2 switch to Hiragana app -> stays Hiragana", b, ImeMode.Hiragana);

            // --- T3: you can switch to Katakana yourself (MOUSE-style: no keypress,
            //         made after the impose window) and it is adopted, not reverted ---
            Focus(a); Thread.Sleep(PastImpose);    // dwell until imposition has ended
            ImeInterop.SetState(a, KatakanaMode);  // change with NO keypress
            Thread.Sleep(Learn);
            AssertMode("T3 user Katakana (no keypress) is adopted, not reverted", a, ImeMode.Katakana);

            // --- T4: KATAKANA carries to an app that DEFAULTS TO HIRAGANA (the bug) ---
            ImeInterop.SetState(b, ImeInterop.HiraganaMode); // B's app default (no keypress)
            Focus(a); Thread.Sleep(200);
            Focus(b); Thread.Sleep(Switch);
            AssertKatakanaFullWidth("T4 Katakana carries onto a Hiragana-default app", b);

            // --- T5: Katakana also carries onto an ALPHANUMERIC app ---
            SetAlphanumeric(a);
            Focus(b); Thread.Sleep(200);
            Focus(a); Thread.Sleep(Switch);
            AssertKatakanaFullWidth("T5 Katakana carries onto an alphanumeric app", a);

            // --- T6: KEYBOARD override within the impose window is honored early,
            //         learned as Hiragana, and carries across a switch ---
            Focus(b); Thread.Sleep(KeySettle);     // still within the impose window
            DeliberateSet(b, ImeInterop.HiraganaMode); // change WITH a keypress (early override)
            Thread.Sleep(Learn);
            ImeInterop.SetState(a, KatakanaMode);  // A left in a stale Katakana (app default)
            Focus(b); Thread.Sleep(200);
            Focus(a); Thread.Sleep(Switch);
            AssertMode("T6 keyboard Hiragana override carries onto a Katakana app", a, ImeMode.Hiragana);
        }
        finally
        {
            KillQuiet(pA); KillQuiet(pB);
        }

        Console.WriteLine();
        Console.WriteLine($"==== RESULT: {_pass} passed, {_fail} failed ====");
        return _fail == 0 ? 0 : 1;
    }

    private static void AssertMode(string name, IntPtr hwnd, ImeMode expected)
    {
        ImeInterop.TryGetState(hwnd, out bool open, out int conv);
        ImeMode actual = ImeInterop.Classify(open, conv);
        Report($"{name}", actual, expected, extra: $"(open={open}, conv=0x{conv:X})");
    }

    private static void AssertKatakanaFullWidth(string name, IntPtr hwnd)
    {
        ImeInterop.TryGetState(hwnd, out bool open, out int conv);
        ImeMode actual = ImeInterop.Classify(open, conv);
        bool fullShape = (conv & ImeInterop.IME_CMODE_FULLSHAPE) != 0;
        bool ok = actual == ImeMode.Katakana && fullShape;
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: got {actual} fullwidth={fullShape}, " +
                          $"expected Katakana fullwidth=True (open={open}, conv=0x{conv:X})");
    }

    private static void Report(string name, object actual, object expected, string extra = "")
    {
        bool ok = actual.Equals(expected);
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: got {actual}, expected {expected} {extra}");
    }

    private static void SetAlphanumeric(IntPtr hwnd)
    {
        IntPtr ime = ImmGetDefaultIMEWnd(hwnd);
        if (ime != IntPtr.Zero)
            SendMessage(ime, WM_IME_CONTROL, (IntPtr)IMC_SETOPENSTATUS, (IntPtr)0);
    }

    // Simulate the user deliberately choosing an IME mode: set the mode AND
    // register a keypress, since the switcher only "learns" keyboard-driven
    // changes. A lone Shift tap is harmless (inserts nothing).
    private const byte VK_SHIFT = 0x10;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private static void DeliberateSet(IntPtr hwnd, int conv)
    {
        ImeInterop.SetState(hwnd, conv);
        keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
        keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static void Focus(IntPtr hwnd)
    {
        IntPtr fg = ImeInterop.GetForegroundWindow();
        uint fgThread = ImeInterop.GetWindowThreadProcessId(fg, out _);
        uint tgtThread = ImeInterop.GetWindowThreadProcessId(hwnd, out _);
        uint cur = GetCurrentThreadId();
        AttachThreadInput(cur, fgThread, true);
        AttachThreadInput(cur, tgtThread, true);
        ShowWindow(hwnd, SW_SHOW);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        SetActiveWindow(hwnd);
        AttachThreadInput(cur, tgtThread, false);
        AttachThreadInput(cur, fgThread, false);
        Console.WriteLine($"  -> focused 0x{hwnd.ToInt64():X}");
    }

    private static IntPtr FindWindowByTitle(string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256);
            ImeInterop.GetWindowText(h, sb, sb.Capacity);
            if (sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static void KillQuiet(Process? p)
    {
        try { if (p is { HasExited: false }) p.Kill(); } catch { }
    }
}
