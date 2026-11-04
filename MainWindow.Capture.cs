using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace ThgOptimization;

/// <summary>Linha da lista de gravações.</summary>
public sealed record CaptureRow(string Title, string Line);

/// <summary>Gravação de frametime de 60 s (como o CapFrameX) e exportação das sessões para planilha.</summary>
public partial class MainWindow
{
    private bool _capturing;

    private async void BtnCapture_Click(object sender, RoutedEventArgs e) => await CaptureAsync();

    /// <summary>Espera 5 s (para o usuário voltar ao jogo), grava 60 s do programa em primeiro plano e mostra o resultado. Falha vira aviso: quem chama pode ser o botão ou o menu da bandeja.</summary>
    public Task CaptureAsync() =>
        Infrastructure.UiGuard.RunAsync("captura", CaptureCoreAsync, ex => CaptureStatus.Text = "A gravação falhou: " + Err(ex));

    private async Task CaptureCoreAsync()
    {
        if (_capturing) return;
        _capturing = true;
        BtnCapture.IsEnabled = false;
        ManageFrameMonitor(); // liga o medidor, se estava desligado
        try
        {
            for (int i = 5; i > 0; i--)
            {
                CaptureStatus.Text = $"Volte para o jogo: a gravação começa em {i} s…";
                await Task.Delay(1000);
            }
            int pid = ForegroundPid();
            if (pid == 0 || pid == Environment.ProcessId)
            {
                CaptureStatus.Text = "Nenhum jogo em primeiro plano. Clique em GRAVAR e volte para o jogo em até 5 segundos.";
                return;
            }
            // Sem abrir o processo: o jogo em primeiro plano pode ser o Valorant (Vanguard-safe).
            string game = Infrastructure.ProcessUtil.NameOf(pid) ?? $"PID {pid}";
            Toast("Gravando 60 s", $"{game}: jogue normalmente.", "");
            var r = await RecordFramesAsync(pid, game, s => CaptureStatus.Text = $"Gravando {game}… {s} s");
            if (r == null)
            {
                CaptureStatus.Text = $"{game} quase não apresentou frames (jogo minimizado, ou usa uma API que o Windows não informa).";
                return;
            }
            _settings.Mutate(() =>
            {
                _settings.Captures.Insert(0, r);
                if (_settings.Captures.Count > 50) _settings.Captures.RemoveRange(50, _settings.Captures.Count - 50);
            });
            _settings.Save();
            CaptureStatus.Text = $"{r.Summary} · {r.DetailText} · arquivo: {r.CsvPath}";
            Toast($"Gravação · {game}", r.Summary, "");
            UpdateCaptures();
        }
        finally
        {
            _capturing = false;
            BtnCapture.IsEnabled = true;
        }
    }

    /// <summary>Grava até 60 s do processo (ou até stop), analisa e salva o CSV. Null se ele quase não apresentou frames.</summary>
    private async Task<CaptureResult?> RecordFramesAsync(int pid, string game, Action<int> onSecond, CancellationToken stop = default)
    {
        var start = DateTime.Now;
        int id = _frames.StartCapture(pid, TimeSpan.FromSeconds(60), 240);
        try
        {
            for (int s = 60; s > 0; s--)
            {
                onSecond(s);
                await Task.Delay(1000, stop);
            }
        }
        catch (OperationCanceledException) { } // parou antes: quem pediu decide o que fazer com o que foi gravado
        var times = _frames.StopCapture(id, out long dropped);
        if (times.Count < 30) return null;
        var r = await Task.Run(() => FrameAnalysis.Analyze(game, start, times, dropped)); // ordena e classifica: fora da janela
        string csv = await Task.Run(() => FrameAnalysis.SaveCsv(r, times));
        return r with { CsvPath = csv };
    }

    private void UpdateCaptures()
    {
        // Diferença para a gravação anterior do mesmo jogo, para comparar antes e depois de uma mudança.
        var list = _settings.Captures.Take(10).Select(c =>
        {
            var prev = _settings.Captures.SkipWhile(x => x != c).Skip(1).FirstOrDefault(x => x.Game == c.Game);
            string delta = prev != null && prev.AvgFps > 0 ? $" · {(c.AvgFps / prev.AvgFps - 1) * 100:+0;-0}% FPS, 1% low {(c.Low1Fps / Math.Max(1, prev.Low1Fps) - 1) * 100:+0;-0}%" : "";
            return new CaptureRow($"{c.Game} · {c.Start:dd/MM HH:mm} · {c.Seconds:0} s", c.Summary + delta + (c.DetailText.Length > 0 ? " · " + c.DetailText : ""));
        }).ToList();
        CaptureList.ItemsSource = list;
    }

    private void BtnExportSessions_Click(object sender, RoutedEventArgs e)
    {
        string path = FrameAnalysis.ExportSessions(_settings.GameSessions, _settings.Captures);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
        Toast("Sessões exportadas", path, "");
    }

    private static int ForegroundPid()
    {
        IntPtr h = GetForegroundWindowCap();
        if (h == IntPtr.Zero) return 0;
        GetWindowThreadProcessIdCap(h, out uint pid);
        return (int)pid;
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern IntPtr GetForegroundWindowCap();
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static extern uint GetWindowThreadProcessIdCap(IntPtr h, out uint pid);
}
