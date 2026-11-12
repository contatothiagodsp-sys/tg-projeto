namespace ThgOptimization.Infrastructure;

/// <summary>
/// Proteção de um tique de timer da janela. Cada etapa roda isolada: se uma falha, as outras continuam. A falha não some:
/// a primeira de cada sequência vai para o registro, e uma etapa que falha <see cref="PersistentAfter"/> vezes seguidas é
/// avisada uma vez (na tela, por quem criou o guarda) e registrada de novo a cada <see cref="PersistentAfter"/> falhas. Quando a
/// etapa volta a funcionar, a contagem zera. Não repete a etapa nem reinicia nada por conta própria: o próximo tique é a próxima tentativa.
/// </summary>
internal sealed class TickGuard(string area, Action<string, Exception>? onPersistent = null, Action<string, Exception>? log = null)
{
    public const int PersistentAfter = 30;

    private readonly Dictionary<string, int> _streak = new();
    private readonly Action<string, Exception> _log = log ?? ((step, ex) => AppLog.Error(area, $"{step}: {ex.GetType().Name}: {ex.Message}"));

    /// <summary>Falhas seguidas desta etapa (0 = a última rodada dela deu certo ou ela nunca falhou).</summary>
    public int Failures(string step) => _streak.GetValueOrDefault(step);

    /// <summary>Roda uma etapa síncrona. True se terminou sem exceção.</summary>
    public bool Step(string step, Action body)
    {
        try { body(); }
        catch (Exception ex) { Failed(step, ex); return false; }
        _streak.Remove(step);
        return true;
    }

    /// <summary>Roda uma etapa assíncrona (a leitura dos sensores, o tique inteiro). True se terminou sem exceção.</summary>
    public async Task<bool> StepAsync(string step, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex) { Failed(step, ex); return false; }
        _streak.Remove(step);
        return true;
    }

    private void Failed(string step, Exception ex)
    {
        int n = _streak[step] = _streak.GetValueOrDefault(step) + 1;
        if (n == 1 || n % PersistentAfter == 0) Safe(() => _log(n == 1 ? step : $"{step} (falha {n} seguida)", ex));
        if (n == PersistentAfter && onPersistent != null) Safe(() => onPersistent(step, ex));
    }

    // O aviso e o registro não podem, eles mesmos, derrubar o tique.
    private static void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }
}
