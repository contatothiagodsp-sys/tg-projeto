using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace ThgOptimization.Tests;

/// <summary>Processo que só espera, com a mesma conta do teste. Morre no Dispose.</summary>
internal sealed class Child : IDisposable
{
    public Process P { get; } = Process.Start(new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1")
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
    public int Id => P.Id;
    /// <summary>Process guarda a prioridade na primeira leitura: sem Refresh, a mudança feita por outro objeto não aparece.</summary>
    public ProcessPriorityClass Priority { get { P.Refresh(); return P.PriorityClass; } }
    public void Dispose() { try { P.Kill(); P.WaitForExit(5000); } catch { } P.Dispose(); }
}

internal static class FirstChance
{
    /// <summary>Win32Exception na thread do teste: o xUnit roda outras classes em paralelo, e uma exceção delas não conta.</summary>
    public static int CountWin32(Action action)
    {
        int thread = Environment.CurrentManagedThreadId, count = 0;
        EventHandler<FirstChanceExceptionEventArgs> handler = (_, e) =>
        {
            if (e.Exception is Win32Exception && Environment.CurrentManagedThreadId == thread) count++;
        };
        AppDomain.CurrentDomain.FirstChanceException += handler;
        try { action(); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= handler; }
        return count;
    }
}
