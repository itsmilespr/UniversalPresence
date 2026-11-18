using System.Runtime.InteropServices;
using UniversalPresence.Configuration;

namespace UniversalPresence.Tray;

/// <summary>
/// Ventana oculta que recibe WM_HOTKEY para los atajos globales.
/// </summary>
public sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private readonly Action<int> _onHotkey;
    private readonly List<int> _ids = new();

    public HotkeyWindow(Action<int> onHotkey)
    {
        _onHotkey = onHotkey;
        CreateHandle(new CreateParams());
    }

    public bool Register(int id, uint modifiers, uint vk)
    {
        if (RegisterHotKey(Handle, id, modifiers, vk))
        {
            _ids.Add(id);
            return true;
        }
        return false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            _onHotkey(m.WParam.ToInt32());
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in _ids)
        {
            try { UnregisterHotKey(Handle, id); } catch { }
        }
        _ids.Clear();
        try { DestroyHandle(); } catch { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
}