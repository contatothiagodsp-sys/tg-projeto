using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using ThgOptimization.Infrastructure;

namespace ThgOptimization;

/// <summary>Como os frames de um jogo chegam à tela. Mesmos nomes do PresentMon, de onde vêm os critérios.</summary>
public enum PresentationMode
{
    Unknown,
    HardwareLegacyFlip,
    HardwareLegacyCopy,
    HardwareIndependentFlip,
    HardwareComposedIndependentFlip,
    ComposedFlip,
    ComposedCopyGpuGdi,
    ComposedCopyCpuGdi,
}

public enum PresentationCollectionState { Completed, Unavailable }

/// <summary>O que foi contado para um processo durante a amostra.</summary>
/// <param name="Presents">Present_Start de DXGI ou D3D9.</param>
/// <param name="FlipTokens">Win32k TokenCompositionSurfaceObject: o present entrou no modelo flip.</param>
/// <param name="InFrameIndependent">Win32k TokenStateChanged em InFrame com IndependentFlip ligado.</param>
/// <param name="InFrameComposed">Win32k TokenStateChanged em InFrame, composto pelo DWM.</param>
/// <param name="BlitsRedirected">DxgKrnl Blit com bRedirectedPresent: cópia pela CPU.</param>
/// <param name="BlitsDirect">DxgKrnl Blit sem redirecionamento.</param>
/// <param name="PresentHistory">DxgKrnl PresentHistory: distingue a cópia composta da cópia direta na tela.</param>
/// <param name="LegacyFlips">DxgKrnl Flip do próprio processo.</param>
/// <param name="MultiPlaneVSyncs">VSyncDPCMultiPlane com mais de um plano ativo, no sistema inteiro: não dá para atribuir a um processo.</param>
/// <param name="VSyncs">Total de VSyncDPCMultiPlane no sistema.</param>
/// <param name="DwmEvents">Eventos do DWM recebidos: só mostram que a coleta do DWM está funcionando.</param>
public sealed record PresentationEvidence(long Presents = 0, long FlipTokens = 0, long InFrameIndependent = 0, long InFrameComposed = 0, long BlitsRedirected = 0,
                                          long BlitsDirect = 0, long PresentHistory = 0, long LegacyFlips = 0, long MultiPlaneVSyncs = 0, long VSyncs = 0, long DwmEvents = 0);

public sealed record PresentationReport(PresentationMode Mode, DetectionConfidence Confidence, PresentationCollectionState State, double DwmShare, string Limitations, bool Alert)
{
    public PresentationEvidence? Evidence { get; init; }

    public string ModeText => Mode switch
    {
        PresentationMode.HardwareIndependentFlip => "Hardware: Independent Flip",
        PresentationMode.HardwareComposedIndependentFlip => "Hardware Composed: Independent Flip",
        PresentationMode.HardwareLegacyFlip => "Hardware: Legacy Flip",
        PresentationMode.HardwareLegacyCopy => "Hardware: Legacy Copy",
        PresentationMode.ComposedFlip => "Composed: Flip",
        PresentationMode.ComposedCopyGpuGdi => "Composed: Copy with GPU GDI",
        PresentationMode.ComposedCopyCpuGdi => "Composed: Copy with CPU GDI",
        _ => "Unknown / dados de ETW insuficientes",
    };

    public string Text => State == PresentationCollectionState.Unavailable
        ? $"Modo de apresentação indisponível: {Limitations}"
        : $"{ModeText} · confiança {ConfidenceText} · DWM em {DwmShare:P0} dos frames" + (Limitations.Length > 0 ? $" · {Limitations}" : "");

    private string ConfidenceText => Confidence switch
    {
        DetectionConfidence.High => "alta",
        DetectionConfidence.Medium => "média",
        DetectionConfidence.Low => "baixa",
        _ => "nenhuma",
    };

    public static PresentationReport Unavailable(string why) => new(PresentationMode.Unknown, DetectionConfidence.Unknown, PresentationCollectionState.Unavailable, 0, why, false);
}

