using System.Globalization;
using System.Text;

namespace ThgOptimization.Core.Intelligence;

/// <summary>Tudo que saiu da análise de uma sessão.</summary>
public sealed record SessionAnalysis(DiagnosisReport Report, SessionSummary Summary, LearningOutcome Learning,
                                     IReadOnlyList<Recommendation> Recommendations, IReadOnlyList<string> ActionNotes, string ReportText);

/// <summary>
/// O caminho inteiro de uma sessão: diagnosticar → comparar com o habitual → aprender → medir as ações em andamento (manter ou desfazer)
/// → recomendar. É a única classe que junta as peças; cada peça continua testável sozinha.
/// </summary>
public sealed class IntelligenceOrchestrator
{
    private readonly DiagnosisEngine _diagnosis;
    private readonly LearningEngine _learning = new();
    private readonly RecommendationEngine _recommend;
    private readonly ActionExecutor _executor;
    private readonly IntelligenceStore _store;
    private readonly Func<AutomationPolicy> _policy;

    public IntelligenceOrchestrator(IntelligenceStore store, ActionExecutor executor, Func<AutomationPolicy> policy, DiagnosisEngine? diagnosis = null)
    {
        _store = store;
        _executor = executor;
        _policy = policy;
        _diagnosis = diagnosis ?? new DiagnosisEngine();
        _recommend = new RecommendationEngine(executor);
    }

    public IntelligenceStore Store => _store;
    public ActionExecutor Executor => _executor;

    /// <summary>Só o diagnóstico e as recomendações, sem aprender nem gravar: para reabrir uma sessão antiga.</summary>
    public (DiagnosisReport Report, IReadOnlyList<Recommendation> Recommendations) Inspect(SessionTelemetry session, MachineProfile? machine)
    {
        var report = _diagnosis.Diagnose(session);
        var profile = _store.Game(session.Context.GameKey, session.Context.Game);
        var scope = new ActionScope(profile.Key, profile.Name, profile.Exe);
        return (report, _recommend.Recommend(report, machine, GamePreset.Parse(profile.Preset), scope, profile.Dismissed.ToHashSet()));
    }

    public SessionAnalysis Analyze(SessionTelemetry session, MachineProfile? machine)
    {
        var report = _diagnosis.Diagnose(session);
        var ctx = session.Context;
        var profile = _store.Game(ctx.GameKey, ctx.Game);
        var summary = SessionSummary.From(session, report);
        var notes = new List<string>();
        LearningOutcome learning = null!;
        IReadOnlyList<Recommendation> recs = Array.Empty<Recommendation>();
        string text = "";

        _store.Mutate(data =>
        {
            report = WithDriverRegression(report, profile, summary);
            learning = _learning.Learn(profile.Baselines, summary);
            var stored = learning.Kind == LearningOutcomeKind.OutlierHeld ? summary with { Outlier = true } : summary;
            profile.History.Insert(0, stored);
            if (learning.Kind == LearningOutcomeKind.RegimeChange && profile.Baselines.FirstOrDefault(x => x.Signature == summary.Signature) is { } rebuilt)
            {
                // As sessões que estavam retidas viraram a referência: deixam de ser "anormais" também no histórico (e voltam a contar nas medições).
                var accepted = rebuilt.Sessions.Select(x => x.Start).ToHashSet();
                for (int i = 0; i < profile.History.Count; i++)
                    if (profile.History[i].Outlier && accepted.Contains(profile.History[i].Start)) profile.History[i] = profile.History[i] with { Outlier = false };
            }
            profile.LastPlayed = ctx.Start;

            EvaluateActions(data, profile, notes);

            var scope = new ActionScope(profile.Key, profile.Name, profile.Exe);
            var preset = GamePreset.Parse(profile.Preset);
            recs = _recommend.Recommend(report, machine, preset, scope, profile.Dismissed.ToHashSet());
            recs = AutoApply(recs, scope, summary.Signature, notes);

            text = SessionReport.Text(report, learning, recs, notes);
            data.History.Insert(0, new DiagnosisEntry
            {
                At = ctx.Start, GameKey = profile.Key, GameName = profile.Name, Minutes = summary.Minutes, Headline = report.Headline,
                Findings = report.Findings.Select(f => new FindingEntry { Kind = f.Kind.ToString(), Title = f.Title, Severity = f.Severity.ToString(), Confidence = f.Confidence.Score }).ToList(),
                Learning = learning.Message, Recommendations = recs.Select(r => r.Title).ToList(), Report = text,
            });
        });
        return new SessionAnalysis(report, summary, learning, recs, notes, text);
    }

