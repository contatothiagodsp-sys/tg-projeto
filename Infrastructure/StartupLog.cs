using System.Diagnostics;

namespace ThgOptimization.Infrastructure;

/// <summary>
/// Tempos da abertura, contados desde o início do processo. Marcar só guarda na memória: no começo da abertura a pasta protegida ainda não foi conferida,
/// e registrar ali criaria essa pasta antes da hora. O registro vai para o disco de uma vez, em <see cref="Flush"/>.
/// </summary>
public static class StartupLog
{
    private static readonly object Gate = new();
    private static readonly List<(string Stage, long Ms)> Marks = new();
    private static bool _flushed;

    private static long SinceProcessStart()
    {
        using var p = Process.GetCurrentProcess();
        return (long)(DateTime.Now - p.StartTime).TotalMilliseconds;
    }

    public static void Mark(string stage)
    {
        long ms = SinceProcessStart();
        bool late;
        lock (Gate)
        {
            Marks.Add((stage, ms));
            late = _flushed;
        }
        if (late) AppLog.Info("startup", $"{stage}: {ms} ms");
    }

    public static IReadOnlyList<(string Stage, long Ms)> Entries { get { lock (Gate) return Marks.ToArray(); } }

    public static string Summary => string.Join(" · ", Entries.Select(e => $"{e.Stage}: {e.Ms} ms"));

    /// <summary>Grava no registro o que ficou guardado. Depois disso, cada marca vai direto.</summary>
    public static void Flush()
    {
        (string Stage, long Ms)[] pending;
        lock (Gate)
        {
            if (_flushed) return;
            _flushed = true;
            pending = Marks.ToArray();
        }
        foreach (var (stage, ms) in pending) AppLog.Info("startup", $"{stage}: {ms} ms");
    }

    /// <summary>Só para os testes.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            Marks.Clear();
            _flushed = false;
        }
    }
}