/// <summary>Amostra o modo de apresentação sob demanda. Separado do FrameMonitor: os eventos do DxgKrnl são muitos e não entram na medição de frames.</summary>
public interface IPresentationModeMonitor
{
    /// <summary>Nunca lança por falha de coleta: devolve State = Unavailable, e o app segue normalmente.</summary>
    Task<PresentationReport> SampleAsync(int pid, TimeSpan duration, CancellationToken cancel = default);
}

/// <summary>
/// Decide o modo a partir das contagens, seguindo a lógica do PresentMon (PresentMonTraceConsumer.cpp). O PresentMon acompanha cada present
/// do início ao fim; aqui se contam eventos numa amostra curta, então só uma maioria clara vira resposta. Resto é Unknown.
/// </summary>
public static class PresentationClassifier
{
    public const int MinPresents = 30;
    private const double HighShare = 0.9, MediumShare = 0.7, MultiPlaneShare = 0.5;

    public static PresentationReport Classify(PresentationEvidence e)
    {
        if (e.Presents < MinPresents)
            return Report(PresentationMode.Unknown, DetectionConfidence.Unknown, 0, $"Só {e.Presents} frames na amostra: o jogo precisa estar desenhando em primeiro plano.", e);

        long flips = e.InFrameIndependent + e.InFrameComposed;
        long blits = e.BlitsRedirected + e.BlitsDirect;
        long total = flips + blits + e.LegacyFlips;
        if (total < MinPresents)
            return Report(PresentationMode.Unknown, DetectionConfidence.Unknown, 0, "Os eventos do Win32k e do DxgKrnl não chegaram em quantidade suficiente.", e);

        // Sem token do Win32k, um Flip é legado. Com token, o PresentMon trata o present como Composed Flip até saber do Independent Flip.
        double dwm = (double)(e.InFrameComposed + e.BlitsDirect + e.BlitsRedirected) / total;
        var (mode, count) = Dominant(e);
        double share = (double)count / total;
        var confidence = share >= HighShare ? DetectionConfidence.High : share >= MediumShare ? DetectionConfidence.Medium : DetectionConfidence.Unknown;
        if (confidence == DetectionConfidence.Unknown)
            return Report(PresentationMode.Unknown, confidence, dwm, "Modos misturados na amostra: houve troca durante a coleta, ou a janela mudou de estado.", e);

        string limits = "Amostra curta, por contagem de eventos.";
        if (mode == PresentationMode.HardwareIndependentFlip && e.VSyncs > 0 && (double)e.MultiPlaneVSyncs / e.VSyncs >= MultiPlaneShare)
        {
            mode = PresentationMode.HardwareComposedIndependentFlip;
            confidence = DetectionConfidence.Low;
            limits = "Mais de um plano ativo na tela, mas sem ligação a este processo: outra janela pode ser o motivo.";
        }
        if (mode == PresentationMode.ComposedFlip) limits += " Composição pelo DWM não significa, por si só, latência problemática.";
        return Report(mode, confidence, dwm, limits, e);
    }

    private static (PresentationMode, long) Dominant(PresentationEvidence e)
    {
        var options = new (PresentationMode Mode, long Count)[]
        {
            (PresentationMode.HardwareIndependentFlip, e.InFrameIndependent),
            (PresentationMode.ComposedFlip, e.InFrameComposed),
            (PresentationMode.ComposedCopyCpuGdi, e.BlitsRedirected),
            (e.PresentHistory > 0 ? PresentationMode.ComposedCopyGpuGdi : PresentationMode.HardwareLegacyCopy, e.BlitsDirect),
            (PresentationMode.HardwareLegacyFlip, e.FlipTokens > 0 ? 0 : e.LegacyFlips),
        };
        var best = options[0];
        foreach (var o in options) if (o.Count > best.Count) best = o;
        return best;
    }

