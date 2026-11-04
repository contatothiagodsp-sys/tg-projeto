using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThgOptimization.Core.Benchmark;

/// <summary>Uma nota de referência, comparada à do usuário. Percent = nota sobre a referência. A tela diz que a referência veio de um PC: ainda não é uma mediana de muitos.</summary>
public sealed record BenchReference(string Hardware, string Test, double Median, double Score)
{
    public double Percent => Median > 0 ? Score / Median * 100 : 0;
    public string Line => $"Referência para {Hardware}: {Median:0}, medida em um PC · você está em {Percent:0}% ({Score:0})";
    public string Markdown => $"**tHg Optimization · {Hardware}**\n\n| Teste | Nota | Referência | |\n|---|---:|---:|---:|\n| {Test} | {Score:0} | {Median:0} | {Percent:0}% |\n";
}

/// <summary>
/// Notas de referência dos benchmarks do próprio tHg, em docs/reference-benchmarks.json (embutido: o app roda sem a pasta docs). As notas não têm fonte pública, porque os testes
/// são do tHg: cada uma tem de vir de uma medição real, na mesma peça e na mesma unidade da nota (cena de jogo e ray tracing: FPS × 10). Hoje são as medições citadas no README.
/// Sem referência para a peça, a tela não mostra nada, em vez de um número aproximado.
/// </summary>
public sealed class ReferenceBenchmarks
{
    internal const string ResourceName = "docs/reference-benchmarks.json";

    private sealed record Document(int Version, Dictionary<string, Dictionary<string, Dictionary<string, double>>>? Gpus, Dictionary<string, Dictionary<string, double>>? Cpus);

    private readonly Document _doc;

    private ReferenceBenchmarks(Document doc) => _doc = doc;

    /// <summary>Null = JSON inválido.</summary>
    internal static ReferenceBenchmarks? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<Document>(json) is { Version: > 0 } doc ? new ReferenceBenchmarks(doc) : null; }
        catch (JsonException ex)
        {
            Infrastructure.AppLog.Warn("bench", ex);
            return null;
        }
    }

    private static readonly Lazy<ReferenceBenchmarks?> Embedded = new(() =>
    {
        using var stream = typeof(ReferenceBenchmarks).Assembly.GetManifestResourceStream(ResourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    /// <summary>Lê uma vez só. Null = o recurso não veio no app, e nenhuma referência aparece.</summary>
    public static ReferenceBenchmarks? Load() => Embedded.Value;

    /// <summary>Chave mais longa cujas palavras aparecem inteiras no nome: "RX 9070" não responde por "RX 9070 XT".</summary>
    internal static string? MatchKey(IEnumerable<string> keys, string name)
    {
        string? best = null;
        foreach (string key in keys)
        {
            if (!Regex.IsMatch(name, $@"(?<![\w]){Regex.Escape(key)}(?![\w])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            if (best == null || key.Length > best.Length) best = key;
        }
        // Se sobra algo depois da chave que muda o modelo ("RX 9070" em "RX 9070 XT"), a chave não serve.
        if (best != null && Regex.IsMatch(name, $@"(?<![\w]){Regex.Escape(best)}\s+(XT|XTX|Ti|Super|X3D|GRE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return null;
        return best;
    }

    /// <param name="family">"rt" ou "game".</param>
    /// <param name="preset">"alta-1440p" no rt, "1080p" no game.</param>
    public BenchReference? FindGpu(string gpuName, string family, string preset, double score)
    {
        if (_doc.Gpus is not { } gpus || MatchKey(gpus.Keys, gpuName) is not { } key) return null;
        return gpus[key].TryGetValue(family, out var tests) && tests.TryGetValue(preset, out double median) && median > 0
            ? new BenchReference(key, $"{family} {preset}", median, score) : null;
    }

    public BenchReference? FindCpu(string cpuName, string test, double score)
    {
        if (_doc.Cpus is not { } cpus || MatchKey(cpus.Keys, cpuName) is not { } key) return null;
        return cpus[key].TryGetValue(test, out double median) && median > 0 ? new BenchReference(key, test, median, score) : null;
    }
}
