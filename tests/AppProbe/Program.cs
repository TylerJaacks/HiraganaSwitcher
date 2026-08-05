using System.Runtime.InteropServices;
using System.Text;
using HiraganaSwitcher;
using ImeMode = HiraganaSwitcher.ImeMode;

namespace AppProbe;

// Tests the running HiraganaSwitcher against REAL applications the user already
// has open (Brave, Discord, Settings, Notepad, ...).
//
// For each target window it: (1) switches the window's thread to the Japanese
// IME, (2) forces it into alphanumeric mode = reproduces the "app forgot
// Hiragana" bug, (3) generates a genuine foreground switch to it, then (4)
// verifies the switcher restored a native (Hiragana/Katakana) mode.
//
// It never types text or changes any app content -- only the IME conversion
// mode and momentary window focus. The originally-focused window is restored
// at the end.
internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc f, IntPtr p);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadKeyboardLayout(string klid, uint flags);
    [DllImport("imm32.dll")] private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr p);

    private const int SW_SHOW = 5;
    private const uint KLF_ACTIVATE = 0x00000001;
    private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_SETOPENSTATUS = 0x0006;

    private static int _pass, _fail, _skip;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0)
        {
            Console.WriteLine("usage: AppProbe <title-substring> [more...]");
            return 2;
        }

        if (args[0] == "--diag")
        {
            foreach (string spec in args.Skip(1)) Diag(spec);
            return 0;
        }

        if (args[0] == "--monitor")
        {
            int secs = args.Length > 1 && int.TryParse(args[1], out int s) ? s : 90;
            Monitor(secs);
            return 0;
        }

        IntPtr original = ImeInterop.GetForegroundWindow();
        IntPtr jpHkl = LoadKeyboardLayout("00000411", 0); // ensure JP layout is loaded; get its HKL

        // A small owned window we can bounce focus to, so every test is a real
        // "switch away then back" rather than a no-op re-focus.
        using var helper = new Form { Text = "AppProbe_helper", Width = 200, Height = 120,
            StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(20, 20) };
        helper.Show();
        System.Windows.Forms.Application.DoEvents();

        foreach (string spec in args)
        {
            IntPtr hwnd = FindWindowByTitle(spec);
            Console.WriteLine($"\n### Target '{spec}' -> hwnd=0x{hwnd.ToInt64():X}");
            if (hwnd == IntPtr.Zero) { Console.WriteLine("  SKIP: window not found"); _skip++; continue; }

            // Focus the target first: input-language changes apply reliably to
            // the foreground window, and it lets us find the real input control.
            Focus(hwnd);
            Thread.Sleep(300);

            // Resolve to the window that actually owns focus (RichEdit for the
            // WinUI3 Notepad, CoreWindow for Settings, widget for Chromium).
            IntPtr input = ImeInterop.FocusedWindow();
            if (input != hwnd) Console.WriteLine($"  (real input window: 0x{input.ToInt64():X})");

            // 1) Make the target use the Japanese IME; poll until it takes.
            bool jp = false;
            for (int i = 0; i < 12 && !jp; i++)
            {
                PostMessage(input, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, jpHkl);
                Thread.Sleep(150);
                jp = ImeInterop.IsJapaneseInput(input);
            }
            if (!jp)
            {
                Console.WriteLine("  SKIP: could not activate Japanese IME on this window (app manages input differently)");
                _skip++;
                continue;
            }

            int KATA = ImeInterop.IME_CMODE_NATIVE | ImeInterop.IME_CMODE_KATAKANA
                     | ImeInterop.IME_CMODE_FULLSHAPE | ImeInterop.IME_CMODE_ROMAN;   // 0x1B
            int HIRA = ImeInterop.HiraganaMode;                                       // 0x19

            // 2) Teach: dwell past the impose window, then choose full-width
            //    Katakana -> switcher adopts it as the sticky mode.
            Thread.Sleep(2700);                 // past the impose window (ImposeMs)
            DeliberateSet(input, KATA);         // user's choice: Katakana
            Thread.Sleep(900);                  // adopt/learn
            ImeInterop.TryGetState(input, out bool ko, out int kc);
            Console.WriteLine($"  taught full-width Katakana: {ImeInterop.Classify(ko, kc)} (conv=0x{kc:X})");

            // 3) Reproduce the bug the way it really happens: switch AWAY first,
            //    then the backgrounded app 'defaults' back to Hiragana, then
            //    switch back. The switcher must re-impose Katakana on the switch.
            Focus(helper.Handle);
            Thread.Sleep(300);
            ImeInterop.SetState(input, HIRA);   // app's remembered default, set while backgrounded
            Thread.Sleep(150);
            Focus(hwnd);

            // 4) Verify full-width Katakana is restored across the switch.
            ImeMode result = ImeMode.Hiragana;
            bool o1 = false; int c1 = 0;
            var trace = new StringBuilder();
            for (int i = 0; i < 30; i++)   // ~1.8s trace to see the fight
            {
                Thread.Sleep(60);
                ImeInterop.TryGetState(ImeInterop.FocusedWindow(), out o1, out c1);
                result = ImeInterop.Classify(o1, c1);
                trace.Append(result == ImeMode.Katakana ? 'K' : result == ImeMode.Hiragana ? 'H' : 'A');
            }
            Console.WriteLine($"  trace(60ms): {trace}");
            // final steady-state read
            Thread.Sleep(200);
            ImeInterop.TryGetState(ImeInterop.FocusedWindow(), out o1, out c1);
            result = ImeInterop.Classify(o1, c1);
            bool full = (c1 & ImeInterop.IME_CMODE_FULLSHAPE) != 0;
            bool ok = result == ImeMode.Katakana && full;
            if (ok) _pass++; else _fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] Katakana carried across switch: {result} " +
                              $"fullwidth={full} (open={o1}, conv=0x{c1:X}) -- expected Katakana fullwidth=True");

            // Politeness: restore this app to English + IME closed, so it is
            // left roughly as found and falls outside the switcher's JP gate.
            IntPtr enHkl = LoadKeyboardLayout("00000409", 0);
            PostMessage(input, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, enHkl);
            SendMessage(ImmGetDefaultIMEWnd(input), WM_IME_CONTROL, (IntPtr)IMC_SETOPENSTATUS, (IntPtr)0);
        }

        // Restore the user's original foreground window.
        if (original != IntPtr.Zero) Focus(original);

        Console.WriteLine($"\n==== REAL-APP RESULT: {_pass} passed, {_fail} failed, {_skip} skipped ====");
        return _fail == 0 ? 0 : 1;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr p, EnumWindowsProc f, IntPtr l);

    private static string Cls(IntPtr h)
    {
        var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize; public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public System.Drawing.Rectangle rcCaret;
    }
    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO gti);

    // Logs the focused window's raw IME state every time it changes. Run it,
    // then in a real app set Hiragana, then full-width Katakana, then switch
    // apps -- the log shows exactly what conversion-mode value each produces.
    private static void Monitor(int seconds)
    {
        string log = Path.Combine(AppContext.BaseDirectory, "ime-monitor.log");
        File.WriteAllText(log, $"# IME monitor started {DateTime.Now}\n");
        Console.WriteLine($"Monitoring focused-window IME for {seconds}s -> {log}");
        Console.WriteLine("Now: focus an app, set Hiragana, then FULL-WIDTH Katakana, then switch apps.\n");
        string last = "";
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            IntPtr f = ImeInterop.FocusedWindow();
            string proc = ImeInterop.ForegroundProcessName(f);
            bool jp = ImeInterop.IsJapaneseInput(f);
            ImeInterop.TryGetState(f, out bool o, out int c);
            ImeMode m = ImeInterop.Classify(o, c);
            string cur = $"{proc}|jp={jp}|open={o}|conv=0x{c:X}|{m}";
            if (cur != last)
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff}  proc={proc,-16} jp={jp,-5} open={o,-5} " +
                              $"conv=0x{c:X4}  NATIVE={(c & 1)!=0} KATA={(c & 2)!=0} FULL={(c & 8)!=0} ROMAN={(c&0x10)!=0}  -> {m}";
                Console.WriteLine(line);
                File.AppendAllText(log, line + "\n");
                last = cur;
            }
            Thread.Sleep(100);
        }
        Console.WriteLine("done.");
    }

    private static void Diag(string spec)
    {
        IntPtr h = FindWindowByTitle(spec);
        Console.WriteLine($"\n### '{spec}' hwnd=0x{h.ToInt64():X} class='{Cls(h)}'");
        if (h == IntPtr.Zero) return;

        // Focus it, then look at the REAL focused window via GetGUIThreadInfo(0).
        Focus(h);
        Thread.Sleep(400);
        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        GetGUIThreadInfo(0, ref gti);
        IntPtr f = gti.hwndFocus != IntPtr.Zero ? gti.hwndFocus : gti.hwndActive;
        Console.WriteLine($"  GUIThreadInfo focus=0x{f.ToInt64():X} class='{Cls(f)}' " +
                          $"tid={ImeInterop.GetWindowThreadProcessId(f, out _)} lang=0x{ImeInterop.GetInputLanguage(f):X}");
        ImeInterop.TryGetState(f, out bool fo, out int fc);
        Console.WriteLine($"    focus-window state: open={fo} conv=0x{fc:X} -> {ImeInterop.Classify(fo, fc)}");
        IntPtr input = ImeInterop.ResolveInputTarget(h);
        Console.WriteLine($"  resolved input=0x{input.ToInt64():X} class='{Cls(input)}'");
        uint tid = ImeInterop.GetWindowThreadProcessId(input, out uint pid);
        Console.WriteLine($"  proc={ImeInterop.ForegroundProcessName(input)} pid={pid} tid={tid} " +
                          $"lang=0x{ImeInterop.GetInputLanguage(input):X} japanese={ImeInterop.IsJapaneseInput(h)}");
        bool got = ImeInterop.TryGetState(h, out bool o, out int c);
        Console.WriteLine($"  state got={got} open={o} conv=0x{c:X} -> {ImeInterop.Classify(o, c)}");
        Console.WriteLine("  top-level child input-ish windows:");
        EnumChildWindows(h, (ch, _) =>
        {
            string cc = Cls(ch);
            if (cc.Contains("Core") || cc.Contains("Input") || cc.Contains("RenderWidget") ||
                cc.Contains("Chrome") || cc.Contains("Intermediate") || cc.Contains("IME"))
                Console.WriteLine($"    child 0x{ch.ToInt64():X} '{cc}'");
            return true;
        }, IntPtr.Zero);
    }

    // Set an IME mode AND register a keypress, to mimic a deliberate user
    // choice (the switcher only learns keyboard-driven changes). Lone Shift is
    // harmless -- it inserts no text into the focused app.
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
        uint fgT = ImeInterop.GetWindowThreadProcessId(fg, out _);
        uint tgT = ImeInterop.GetWindowThreadProcessId(hwnd, out _);
        uint cur = GetCurrentThreadId();
        AttachThreadInput(cur, fgT, true);
        AttachThreadInput(cur, tgT, true);
        ShowWindow(hwnd, SW_SHOW);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        SetActiveWindow(hwnd);
        AttachThreadInput(cur, tgT, false);
        AttachThreadInput(cur, fgT, false);
    }

    private static IntPtr FindWindowByTitle(string sub)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            ImeInterop.GetWindowText(h, sb, sb.Capacity);
            string t = sb.ToString();
            if (t.Contains(sub, StringComparison.OrdinalIgnoreCase)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
