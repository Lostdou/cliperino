using System.Runtime.InteropServices;

namespace Cliperino.UI;

sealed class HotkeyWindow : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const int HotkeyId = 0xC11;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    bool registered;

    public event Action? Pressed;

    public HotkeyWindow() => CreateHandle(new CreateParams { Parent = HWND_MESSAGE });

    public bool Register(string text)
    {
        Unregister();
        if (!TryParse(text, out var modifiers, out var key)) return false;
        registered = RegisterHotKey(Handle, HotkeyId, modifiers | MOD_NOREPEAT, (uint)key);
        return registered;
    }

    public void Unregister()
    {
        if (registered) UnregisterHotKey(Handle, HotkeyId);
        registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam == HotkeyId) Pressed?.Invoke();
        base.WndProc(ref m);
    }

    public static bool TryParse(string text, out uint modifiers, out Keys key)
    {
        modifiers = 0;
        key = Keys.None;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": modifiers |= MOD_WIN; break;
                default:
                    if (key != Keys.None) return false;
                    var name = part.Length == 1 && char.IsDigit(part[0]) ? "D" + part : part;
                    if (!Enum.TryParse(name, ignoreCase: true, out key) || IsModifierKey(key)) return false;
                    break;
            }
        }
        return key != Keys.None;
    }

    public static string? Format(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key == Keys.None || IsModifierKey(key)) return null;
        var parts = new List<string>();
        if (keyData.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (keyData.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (keyData.HasFlag(Keys.Shift)) parts.Add("Shift");
        parts.Add(key is >= Keys.D0 and <= Keys.D9 ? ((int)(key - Keys.D0)).ToString() : key.ToString());
        return string.Join("+", parts);
    }

    static bool IsModifierKey(Keys k) => k is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
        or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or Keys.Menu or Keys.LMenu or Keys.RMenu
        or Keys.LWin or Keys.RWin;

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
