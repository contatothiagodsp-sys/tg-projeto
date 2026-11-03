using System.Globalization;
using System.Text;

namespace ThgOptimization.Infrastructure;

/// <summary>Uma função que a busca do Ctrl+K encontra: uma aba ou um ajuste dentro de uma página.</summary>
/// <param name="TargetId">Id do ajuste, para a tela rolar até ele. Vazio = é a página inteira.</param>
public sealed record SearchEntry(string Page, string Label, string Context, string Keywords = "", string TargetId = "");

/// <summary>Busca global, sem depender da janela. Ignora acentos e maiúsculas: "memoria" acha "Memória".</summary>
public static class SearchIndex
{
    public const int MaxResults = 8;

    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>3 = o nome começa com a busca; 2 = o nome contém; 1 = todas as palavras aparecem nas palavras-chave ou no contexto; 0 = não.</summary>
    public static int Score(string query, SearchEntry e)
    {
        string q = Fold(query.Trim());
        if (q.Length == 0) return 0;
        string label = Fold(e.Label);
        if (label.StartsWith(q, StringComparison.Ordinal)) return 3;
        if (label.Contains(q, StringComparison.Ordinal)) return 2;
        string keys = Fold(e.Keywords + " " + e.Context);
        return q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => keys.Contains(w, StringComparison.Ordinal)) ? 1 : 0;
    }

    /// <summary>Melhores resultados. No empate, vale a ordem de entrada: as abas vêm antes dos ajustes.</summary>
    public static List<SearchEntry> Search(string query, IEnumerable<SearchEntry> entries, int take = MaxResults) =>
        entries.Select(e => (Entry: e, Score: Score(query, e)))
               .Where(x => x.Score > 0)
               .OrderByDescending(x => x.Score)
               .Take(take)
               .Select(x => x.Entry)
               .ToList();
}
