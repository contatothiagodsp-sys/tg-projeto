using System.IO;
using System.Net;
using System.Text;

namespace ThgOptimization.Core.Benchmark;

/// <summary>Relatório A/B em um único HTML, com o gráfico embutido: abre no navegador e pode ser enviado como arquivo solto.</summary>
public static class BenchmarkReport
{
    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "tHgOptimization", "BenchLab");

    public static string Save(AbComparison r, byte[]? chartPng, string? folder = null)
    {
        folder ??= Folder;
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, FileName(r));
        File.WriteAllText(path, BuildHtml(r, chartPng), new UTF8Encoding(true));
        return path;
    }

    /// <summary>O rótulo é texto livre: tudo que não pode estar num nome de arquivo sai, e o tamanho é limitado.</summary>
    internal static string FileName(AbComparison r)
    {
        static string Safe(string label)
        {
            string clean = string.Concat(label.Split(Path.GetInvalidFileNameChars())).Trim();
            return clean.Length > 30 ? clean[..30] : clean;
        }
        return $"{r.ComparedAt:yyyyMMdd-HHmmss}-{Safe(r.A.ConfigLabel)}-vs-{Safe(r.B.ConfigLabel)}.html";
    }

    internal static string BuildHtml(AbComparison r, byte[]? chartPng)
    {
        // Rótulos e nomes de jogo são texto de fora: tudo passa por E antes de entrar no HTML.
        static string E(string text) => WebUtility.HtmlEncode(text);
        string method = r.Method switch
        {
            AbTestMethod.MannWhitney => "Mann-Whitney",
            AbTestMethod.StudentT => "t de Student",
            _ => "nenhum (uma captura de cada lado)",
        };
        string p = r.PValue switch
        {
            null => "—",
            < 0.001 => "&lt; 0,001",
            { } v => v.ToString("0.000", AbTable.PtBr),
        };
        string verdictClass = r.Kind switch
        {
            AbVerdictKind.Improvement or AbVerdictKind.SmallImprovement => "good",
            AbVerdictKind.Regression => "bad",
            _ => "neutral",
        };

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>Performance Lab · ")
            .Append(E(r.A.ConfigLabel)).Append(" x ").Append(E(r.B.ConfigLabel)).Append("</title><style>\n")
            .Append("body{background:#0a0e14;color:#e5e7eb;font:15px/1.5 'Segoe UI',sans-serif;margin:0 auto;max-width:1240px;padding:32px 20px}\n")
            .Append("h1{font-size:24px;margin:0 0 4px}.sub{color:#9ca3af;margin:0 0 24px}.verdict{font-size:20px;font-weight:600;margin:0 0 4px}\n")
            .Append(".good{color:#00c86e}.bad{color:#ff3860}.neutral{color:#9ca3af}\n")
            .Append("table{border-collapse:collapse;width:100%;margin:20px 0}th,td{border-bottom:1px solid #1f2937;padding:8px 12px;text-align:right}\n")
            .Append("th:first-child,td:first-child{text-align:left}th{color:#9ca3af;font-weight:600}img{max-width:100%;display:block;border-radius:6px}\n")
            .Append("</style></head><body>\n")
            .Append("<h1>Performance Lab</h1>\n<p class=\"sub\">").Append(E(r.A.Game)).Append(" · ")
            .Append(r.ComparedAt.ToString("dd/MM/yyyy HH:mm", AbTable.PtBr)).Append("</p>\n")
            .Append("<p class=\"verdict ").Append(verdictClass).Append("\">").Append(E(r.Verdict)).Append("</p>\n")
            .Append("<p class=\"sub\">Teste: ").Append(method).Append(" · p = ").Append(p).Append(" · d de Cohen = ")
            .Append(r.EffectSizeCohensD.ToString("0.00", AbTable.PtBr)).Append(" · capturas: ").Append(r.CapturesA).Append(" (A) e ").Append(r.CapturesB).Append(" (B)</p>\n");

        html.Append("<table><thead><tr><th>Métrica</th><th>A · ").Append(E(r.A.ConfigLabel)).Append("</th><th>B · ").Append(E(r.B.ConfigLabel)).Append("</th><th>Diferença</th></tr></thead><tbody>\n");
        foreach (var row in AbTable.Rows(r))
        {
            string color = row.IsBetter switch { true => "good", false => "bad", null => "neutral" };
            html.Append("<tr><td>").Append(E(row.Metric)).Append("</td><td>").Append(E(row.A)).Append("</td><td>").Append(E(row.B))
                .Append("</td><td class=\"").Append(color).Append("\">").Append(E(row.Delta)).Append("</td></tr>\n");
        }
        html.Append("</tbody></table>\n");

        if (chartPng is { Length: > 0 })
            html.Append("<img alt=\"Frametime de A e B\" src=\"data:image/png;base64,").Append(Convert.ToBase64String(chartPng)).Append("\">\n");
        return html.Append("</body></html>\n").ToString();
    }
}
