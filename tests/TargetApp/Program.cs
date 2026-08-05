using System.Runtime.InteropServices;

namespace TargetApp;

// A stand-in "application" for the end-to-end test. Each instance is its own
// process (so it has its own IME context, exactly like Notepad vs Brave), it
// activates the Japanese IME on its own thread, and by default starts in
// alphanumeric mode -- reproducing the "app forgets Hiragana" bug the switcher
// is meant to fix.
internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint flags);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint KLF_ACTIVATE = 0x00000001;
    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_SETOPENSTATUS = 0x0006;

    [STAThread]
    private static void Main(string[] args)
    {
        string title = args.Length > 0 ? args[0] : "HSW_TARGET";
        Application.EnableVisualStyles();

        var form = new Form
        {
            Text = title,
            Width = 480,
            Height = 260,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(args.Length > 1 && int.TryParse(args[1], out int x) ? x : 100, 100),
            ShowInTaskbar = true,
        };
        var box = new TextBox { Multiline = true, Dock = DockStyle.Fill };
        form.Controls.Add(box);

        form.Shown += (_, _) =>
        {
            // Activate Japanese IME for this thread.
            LoadKeyboardLayout("00000411", KLF_ACTIVATE);
            box.Focus();
            // Start in alphanumeric (IME closed) to simulate the reset bug.
            IntPtr ime = ImmGetDefaultIMEWnd(form.Handle);
            if (ime != IntPtr.Zero)
                SendMessage(ime, WM_IME_CONTROL, (IntPtr)IMC_SETOPENSTATUS, (IntPtr)0);
        };

        Application.Run(form);
    }
}
