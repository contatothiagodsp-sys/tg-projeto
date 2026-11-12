using System.IO;
using System.Text;
using System.Text.Json;

namespace ThgOptimization.Core.Intelligence;

/// <summary>O que o tHg sabe sobre um jogo neste PC. Tudo local.</summary>
public sealed class GameIntelProfile
{
    public const int MaxHistory = 60;

    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Exe { get; set; }
    public string? Launcher { get; set; }
    public string? InstallDir { get; set; }
    /// <summary>O usuário corrigiu a associação do executável: a detecção automática não sobrescreve.</summary>
    public bool ExeSetByUser { get; set; }
    public string Preset { get; set; } = UsagePreset.Balanced.Key;
    public List<GameBaseline> Baselines { get; set; } = new();
    public List<SessionSummary> History { get; set; } = new();
    /// <summary>Recomendações que o usuário dispensou para este jogo.</summary>
    public List<string> Dismissed { get; set; } = new();
    public DateTime? LastPlayed { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public SessionSummary? LastSession => History.Count > 0 ? History[0] : null;
}

public sealed class FindingEntry
{
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Severity { get; set; } = "";
    public double Confidence { get; set; }
}

/// <summary>Uma linha do histórico de inteligência: o que foi diagnosticado, com que confiança e o que foi aprendido.</summary>
public sealed class DiagnosisEntry
{
    public DateTime At { get; set; }
    public string GameKey { get; set; } = "";
    public string GameName { get; set; } = "";
    public double Minutes { get; set; }
    public string Headline { get; set; } = "";
    public List<FindingEntry> Findings { get; set; } = new();
    public string Learning { get; set; } = "";
    public List<string> Recommendations { get; set; } = new();
    /// <summary>Relatório completo em texto. Só as entradas mais recentes guardam, para o arquivo não crescer sem limite.</summary>
    public string? Report { get; set; }
}

public sealed class IntelligenceData
{
    public const int CurrentVersion = 1, MaxHistory = 200, MaxJournal = 300, MaxReports = 30;

    public int Version { get; set; } = CurrentVersion;
    public Dictionary<string, GameIntelProfile> Games { get; set; } = new();
    public List<DiagnosisEntry> History { get; set; } = new();
    public List<ActionRecord> Journal { get; set; } = new();
}

/// <summary>
/// Persistência local do tHg Intelligence: um JSON na pasta protegida do app, gravado de forma atômica. Nada sai do PC.
/// Arquivo corrompido é guardado ao lado (.corrompido) e o app segue com dados vazios, sem travar e sem apagar a evidência.
/// </summary>
public sealed class IntelligenceStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private const long MaxImportBytes = 16L * 1024 * 1024;

    private readonly string _path;
    private readonly object _gate = new();
    private IntelligenceData _data = new();
    private readonly Action<string>? _warn;

    public string FilePath => _path;
    /// <summary>Aviso da última carga (arquivo ilegível, versão mais nova). Null = carregou normalmente.</summary>
    public string? LoadWarning { get; private set; }

