using System.Runtime.InteropServices;
using KodizSignage.Core.Models;
using KodizSignage.Native;
using Serilog;
using static KodizSignage.Native.NativeMethods;

namespace KodizSignage.Services;

public interface IDisplayService
{
    /// <summary>All active monitors with physical-pixel bounds, ordered by display number.</summary>
    IReadOnlyList<DisplayInfo> GetDisplays();
}

public sealed class DisplayService : IDisplayService
{
    private readonly ILogger _log;

    public DisplayService(ILogger log)
    {
        _log = log.ForContext<DisplayService>();
    }

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var result = new List<DisplayInfo>();
        try
        {
            var friendlyNames = GetFriendlyNames();
            MonitorEnumProc callback = (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    var r = info.rcMonitor;
                    var name = friendlyNames.TryGetValue(info.szDevice, out var friendly) && !string.IsNullOrWhiteSpace(friendly)
                        ? friendly
                        : GetAdapterMonitorName(info.szDevice);
                    result.Add(new DisplayInfo(info.szDevice, name, r.Left, r.Top, r.Width, r.Height,
                        (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
                }

                return true;
            };

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Enumerating displays failed");
        }

        return result.OrderBy(d => d.Number == 0 ? int.MaxValue : d.Number).ThenBy(d => d.X).ThenBy(d => d.Y).ToList();
    }

    /// <summary>Maps GDI device names (\\.\DISPLAY1) to EDID monitor names ("SAMSUNG") via the DisplayConfig API.</summary>
    private Dictionary<string, string> GetFriendlyNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0)
            {
                return names;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            {
                return names;
            }

            foreach (var path in paths.Take((int)pathCount))
            {
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = path.sourceInfo.adapterId,
                        id = path.sourceInfo.id,
                    },
                };
                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = path.targetInfo.adapterId,
                        id = path.targetInfo.id,
                    },
                };

                if (DisplayConfigGetDeviceInfo(ref source) == 0 &&
                    DisplayConfigGetDeviceInfo(ref target) == 0 &&
                    !string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName))
                {
                    names.TryAdd(source.viewGdiDeviceName, target.monitorFriendlyDeviceName);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "DisplayConfig name lookup failed");
        }

        return names;
    }

    private static string GetAdapterMonitorName(string deviceName)
    {
        var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        return EnumDisplayDevices(deviceName, 0, ref device, 0) && !string.IsNullOrWhiteSpace(device.DeviceString)
            ? device.DeviceString
            : string.Empty;
    }
}
