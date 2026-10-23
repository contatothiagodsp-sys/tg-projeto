using System.Diagnostics;

namespace ThgOptimization.Infrastructure;

/// <summary>
/// Consultas a processos que sempre liberam os objetos Process: cada um segura um handle do Windows até ser descartado,
/// e "GetProcessesByName(x).Length > 0" chamado a cada poucos segundos deixava handles acumulando até o próximo GC.
/// </summary>
public static class ProcessUtil
{
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
