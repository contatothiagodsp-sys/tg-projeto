namespace ThgOptimization.Core.Intelligence;

/// <summary>Classe de risco de uma ação. Não existe classe "silenciosa": tudo que altera algo passa pelo executor e fica no diário.</summary>
public enum RiskClass
{
    /// <summary>Somente leitura.</summary>
    A,
    /// <summary>Ajuste simples e reversível, com estado anterior guardado.</summary>
    B,
    /// <summary>Ajuste avançado: exige recomendação, explicação e autorização explícita a cada vez.</summary>
    C,
    /// <summary>Hardware sensível (clock, tensão, limites de potência). O executor nunca aplica.</summary>
    D,
}

/// <summary>Quanto o tHg pode fazer sozinho. O padrão é recomendar e esperar o usuário.</summary>
public enum AutomationLevel
{
    /// <summary>Só diagnóstico: nenhuma ação é aplicada pelo motor, nem com clique.</summary>
    DiagnosisOnly,
    /// <summary>Diagnostica e recomenda; cada ação é aplicada pelo usuário.</summary>
    Recommend,
    /// <summary>Além disso, aplica sozinho as ações de classe B que o usuário pré-aprovou na lista.</summary>
    SafeAuto,
}

/// <summary>Onde a ação vale: global ou em um jogo (chave do perfil e executável).</summary>
public sealed record ActionScope(string? GameKey = null, string? GameName = null, string? ExePath = null)
{
    public static readonly ActionScope Global = new();
    public string Label => GameName ?? "todo o PC";
}

/// <summary>
/// O contrato de uma otimização. É uma entrada de catálogo fechado: o motor só referencia o Id. Não há linha de comando,
/// caminho ou valor vindo do diagnóstico; o que a ação altera está fixo no alvo dela.
/// </summary>
public sealed record ActionSpec(string Id, string Name, string Description, string Goal, RiskClass Risk)
{
    public bool Reversible { get; init; } = true;
    public bool NeedsRestart { get; init; }
    public bool PerGame { get; init; }
    /// <summary>O que é alterado, em palavras (qual configuração, onde).</summary>
    public string Changes { get; init; } = "";
    public string HowToUndo { get; init; } = "Desfazer no histórico do tHg Intelligence devolve o valor anterior.";
    public string HowToMeasure { get; init; } = "Compare as próximas sessões deste jogo com a referência anterior.";
    public string Compatibility { get; init; } = "";
    public ConfidenceLevel MinConfidence { get; init; } = ConfidenceLevel.Medium;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Diagnósticos que justificam a ação. Sem um deles com a confiança mínima, ela não é recomendada.</summary>
    public IReadOnlyList<DiagnosisKind> Evidence { get; init; } = Array.Empty<DiagnosisKind>();
    /// <summary>Métricas usadas para avaliar o resultado.</summary>
    public IReadOnlyList<string> Metrics { get; init; } = new[] { "1% low", "frametime P99", "engasgos por minuto" };
}

/// <summary>
/// O alvo de uma ação: lê o estado, diz o estado desejado e escreve um estado. O estado é um texto curto ("on", "off", um GUID).
/// A implementação usa os mecanismos que o app já tem (configurações, backup de ajustes), sem um segundo sistema de backup.
/// </summary>
public interface IActionTarget
{
    /// <summary>Motivo de não se aplicar neste PC/jogo, ou null.</summary>
    string? Incompatibility(ActionScope scope);
    /// <summary>Estado atual. Null = não foi possível ler (a ação não segue).</summary>
    string? Read(ActionScope scope);
    string Desired(ActionScope scope);
    void Write(ActionScope scope, string state, CancellationToken ct);
}

public enum ActionStatus
{
    Applied, AlreadyEquivalent, Refused, Incompatible, Failed, RolledBack, RollbackFailed, Cancelled,
    Reverted, Conflict, NothingToRevert,
}

/// <summary>Uma linha do diário: o que foi mudado, de quanto para quanto, por quê e o que aconteceu depois.</summary>
public sealed class ActionRecord
{
    public string EntryId { get; set; } = "";
    public string ActionId { get; set; } = "";
    public string ActionName { get; set; } = "";
    public string? GameKey { get; set; }
    public string? GameName { get; set; }
    public string? ExePath { get; set; }
    public DateTime At { get; set; }
    public string? Before { get; set; }
    public string? Applied { get; set; }
    public ActionStatus Status { get; set; }
    public string Detail { get; set; } = "";
    /// <summary>Por que foi aplicada (diagnóstico que justificou).</summary>
    public string Reason { get; set; } = "";
    public bool Automatic { get; set; }
    /// <summary>Sessão de jogo a que a ação pertence, quando é temporária.</summary>
    public string? SessionId { get; set; }
    /// <summary>Resultado da medição posterior: "kept", "reverted-regression", "reverted-no-benefit" ou null (ainda medindo).</summary>
    public string? Evaluation { get; set; }
    public string? EvaluationText { get; set; }
    /// <summary>Assinatura das condições em que foi aplicada: a medição só usa sessões comparáveis.</summary>
    public string? Signature { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive => Status == ActionStatus.Applied;
    [System.Text.Json.Serialization.JsonIgnore]
    public ActionScope Scope => new(GameKey, GameName, ExePath);
}

public sealed record ActionResult(ActionStatus Status, string Message, ActionRecord? Record = null)
{
    public bool Ok => Status is ActionStatus.Applied or ActionStatus.AlreadyEquivalent or ActionStatus.Reverted;
}

/// <summary>Quem autoriza o quê. O executor consulta antes de qualquer escrita.</summary>
public sealed record AutomationPolicy(AutomationLevel Level, IReadOnlySet<string> PreApproved)
{
    public static readonly AutomationPolicy Default = new(AutomationLevel.Recommend, new HashSet<string>());

