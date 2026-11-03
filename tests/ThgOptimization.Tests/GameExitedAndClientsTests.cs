using System.Diagnostics;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

public class ProcessHasExitedTests
{
    private static readonly List<ProcessSnapshot> List = new()
    {
        new(100, "VALORANT-Win64-Shipping", 0),
        new(200, "outro", 0),
    };

    [Fact]
    public void Na_lista_com_o_mesmo_nome_continua_aberto() =>
        Assert.False(ProcessUtil.HasExited(List, 100, "valorant-win64-shipping"));

    [Fact]
    public void Fora_da_lista_fechou() => Assert.True(ProcessUtil.HasExited(List, 300, "VALORANT-Win64-Shipping"));

    [Fact]
    public void PID_reaproveitado_por_outro_programa_conta_como_fechado() =>
        Assert.True(ProcessUtil.HasExited(List, 200, "VALORANT-Win64-Shipping"));

    [Fact]
    public void Lista_vazia_nao_encerra_a_sessao() =>
        Assert.False(ProcessUtil.HasExited(new List<ProcessSnapshot>(), 100, "VALORANT-Win64-Shipping"));

    [Fact]
    public void Com_a_lista_real()
    {
        using var child = new Child();
        var name = child.P.ProcessName;
        Assert.False(ProcessUtil.HasExited(ProcessUtil.Snapshot(), child.Id, name));
        child.P.Kill();
        child.P.WaitForExit(5000);
        Assert.True(ProcessUtil.HasExited(ProcessUtil.Snapshot(), child.Id, name));
    }
}

/// <summary>
/// Nada aqui rebaixa de verdade: ao aplicar, ClientDemoter grava as configurações reais do usuário. Com all = 0 a afinidade
/// nunca confere, então o alvo é aberto e recusado, e o caminho de filtro e de abertura é testado sem efeito colateral.
/// </summary>
public class ClientDemoterTests
{
    private const ulong NoMatch = 0;
    private const string Client = "LeagueClientUx";

    private static (ClientDemoter Demoter, Func<int> Opened) New(params string[] targets)
    {
        var demoter = new ClientDemoter(new AppSettings());
        demoter.SetTargets(targets);
        int opened = 0;
        demoter.Open = id => { opened++; return Process.GetProcessById(id); };
        return (demoter, () => opened);
    }

    private static List<ProcessSnapshot> One(int pid, string name) => new() { new ProcessSnapshot(pid, name, 0) };

    [Fact]
    public void So_abre_o_que_esta_na_lista_de_alvos()
    {
        using var child = new Child();
        var (demoter, opened) = New(ClientDemoter.LolClients);
        Assert.Equal(0, demoter.Apply(One(child.Id, "outro-programa"), null, NoMatch));
        Assert.Equal(0, opened());
        Assert.Equal(0, demoter.Apply(One(child.Id, Client), null, NoMatch));
        Assert.Equal(1, opened());
    }

    [Fact]
    public void Nome_compara_sem_diferenciar_maiusculas()
    {
        using var child = new Child();
        var (demoter, opened) = New(ClientDemoter.LolClients);
        demoter.Apply(One(child.Id, Client.ToLowerInvariant()), null, NoMatch);
        Assert.Equal(1, opened());
    }

    [Fact]
    public void Recusado_nao_e_reaberto_a_cada_rodada()
    {
        using var child = new Child();
        var (demoter, opened) = New(ClientDemoter.LolClients);
        for (int i = 0; i < 3; i++) demoter.Apply(One(child.Id, Client), null, NoMatch);
        Assert.Equal(1, opened());
    }

    [Fact]
    public void PID_que_sumiu_e_voltou_e_avaliado_de_novo()
    {
        using var child = new Child();
        var (demoter, opened) = New(ClientDemoter.LolClients);
        demoter.Apply(One(child.Id, Client), null, NoMatch);
        demoter.Apply(One(child.Id + 4, "outro-programa"), null, NoMatch); // o PID saiu da lista
        demoter.Apply(One(child.Id, Client), null, NoMatch);
        Assert.Equal(2, opened());
    }

    [Fact]
    public void Afinidade_diferente_nao_e_mexida()
    {
        using var child = new Child();
        using var before = Process.GetProcessById(child.Id);
        IntPtr affinity = before.ProcessorAffinity;
        var (demoter, _) = New(ClientDemoter.LolClients);
        Assert.Equal(0, demoter.Apply(One(child.Id, Client), 0b11, NoMatch));
        using var after = Process.GetProcessById(child.Id);
        Assert.Equal(affinity, after.ProcessorAffinity);
        Assert.Equal(ProcessPriorityClass.Normal, child.Priority);
    }

    [Fact]
    public void Acesso_negado_nao_lanca_nem_tenta_abrir()
    {
        var csrss = Process.GetProcessesByName("csrss");
        int pid = csrss[0].Id;
        foreach (var p in csrss) p.Dispose();
        var (demoter, opened) = New(ClientDemoter.LolClients);
        Assert.Equal(0, FirstChance.CountWin32(() => demoter.Apply(One(pid, Client), null, NoMatch)));
        Assert.Equal(0, opened());
    }

    [Fact]
    public void Vanguard_nunca_e_aberto_mesmo_se_estiver_nos_alvos()
    {
        using var child = new Child();
        var (demoter, opened) = New("vgc");
        Assert.Equal(0, demoter.Apply(One(child.Id, "vgc"), null, NoMatch));
        Assert.Equal(0, opened());
    }

    [Fact]
    public void Lista_vazia_nao_faz_nada()
    {
        var (demoter, opened) = New(ClientDemoter.LolClients);
        Assert.Equal(0, demoter.Apply(new List<ProcessSnapshot>(), null, NoMatch));
        Assert.Equal(0, opened());
    }

    [Fact]
    public void Sem_alvos_o_Sample_nem_le_a_lista()
    {
        // CS2 e Universal: chamado a cada tick, sem nenhum alvo, tem de sair na primeira linha.
        var (demoter, opened) = New();
        Assert.Equal(0, demoter.Sample(force: true));
        Assert.Equal(0, opened());
    }
}