    /// <summary>Alerta só com evidência suficiente de cópia pela composição ou de apresentação legada. Composed Flip, sozinho, não alerta.</summary>
    private static PresentationReport Report(PresentationMode mode, DetectionConfidence confidence, double dwm, string limits, PresentationEvidence e)
    {
        bool alert = confidence is DetectionConfidence.High or DetectionConfidence.Medium
                     && mode is PresentationMode.ComposedCopyGpuGdi or PresentationMode.ComposedCopyCpuGdi or PresentationMode.HardwareLegacyCopy or PresentationMode.HardwareLegacyFlip;
        return new PresentationReport(mode, confidence, PresentationCollectionState.Completed, dwm, limits, alert) { Evidence = e };
    }
}

/// <summary>
/// Sessão ETW própria e curta, só quando o usuário pede. Ids, keywords e nomes de campos vêm de PresentData/ETW do PresentMon. A leitura dos
/// campos pelo nome é mais cara que a do FrameMonitor, o que é aceitável aqui e fica fora do caminho de frames.
/// </summary>
public sealed class EtwPresentationModeMonitor : IPresentationModeMonitor
{
    private const string SessionName = "tHgOptimization-Presentation";
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9"), D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9"),
                                 DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D"), Win32k = new("8C416C79-D49B-4F01-A467-E56D3AA8234C"),
                                 DwmCore = new("9E9BBA3C-2E38-40CB-99F4-9E8281425164");
    private const ulong PresentKeywords = 0x8000000000000002, DxgKrnlKeywords = 0x4000000008000001, Win32kKeywords = 0x8000000400001000, DwmKeywords = 0x8000000000000081;
    private const int DxgiPresent = 42, DxgiPresentMpo = 55, D3D9Present = 1;
    private const int DxgkBlit = 0xA6, DxgkFlip = 0xA8, DxgkPresentHistoryStart = 0xAB, DxgkPresentHistoryDetailed = 0xD7, DxgkVSyncMultiPlane = 0x111;
    private const int Win32kTokenSurface = 0xC9, Win32kTokenState = 0x12D, TokenStateInFrame = 3;
    private const int MaxTrackedTokens = 8192;

    public async Task<PresentationReport> SampleAsync(int pid, TimeSpan duration, CancellationToken cancel = default)
    {
        TraceEventSession? session = null;
        Task? pump = null;
        try
        {
            var counter = new Counter(pid);
            session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create) { StopOnDispose = true };
            session.EnableProvider(Dxgi, TraceEventLevel.Informational, PresentKeywords);
            session.EnableProvider(D3D9, TraceEventLevel.Informational, PresentKeywords);
            session.EnableProvider(DxgKrnl, TraceEventLevel.Informational, DxgKrnlKeywords);
            session.EnableProvider(Win32k, TraceEventLevel.Informational, Win32kKeywords);
            session.EnableProvider(DwmCore, TraceEventLevel.Informational, DwmKeywords);
            var parser = new RegisteredTraceEventParser(session.Source);
            parser.All += counter.OnEvent;
            var active = session;
            // Parar a sessão antes de o processamento começar faz Process lançar: a amostra já terminou, então só registra.
            pump = Task.Factory.StartNew(() =>
            {
                try { active.Source.Process(); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException) { AppLog.Error("apresentação", ex); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await Task.Delay(duration, cancel).ConfigureAwait(false);
            return PresentationClassifier.Classify(counter.Evidence());
        }
        catch (OperationCanceledException) { return PresentationReport.Unavailable("Coleta cancelada."); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            AppLog.Error("apresentação", ex);
            return PresentationReport.Unavailable(ex is UnauthorizedAccessException ? "é preciso rodar como administrador." : ex.Message);
        }
        finally
        {
            if (session != null)
            {
                // Source só existe numa sessão que chegou a abrir: sem administrador, pedi-la aqui lançaria por cima do motivo real.
                try { if (pump != null) session.Source.StopProcessing(); session.Dispose(); }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException) { AppLog.Error("apresentação", ex); }
            }
            if (pump != null)
                try { await pump.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ObjectDisposedException) { AppLog.Error("apresentação", ex); }
        }
    }

    /// <summary>Roda na thread da sessão. A leitura final acontece com os contadores ainda mudando: uma amostra não precisa de exatidão por frame.</summary>
    private sealed class Counter
    {
        private readonly int _pid;
        private readonly HashSet<(ulong Luid, ulong Count, ulong Bind)> _tokens = new();
        private long _presents, _flipTokens, _independent, _composed, _blitsRedirected, _blitsDirect, _history, _flips, _multiPlane, _vsyncs, _dwm;

        public Counter(int pid) => _pid = pid;

        public void OnEvent(TraceEvent e)
        {
            var provider = e.ProviderGuid;
            int id = (int)e.ID;
            try
            {
                if (provider == Dxgi) { if (e.ProcessID == _pid && id is DxgiPresent or DxgiPresentMpo) Interlocked.Increment(ref _presents); }
                else if (provider == D3D9) { if (e.ProcessID == _pid && id == D3D9Present) Interlocked.Increment(ref _presents); }
                else if (provider == Win32k) OnWin32k(e, id);
                else if (provider == DxgKrnl) OnDxgKrnl(e, id);
                else if (provider == DwmCore) Interlocked.Increment(ref _dwm);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or ArgumentException or OverflowException or IndexOutOfRangeException)
            {
                AppLog.Error("apresentação", ex); // campo em outro formato nesta versão do Windows: a contagem fica menor e o resultado tende a Unknown
            }
        }

        private void OnWin32k(TraceEvent e, int id)
        {
            if (id == Win32kTokenSurface && e.ProcessID == _pid)
            {
                Interlocked.Increment(ref _flipTokens);
                if (_tokens.Count >= MaxTrackedTokens) _tokens.Clear();
                _tokens.Add(Key(e));
            }
            else if (id == Win32kTokenState && Convert.ToInt32(e.PayloadByName("NewState")) == TokenStateInFrame && _tokens.Remove(Key(e)))
            {
                // Quem emite este evento é o DWM: o processo só é conhecido pela chave guardada acima.
                if (Convert.ToInt32(e.PayloadByName("IndependentFlip")) != 0) Interlocked.Increment(ref _independent);
                else Interlocked.Increment(ref _composed);
            }
        }

        private static (ulong, ulong, ulong) Key(TraceEvent e) =>
            (Convert.ToUInt64(e.PayloadByName("CompositionSurfaceLuid")), Convert.ToUInt64(e.PayloadByName("PresentCount")), Convert.ToUInt64(e.PayloadByName("BindId")));

        private void OnDxgKrnl(TraceEvent e, int id)
        {
            if (id == DxgkVSyncMultiPlane)
            {
                Interlocked.Increment(ref _vsyncs);
                if (e.PayloadByName("PresentIdOrPhysicalAddress") is Array planes)
                {
                    int active = 0;
                    foreach (object? plane in planes) if (Convert.ToUInt64(plane) != 0) active++;
                    if (active > 1) Interlocked.Increment(ref _multiPlane);
                }
                return;
            }
            if (e.ProcessID != _pid) return;
            switch (id)
            {
                case DxgkBlit:
                    if (Convert.ToInt32(e.PayloadByName("bRedirectedPresent")) != 0) Interlocked.Increment(ref _blitsRedirected);
                    else Interlocked.Increment(ref _blitsDirect);
                    break;
                case DxgkFlip: Interlocked.Increment(ref _flips); break;
                case DxgkPresentHistoryStart or DxgkPresentHistoryDetailed: Interlocked.Increment(ref _history); break;
            }
        }

        public PresentationEvidence Evidence() => new(Interlocked.Read(ref _presents), Interlocked.Read(ref _flipTokens), Interlocked.Read(ref _independent), Interlocked.Read(ref _composed),
            Interlocked.Read(ref _blitsRedirected), Interlocked.Read(ref _blitsDirect), Interlocked.Read(ref _history), Interlocked.Read(ref _flips),
            Interlocked.Read(ref _multiPlane), Interlocked.Read(ref _vsyncs), Interlocked.Read(ref _dwm));
    }
}
