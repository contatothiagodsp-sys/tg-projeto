using System.Runtime.InteropServices;

namespace ThgOptimization;

/// <summary>Um monitor ligado à área de trabalho: taxa de atualização atual e a maior que ele aceita nesta resolução.</summary>
public sealed record DisplayState(string Device, string Monitor, string Adapter, bool Primary,
                                  int Width, int Height, int CurrentHz, int MaxHz)
{
    public bool BelowMax => MaxHz > CurrentHz + 1; // 59/60 Hz e 143/144 Hz contam como iguais
    public string Label => $"{(Monitor.Length > 0 ? Monitor : Device)} ({Width}×{Height})";
}

/// <summary>
/// Monitores e taxa de atualização (Hz). O erro mais comum em PC gamer: monitor de 144/165/240 Hz rodando em 60 Hz
/// porque ninguém mudou a configuração do Windows. Usa EnumDisplaySettings/ChangeDisplaySettingsEx, que só listam modos
/// que o monitor e o cabo aceitam.
/// </summary>
public static class DisplayTuner
{
    public static List<DisplayState> List()
    {
        var result = new List<DisplayState>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>())
        {
            if ((dd.StateFlags & AttachedToDesktop) == 0) continue;
            string device = dd.DeviceName, adapter = dd.DeviceString;
            bool primary = (dd.StateFlags & PrimaryDevice) != 0;

            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            string monitor = EnumDisplayDevices(device, 0, ref mon, 0) ? mon.DeviceString : "";
            if (monitor is "Generic PnP Monitor" or "Monitor PnP genérico") monitor = "";

            var cur = NewMode();
            if (!EnumDisplaySettings(device, EnumCurrentSettings, ref cur)) continue;

            int max = (int)cur.dmDisplayFrequency;
            var m = NewMode();
            for (int n = 0; EnumDisplaySettings(device, n, ref m); n++, m = NewMode())
            {
                if (m.dmPelsWidth != cur.dmPelsWidth || m.dmPelsHeight != cur.dmPelsHeight) continue;
                if (m.dmBitsPerPel != cur.dmBitsPerPel || (m.dmDisplayFlags & Interlaced) != 0) continue;
                max = Math.Max(max, (int)m.dmDisplayFrequency);
            }
            result.Add(new DisplayState(device, monitor, adapter, primary, (int)cur.dmPelsWidth, (int)cur.dmPelsHeight,
                                        (int)cur.dmDisplayFrequency, max));
        }
        return result.OrderByDescending(d => d.Primary).ToList();
    }

    /// <summary>Coloca o monitor na maior taxa de atualização da resolução atual. Guarda a anterior para desfazer.</summary>
    public static string SetMaxRefresh(DisplayState d, AppSettings s)
    {
        string bk = $"display.{d.Device}";
        if (!s.TweakBackup.ContainsKey(bk)) s.TweakBackup[bk] = d.CurrentHz.ToString();
        return SetRefresh(d.Device, d.MaxHz);
    }

    /// <summary>Volta a taxa de atualização guardada antes de o app mudar.</summary>
    public static void RestoreAll(AppSettings s)
    {
        foreach (var (key, prev) in s.TweakBackup.Where(kv => kv.Key.StartsWith("display.")).ToList())
            if (int.TryParse(prev, out int hz)) SetRefresh(key["display.".Length..], hz);
    }

    private static string SetRefresh(string device, int hz)
    {
        var dm = NewMode();
        if (!EnumDisplaySettings(device, EnumCurrentSettings, ref dm)) return "Não foi possível ler o modo atual do monitor.";
        dm.dmDisplayFrequency = (uint)hz;
        dm.dmFields = DmDisplayFrequency;

        int test = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CdsTest, IntPtr.Zero);
        if (test != 0) return $"O monitor não aceitou {hz} Hz (código {test}).";
        int r = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
        return r switch
        {
            0 => "",
            1 => $"{hz} Hz gravado; vale depois de reiniciar.",
            _ => $"O Windows recusou {hz} Hz (código {r}).",
        };
    }

    private static DEVMODE NewMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

    // ------------------------------------------------------------------ Win32

    private const int EnumCurrentSettings = -1;
    private const int AttachedToDesktop = 0x1, PrimaryDevice = 0x4;
    private const uint Interlaced = 0x2;
    private const uint DmDisplayFrequency = 0x400000;
    private const uint CdsUpdateRegistry = 0x1, CdsTest = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string device, int modeNum, ref DEVMODE dm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
}
