using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace ThgOptimization.Tests;

public sealed class AmdSnapshotTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("thg-amd-").FullName;
    private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };
    private static readonly byte[] Binario = { 0x00, 0x01, 0x02, 0xFF };

    private const string Json = """
        {"Settings":[
          {"Name":"Radeon Anti-Lag","Value":"FALSE"},
          {"Name":"Radeon Chill","Value":"TRUE"},
          {"Name":"Radeon Boost","Value":1},
          {"Name":"Outra opção","Value":"mantida"}
        ]}
        """;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Zip(string name, byte[] rssettings)
    {
        string path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var s = zip.CreateEntry("rssettings.json").Open()) s.Write(rssettings);
        using (var s = zip.CreateEntry("gmdb.blb").Open()) s.Write(Binario);
        return path;
    }

    private static byte[] Read(string zipPath, string entry)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        using var s = zip.GetEntry(entry)!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string? Valor(JsonNode root, string nome) =>
        root["Settings"]!.AsArray().First(o => o!["Name"]!.GetValue<string>() == nome)!["Value"]!.ToJsonString();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generate_liga_AntiLag_desliga_Chill_e_Boost(bool comBom)
    {
        byte[] json = Encoding.UTF8.GetBytes(Json);
        string input = Zip("in.zip", comBom ? Bom.Concat(json).ToArray() : json);
        string output = Path.Combine(_dir, "out.zip");

        var changes = AmdSnapshot.Generate(input, output);

        Assert.Contains(changes, c => c.Contains("Anti-Lag"));
        Assert.Contains(changes, c => c.Contains("Chill"));
        Assert.Contains(changes, c => c.Contains("Boost"));

        byte[] novo = Read(output, "rssettings.json");
        Assert.Equal(comBom, novo.AsSpan(0, 3).SequenceEqual(Bom)); // a marca UTF-8 fica como estava
        var root = JsonNode.Parse(novo.AsSpan(comBom ? 3 : 0))!;
        Assert.Equal("\"TRUE\"", Valor(root, "Radeon Anti-Lag"));
        Assert.Equal("\"FALSE\"", Valor(root, "Radeon Chill"));
        Assert.Equal("0", Valor(root, "Radeon Boost"));         // mantém o tipo original (número)
        Assert.Equal("\"mantida\"", Valor(root, "Outra opção"));
    }

    [Fact]
    public void Generate_nao_toca_nos_outros_arquivos()
    {
        string output = Path.Combine(_dir, "out.zip");
        AmdSnapshot.Generate(Zip("in.zip", Encoding.UTF8.GetBytes(Json)), output);
        Assert.Equal(Binario, Read(output, "gmdb.blb"));
    }

    [Fact]
    public void Generate_ja_certo_nao_lista_mudancas()
    {
        const string certo = """{"Settings":[{"Name":"Radeon Anti-Lag","Value":"TRUE"},{"Name":"Radeon Chill","Value":"FALSE"}]}""";
        Assert.Empty(AmdSnapshot.Generate(Zip("in.zip", Encoding.UTF8.GetBytes(certo)), Path.Combine(_dir, "out.zip")));
    }

    [Fact]
    public void Generate_recusa_arquivo_que_nao_e_zip() =>
        Assert.Throws<InvalidDataException>(() => AmdSnapshot.Generate(Path.Combine(_dir, "in.rar"), Path.Combine(_dir, "out.zip")));

    [Fact]
    public void Generate_recusa_instantaneo_sem_opcoes_de_jogo()
    {
        string input = Zip("in.zip", Encoding.UTF8.GetBytes("""{"Settings":[{"Name":"Outra","Value":"x"}]}"""));
        var ex = Assert.Throws<InvalidDataException>(() => AmdSnapshot.Generate(input, Path.Combine(_dir, "out.zip")));
        Assert.Contains("Anti-Lag", ex.Message);
    }
}
