using System.Windows.Threading;

namespace ThgOptimization.Infrastructure;

/// <summary>
/// Lista central dos timers do app, para responder "o que ele está fazendo agora". Só descreve: quem liga e desliga cada timer continua sendo o dono dele.
/// Todo DispatcherTimer da janela passa por aqui, e um teste confere.
/// </summary>
public static class TimerRegistry
{
    /// <param name="Essential">Não entra em "pausar tudo que não for essencial": sensores, curvas de fan, Modo Jogo.</param>
    public sealed record Entry(string Name, string Area, TimeSpan Interval, bool Repeating, Func<bool>? IsActive, bool Essential = true)
    {
        internal Func<TimeSpan>? IntervalNow { get; init; }
        internal Action? Pause { get; init; }
        internal Action? Resume { get; init; }

        /// <summary>Consulta segura: quem lê é um painel, e um timer que já foi embora não pode derrubá-lo.</summary>
        public bool Running
        {
            get
            {
                try { return IsActive?.Invoke() ?? true; }
                catch (Exception ex) { AppLog.Debug("timers", ex); return false; }
            }
        }

        public string IntervalText => Interval.TotalSeconds >= 1 ? $"{Interval.TotalSeconds:0.#} s" : $"{Interval.TotalMilliseconds:0} ms";
        public string StateText => Running ? "Ativo" : "Parado";
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new();
    /// <summary>Quem "pausar tudo" parou: "retomar" religa estes e nada além deles.</summary>
    private static readonly List<Entry> Paused = new();

    public static void Register(string name, string area, TimeSpan interval, bool repeating, Func<bool>? isActive = null) =>
        Add(new Entry(name, area, interval, repeating, isActive));

    /// <summary>Timer com o ritmo que muda em uso, como o principal: 1 s na janela e 5 s na bandeja.</summary>
    public static void Register(string name, string area, Func<TimeSpan> intervalNow, Func<bool> isActive) =>
        Add(new Entry(name, area, TimeSpan.Zero, true, isActive) { IntervalNow = intervalNow });

    /// <summary>Timer que "pausar tudo" pode parar.</summary>
    public static void RegisterPausable(string name, string area, TimeSpan interval, Func<bool> isActive, Action pause, Action resume) =>
        Add(new Entry(name, area, interval, true, isActive, Essential: false) { Pause = pause, Resume = resume });

    /// <summary>Timer curto de uma tarefa, como o progresso de um benchmark. Sai da lista com <see cref="Unregister"/>.</summary>
    public static void Track(string name, string area, DispatcherTimer timer) =>
        Add(new Entry(name, area, timer.Interval, true, () => timer.IsEnabled));

    private static void Add(Entry entry)
    {
        lock (Gate) Entries[entry.Name] = entry;
    }

    public static void Unregister(string name)
    {
        lock (Gate)
        {
            if (Entries.Remove(name, out var gone)) Paused.Remove(gone);
        }
    }

    /// <summary>Cópia ordenada por área, que pode ser lida de qualquer thread enquanto a lista muda.</summary>
    public static IReadOnlyList<Entry> Snapshot()
    {
        Entry[] copy;
        lock (Gate) copy = Entries.Values.ToArray();
        return copy.Select(e => e.IntervalNow is { } now ? e with { Interval = SafeInterval(now, e.Interval) } : e)
                   .OrderBy(e => e.Area, StringComparer.CurrentCulture).ThenBy(e => e.Name, StringComparer.CurrentCulture).ToList();
    }

    private static TimeSpan SafeInterval(Func<TimeSpan> now, TimeSpan fallback)
    {
        try { return now(); }
        catch (Exception ex) { AppLog.Debug("timers", ex); return fallback; }
    }

    /// <summary>Para o que não é essencial e está ativo. Devolve quantos parou. Chamado da janela, como os timers.</summary>
    public static int PauseNonEssential()
    {
        Entry[] candidates;
        lock (Gate) candidates = Entries.Values.Where(e => !e.Essential && e.Pause != null).ToArray();
        int stopped = 0;
        foreach (var entry in candidates)
        {
            if (!entry.Running) continue;
            entry.Pause!();
            lock (Gate) Paused.Add(entry);
            stopped++;
        }
        return stopped;
    }

    /// <summary>Devolve só o que <see cref="PauseNonEssential"/> parou: o que já estava parado continua assim.</summary>
    public static int ResumePaused()
    {
        Entry[] paused;
        lock (Gate)
        {
            paused = Paused.ToArray();
            Paused.Clear();
        }
        foreach (var entry in paused) entry.Resume?.Invoke();
        return paused.Length;
    }

    public static bool AnyPaused { get { lock (Gate) return Paused.Count > 0; } }

    /// <summary>Só para os testes.</summary>
    internal static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
            Paused.Clear();
        }
    }
}
