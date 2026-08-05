# HiraganaSwitcher

Keeps the **Japanese IME conversion mode sticky across application / focus
switches** on Windows 11.

## The problem it fixes

With Windows' per-window IME state, you set the Japanese IME to **Hiragana**
("あ") in app A (e.g. Brave), switch to app B (e.g. Notepad), and B drops you
back to **Alphanumeric** ("A"), losing your mode. You have to press the
toggle key again in every app.

## What it does

There is one global **sticky mode**: Hiragana or **full-width Katakana**.

- On every focus/app switch it **imposes the sticky mode** on the newly focused
  window for a short window (~2.5 s) whenever that window's mode differs — this
  covers both **Alphanumeric ("A")** *and* a **wrong native mode** (e.g. an app
  that comes up in Hiragana when you were using Katakana). The window is long
  enough to beat an app's *delayed* focus-in IME restore (Discord/Settings
  re-assert their own mode ~1–2 s after gaining focus). This is what makes
  Katakana actually follow you.
- After that window it **stops imposing and adopts whatever mode you set** — so
  you can always change the mode yourself, by **keyboard or mouse**. A
  keyboard-driven change lets you override *early*, before the window is up.
  Whatever native mode you settle on becomes the new sticky mode.
- When you switch a window to Alphanumeric (to type ASCII), that is respected
  for the rest of that visit.

It only acts on windows whose active layout is the Japanese IME, so windows you
are genuinely using in English are left alone. Restored Katakana is always
**full-width** (FULLSHAPE).

## How it works (technical)

- `SetWinEventHook` for `EVENT_SYSTEM_FOREGROUND` + `EVENT_OBJECT_FOCUS`
  detects switches; an 80 ms timer drives the impose/learn loop.
- After a switch there is a **~2.5 s impose window**. While it is open the
  sticky mode is imposed over the app's default and over an app's *delayed*
  focus-in IME restore. Once it closes, imposition stops and the switcher adopts
  whatever mode the window is in — so you can always change modes yourself. This
  time-boxing (rather than imposing forever) is what lets you leave Hiragana.
- A **low-level keyboard hook** (`WH_KEYBOARD_LL`) records your last keypress; a
  keyboard-driven change lets you override *early*, before the impose window is
  up. It is only an accelerator — a mouse-driven IME change (or an elevated app
  where the hook can't see keys) still works once the window closes.
- IME state is read/written with the classic `WM_IME_CONTROL`
  (`IMC_GET/SETOPENSTATUS`, `IMC_GET/SETCONVERSIONMODE`) sent to each window's
  default IME window (`ImmGetDefaultIMEWnd`).
- Crucially it targets the **actually-focused window** via `GetGUIThreadInfo`
  (not just the top-level foreground window). That is what makes it work with
  modern app hosts:
  - **WinUI3** apps (the new Notepad) — input lives in a cross-thread
    `RichEditD2DPT` / `InputSiteWindowClass` island.
  - **UWP** apps (Settings, Search, Start) — input lives in a child
    `Windows.UI.Core.CoreWindow` (also resolved explicitly).
  - **Chromium/Electron** apps (Brave, Discord).

See [ImeInterop.cs](ImeInterop.cs) and [Program.cs](Program.cs).

## Build & run

```powershell
dotnet build HiraganaSwitcher.csproj -c Release
# then run the tray app:
.\bin\Release\net10.0-windows\HiraganaSwitcher.exe
```

It runs in the **system tray** (no window). Right-click the tray icon for:

- **Enabled** — toggle the switcher on/off.
- **Open log** — open `hiragana-switcher.log` (written next to the exe).
- **Exit**.

Run `HiraganaSwitcher.exe --headless` to run without the tray icon (used by the
tests).

### Start automatically at login (optional)

Put a shortcut to the exe in:
`%AppData%\Microsoft\Windows\Start Menu\Programs\Startup`

## Tests

Under [tests/](tests):

- **TargetApp** — a stand-in app (own process, own IME context) that activates
  the Japanese IME and starts in alphanumeric, reproducing the bug.
- **TestDriver** — deterministic end-to-end test: spawns two `TargetApp`
  windows, generates real foreground switches, and asserts: alphanumeric →
  Hiragana; a Hiragana-default app stays Hiragana; **you can switch to Katakana
  yourself with no keypress (mouse-style) and it is adopted, not reverted**;
  **full-width Katakana carries onto both a Hiragana-default app and an
  alphanumeric app**; and a keyboard override back to Hiragana carries too.
  **8/8 pass.**
- **AppProbe** — drives the switcher against **real** running apps
  (`AppProbe.exe "Notepad" "Brave" "Discord" "Settings"`): teaches full-width
  Katakana with a real keypress, has the app "default" back to Hiragana, then
  generates a real switch and verifies **Katakana is re-imposed full-width**. It
  never types text or changes app content (only a harmless lone-Shift tap and
  IME mode) and restores each app to English input afterwards.
  `--diag <title...>` prints a non-destructive window/IME diagnostic;
  `--monitor <seconds>` logs the focused window's raw IME state on every change.

Verified full-width-Katakana-carry across **Notepad (WinUI3), Brave, Discord,
and Settings (UWP)**, plus 8/8 in the deterministic driver.

Build the tests:

```powershell
dotnet build tests/TargetApp/TargetApp.csproj -c Release
dotnet build tests/TestDriver/TestDriver.csproj -c Release
dotnet build tests/AppProbe/AppProbe.csproj -c Release
```

## Notes / limitations

- On a switch it imposes the sticky mode (Hiragana or Katakana) whenever the
  window differs. To type ASCII in an app, toggle to Alphanumeric **after** the
  switch — a keyboard-driven change is respected for the rest of that visit — or
  pause it from the tray. The sticky mode itself only changes when you make a
  deliberate keyboard change to Hiragana/Katakana.
- The Windows **Search/Start flyout** dismisses itself when it loses focus, so
  it cannot be driven by an automated focus-bouncing test — but it uses the same
  UWP `CoreWindow` path as Settings, which is verified.
- Elevated (admin) windows can only be controlled if the switcher also runs
  elevated (Windows UIPI). For those, run it as administrator.