    /// <summary>Motivo da recusa, ou null se pode seguir.</summary>
    public string? Refusal(ActionSpec spec, bool userApprovedNow)
    {
        if (spec.Risk == RiskClass.D) return "Hardware sensível: o tHg Intelligence não aplica este tipo de ajuste.";
        if (spec.Risk == RiskClass.A) return null;
        if (Level == AutomationLevel.DiagnosisOnly) return "A automação está desligada (somente diagnóstico).";
        if (userApprovedNow) return null;
        if (spec.Risk == RiskClass.C) return "Ajuste avançado: precisa da sua autorização explícita.";
        if (Level != AutomationLevel.SafeAuto) return "Aguardando a sua autorização.";
        return PreApproved.Contains(spec.Id) ? null : "Esta ação não está na sua lista de ações automáticas aprovadas.";
    }
}

/// <summary>
/// Aplica e desfaz ações de forma transacional: guarda o estado anterior, confere o resultado, desfaz se a confirmação falhar e,
/// ao reverter, não sobrescreve um valor que outro programa (ou o usuário) mudou depois. Catálogo fechado: Id desconhecido é recusado.
/// </summary>
public sealed class ActionExecutor
{
    private readonly IReadOnlyDictionary<string, (ActionSpec Spec, IActionTarget Target)> _catalog;
    private readonly Func<DateTime> _now;
    private readonly Action<ActionRecord> _journal;
    private readonly object _gate = new();

    public ActionExecutor(IEnumerable<(ActionSpec Spec, IActionTarget Target)> catalog, Action<ActionRecord> journal, Func<DateTime>? now = null)
    {
        _catalog = catalog.ToDictionary(c => c.Spec.Id, c => c, StringComparer.Ordinal);
        _journal = journal;
        _now = now ?? (() => DateTime.Now);
    }

    public IEnumerable<ActionSpec> Specs => _catalog.Values.Select(c => c.Spec);
    public ActionSpec? Spec(string id) => _catalog.TryGetValue(id, out var c) ? c.Spec : null;

    /// <summary>A ação se aplica aqui e ainda não está no estado desejado? Para a recomendação não oferecer o que já existe.</summary>
    public (bool Applicable, string? Why) Check(string actionId, ActionScope scope)
    {
        if (!_catalog.TryGetValue(actionId, out var c)) return (false, "Ação desconhecida.");
        try
        {
            if (c.Target.Incompatibility(scope) is string no) return (false, no);
            string? now = c.Target.Read(scope);
            if (now == null) return (false, "Não foi possível ler a configuração atual.");
            return now == c.Target.Desired(scope) ? (false, "Você já usa uma configuração equivalente.") : (true, null);
        }
        catch (Exception ex) { return (false, "Não foi possível verificar: " + ex.Message); }
    }