    /// <summary>
    /// Piora em relação à referência de outro driver, com o resto igual: registra como possível regressão de driver. É coincidência no
    /// tempo, e o texto diz isso. Só vale quando a única diferença entre as condições é o driver.
    /// </summary>
    private static DiagnosisReport WithDriverRegression(DiagnosisReport report, GameIntelProfile profile, SessionSummary s)
    {
        if (s.GpuDriver == null || profile.Baselines.Any(b => b.Signature == s.Signature)) return report;
        string[] now = s.Signature.Split('|');
        foreach (var b in profile.Baselines.Where(b => b.Established))
        {
            string[] then = b.Signature.Split('|');
            if (then.Length != now.Length || now.Length < 4) continue;
            if (then[0] != now[0] || then[2] != now[2] || then[3] != now[3] || then[1] == now[1] || then[1] == "?") continue;
            var worse = LearningEngine.Compare(b, s).Where(d => d.Worse).ToList();
            if (worse.Count == 0) continue;
            var finding = new Finding(DiagnosisKind.Driver, Severity.Attention, "Piora depois da troca do driver de vídeo",
                $"Esta é a primeira sessão com o driver {now[1]}, e ela ficou abaixo do habitual com o driver {then[1]}, nas mesmas condições. " +
                "Uma sessão não prova que o driver é a causa: se a piora se repetir, vale testar a versão anterior.",
                new Confidence(0.45, new[] { "uma única sessão com o driver novo", "comparação com a referência do driver anterior" }),
                worse.Select(d => new Evidence(d.Metric, LearningEngine.Describe(d), "abaixo do habitual com o driver anterior")).ToList(),
                new[] { "Coincidência no tempo: um patch do jogo no mesmo período produz o mesmo efeito." });
            return report with { Findings = report.Findings.Append(finding).ToList() };
        }
        return report;
    }

    /// <summary>Mede cada ação ativa deste jogo contra as sessões anteriores a ela. Melhora comprovada mantém; regressão ou nenhum benefício desfazem.</summary>
    private void EvaluateActions(IntelligenceData data, GameIntelProfile profile, List<string> notes)
    {
        foreach (var rec in data.Journal.Where(r => r.IsActive && r.Evaluation == null && r.GameKey == profile.Key).ToList())
        {
            // Só sessões aceitas e nas mesmas condições da aplicação: uma sessão anormal não decide o destino de um ajuste.
            var comparable = profile.History.Where(h => !h.Outlier && h.Minutes >= LearningEngine.MinMinutes && (rec.Signature == null || h.Signature == rec.Signature)).ToList();
            var before = comparable.Where(h => h.Start < rec.At).Take(5).ToList();
            var after = comparable.Where(h => h.Start >= rec.At).Take(5).ToList();
            var result = ExperimentEvaluator.Evaluate(before, after);
            if (result.Verdict is ExperimentVerdict.Inconclusive or ExperimentVerdict.Invalid) continue;

            string effects = string.Join(" · ", result.Effects.Select(e => e.Line));
            if (result.KeepChange)
            {
                rec.Evaluation = "kept";
                rec.EvaluationText = $"Testada em {after.Count} sessões comparáveis. {result.Summary} A alteração foi mantida. {effects}";
                notes.Add($"{rec.ActionName}: {rec.EvaluationText}");
                continue;
            }
            bool regression = result.Verdict == ExperimentVerdict.Regression;
            var undo = _executor.Revert(rec, regression ? "Desfeita automaticamente por regressão medida." : "Desfeita por não trazer benefício mensurável.");
            rec.Evaluation = undo.Status switch
            {
                ActionStatus.Reverted => regression ? "reverted-regression" : "reverted-no-benefit",
                ActionStatus.Conflict => "conflict",
                _ => "revert-failed",
            };
            rec.EvaluationText = $"{result.Summary} {undo.Message} {effects}";
            notes.Add($"{rec.ActionName}: {rec.EvaluationText}");
        }
    }

