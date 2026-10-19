using System.ComponentModel;
using System.Runtime.InteropServices;
using static BtQuickConnect.Native;

namespace BtQuickConnect;

internal sealed record BtDevice(string Address, string Name, bool Connected, bool Paired);

internal enum KsResult { Ok, NotAudio, Failed }

internal static class Bluetooth
{
    /// <summary>Спаренные (запомненные) классические Bluetooth-устройства. null — нет адаптера / Bluetooth выключен.</summary>
    public static List<BtDevice>? GetPairedDevices()
    {
        var p = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
            fReturnAuthenticated = 1,
            fReturnRemembered = 1,
            fReturnConnected = 1,
            fReturnUnknown = 0,
            fIssueInquiry = 0,
        };
        var info = NewInfo();
        var list = new List<BtDevice>();
        IntPtr h = BluetoothFindFirstDevice(ref p, ref info);
        if (h == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            const int ERROR_NO_MORE_ITEMS = 259;
            return err == ERROR_NO_MORE_ITEMS ? list : null;
        }
        try
        {
            do
            {
                if (info.fRemembered != 0 || info.fAuthenticated != 0)
                    list.Add(new BtDevice(info.Address.ToString("X12"), info.szName.Trim(),
                        info.fConnected != 0, true));
                info = NewInfo();
            } while (BluetoothFindNextDevice(h, ref info));
        }
        finally { BluetoothFindDeviceClose(h); }
        return list;
    }

    private static BLUETOOTH_DEVICE_INFO NewInfo() =>
        new() { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(), szName = "" };

    public static KsResult Connect(string address, out string error) =>
        SendOneShot(address, KSPROPERTY_ONESHOT_RECONNECT, stopOnFirst: true, out error);

    public static KsResult Disconnect(string address, out string error) =>
        SendOneShot(address, KSPROPERTY_ONESHOT_DISCONNECT, stopOnFirst: false, out error);

    /// <summary>
    /// Тот же запрос, что посылает кнопка «Подключить»/«Отключить» в параметрах Windows:
    /// свойство KSPROPSETID_BtAudio у KS-фильтров аудиодрайвера этого устройства.
    /// </summary>
    private static KsResult SendOneShot(string address, uint propId, bool stopOnFirst, out string error)
    {
        error = "";
        Guid? container = FindContainerId(address);
        if (container == null)
        {
            error = "Устройство не найдено в системе — возможно, оно больше не спарено.";
            return KsResult.Failed;
        }

        var paths = FindAudioFilters(container.Value);
        if (paths.Count == 0)
        {
            error = "У устройства нет аудиопрофиля: Windows не умеет подключать такие устройства со стороны ПК. "
                  + "Включите его — оно подключится само.";
            return KsResult.NotAudio;
        }

        bool any = false;
        int lastErr = 0;
        foreach (var path in paths)
        {
            using var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { lastErr = Marshal.GetLastWin32Error(); continue; }

            var prop = new KSPROPERTY { Set = KSPROPSETID_BtAudio, Id = propId, Flags = KSPROPERTY_TYPE_GET };
            if (DeviceIoControl(h, IOCTL_KS_PROPERTY, ref prop, Marshal.SizeOf<KSPROPERTY>(),
                    IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                any = true;
                if (stopOnFirst) break;
            }
            else lastErr = Marshal.GetLastWin32Error();
        }

        if (any) return KsResult.Ok;
        error = new Win32Exception(lastErr).Message + $" (0x{lastErr:X})";
        return KsResult.Failed;
    }

    /// <summary>ContainerId узла BTHENUM\DEV_&lt;адрес&gt; — общий для всех профилей и аудиоузлов устройства.</summary>
    private static Guid? FindContainerId(string address)
    {
        string prefix = @"BTHENUM\DEV_" + address.ToUpperInvariant() + @"\";
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, "BTHENUM", IntPtr.Zero, DIGCF_ALLCLASSES);
        if (set == new IntPtr(-1)) return null;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var buf = new char[512];
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (!SetupDiGetDeviceInstanceIdW(set, ref data, buf, buf.Length, out int req)) continue;
                string id = new string(buf, 0, Math.Max(0, req - 1));
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return GetContainer(set, ref data);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return null;
    }

    private static Guid? GetContainer(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var key = DEVPKEY_Device_ContainerId;
        var buf = new byte[16];
        if (SetupDiGetDevicePropertyW(set, ref data, ref key, out uint type, buf, buf.Length, out _, 0)
            && type == DEVPROP_TYPE_GUID)
            return new Guid(buf);
        return null;
    }

    /// <summary>Пути KS-фильтров категории AUDIO, принадлежащих тому же контейнеру (A2DP и Hands-Free).</summary>
    private static List<string> FindAudioFilters(Guid container)
    {
        var result = new List<string>();
        Guid cat = KSCATEGORY_AUDIO;
        IntPtr set = SetupDiGetClassDevsW(ref cat, null, IntPtr.Zero, DIGCF_DEVICEINTERFACE | DIGCF_PRESENT);
        if (set == new IntPtr(-1)) return result;
        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref cat, i, ref iface); i++)
            {
                var dev = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, out int size, ref dev);
                if (size <= 0) continue;
                IntPtr detail = Marshal.AllocHGlobal(size);
                try
                {
                    // cbSize структуры SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 на x64, 6 на x86
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref iface, detail, size, out _, ref dev)) continue;
                    if (GetContainer(set, ref dev) != container) continue;
                    string? path = Marshal.PtrToStringUni(detail + 4);
                    if (!string.IsNullOrEmpty(path)) result.Add(path);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }
}