    public ActionResult Apply(string actionId, ActionScope scope, AutomationPolicy policy, bool userApprovedNow, string reason,
                              string? sessionId = null, string? signature = null, CancellationToken ct = default)
    {
        if (!_catalog.TryGetValue(actionId, out var c)) return new ActionResult(ActionStatus.Refused, "Ação fora do catálogo: recusada.");
        var (spec, target) = c;
        if (policy.Refusal(spec, userApprovedNow) is string refusal) return new ActionResult(ActionStatus.Refused, refusal);
        if (spec.PerGame && scope.GameKey == null) return new ActionResult(ActionStatus.Refused, "Esta ação vale para um jogo: escolha o jogo.");

        lock (_gate)
        {
            string? before;
            string desired;
            try
            {
                if (target.Incompatibility(scope) is string no) return new ActionResult(ActionStatus.Incompatible, no);
                before = target.Read(scope);
                desired = target.Desired(scope);
            }
            catch (Exception ex) { return new ActionResult(ActionStatus.Failed, "Não foi possível ler o estado atual: " + ex.Message); }
            if (before == null) return new ActionResult(ActionStatus.Failed, "Não foi possível ler o estado atual: nada foi alterado.");
            if (before == desired) return new ActionResult(ActionStatus.AlreadyEquivalent, "Você já usa uma configuração equivalente: nada foi alterado.");
            if (ct.IsCancellationRequested) return new ActionResult(ActionStatus.Cancelled, "Cancelado antes de alterar qualquer coisa.");

            var rec = new ActionRecord
            {
                EntryId = Guid.NewGuid().ToString("N"), ActionId = spec.Id, ActionName = spec.Name, GameKey = scope.GameKey, GameName = scope.GameName,
                ExePath = scope.ExePath, At = _now(), Before = before, Reason = reason, Automatic = !userApprovedNow, SessionId = sessionId, Signature = signature,
            };

            string? failure = Write(target, scope, desired, spec.Timeout, ct);
            string? after = failure == null ? SafeRead(target, scope) : null;
            if (failure == null && after == desired)
            {
                rec.Applied = after;
                rec.Status = ActionStatus.Applied;
                rec.Detail = $"{before} → {after}";
                _journal(rec);
                return new ActionResult(ActionStatus.Applied, $"{spec.Name}: aplicado ({rec.Detail}).", rec);
            }

            // Não confirmou: devolve o estado anterior e conta a verdade.
            failure ??= $"o estado depois da escrita é \"{after ?? "ilegível"}\", e o esperado era \"{desired}\"";
            string? undo = Write(target, scope, before, spec.Timeout, CancellationToken.None);
            bool restored = undo == null && SafeRead(target, scope) == before;
            rec.Status = ct.IsCancellationRequested && restored ? ActionStatus.Cancelled : restored ? ActionStatus.RolledBack : ActionStatus.RollbackFailed;
            rec.Detail = restored ? $"Falhou ({failure}); o estado anterior ({before}) foi restaurado."
                                  : $"Falhou ({failure}) e a restauração também ({undo ?? "estado não confirmado"}). Valor anterior: {before}.";
            _journal(rec);
            return new ActionResult(rec.Status, $"{spec.Name}: {rec.Detail}", rec);
        }
    }

    /// <summary>Desfaz uma ação aplicada. Se o valor atual não é o que o tHg deixou, alguém mudou depois: não sobrescreve.</summary>
    public ActionResult Revert(ActionRecord rec, string why = "")
    {
        if (!_catalog.TryGetValue(rec.ActionId, out var c)) return new ActionResult(ActionStatus.Refused, "Ação fora do catálogo: recusada.");
        if (!rec.IsActive || rec.Before == null) return new ActionResult(ActionStatus.NothingToRevert, "Esta ação não está ativa.");
        lock (_gate)
        {
            var scope = rec.Scope;
            string? current = SafeRead(c.Target, scope);
            if (current == rec.Before)
            {
                rec.Status = ActionStatus.Reverted;
                rec.Detail = "O valor já estava no estado anterior. " + why;
                _journal(rec);
                return new ActionResult(ActionStatus.Reverted, $"{rec.ActionName}: já estava desfeito.", rec);
            }
            if (current != rec.Applied)
            {
                rec.Status = ActionStatus.Conflict;
                rec.Detail = $"Valor atual \"{current ?? "ilegível"}\" difere do que o tHg aplicou (\"{rec.Applied}\"): alterado por fora. Não foi sobrescrito. Valor original: {rec.Before}.";
                _journal(rec);
                return new ActionResult(ActionStatus.Conflict, $"{rec.ActionName}: {rec.Detail}", rec);
            }
            string? failure = Write(c.Target, scope, rec.Before, c.Spec.Timeout, CancellationToken.None);
            if (failure == null && SafeRead(c.Target, scope) == rec.Before)
            {
                rec.Status = ActionStatus.Reverted;
                rec.Detail = $"{rec.Applied} → {rec.Before}. " + why;
                _journal(rec);
                return new ActionResult(ActionStatus.Reverted, $"{rec.ActionName}: desfeito.", rec);
            }
            rec.Status = ActionStatus.RollbackFailed;
            rec.Detail = $"Não foi possível desfazer ({failure ?? "estado não confirmado"}). Valor original: {rec.Before}.";
            _journal(rec);
            return new ActionResult(ActionStatus.RollbackFailed, $"{rec.ActionName}: {rec.Detail}", rec);
        }
    }

    private static string? SafeRead(IActionTarget target, ActionScope scope)
    {
        try { return target.Read(scope); }
        catch (Exception) { return null; }
    }

    /// <summary>Escreve com limite de tempo. Devolve o motivo da falha, ou null.</summary>
    private static string? Write(IActionTarget target, ActionScope scope, string state, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var task = Task.Run(() => target.Write(scope, state, cts.Token), CancellationToken.None);
            if (!task.Wait(timeout))
            {
                cts.Cancel();
                return $"tempo esgotado ({timeout.TotalSeconds:0} s)";
            }
            return ct.IsCancellationRequested ? "cancelado" : null;
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException) { return "cancelado"; }
        catch (AggregateException ex) { return ex.InnerException?.Message ?? ex.Message; }
    }
}
