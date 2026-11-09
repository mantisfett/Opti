using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Opti.Native;

/// <summary>Поддержка и состояние аппаратного планирования GPU (D3DKMT_WDDM_2_7_CAPS).</summary>
[SupportedOSPlatform("windows")]
public static class HagsNative
{
    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct OPEN_FROM_LUID { public LUID AdapterLuid; public uint hAdapter; }
    [StructLayout(LayoutKind.Sequential)] private struct QUERY_INFO { public uint hAdapter; public int Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }
    [StructLayout(LayoutKind.Sequential)] private struct CLOSE_ADAPTER { public uint hAdapter; }

    private const int KMTQAITYPE_WDDM_2_7_CAPS = 70;

    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromLuid(ref OPEN_FROM_LUID p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QUERY_INFO p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CLOSE_ADAPTER p);

    /// <summary>luidHex — «HHHHHHHHLLLLLLLL», как в DxgiAdapter.Luid.</summary>
    public static (bool Supported, bool Enabled)? Query(string luidHex)
    {
        if (luidHex.Length != 16) return null;
        var open = new OPEN_FROM_LUID
        {
            AdapterLuid = new LUID
            {
                HighPart = unchecked((int)Convert.ToUInt32(luidHex[..8], 16)),
                LowPart = Convert.ToUInt32(luidHex[8..], 16),
            },
        };
        try
        {
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0) return null;
            var buf = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(buf, 0);
                var q = new QUERY_INFO { hAdapter = open.hAdapter, Type = KMTQAITYPE_WDDM_2_7_CAPS, pPrivateDriverData = buf, PrivateDriverDataSize = 4 };
                if (D3DKMTQueryAdapterInfo(ref q) != 0) return null;
                uint caps = (uint)Marshal.ReadInt32(buf);
                return ((caps & 1) != 0, (caps & 2) != 0);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                var c = new CLOSE_ADAPTER { hAdapter = open.hAdapter };
                _ = D3DKMTCloseAdapter(ref c);
            }
        }
        catch (EntryPointNotFoundException) { return null; }
    }
}
