using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ThgOptimization;

/// <summary>
/// Dispositivo HID aberto pelo caminho do Windows (SetupAPI + CreateFile), com escrita e leitura com tempo limite:
/// nenhuma operação espera mais que o timeout (padrão 2 s), então um aparelho que não responde nunca trava o app.
/// </summary>
public sealed class HidDevice : IDisposable
{
    public string Path { get; }
    public ushort UsagePage { get; }
    public int InputLength { get; }
    public int OutputLength { get; }
    private readonly FileStream _fs;

    private HidDevice(string path, FileStream fs, ushort usagePage, int inLen, int outLen)
    { Path = path; _fs = fs; UsagePage = usagePage; InputLength = inLen; OutputLength = outLen; }

    /// <summary>Caminhos das interfaces HID presentes com este VID/PID.</summary>
    public static List<string> Find(ushort vid, ushort pid)
    {
        var result = new List<string>();
        HidD_GetHidGuid(out Guid guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x02 | 0x10 /* PRESENT | DEVICEINTERFACE */);
        if (set == new IntPtr(-1)) return result;
        try
        {
            string tag = $"vid_{vid:x4}&pid_{pid:x4}";
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int need, IntPtr.Zero);
                if (need <= 0 || need > 4096) continue;
                IntPtr buf = Marshal.AllocHGlobal(need);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6); // cbSize de SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buf, need, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(buf + 4) ?? "";
                    if (path.Contains(tag, StringComparison.OrdinalIgnoreCase)) result.Add(path);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    /// <summary>Abre (compartilhado) e lê as capacidades: página de uso e tamanho dos relatórios.</summary>
    public static HidDevice? Open(string path)
    {
        var h = CreateFile(path, 0xC0000000 /* READ|WRITE */, 3 /* SHARE_READ|WRITE */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x40000000 /* OVERLAPPED */, IntPtr.Zero);
        if (h.IsInvalid) return null;
        ushort page = 0; int inLen = 0, outLen = 0;
        if (HidD_GetPreparsedData(h, out IntPtr pre))
        {
            var caps = new byte[64];
            if (HidP_GetCaps(pre, caps) == 0x00110000 /* HIDP_STATUS_SUCCESS */)
            {
                page = BitConverter.ToUInt16(caps, 2);
                inLen = BitConverter.ToUInt16(caps, 4);
                outLen = BitConverter.ToUInt16(caps, 6);
            }
            HidD_FreePreparsedData(pre);
        }
        if (outLen == 0) { h.Dispose(); return null; }
        return new HidDevice(path, new FileStream(h, FileAccess.ReadWrite, 0, isAsync: true), page, inLen, outLen);
    }

    /// <summary>Escreve um relatório (o 1º byte é o ID do relatório), completado com zeros até o tamanho do aparelho.</summary>
    public void Write(ReadOnlySpan<byte> report, int timeoutMs = 2000)
    {
        var buf = new byte[OutputLength];
        report[..Math.Min(report.Length, OutputLength)].CopyTo(buf);
        using var cts = new CancellationTokenSource(timeoutMs);
        try { _fs.WriteAsync(buf, cts.Token).AsTask().GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { throw new TimeoutException("O aparelho não aceitou os dados a tempo."); }
    }

    /// <summary>Lê um relatório. Devolve null se nada chegou dentro do tempo.</summary>
    public byte[]? Read(int timeoutMs)
    {
        if (InputLength <= 0) return null;
        var buf = new byte[InputLength];
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            int n = _fs.ReadAsync(buf, cts.Token).AsTask().GetAwaiter().GetResult();
            return n > 0 ? buf : null;
        }
        catch (OperationCanceledException) { return null; }
    }

    public void Dispose() { try { _fs.Dispose(); } catch { } }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, byte[] caps);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
}
