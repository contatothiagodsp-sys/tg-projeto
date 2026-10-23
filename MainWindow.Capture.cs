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

    /// <summary>Espera 5 s (para o usuário voltar ao jogo), grava 60 s do programa em primeiro plano e mostra o resultado.</summary>
    public async Task CaptureAsync()
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
            string game;
            try { game = Process.GetProcessById(pid).ProcessName; } catch { game = $"PID {pid}"; }
            Toast("Gravando 60 s", $"{game}: jogue normalmente.", "");
            var start = DateTime.Now;
            int id = _frames.StartCapture(pid);
            for (int s = 60; s > 0; s--)
            {
                CaptureStatus.Text = $"Gravando {game}… {s} s";
                await Task.Delay(1000);
            }
            var times = _frames.StopCapture(id);
            if (times.Count < 30)
            {
                CaptureStatus.Text = $"{game} quase não apresentou frames (jogo minimizado, ou usa uma API que o Windows não informa).";
                return;
            }
            var r = FrameAnalysis.Analyze(game, start, times);
            string csv = await Task.Run(() => FrameAnalysis.SaveCsv(r, times));
            r = r with { CsvPath = csv };
            _settings.Captures.Insert(0, r);
            if (_settings.Captures.Count > 50) _settings.Captures.RemoveRange(50, _settings.Captures.Count - 50);
            _settings.Save();
            CaptureStatus.Text = $"{r.Summary} · arquivo: {csv}";
            Toast($"Gravação · {game}", r.Summary, "");
            UpdateCaptures();
        }
        finally
        {
            _capturing = false;
            BtnCapture.IsEnabled = true;
        }
    }

    private void UpdateCaptures()
    {
        // Diferença para a gravação anterior do mesmo jogo, para comparar antes e depois de uma mudança.
        var list = _settings.Captures.Take(10).Select(c =>
        {
            var prev = _settings.Captures.SkipWhile(x => x != c).Skip(1).FirstOrDefault(x => x.Game == c.Game);
            string delta = prev != null && prev.AvgFps > 0 ? $" · {(c.AvgFps / prev.AvgFps - 1) * 100:+0;-0}% FPS, 1% low {(c.Low1Fps / Math.Max(1, prev.Low1Fps) - 1) * 100:+0;-0}%" : "";
            return new CaptureRow($"{c.Game} · {c.Start:dd/MM HH:mm} · {c.Seconds:0} s", c.Summary + delta);
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
