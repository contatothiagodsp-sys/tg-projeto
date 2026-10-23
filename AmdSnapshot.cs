using System.IO;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ThgOptimization;

/// <summary>
/// Configuração para jogos no AMD Software (Adrenalin) pelo próprio "Instantâneo" dele: o usuário exporta as configurações
/// (um .zip com rssettings.json, gmdb.blb, GExDb.blb…), o tHg muda só as opções de jogo em rssettings.json e devolve um
/// .zip igual, byte a byte, no resto (CPU, BIOS, telas, perfis por jogo ficam como estavam). O usuário importa pelo botão
/// "Importar"; se não confirmar em 15 s, o próprio AMD Software desfaz.
/// </summary>
public static class AmdSnapshot
{
    private static readonly JsonSerializerOptions Out = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = false };

    /// <summary>
    /// Gera o .zip ajustado. Devolve a lista do que mudou (vazia = já estava tudo certo).
    /// Não depende da estrutura de uma versão específica do AMD Software: procura em todos os arquivos JSON do instantâneo
    /// qualquer objeto com "Name" e "Value" (o formato muda entre versões do driver e entre gerações de placa) e só altera as
    /// opções conhecidas, mantendo o tipo do valor original (número, texto ou verdadeiro/falso).
    /// </summary>
    public static List<string> Generate(string input, string output)
    {
        if (!input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Use o arquivo .zip criado pelo botão Exportar do AMD Software (sem compactar de novo em .rar).");
        using var zin = ZipFile.OpenRead(input);
        var changes = new List<string>();
        var rewritten = new Dictionary<string, byte[]>();
        var seen = new List<string>();

        foreach (var e in zin.Entries)
        {
            if (e.Length == 0 || e.Length > 20L << 20) continue;
            JsonNode? root;
            bool bom = false;
            try
            {
                using var st = e.Open();
                using var ms = new MemoryStream();
                st.CopyTo(ms);
                var bytes = ms.ToArray();
                int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                bom = start == 3;
                if (bytes.Length <= start || (bytes[start] != (byte)'{' && bytes[start] != (byte)'[' && !char.IsWhiteSpace((char)bytes[start]))) continue;
                root = JsonNode.Parse(bytes.AsSpan(start));
            }
            catch { continue; }
            if (root == null) continue;
            seen.Add(e.FullName);
            int before = changes.Count;
            Walk(root, changes);
            if (changes.Count > before)
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(root, Out);
                rewritten[e.FullName] = bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(json).ToArray() : json; // mantém a marca UTF-8 se havia
            }
        }

        if (seen.Count == 0)
            throw new InvalidDataException("Nenhum arquivo de configuração foi encontrado dentro do .zip. Arquivos: " +
                                           string.Join(", ", zin.Entries.Select(x => x.FullName).Take(12)));
        bool foundAny = _matched;
        _matched = false;
        if (!foundAny)
            throw new InvalidDataException("Este instantâneo não tem as opções de jogo (Anti-Lag, Chill, Boost…). Em algumas versões do AMD Software " +
                                           "elas só entram no arquivo depois de abrir a aba Jogos → Gráficos uma vez. Abra essa aba, exporte de novo e tente outra vez.");

        string tmp = output + ".tmp";
        using (var fs = File.Create(tmp))
        using (var zout = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var e in zin.Entries)
            {
                var ne = zout.CreateEntry(e.FullName, CompressionLevel.Optimal);
                ne.LastWriteTime = e.LastWriteTime;
                using var dst = ne.Open();
                if (rewritten.TryGetValue(e.FullName, out var data)) dst.Write(data);
                else { using var src = e.Open(); src.CopyTo(dst); }
            }
        }
        File.Move(tmp, output, overwrite: true);
        return changes.Distinct().ToList();
    }

    [ThreadStatic] private static bool _matched;

    private static void Walk(JsonNode node, List<string> changes)
    {
        if (node is JsonArray arr) { foreach (var c in arr) if (c != null) Walk(c, changes); return; }
        if (node is not JsonObject obj) return;
        string? name = Prop(obj, "Name")?.GetValueKind() == JsonValueKind.String ? Prop(obj, "Name")!.GetValue<string>() : null;
        if (name != null && Prop(obj, "Value") != null) Apply(obj, name, changes);
        foreach (var kv in obj.ToList()) if (kv.Value != null) Walk(kv.Value, changes);
    }

    private static JsonNode? Prop(JsonObject o, string key) =>
        o.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static string KeyOf(JsonObject o, string key) =>
        o.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Key ?? key;

    /// <summary>Nome normalizado: sem ™/®, minúsculo, sem "Radeon"/"AMD" na frente.</summary>
    private static string Norm(string n) =>
        System.Text.RegularExpressions.Regex.Replace(n.Replace("™", "").Replace("®", "").Trim().ToLowerInvariant(), @"^(amd|radeon)\s+", "").Trim();

    private static void Apply(JsonObject st, string rawName, List<string> changes)
    {
        string n = Norm(rawName);
        (bool On, string Label)? toggle = n switch
        {
            "anti-lag" or "antilag" => (true, "Radeon Anti-Lag: ligado (menos atraso entre o mouse e a tela)"),
            "chill" => (false, "Radeon Chill: desligado (segurava o FPS)"),
            "enhanced sync" => (false, "Enhanced Sync: desligado (causa engasgos com FreeSync)"),
            "super resolution" => (false, "Radeon Super Resolution: desligado (mudava a resolução)"),
            "surface format optimization" => (true, "Otimização do formato de superfície: ligada"),
            "advertisements" => (false, "Anúncios no AMD Software: desligados"),
            _ when n.StartsWith("boost") => (false, "Radeon Boost: desligado (baixava a resolução em movimento)"),
            _ when n.StartsWith("fluid motion frames") => (false, "Fluid Motion Frames: desligado (quadros gerados aumentam o atraso)"),
            _ => null,
        };
        string valueKey = KeyOf(st, "Value");
        var cur = st[valueKey];
        if (toggle is { } t)
        {
            _matched = true;
            JsonNode? want = cur?.GetValueKind() switch
            {
                JsonValueKind.True or JsonValueKind.False => JsonValue.Create(t.On),
                JsonValueKind.String => JsonValue.Create(cur!.GetValue<string>().Equals("TRUE", StringComparison.OrdinalIgnoreCase) || cur.GetValue<string>().Equals("FALSE", StringComparison.OrdinalIgnoreCase)
                                                         ? (t.On ? "TRUE" : "FALSE") : (t.On ? "1" : "0")),
                JsonValueKind.Number => JsonValue.Create(t.On ? 1 : 0),
                _ => null,
            };
            if (want != null && cur!.ToJsonString() != want.ToJsonString()) { st[valueKey] = want; changes.Add(t.Label); }
        }
        else if (n == "texture filtering quality" && cur?.GetValueKind() == JsonValueKind.String)
        {
            _matched = true;
            string v = cur.GetValue<string>();
            if (v is "Standard" or "High" or "Padrão" or "Alta")
            {
                st[valueKey] = v is "Padrão" or "Alta" ? "Desempenho" : "Performance";
                string idx = KeyOf(st, "FeatureListIndex");
                if (st[idx] != null) st[idx] = 0;
                changes.Add("Qualidade da filtragem de textura: Desempenho");
            }
        }
    }

    /// <summary>Jogos com perfil próprio alterado no AMD Software (eles ignoram o global): o usuário deve conferir.</summary>
    public static List<string> CustomizedGames(string input)
    {
        var list = new List<string>();
        try
        {
            using var zin = ZipFile.OpenRead(input);
            if (zin.GetEntry("gmdb.blb") is not { } e) return list;
            using var s = e.Open();
            if (JsonNode.Parse(s)?["games"] is not JsonArray games) return list;
            foreach (var g in games.OfType<JsonObject>())
                if (g["profileCustomized"]?.GetValue<string>() == "TRUE" && g["title"]?.GetValue<string>() is string t) list.Add(t);
        }
        catch { }
        return list;
    }
}
