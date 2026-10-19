using System.Runtime.InteropServices;

namespace BtQuickConnect;

/// <summary>Назначенная кнопка: либо клавиша (Key), либо кнопка мыши (Mouse), плюс модификаторы.</summary>
internal sealed class HotkeyBinding
{
    public const int ModAlt = 1, ModCtrl = 2, ModShift = 4, ModWin = 8;

    public Keys Key { get; set; }
    public MouseButtons Mouse { get; set; }
    public int Modifiers { get; set; }

    public bool IsMouse => Mouse != MouseButtons.None;

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & ModCtrl) != 0) parts.Add("Ctrl");
        if ((Modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((Modifiers & ModShift) != 0) parts.Add("Shift");
        if ((Modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(IsMouse ? MouseName(Mouse) : KeyName(Key));
        return string.Join(" + ", parts);
    }

    private static string MouseName(MouseButtons b) => b switch
    {
        MouseButtons.Middle => "Средняя кнопка мыши",
        MouseButtons.XButton1 => "Боковая кнопка мыши «Назад»",
        MouseButtons.XButton2 => "Боковая кнопка мыши «Вперёд»",
        _ => b.ToString(),
    };

    private static string KeyName(Keys k)
    {
        if (k is >= Keys.D0 and <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
        if (k is >= Keys.NumPad0 and <= Keys.NumPad9) return "Num " + (k - Keys.NumPad0);
        uint ch = HotkeyNative.MapVirtualKeyW((uint)k, 2 /* MAPVK_VK_TO_CHAR */) & 0x7FFF;
        if (ch > 32 && k is >= Keys.Oem1 and <= Keys.Oem102) return ((char)ch).ToString();
        return k switch
        {
            Keys.MediaPlayPause => "Play/Pause",
            Keys.MediaNextTrack => "Следующий трек",
            Keys.MediaPreviousTrack => "Предыдущий трек",
            Keys.MediaStop => "Stop",
            Keys.VolumeMute => "Без звука",
            Keys.VolumeUp => "Громкость +",
            Keys.VolumeDown => "Громкость −",
            Keys.LaunchApplication1 => "Мой компьютер",
            Keys.LaunchApplication2 => "Калькулятор",
            Keys.LaunchMail => "Почта",
            Keys.SelectMedia => "Медиаплеер",
            _ => k.ToString(),
        };
    }

    /// <summary>Клавиши, которые разумно ловить без модификаторов: F1–F24, мультимедиа, Pause, Scroll Lock.</summary>
    public static bool AllowedWithoutModifiers(Keys k) =>
        k is >= Keys.F1 and <= Keys.F24
        or >= Keys.BrowserBack and <= Keys.LaunchApplication2
        or Keys.Pause or Keys.Scroll;

    public static int CurrentModifiers()
    {
        int m = 0;
        if (HotkeyNative.Down(0x11)) m |= ModCtrl;
        if (HotkeyNative.Down(0x12)) m |= ModAlt;
        if (HotkeyNative.Down(0x10)) m |= ModShift;
        if (HotkeyNative.Down(0x5B) || HotkeyNative.Down(0x5C)) m |= ModWin;
        return m;
    }
}

/// <summary>
/// Глобальная кнопка подключения: клавиши — через RegisterHotKey,
/// кнопки мыши — через низкоуровневый хук (нажатие «съедается», чтобы браузер не листал назад).
/// </summary>
internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int HotkeyId = 1;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly SynchronizationContext _ui;
    private readonly HotkeyNative.LowLevelMouseProc _proc;
    private IntPtr _hook;
    private bool _registered;
    private HotkeyBinding? _mouseBinding;
    private MouseButtons _swallowUp;

    public event Action? Pressed;

    /// <summary>Режим записи: сюда уходит любое нажатие средней/боковой кнопки мыши.</summary>
    public Action<MouseButtons, int>? MouseCapture { get; set; }

    public HotkeyManager(SynchronizationContext ui)
    {
        _ui = ui;
        _proc = HookProc;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
    }

    /// <summary>Снимает старое назначение и ставит новое. false — комбинация занята другой программой.</summary>
    public bool Apply(HotkeyBinding? b, out string error)
    {
        error = "";
        Clear();
        if (b == null) return true;

        if (b.IsMouse)
        {
            _mouseBinding = b;
            return EnsureHook(out error);
        }

        uint mods = (uint)b.Modifiers | MOD_NOREPEAT;
        if (!HotkeyNative.RegisterHotKey(Handle, HotkeyId, mods, (uint)b.Key))
        {
            error = $"Комбинация {b} уже занята другой программой или системой.";
            return false;
        }
        _registered = true;
        return true;
    }

    public void Clear()
    {
        if (_registered) HotkeyNative.UnregisterHotKey(Handle, HotkeyId);
        _registered = false;
        _mouseBinding = null;
        if (MouseCapture == null) RemoveHook();
    }

    public bool BeginCapture(Action<MouseButtons, int> onMouse, out string error)
    {
        Clear();
        MouseCapture = onMouse;
        return EnsureHook(out error);
    }

    public void EndCapture()
    {
        MouseCapture = null;
        if (_mouseBinding == null) RemoveHook();
    }

    private bool EnsureHook(out string error)
    {
        error = "";
        if (_hook != IntPtr.Zero) return true;
        _hook = HotkeyNative.SetWindowsHookExW(14 /* WH_MOUSE_LL */, _proc, HotkeyNative.GetModuleHandleW(null), 0);
        if (_hook != IntPtr.Zero) return true;
        error = "Не удалось перехватить кнопки мыши (код " + Marshal.GetLastWin32Error() + ").";
        return false;
    }

    private void RemoveHook()
    {
        if (_hook == IntPtr.Zero) return;
        HotkeyNative.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<HotkeyNative.MSLLHOOKSTRUCT>(lParam);
            MouseButtons btn = msg switch
            {
                0x0207 or 0x0208 => MouseButtons.Middle,                       // WM_MBUTTONDOWN/UP
                0x020B or 0x020C => (data.mouseData >> 16) == 1                // WM_XBUTTONDOWN/UP
                    ? MouseButtons.XButton1 : MouseButtons.XButton2,
                _ => MouseButtons.None,
            };
            bool down = msg is 0x0207 or 0x020B;
            bool up = msg is 0x0208 or 0x020C;

            if (btn != MouseButtons.None)
            {
                if (up && _swallowUp == btn)
                {
                    _swallowUp = MouseButtons.None;
                    return (IntPtr)1;
                }
                if (down)
                {
                    int mods = HotkeyBinding.CurrentModifiers();
                    var capture = MouseCapture;
                    if (capture != null)
                    {
                        _swallowUp = btn;
                        _ui.Post(_ => capture(btn, mods), null);
                        return (IntPtr)1;
                    }
                    var b = _mouseBinding;
                    if (b != null && b.Mouse == btn && b.Modifiers == mods)
                    {
                        _swallowUp = btn;
                        _ui.Post(_ => Pressed?.Invoke(), null);
                        return (IntPtr)1;
                    }
                }
            }
        }
        return HotkeyNative.CallNextHookEx(_hook, code, wParam, lParam);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId) Pressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        MouseCapture = null;
        Clear();
        RemoveHook();
        DestroyHandle();
    }
}

internal static class HotkeyNative
{
    public delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public int x, y;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKeyW(uint code, uint mapType);

    public static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
