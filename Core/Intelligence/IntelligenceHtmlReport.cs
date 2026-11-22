using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace ThgOptimization.Core.Intelligence;

/// <summary>
/// Relatório autossuficiente de diagnóstico em HTML (offline e sanitizado): pode ser salvo em Documentos e aberto no navegador
/// sem expor identificadores da máquina (números de série ou nome do computador).
/// </summary>
public static class IntelligenceHtmlReport
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "tHgOptimization", "Intelligence");

    public static string Save(DiagnosisReport report, SessionHealthScore health, LearningOutcome? learning,
                              IReadOnlyList<Recommendation> recs, MachineProfile? machine, string? folder = null)
    {
        folder ??= Folder;
        Directory.CreateDirectory(folder);
        string slug = SafeSlug(report.Context.Game);
        string name = $"{report.Context.Start:yyyyMMdd-HHmmss}-{slug}-diagnostico.html";
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, Build(report, health, learning, recs, machine), new UTF8Encoding(true));
        return path;
    }

    internal static string SafeSlug(string text)
    {
        var clean = new string((text ?? "jogo").Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        while (clean.Contains("--", StringComparison.Ordinal)) clean = clean.Replace("--", "-", StringComparison.Ordinal);
        if (clean.Length > 36) clean = clean[..36].TrimEnd('-');
        return clean.Length > 0 ? clean : "jogo";
    }

    public static string Build(DiagnosisReport report, SessionHealthScore health, LearningOutcome? learning,
                               IReadOnlyList<Recommendation> recs, MachineProfile? machine)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

        var c = report.Context;
        string gradeClass = health.Score >= 85 ? "good" : health.Score >= 60 ? "warn" : "bad";

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"pt-BR\"><head><meta charset=\"utf-8\">")
          .Append("<title>tHg Intelligence · ").Append(E(c.Game)).Append("</title>\n<style>\n")
          .Append("*{box-sizing:border-box}body{background:#0a0e14;color:#e5e7eb;font:14px/1.55 'Segoe UI',system-ui,sans-serif;margin:0 auto;max-width:1080px;padding:32px 20px}\n")
          .Append("h1{font-size:26px;margin:0 0 4px}h2{font-size:13px;letter-spacing:.08em;text-transform:uppercase;color:#9ca3af;margin:0 0 12px}\n")
          .Append(".sub{color:#9ca3af;margin:0 0 20px}.card{background:#11171f;border:1px solid #1f2937;border-radius:10px;padding:20px;margin:0 0 16px}\n")
          .Append(".hero{display:flex;justify-content:space-between;align-items:center;gap:20px;flex-wrap:wrap;border-color:#007a8c}\n")
          .Append(".score{font-size:38px;font-weight:700;font-family:'Cascadia Mono',Consolas,monospace}\n")
          .Append(".good{color:#00ff88}.warn{color:#ffb020}.bad{color:#ff3860}.accent{color:#00e5ff}.muted{color:#9ca3af}\n")
          .Append(".grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:12px}\n")
          .Append(".stat{background:#0d131a;border:1px solid #1f2937;border-radius:8px;padding:12px}\n")
          .Append(".stat .k{font-size:11px;color:#9ca3af;text-transform:uppercase}.stat .v{font-size:22px;font-weight:700;font-family:'Cascadia Mono',Consolas,monospace}\n")
          .Append(".pill{display:inline-block;padding:2px 10px;border-radius:99px;font-size:11px;font-weight:600;background:#1a2230}\n")
          .Append(".finding{border-top:1px solid #1f2937;padding:14px 0}.finding:first-of-type{border-top:none;padding-top:0}\n")
          .Append("ul{margin:6px 0 0 18px;padding:0}li{margin:3px 0}\n")
          .Append(".bar-wrap{height:6px;background:#1f2937;border-radius:4px;overflow:hidden;margin-top:6px}.bar{height:100%;background:#00e5ff}\n")
          .Append("</style></head><body>\n");

        sb.Append("<h1>tHg Intelligence · ").Append(E(c.Game)).Append("</h1>\n")
          .Append("<p class=\"sub\">").Append(c.Start.ToString("dd/MM/yyyy HH:mm", Pt))
          .Append(" · ").Append(E(SessionReport.Duration(report.Quality.Seconds)))
          .Append(" · ").Append(E(c.HardwareId ?? "hardware não identificado"))
          .Append(" · driver ").Append(E(c.GpuDriver ?? "não lido"))
          .Append(" · ").Append(E(c.Resolution ?? "resolução não lida")).Append("</p>\n");

        // Saúde da sessão
        sb.Append("<div class=\"card hero\"><div><h2>Resumo &amp; Saúde da Sessão</h2>")
          .Append("<div style=\"font-size:18px;font-weight:600\">").Append(E(report.Headline)).Append("</div>")
          .Append("<div class=\"muted\" style=\"margin-top:4px\">").Append(E(health.Summary)).Append("</div></div>")
          .Append("<div style=\"text-align:right\"><div class=\"score ").Append(gradeClass).Append("\">")
          .Append(health.Score).Append("/100</div><div class=\"pill ").Append(gradeClass).Append("\">Grau ")
          .Append(E(health.Grade)).Append("</div></div></div>\n");

        sb.Append("<div class=\"card\"><h2>Decomposição do Índice</h2><div class=\"grid\">");
        void SubBar(string label, int val, int max)
        {
            int pct = max > 0 ? Math.Clamp(val * 100 / max, 0, 100) : 0;
            sb.Append("<div class=\"stat\"><div class=\"k\">").Append(E(label)).Append("</div><div class=\"v\" style=\"font-size:18px\">")
              .Append(val).Append('/').Append(max).Append("</div><div class=\"bar-wrap\"><div class=\"bar\" style=\"width:")
              .Append(pct).Append("%\"></div></div></div>");
        }
        SubBar("Fluidez", health.FluencyScore, 40);
        SubBar("Térmico", health.ThermalScore, 25);
        SubBar("Estabilidade", health.StabilityScore, 20);
        SubBar("Recursos e Rede", health.ResourceScore, 15);
        sb.Append("</div></div>\n");

        // Desempenho
        sb.Append("<div class=\"card\"><h2>Desempenho Medido</h2><div class=\"grid\">");
        if (report.Frames is { } fr)
        {
            bool cap = fr.Origin == MetricOrigin.Captured;
            void Stat(string k, string v, string note) =>
                sb.Append("<div class=\"stat\"><div class=\"k\">").Append(E(k)).Append("</div><div class=\"v\">").Append(E(v))
                  .Append("</div><div class=\"muted\" style=\"font-size:11px\">").Append(E(note)).Append("</div></div>");
            Stat("FPS Médio", fr.AvgFps.ToString("0", Pt), cap ? "medido (ETW)" : "estimado");
            Stat("1% Low", fr.Low1Fps is double l1 ? l1.ToString("0", Pt) + " FPS" : "—", l1Note(fr.Low1Fps != null));
            Stat("0,1% Low", fr.Low01Fps is double l01 ? l01.ToString("0", Pt) + " FPS" : "—", fr.Low01Fps != null ? "medido" : "amostra pequena");
            Stat("Frametime P99", cap ? fr.P99Ms.ToString("0.0", Pt) + " ms" : "—", cap ? $"P50 {fr.P50Ms.ToString("0.0", Pt)} ms" : "indisponível");
            Stat("Engasgos/min", cap ? fr.StuttersPerMinute.ToString("0.#", Pt) : "—", cap ? $"{fr.Stutters} no total" : "indisponível");
            static string l1Note(bool ok) => ok ? "medido (ETW)" : "indisponível";
        }
        else sb.Append("<p class=\"muted\">FPS indisponível nesta sessão.</p>");
        sb.Append("</div><p class=\"muted\" style=\"margin:10px 0 0;font-size:12px\">Latência de entrada (input lag): não medida pelo tHg.</p></div>\n");

        // Achados
        sb.Append("<div class=\"card\"><h2>Achados do Diagnóstico</h2>");
        if (report.Findings.Count == 0)
        {
            sb.Append("<p class=\"good\">Nenhum problema identificado com base nos dados coletados.</p>");
        }
        else
        {
            foreach (var f in report.Findings.OrderByDescending(x => x.Severity).ThenByDescending(x => x.Confidence.Score))
            {
                string sevCls = f.Severity == Severity.Problem ? "bad" : f.Severity == Severity.Attention ? "warn" : "good";
                sb.Append("<div class=\"finding\"><div style=\"display:flex;justify-content:space-between;gap:12px;align-items:center\">")
                  .Append("<strong>").Append(E(f.Title)).Append("</strong>")
                  .Append("<span class=\"pill ").Append(sevCls).Append("\">confiança ").Append(E(f.Confidence.Text))
                  .Append(" · ").Append((f.Confidence.Score * 100).ToString("0", Pt)).Append("%</span></div>")
                  .Append("<p style=\"margin:6px 0\">").Append(E(f.Interpretation)).Append("</p>");
                if (f.Evidence.Count > 0)
                {
                    sb.Append("<ul>");
                    foreach (var ev in f.Evidence)
                        sb.Append("<li><strong>").Append(E(ev.Metric)).Append(":</strong> ").Append(E(ev.Value)).Append(" <span class=\"muted\">(").Append(E(ev.Meaning)).Append(")</span></li>");
                    sb.Append("</ul>");
                }
                foreach (string lim in f.Limitations)
                    sb.Append("<div class=\"muted\" style=\"font-size:12px;margin-top:4px\">Limitação: ").Append(E(lim)).Append("</div>");
                sb.Append("</div>");
            }
        }
        sb.Append("</div>\n");

        if (report.RuledOut.Count > 0)
        {
            sb.Append("<div class=\"card\"><h2>Verificado e Sem Problema</h2><ul>");
            foreach (string r in report.RuledOut) sb.Append("<li class=\"good\">").Append(E(r)).Append("</li>");
            sb.Append("</ul></div>\n");
        }

        if (learning != null)
        {
            sb.Append("<div class=\"card\"><h2>Comparação com o Habitual</h2><p>").Append(E(learning.Message)).Append("</p>");
            if (learning.Deviations.Count > 0)
            {
                sb.Append("<ul>");
                foreach (var d in learning.Deviations)
                    sb.Append("<li class=\"").Append(d.Worse ? "warn" : "good").Append("\">").Append(E(LearningEngine.Describe(d))).Append("</li>");
                sb.Append("</ul>");
            }
            sb.Append("</div>\n");
        }

        sb.Append("<div class=\"card\"><h2>Recomendações</h2>");
        if (recs.Count == 0)
            sb.Append("<p class=\"muted\">Nenhuma otimização recomendada para os dados desta sessão.</p>");
        else
            foreach (var r in recs)
            {
                sb.Append("<div class=\"finding\"><strong>").Append(E(r.Title)).Append("</strong> ")
                  .Append("<span class=\"pill\">risco ").Append(r.Risk).Append("</span>")
                  .Append("<p style=\"margin:6px 0\">").Append(E(r.Proposal)).Append("</p>")
                  .Append("<div class=\"muted\" style=\"font-size:12px\">Como desfazer: ").Append(E(r.HowToUndo))
                  .Append(" · Como medir: ").Append(E(r.HowToMeasure)).Append("</div></div>");
            }
        sb.Append("</div>\n");

        if (report.Limitations.Count > 0 || machine != null)
        {
            sb.Append("<div class=\"card\"><h2>Limitações &amp; Privacidade Local</h2><ul>");
            foreach (string l in report.Limitations) sb.Append("<li class=\"muted\">").Append(E(l)).Append("</li>");
            if (machine != null)
                sb.Append("<li class=\"muted\">Perfil local: ").Append(E(machine.HardwareId))
                  .Append(" (números de série e nome do computador são omitidos por privacidade).</li>");
            sb.Append("</ul></div>\n");
        }

        return sb.Append("</body></html>\n").ToString();
    }
}
