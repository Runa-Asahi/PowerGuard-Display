using System.Runtime.InteropServices;
using static PowerGuard.App.Native.NativeMethods;

namespace PowerGuard.Core.Tests;

/// <summary>结构体大小与 Windows SDK 头文件中的 sizeof 一致（x64）。</summary>
public class NativeLayoutTests
{
    [Fact] public void PathInfo_Is72() => Assert.Equal(72, Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>());
    [Fact] public void ModeInfo_Is64() => Assert.Equal(64, Marshal.SizeOf<DISPLAYCONFIG_MODE_INFO>());
    [Fact] public void TargetDeviceName_Is420() => Assert.Equal(420, Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>());
    [Fact] public void SystemPowerStatus_Is12() => Assert.Equal(12, Marshal.SizeOf<SYSTEM_POWER_STATUS>());
    [Fact] public void PowerBroadcastSetting_DataAtOffset20() => Assert.Equal(20, (int)Marshal.OffsetOf<POWERBROADCAST_SETTING>(nameof(POWERBROADCAST_SETTING.Data)));
    [Fact] public void AcDcGuid() => Assert.Equal(Guid.Parse("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548"), GUID_ACDC_POWER_SOURCE);
}
