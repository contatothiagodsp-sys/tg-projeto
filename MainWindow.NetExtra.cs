using System.Windows;
using System.Windows.Controls;

namespace ThgOptimization;

/// <summary>HARDWARE · Rede: servidores de jogos e comparação/troca de DNS.</summary>
public partial class MainWindow
{
    private async void BtnServers_Click(object sender, RoutedEventArgs e)
    {
        BtnServers.IsEnabled = false;
        ServersStatus.Text = "Medindo servidores e DNS (uns 20 segundos)…";
        try
        {
            var servers = await NetExtra.PingServersAsync(CancellationToken.None);
            ServerList.ItemsSource = servers;
            var dns = await NetExtra.BenchmarkDnsAsync(CancellationToken.None);
            DnsList.ItemsSource = dns;
            var best = dns.FirstOrDefault(d => d.Ms != null);
            var cur = dns.FirstOrDefault(d => d.Current);
            ServersStatus.Text = best == null ? "Nenhum DNS respondeu: confira a conexão."
                : cur?.Ms is double c && best.Ms is double b && c - b > 5 && !best.Current
                    ? $"O DNS {best.Name} respondeu {c - b:0} ms mais rápido que o seu. Isso acelera a entrada em partidas e sites, não o ping dentro do jogo."
                    : "O DNS que você usa já está entre os mais rápidos.";
        }
        catch (Exception ex) { ServersStatus.Text = $"Não foi possível medir: {ex.Message}"; }
        BtnServers.IsEnabled = true;
    }

    private void BtnUseDns_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DnsResult d } || !RequireAdmin()) return;
        var pub = NetExtra.PublicDns.FirstOrDefault(p => p.Primary == d.Server);
        if (pub.Primary == null) { ServersStatus.Text = "Esse já é o DNS do seu roteador ou provedor. Use \"Voltar ao DNS anterior\" para retornar a ele."; return; }
        string err = NetExtra.SetDns(pub.Primary, pub.Secondary, _settings);
        ServersStatus.Text = err.Length == 0 ? $"DNS {pub.Name} em uso ({pub.Primary} e {pub.Secondary}). \"Voltar ao DNS anterior\" desfaz." : $"Não foi possível trocar: {err}";
    }

    private void BtnDnsRestore_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        string err = NetExtra.RestoreDns(_settings);
        ServersStatus.Text = err.Length == 0 ? "DNS voltou a ser como antes." : err;
    }
}