    /// <summary>No nível "automático seguro", aplica as ações de classe B que o usuário pré-aprovou. As aplicadas saem da lista de pendentes.</summary>
    private IReadOnlyList<Recommendation> AutoApply(IReadOnlyList<Recommendation> recs, ActionScope scope, string signature, List<string> notes)
    {
        var policy = _policy();
        if (policy.Level != AutomationLevel.SafeAuto) return recs;
        var left = new List<Recommendation>();
        foreach (var r in recs)
        {
            if (r.ActionId == null || r.Risk != RiskClass.B || !policy.PreApproved.Contains(r.ActionId)) { left.Add(r); continue; }
            var result = _executor.Apply(r.ActionId, scope, policy, userApprovedNow: false, reason: r.Problem, signature: signature);
            notes.Add("Automático: " + result.Message);
            if (!result.Ok) left.Add(r);
        }
        return left;
    }
}

/// <summary>O relatório de uma sessão, em texto. Separa o que foi medido do que foi estimado e do que não estava disponível.</summary>
public static class SessionReport
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static string Text(DiagnosisReport r, LearningOutcome? learning, IReadOnlyList<Recommendation> recs, IReadOnlyList<string>? actionNotes = null)
    {
        var sb = new StringBuilder();
        var c = r.Context;
        sb.AppendLine($"RELATÓRIO DE SESSÃO · {c.Game}");
        sb.AppendLine($"{c.Start:dd/MM/yyyy HH:mm} · {Duration(r.Quality.Seconds)}");
        sb.AppendLine($"Condições: {c.HardwareId ?? "hardware não identificado"} · driver {c.GpuDriver ?? "não lido"} · {c.Resolution ?? "resolução não lida"}");
        sb.AppendLine();
        sb.AppendLine("RESUMO");
        sb.AppendLine("  " + r.Headline);

        sb.AppendLine();
        sb.AppendLine("DESEMPENHO");
        if (r.Frames is { } f)
        {
            string origin = f.Origin == MetricOrigin.Captured ? "medido por frame (ETW)" : "estimado do FPS por segundo";
            sb.AppendLine($"  FPS médio: {f.AvgFps.ToString("0", Pt)} ({origin})");
            if (f.Origin == MetricOrigin.Captured)
            {
                sb.AppendLine($"  Frametime: P50 {f.P50Ms.ToString("0.0", Pt)} ms · P95 {f.P95Ms.ToString("0.0", Pt)} ms · P99 {f.P99Ms.ToString("0.0", Pt)} ms");
                sb.AppendLine("  1% low: " + (f.Low1Fps is double l1 ? $"{l1.ToString("0", Pt)} FPS" : "indisponível (amostra pequena)"));
                sb.AppendLine("  0,1% low: " + (f.Low01Fps is double l01 ? $"{l01.ToString("0", Pt)} FPS" : "indisponível (menos de 10 mil frames)"));
                sb.AppendLine($"  Engasgos: {f.Stutters} ({f.StuttersPerMinute.ToString("0.#", Pt)} por minuto)");
            }
            else sb.AppendLine("  Percentis de frametime, 1% low e engasgos: indisponíveis (sem gravação de frames).");
        }
        else sb.AppendLine("  FPS: indisponível nesta sessão.");
        sb.AppendLine("  Latência de entrada (input lag): não medida pelo tHg.");

        if (r.Findings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("DIAGNÓSTICO");
            foreach (var x in r.Findings.OrderByDescending(x => x.Severity).ThenByDescending(x => x.Confidence.Score))
            {
                sb.AppendLine($"  • {x.Title} · confiança {x.Confidence.Text} ({(x.Confidence.Score * 100).ToString("0", Pt)}%)");
                sb.AppendLine($"    {x.Interpretation}");
                foreach (var e in x.Evidence) sb.AppendLine($"    - {e.Metric}: {e.Value} ({e.Meaning})");
                foreach (string l in x.Limitations) sb.AppendLine($"    ! {l}");
            }
        }
        if (r.RuledOut.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("VERIFICADO E SEM PROBLEMA");
            foreach (string x in r.RuledOut) sb.AppendLine("  • " + x);
        }
        if (learning != null)
        {
            sb.AppendLine();
            sb.AppendLine("COMPARAÇÃO COM SESSÕES ANTERIORES");
            sb.AppendLine("  " + learning.Message);
            foreach (var d in learning.Deviations) sb.AppendLine($"  - {LearningEngine.Describe(d)}" + (d.Worse ? " · pior" : " · melhor"));
        }
        if (actionNotes is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("OTIMIZAÇÕES");
            foreach (string n in actionNotes) sb.AppendLine("  • " + n);
        }
        sb.AppendLine();
        sb.AppendLine("RECOMENDAÇÕES");
        if (recs.Count == 0) sb.AppendLine("  Nenhuma: não há evidência de problema que justifique uma mudança.");
        foreach (var x in recs)
        {
            sb.AppendLine($"  • {x.Title} (risco {x.Risk}, confiança {x.Confidence.Text})");
            sb.AppendLine($"    {x.Proposal}");
        }
        if (r.Limitations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("LIMITAÇÕES DESTA ANÁLISE");
            foreach (string x in r.Limitations) sb.AppendLine("  • " + x);
        }
        return sb.ToString();
    }

    public static string Duration(double seconds) => seconds >= 3600 ? $"{(seconds / 3600).ToString("0.#", Pt)} h" : seconds >= 60 ? $"{seconds / 60:0} min" : $"{seconds:0} s";
}

