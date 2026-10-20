using static Cliperino.Core.Lang;
using Vortice.DXGI;
using Cliperino.Core;

namespace Cliperino.Capture;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel }

public sealed record GpuAdapter(int Index, string Name, GpuVendor Vendor, uint VendorId, uint DeviceId);

// indices tal como los enumera dxgi, lo que espera ffmpeg
public sealed record MonitorInfo(string DeviceName, Rectangle Bounds, GpuAdapter Adapter, int OutputIndex, bool IsPrimary)
{
    public string Label =>
        $"{(IsPrimary ? T("Principal", "Primary") : DeviceName.Replace(@"\\.\", ""))} - {Bounds.Width}x{Bounds.Height} ({Adapter.Name})";
}

public sealed record GpuSnapshot(IReadOnlyList<GpuAdapter> Adapters, IReadOnlyList<MonitorInfo> Monitors)
{
    public MonitorInfo? FindMonitor(string? deviceName) =>
        Monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
        ?? Monitors.FirstOrDefault(m => m.IsPrimary)
        ?? Monitors.FirstOrDefault();
}

public static class GpuInfo
{
    const uint MicrosoftBasicRenderVendorId = 0x1414;

    public static GpuSnapshot Enumerate()
    {
        var adapters = new List<GpuAdapter>();
        var monitors = new List<MonitorInfo>();
        var primary = Screen.PrimaryScreen?.DeviceName;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter).Failure || adapter == null) break;
                using (adapter)
                {
                    var desc = adapter.Description1;
                    if ((desc.Flags & AdapterFlags.Software) != 0 || desc.VendorId == MicrosoftBasicRenderVendorId) continue;
                    var gpu = new GpuAdapter((int)i, desc.Description.Trim(), ToVendor(desc.VendorId), desc.VendorId, desc.DeviceId);
                    adapters.Add(gpu);

                    for (uint j = 0; ; j++)
                    {
                        if (adapter.EnumOutputs(j, out var output).Failure || output == null) break;
                        using (output)
                        {
                            var od = output.Description;
                            var r = od.DesktopCoordinates;
                            monitors.Add(new MonitorInfo(od.DeviceName, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom),
                                gpu, (int)j, string.Equals(od.DeviceName, primary, StringComparison.OrdinalIgnoreCase)));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Error enumerando GPUs: {ex.Message}");
        }
        return new GpuSnapshot(adapters, monitors);
    }

    static GpuVendor ToVendor(uint vendorId) => vendorId switch
    {
        0x10DE => GpuVendor.Nvidia,
        0x1002 or 0x1022 => GpuVendor.Amd,
        0x8086 => GpuVendor.Intel,
        _ => GpuVendor.Unknown,
    };
}
