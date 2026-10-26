using System.Runtime.InteropServices;

namespace PowerGuard.App.Native;

/// <summary>
/// Win32 P/Invoke 声明。签名与结构体布局依据 Microsoft Learn：
/// winuser.h RegisterPowerSettingNotification / WM_POWERBROADCAST / POWERBROADCAST_SETTING，
/// winbase.h GetSystemPowerStatus / SYSTEM_POWER_STATUS，
/// winuser.h GetDisplayConfigBufferSizes / QueryDisplayConfig / DisplayConfigGetDeviceInfo，
/// wingdi.h DISPLAYCONFIG_PATH_INFO / DISPLAYCONFIG_MODE_INFO / DISPLAYCONFIG_TARGET_DEVICE_NAME。
/// </summary>
internal static class NativeMethods
{
    // ── 电源 ──
    public const int WM_POWERBROADCAST = 0x0218;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_QUERYENDSESSION = 0x0011;
    public const int WM_ENDSESSION = 0x0016;
    public const int PBT_APMPOWERSTATUSCHANGE = 0x000A;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const int PBT_APMRESUMESUSPEND = 0x0007;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;
    public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x0;

    /// <summary>GUID_ACDC_POWER_SOURCE：Data 为 DWORD，0=AC，1=DC（电池），2=短时电源（UPS）。</summary>
    public static readonly Guid GUID_ACDC_POWER_SOURCE = new("5d3e9a59-e9D5-4b00-a6bd-ff34ff516548");

    /// <summary>GUID_LIDSWITCH_STATE_CHANGE：Data 为 DWORD，0=合上，1=打开。注册后 Windows 会立即推送一次当前状态。</summary>
    public static readonly Guid GUID_LIDSWITCH_STATE_CHANGE = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data; // 实际为 UCHAR Data[1]，ACDC 设置的数据长度为 4
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;       // 0=离线，1=在线，255=未知
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, int Flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    // ── 显示配置 ──
    public const uint QDC_ALL_PATHS = 0x00000001;
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    public const uint QDC_DATABASE_CURRENT = 0x00000004;
    public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    public enum DISPLAYCONFIG_TOPOLOGY_ID : uint
    {
        Internal = 0x1, Clone = 0x2, Extend = 0x4, External = 0x8,
    }

    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY
    public const uint OUTPUT_TECH_HDMI = 5;
    public const uint OUTPUT_TECH_LVDS = 6;
    public const uint OUTPUT_TECH_DISPLAYPORT_EMBEDDED = 11;
    public const uint OUTPUT_TECH_UDI_EMBEDDED = 13;
    public const uint OUTPUT_TECH_INTERNAL = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO // 72 字节
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>DISPLAYCONFIG_MODE_INFO 为 64 字节（含 48 字节 union），本程序不读取 union 内容。</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME // 420 字节
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, out DISPLAYCONFIG_TOPOLOGY_ID currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    // ── 设置显示拓扑（与 Win+P 相同的系统接口） ──
    public const uint SDC_TOPOLOGY_INTERNAL = 0x00000001;
    public const uint SDC_TOPOLOGY_CLONE = 0x00000002;
    public const uint SDC_TOPOLOGY_EXTEND = 0x00000004;
    public const uint SDC_TOPOLOGY_EXTERNAL = 0x00000008;
    public const uint SDC_APPLY = 0x00000080;

    /// <summary>SetDisplayConfig(0, NULL, 0, NULL, SDC_APPLY | SDC_TOPOLOGY_*)：按数据库中保存的拓扑切换。</summary>
    [DllImport("user32.dll")]
    public static extern int SetDisplayConfig(uint numPathArrayElements, IntPtr pathArray, uint numModeInfoArrayElements, IntPtr modeInfoArray, uint flags);

    // ── 关机 / 注销 ──
    public const long ENDSESSION_CLOSEAPP = 0x00000001;
    public const long ENDSESSION_CRITICAL = 0x40000000;
    public const long ENDSESSION_LOGOFF = 0x80000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszReason);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

    // ── 窗口 ──
    public const int SW_RESTORE = 9;
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
}
