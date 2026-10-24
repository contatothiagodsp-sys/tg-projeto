using System.Runtime.InteropServices;

namespace ThgOptimization;

/// <summary>
/// HDR por monitor (a mesma chave de Configurações → Vídeo → "Usar HDR", pela API DisplayConfig) e proteções para telas
/// OLED: esconder a barra de tarefas (imagem parada é o que marca OLED) e desligar a tela depois de alguns minutos sem uso.
/// Tudo reversível: o estado de antes fica em TweakBackup ("oled.").
/// </summary>
public static class DisplayExtras
{
    public sealed record HdrDisplay(uint AdapterLow, int AdapterHigh, uint TargetId, bool Supported, bool Enabled);

    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const int PathSize = 72, ModeSize = 64;

    public static List<HdrDisplay> HdrDisplays()
    {
        var list = new List<HdrDisplay>();
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0 || np == 0) return list;
        var paths = new byte[np * PathSize];
        var modes = new byte[nm * ModeSize];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return list;
        for (int i = 0; i < np; i++)
        {
            int o = i * PathSize + 20; // targetInfo: LUID adapterId (8) + id (4)
            uint low = BitConverter.ToUInt32(paths, o);
            int high = BitConverter.ToInt32(paths, o + 4);
            uint id = BitConverter.ToUInt32(paths, o + 8);
            var info = new byte[32];
            Header(info, 9 /* GET_ADVANCED_COLOR_INFO */, 32, low, high, id);
            if (DisplayConfigGetDeviceInfo(info) != 0) continue;
            uint v = BitConverter.ToUInt32(info, 20);
            bool supported = (v & 1) != 0, enabled = (v & 2) != 0, forceDisabled = (v & 8) != 0;
            list.Add(new HdrDisplay(low, high, id, supported && !forceDisabled, enabled));
        }
        return list;
    }

    /// <summary>Liga/desliga o HDR em todos os monitores compatíveis. Devolve quantos mudaram.</summary>
    public static int SetHdr(bool on)
    {
        int n = 0;
        foreach (var d in HdrDisplays().Where(d => d.Supported && d.Enabled != on))
        {
            var set = new byte[24];
            Header(set, 10 /* SET_ADVANCED_COLOR_STATE */, 24, d.AdapterLow, d.AdapterHigh, d.TargetId);
            BitConverter.GetBytes(on ? 1u : 0u).CopyTo(set, 20);
            if (DisplayConfigSetDeviceInfo(set) == 0) n++;
        }
        return n;
    }

    private static void Header(byte[] b, int type, int size, uint low, int high, uint id)
    {
        BitConverter.GetBytes(type).CopyTo(b, 0);
        BitConverter.GetBytes(size).CopyTo(b, 4);
        BitConverter.GetBytes(low).CopyTo(b, 8);
        BitConverter.GetBytes(high).CopyTo(b, 12);
        BitConverter.GetBytes(id).CopyTo(b, 16);
    }

    // ------------------------------------------------------------------ OLED: barra de tarefas escondida

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public int l, t, r, b; public IntPtr lParam; }

    public static bool TaskbarAutoHide
    {
        get
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindow("Shell_TrayWnd", null) };
            return ((long)SHAppBarMessage(4 /* GETSTATE */, ref d) & 1) != 0;
        }
    }

    public static void SetTaskbarAutoHide(bool on, AppSettings s)
    {
        if (!s.TweakBackup.ContainsKey("oled.autohide")) s.TweakBackup["oled.autohide"] = TaskbarAutoHide ? "1" : "0";
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindow("Shell_TrayWnd", null), lParam = (IntPtr)(on ? 1 : 2 /* ALWAYSONTOP */) };
        SHAppBarMessage(10 /* SETSTATE */, ref d);
    }

    // ------------------------------------------------------------------ OLED: desligar a tela sem uso

    private const string SubVideo = "7516b95f-f776-4464-8c53-06167f40cc99", VideoIdle = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";

    /// <summary>Minutos até desligar a tela (na tomada), ou 0 = nunca.</summary>
    public static int ScreenOffMinutes()
    {
        if (Infrastructure.PowerApi.ReadAc(null, SubVideo, VideoIdle) is int seconds) return seconds / 60;
        var (_, o) = SecurityAudit.Run("powercfg.exe", $"/qh scheme_current {SubVideo} {VideoIdle}", 10_000);
        var hex = System.Text.RegularExpressions.Regex.Matches(o, @"0x([0-9a-fA-F]{8})");
        return hex.Count >= 2 ? Convert.ToInt32(hex[^2].Groups[1].Value, 16) / 60 : -1;
    }

    public static void SetScreenOff(int minutes, AppSettings s)
    {
        if (!s.TweakBackup.ContainsKey("oled.screenoff") && ScreenOffMinutes() is int cur && cur >= 0) s.TweakBackup["oled.screenoff"] = cur.ToString();
        SecurityAudit.Run("powercfg.exe", $"/setacvalueindex scheme_current {SubVideo} {VideoIdle} {Math.Clamp(minutes, 0, 600) * 60}", 10_000);
        SecurityAudit.Run("powercfg.exe", "/setactive scheme_current", 10_000);
    }

    public static void RestoreOled(AppSettings s)
    {
        if (s.TweakBackup.GetValueOrDefault("oled.autohide") is string a)
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindow("Shell_TrayWnd", null), lParam = (IntPtr)(a == "1" ? 1 : 2) };
            SHAppBarMessage(10, ref d);
        }
        if (s.TweakBackup.GetValueOrDefault("oled.screenoff") is string m && int.TryParse(m, out int min))
        {
            SecurityAudit.Run("powercfg.exe", $"/setacvalueindex scheme_current {SubVideo} {VideoIdle} {Math.Clamp(min, 0, 600) * 60}", 10_000);
            SecurityAudit.Run("powercfg.exe", "/setactive scheme_current", 10_000);
        }
        foreach (var k in s.TweakBackup.Keys.Where(k => k.StartsWith("oled.")).ToList()) s.TweakBackup.Remove(k);
        s.Save();
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, byte[] paths, ref uint numModes, byte[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(byte[] packet);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(byte[] packet);
    [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
}