/// <summary>O que o tHg deve fazer com a própria coleta depois de medir o próprio consumo.</summary>
public enum SelfImpactDecision { Keep, Reduce, Restore }

/// <summary>
/// Vigia o consumo de CPU do próprio tHg. Passou do orçamento de forma sustentada durante o jogo: manda reduzir a coleta não essencial
/// (um degrau por vez). Voltou a ficar bem abaixo por um tempo: manda restaurar. A leitura isolada não decide nada.
/// </summary>
public sealed class SelfImpactGovernor
{
    /// <summary>Orçamentos de CPU do processo, em % do total da máquina. Em repouso é a meta de docs/auditoria-consumo.md.</summary>
    public const double IdleBudgetPct = 2.0, GamingBudgetPct = 1.5;
    public const int MaxLevel = 2;

    private readonly int _sustain, _calm;
    private int _over, _under;

    /// <param name="sustain">Leituras seguidas acima do orçamento para reduzir.</param>
    /// <param name="calm">Leituras seguidas abaixo de metade do orçamento para restaurar.</param>
    public SelfImpactGovernor(int sustain = 6, int calm = 18)
    {
        _sustain = sustain;
        _calm = calm;
    }

    /// <summary>0 = coleta normal; 1 = perfil econômico; 2 = também pausa os timers não essenciais.</summary>
    public int Level { get; private set; }
    public double LastCpuPct { get; private set; }

    public SelfImpactDecision Observe(double cpuPct, bool gaming)
    {
        LastCpuPct = cpuPct;
        double budget = gaming ? GamingBudgetPct : IdleBudgetPct;
        if (cpuPct > budget) { _over++; _under = 0; }
        else if (cpuPct < budget / 2) { _under++; _over = 0; }
        else { _over = 0; _under = 0; }

        if (gaming && _over >= _sustain && Level < MaxLevel)
        {
            Level++;
            _over = 0;
            return SelfImpactDecision.Reduce;
        }
        if (Level > 0 && (!gaming || _under >= _calm))
        {
            Level = 0;
            _under = 0;
            return SelfImpactDecision.Restore;
        }
        return SelfImpactDecision.Keep;
    }
}
