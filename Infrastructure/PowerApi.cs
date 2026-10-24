using System.Runtime.InteropServices;
using System.Text;

namespace ThgOptimization.Infrastructure;

/// <summary>
/// Leitura do plano de energia direto pela powrprof.dll (a mesma API que o powercfg usa por baixo), sem abrir um processo
/// por consulta. Cada função devolve null quando a API falha; quem chama cai então no powercfg, como antes.
/// </summary>
public static class PowerApi
{
    /// <summary>GUID do plano ativo, em minúsculas (igual ao /getactivescheme).</summary>
    public static string? ActiveScheme()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStructure<Guid>(ptr).ToString(); }
            finally { LocalFree(ptr); }
        }
        catch { return null; }
    }

    /// <summary>Valor "na tomada" (AC) de uma opção. scheme null = plano ativo (o scheme_current do powercfg).</summary>
    public static int? ReadAc(string? scheme, string sub, string setting)
    {
        try
        {
            if ((scheme ?? ActiveScheme()) is not string s || !Guid.TryParse(s, out var g)
                || !Guid.TryParse(sub, out var sg) || !Guid.TryParse(setting, out var st)) return null;
            return PowerReadACValueIndex(IntPtr.Zero, ref g, ref sg, ref st, out uint v) == 0 ? (int)v : null;
        }
        catch { return null; }
    }

    /// <summary>Planos existentes: GUID e nome traduzido (igual ao /list).</summary>
    public static List<(string Guid, string Name)>? Schemes()
    {
        try
        {
            var list = new List<(string, string)>();
            for (uint i = 0; ; i++)
            {
                var g = Guid.Empty;
                uint size = (uint)Marshal.SizeOf<Guid>();
                uint r = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, ref g, ref size);
                if (r == ErrorNoMoreItems) break;
                if (r != 0) return null;
                list.Add((g.ToString(), FriendlyName(g)));
            }
            return list.Count > 0 ? list : null;
        }
        catch { return null; }
    }

    private static string FriendlyName(Guid g)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size == 0) return "";
        var buf = new byte[size];
        if (PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0) return "";
        string name = Encoding.Unicode.GetString(buf).TrimEnd('\0');
        // Planos de fábrica guardam o nome como recurso ("@%SystemRoot%\system32\powrprof.dll,-14"): resolve na língua do Windows.
        if (name.StartsWith('@'))
        {
            var sb = new StringBuilder(256);
            if (SHLoadIndirectString(name, sb, sb.Capacity, IntPtr.Zero) == 0) name = sb.ToString();
        }
        return name;
    }

    private const uint AccessScheme = 16, ErrorNoMoreItems = 259;

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid powerSettingGuid, out uint acValueIndex);
    [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupGuid, uint accessFlags, uint index, ref Guid buffer, ref uint bufferSize);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid, IntPtr powerSettingGuid, byte[]? buffer, ref uint bufferSize);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int SHLoadIndirectString(string source, StringBuilder outBuf, int outBufSize, IntPtr reserved);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
}
