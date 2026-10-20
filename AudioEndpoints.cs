using System.Runtime.InteropServices;

namespace BtQuickConnect;

/// <summary>Звук устройства: Connected — есть ли (null — не понять по аудиоустройствам);
/// AllUp — не осталось отключённых профилей (стерео и гарнитура подняты).</summary>
internal sealed record AudioState(bool? Connected, bool AllUp);

/// <summary>
/// Аудиоустройства Windows (наушники, гарнитура) одного Bluetooth-устройства — по ContainerId.
/// Их состояние меняется сразу, как профиль подключился или отключился, а радиосоединение (ACL),
/// которое видит BluetoothFindFirstDevice, Windows держит после отключения звука ещё ~3 с.
/// </summary>
internal static class AudioEndpoints
{
    private const int DEVICE_STATE_ACTIVE = 1;
    private const int DEVICE_STATE_DISABLED = 2;
    private const int DEVICE_STATE_UNPLUGGED = 8;
    private const int DEVICE_STATEMASK_ALL = 0xF;
    private const int eAll = 2;
    private const ushort VT_CLSID = 72;

    /// <summary>
    /// Есть ли звук по состояниям аудиоустройств: ACTIVE — да; только UNPLUGGED — нет.
    /// NOTPRESENT (старые записи) не в счёт. Если что-то выключено в панели звука (DISABLED),
    /// по оставшимся судить нельзя (выключили стерео, а Hands-Free не поднят) — null,
    /// и о подключении судим по радиосоединению.
    /// </summary>
    internal static bool? Evaluate(IEnumerable<int> states)
    {
        bool unplugged = false, disabled = false;
        foreach (int s in states)
        {
            if (s == DEVICE_STATE_ACTIVE) return true;
            if (s == DEVICE_STATE_UNPLUGGED) unplugged = true;
            if (s == DEVICE_STATE_DISABLED) disabled = true;
        }
        return unplugged && !disabled ? false : null;
    }

    /// <summary>Все ли профили подняты: среди аудиоустройств нет отключённых (UNPLUGGED).</summary>
    internal static bool AllUp(IEnumerable<int> states) => !states.Contains(DEVICE_STATE_UNPLUGGED);

    public static AudioState Read(Guid container)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            if (en.EnumAudioEndpoints(eAll, DEVICE_STATEMASK_ALL, out var col) != 0) return new AudioState(null, true);
            try
            {
                col.GetCount(out uint n);
                var states = new List<int>();
                for (uint i = 0; i < n; i++)
                {
                    if (col.Item(i, out var dev) != 0) continue;
                    try
                    {
                        if (ContainerOf(dev) != container) continue;
                        if (dev.GetState(out int st) == 0) states.Add(st);
                    }
                    finally { Marshal.ReleaseComObject(dev); }
                }
                return new AudioState(Evaluate(states), AllUp(states));
            }
            finally { Marshal.ReleaseComObject(col); }
        }
        finally { Marshal.ReleaseComObject(en); }
    }

    private static Guid? ContainerOf(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0 /* STGM_READ */, out var ps) != 0) return null;
        try
        {
            var key = new PROPERTYKEY { fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };
            if (ps.GetValue(ref key, out var v) != 0) return null;
            try { return v.vt == VT_CLSID && v.p != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(v.p) : null; }
            finally { PropVariantClear(ref v); }
        }
        finally { Marshal.ReleaseComObject(ps); }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p, p2; }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT v);
}
