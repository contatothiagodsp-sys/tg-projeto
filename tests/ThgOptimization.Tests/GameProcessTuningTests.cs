using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

public class GameHandleTests
{
    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr handle, int infoClass, byte[] info, int length, out int returned);

    private const uint VmRead = 0x10, VmWrite = 0x20;

    /// <summary>
    /// O motivo de o jogo não passar por Process.Handle: se uma versão do .NET mudar isso, este teste avisa.
    /// PUBLIC_OBJECT_BASIC_INFORMATION: Attributes@0, GrantedAccess@4.
    /// </summary>
    [Fact]
    public void Process_Handle_abre_com_leitura_e_escrita_de_memoria()
    {
        using var child = new Child();
        using var p = Process.GetProcessById(child.Id);
        var info = new byte[56];
        Assert.Equal(0, NtQueryObject(p.Handle, 0, info, info.Length, out _));
        uint granted = BitConverter.ToUInt32(info, 4);
        Assert.Equal(VmRead | VmWrite, granted & (VmRead | VmWrite));
    }

    [Fact]
    public void WithHandle_entrega_so_os_direitos_pedidos()
    {
        using var child = new Child();
        uint granted = 0;
        Assert.True(ProcessUtil.WithHandle(child.Id, ProcessUtil.SetInformation, h =>
        {
            var info = new byte[56];
            Assert.Equal(0, NtQueryObject(h, 0, info, info.Length, out _));
            granted = BitConverter.ToUInt32(info, 4);
            return true;
        }));
        Assert.Equal(ProcessUtil.SetInformation, granted & ~ProcessUtil.SetLimitedInformation); // SET_INFORMATION inclui o limitado
        Assert.Equal(0u, granted & (VmRead | VmWrite));
    }

    [Fact]
    public void Processo_inexistente_falha_sem_excecao()
    {
        const int gone = 0x7FFFFFF0;
        Assert.Equal(0, FirstChance.CountWin32(() =>
        {
            Assert.False(ProcessIo.SetIoPriority(gone, 2));
            Assert.False(PowerThrottling.SetEco(gone, false));
            Assert.False(PowerThrottling.Reset(gone));
            Assert.Null(ProcessIo.GetIoPriority(gone));
        }));
    }
}

public class GameIoPriorityTests
{
    [Theory]
    [InlineData(AutoProfile.Cs2, ProcessPriorityClass.AboveNormal, true)]
    [InlineData(AutoProfile.Cs2, ProcessPriorityClass.High, true)]
    [InlineData(AutoProfile.Universal, ProcessPriorityClass.AboveNormal, true)]
    [InlineData(AutoProfile.Universal, ProcessPriorityClass.High, true)]
    [InlineData(AutoProfile.Universal, ProcessPriorityClass.Normal, false)]
    [InlineData(AutoProfile.Cs2, ProcessPriorityClass.Normal, false)]
    [InlineData(AutoProfile.Valorant, ProcessPriorityClass.High, false)]
    [InlineData(AutoProfile.Lol, ProcessPriorityClass.High, false)]
    public void Quem_ganha_disco_alto(AutoProfile profile, ProcessPriorityClass priority, bool expected) =>
        Assert.Equal(expected, GameModeEngine.RaisesIo(profile, priority));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Niveis_comuns_valem_na_hora(int level)
    {
        using var child = new Child();
        Assert.True(ProcessIo.SetIoPriority(child.Id, level));
        Assert.Equal(level, ProcessIo.GetIoPriority(child.Id));
    }

    [Fact]
    public void Nivel_alto_so_diz_que_aplicou_se_aplicou()
    {
        // Sem administrador o Windows recusa e o nível não muda. Como administrador (o tHg sempre é), o privilégio é ligado e tem de valer.
        using var child = new Child();
        bool ok = ProcessIo.SetIoPriority(child.Id, 3);
        Assert.Equal(ok ? 3 : 2, ProcessIo.GetIoPriority(child.Id));
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) Assert.True(ok);
    }

    [Fact]
    public void EcoQos_pelo_PID_liga_e_desliga()
    {
        using var child = new Child();
        Assert.True(PowerThrottling.SetEco(child.Id, false));
        Assert.True(PowerThrottling.Reset(child.Id));
    }

    [Fact]
    public void Nucleos_preferidos_com_handle_minimo()
    {
        using var child = new Child();
        Assert.True(CpuTopology.SetPreferredCores(child.P, NativePerfApi.AllCoresMask()));
    }

    [Fact]
    public void Nucleos_preferidos_pelo_PID_saem_ao_limpar()
    {
        // É o que devolve os programas de fundo depois do jogo: restaurar a afinidade não desfaz CPU Sets.
        using var child = new Child();
        Assert.False(CpuTopology.HasPreferredCores(child.Id));
        Assert.True(CpuTopology.SetPreferredCoresById(child.Id, 0b11));
        Assert.True(CpuTopology.HasPreferredCores(child.Id));
        Assert.True(CpuTopology.ClearPreferredCoresById(child.Id));
        Assert.False(CpuTopology.HasPreferredCores(child.Id));
    }

    [Fact]
    public void Limpar_processo_que_nao_existe_nao_lanca() => Assert.False(CpuTopology.ClearPreferredCoresById(0x7FFFFFF0));
}

public class GameModeDefaultsTests
{
    [Fact]
    public void Instalacao_nova_so_liga_com_12_threads_ou_mais()
    {
        var s = new AppSettings();
        bool expected = Environment.ProcessorCount >= 12;
        Assert.Equal(expected, s.GameModeAvoidCpu0);
        Assert.Equal(expected, s.GameModeBgCores);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Escolha_ja_salva_e_respeitada(bool saved)
    {
        string json = $"{{\"GameModeAvoidCpu0\":{(saved ? "true" : "false")},\"GameModeBgCores\":{(saved ? "true" : "false")}}}";
        var s = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(saved, s.GameModeAvoidCpu0);
        Assert.Equal(saved, s.GameModeBgCores);
    }
}
