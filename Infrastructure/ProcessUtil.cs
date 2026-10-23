using System.Diagnostics;

namespace ThgOptimization.Infrastructure;

/// <summary>
/// Consultas a processos que sempre liberam os objetos Process: cada um segura um handle do Windows até ser descartado,
/// e "GetProcessesByName(x).Length > 0" chamado a cada poucos segundos deixava handles acumulando até o próximo GC.
/// </summary>
public static class ProcessUtil
{
    /// <summary>
    /// Caminho do executável só com PROCESS_QUERY_LIMITED_INFORMATION. Process.MainModule abre com VM_READ e lê a memória
    /// do processo, o que anti-cheats (EAC, BattlEye) registram quando o alvo é o jogo. null = o Windows negou.
    /// </summary>
    public static string? ImagePath(Process p)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, p.Id);
            if (h == IntPtr.Zero) return null;
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        catch { return null; }
        finally { if (h != IntPtr.Zero) CloseHandle(h); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    /// <summary>Há algum processo com este nome (sem ".exe")?</summary>
    public static bool IsRunning(string name)
    {
        var procs = Process.GetProcessesByName(name);
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    /// <summary>Executa a ação em cada processo com este nome e descarta todos depois.</summary>
    public static void ForEach(string name, Action<Process> action)
    {
        foreach (var p in Process.GetProcessesByName(name))
            using (p)
                try { action(p); } catch { }
    }
}