    public IntelligenceStore(string path, Action<string>? warn = null)
    {
        _path = path;
        _warn = warn;
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var data = JsonSerializer.Deserialize<IntelligenceData>(File.ReadAllText(_path, Encoding.UTF8), Json);
            if (data == null) throw new JsonException("arquivo vazio");
            if (data.Version > IntelligenceData.CurrentVersion)
            {
                // Arquivo de uma versão mais nova do app: não é reescrito por esta, para não perder o que ela não entende.
                LoadWarning = "Os dados do tHg Intelligence foram gravados por uma versão mais nova do app. Nesta versão eles não são alterados.";
                _readOnly = true;
            }
            _data = Normalize(data);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LoadWarning = "Os dados do tHg Intelligence não puderam ser lidos e foram guardados como cópia. O aprendizado recomeça do zero.";
            _warn?.Invoke($"{LoadWarning} ({ex.Message})");
            try { if (File.Exists(_path)) File.Move(_path, _path + ".corrompido", overwrite: true); }
            catch (Exception move) when (move is IOException or UnauthorizedAccessException) { _warn?.Invoke(move.Message); }
            _data = new IntelligenceData();
        }
    }

    private bool _readOnly;

    private static IntelligenceData Normalize(IntelligenceData d)
    {
        d.Games ??= new();
        d.History ??= new();
        d.Journal ??= new();
        foreach (var g in d.Games.Values)
        {
            g.Baselines ??= new();
            g.History ??= new();
            g.Dismissed ??= new();
            foreach (var b in g.Baselines) { b.Sessions ??= new(); b.Held ??= new(); }
        }
        return d;
    }

    public T Read<T>(Func<IntelligenceData, T> read)
    {
        lock (_gate) return read(_data);
    }

    /// <summary>Altera e grava. Uma falha de disco não derruba quem chamou: os dados seguem em memória e o aviso vai para o log.</summary>
    public void Mutate(Action<IntelligenceData> change)
    {
        lock (_gate)
        {
            change(_data);
            Trim(_data);
            SaveLocked();
        }
    }

    public GameIntelProfile Game(string key, string name)
    {
        lock (_gate)
        {
            if (!_data.Games.TryGetValue(key, out var g))
            {
                g = new GameIntelProfile { Key = key, Name = name };
                _data.Games[key] = g;
            }
            if (g.Name.Length == 0) g.Name = name;
            return g;
        }
    }

    internal static void Trim(IntelligenceData d)
    {
        if (d.History.Count > IntelligenceData.MaxHistory) d.History.RemoveRange(IntelligenceData.MaxHistory, d.History.Count - IntelligenceData.MaxHistory);
        for (int i = IntelligenceData.MaxReports; i < d.History.Count; i++) d.History[i].Report = null;
        if (d.Journal.Count > IntelligenceData.MaxJournal)
        {
            // Ações ainda ativas nunca saem do diário: sem a linha, não haveria como desfazer.
            var keep = d.Journal.Where(r => r.IsActive).ToList();
            var rest = d.Journal.Where(r => !r.IsActive).Take(Math.Max(0, IntelligenceData.MaxJournal - keep.Count));
            d.Journal = d.Journal.Where(r => keep.Contains(r) || rest.Contains(r)).ToList();
        }
        foreach (var g in d.Games.Values)
            if (g.History.Count > GameIntelProfile.MaxHistory) g.History.RemoveRange(GameIntelProfile.MaxHistory, g.History.Count - GameIntelProfile.MaxHistory);
    }

    /// <summary>Remove o que passou do prazo de retenção. Ações ativas e as referências (baselines) ficam.</summary>
    public int Prune(TimeSpan retention, DateTime now)
    {
        int removed = 0;
        Mutate(d =>
        {
            DateTime cut = now - retention;
            removed += d.History.RemoveAll(h => h.At < cut);
            removed += d.Journal.RemoveAll(r => !r.IsActive && r.At < cut);
            foreach (var g in d.Games.Values) removed += g.History.RemoveAll(s => s.Start < cut);
        });
        return removed;
    }

    private void SaveLocked()
    {
        if (_readOnly) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json), new UTF8Encoding(false));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _warn?.Invoke("Não foi possível gravar os dados do tHg Intelligence: " + ex.Message); }
    }

    public void Export(string destination)
    {
        lock (_gate) File.WriteAllText(destination, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    /// <summary>
    /// Importa um arquivo exportado. O conteúdo é só dado: perfis, históricos e referências. O diário de ações do arquivo não é importado
    /// (uma ação "ativa" vinda de outro PC não corresponde a nada aqui, e desfazê-la escreveria um valor estranho).
    /// </summary>
    public (bool Ok, string Message) Import(string source)
    {
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists) return (false, "Arquivo não encontrado.");
            if (info.Length > MaxImportBytes) return (false, "Arquivo grande demais para ser um export do tHg Intelligence.");
            var data = JsonSerializer.Deserialize<IntelligenceData>(File.ReadAllText(source, Encoding.UTF8), Json);
            if (data == null || data.Version < 1) return (false, "O arquivo não é um export do tHg Intelligence.");
            if (data.Version > IntelligenceData.CurrentVersion) return (false, "O arquivo é de uma versão mais nova do tHg.");
            Normalize(data);
            int games = 0;
            Mutate(d =>
            {
                foreach (var (key, g) in data.Games)
                {
                    if (string.IsNullOrWhiteSpace(key) || key.Length > 200) continue;
                    d.Games[key] = g;
                    games++;
                }
                d.History = data.History.Concat(d.History).GroupBy(h => (h.At, h.GameKey)).Select(x => x.First()).OrderByDescending(h => h.At).ToList();
            });
            return (true, $"{games} perfil(is) de jogo importado(s).");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (false, "Não foi possível importar: " + ex.Message);
        }
    }

    /// <summary>Apaga perfis, referências e histórico. As ações ainda ativas continuam no diário, para poderem ser desfeitas.</summary>
    public void DeleteAll() => Mutate(d =>
    {
        d.Games.Clear();
        d.History.Clear();
        d.Journal.RemoveAll(r => !r.IsActive);
    });

    public bool DeleteGame(string key)
    {
        bool removed = false;
        Mutate(d =>
        {
            removed = d.Games.Remove(key);
            d.History.RemoveAll(h => h.GameKey == key);
        });
        return removed;
    }

    public long SizeBytes { get { try { return File.Exists(_path) ? new FileInfo(_path).Length : 0; } catch (IOException) { return 0; } } }
}
