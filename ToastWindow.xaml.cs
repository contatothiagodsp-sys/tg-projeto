using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ThgOptimization;

/// <summary>O DESFAZER de um aviso: roda uma vez só, e some com o aviso. Usar só onde existe como voltar de verdade.</summary>
internal sealed class ToastUndo
{
    private Action? _action;
    public bool Available => _action != null;
    public void Set(Action? action) => _action = action;
    public void Clear() => _action = null;

    public void Invoke()
    {
        var action = _action;
        _action = null;
        action?.Invoke();
    }
}

/// <summary>Aviso no canto inferior direito da tela (dispositivo conectado/removido). Não rouba o foco.</summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(5) };

    private readonly ToastUndo _undo = new();
    internal Visibility UndoVisibility => TUndo.Visibility;

    public ToastWindow()
    {
        InitializeComponent();
        TUndo.Click += (_, _) =>
        {
            _undo.Invoke();
            TUndo.Visibility = Visibility.Collapsed;
            _hide.Stop();
            Fade(0, Hide);
        };
        SourceInitialized += (_, _) =>
        {
            Infrastructure.WindowExStyle.Add(new WindowInteropHelper(this).Handle, 0x80 | 0x8000000); // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        };
        SizeChanged += (_, _) => Place();
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            _undo.Clear(); // o prazo do DESFAZER é o do aviso
            TUndo.Visibility = Visibility.Collapsed;
            Fade(0, () => Hide());
        };
        Closed += (_, _) => _hide.Stop(); // Hide numa janela já fechada lançaria exceção
    }

    /// <summary>Mostra (ou atualiza, se já visível) o aviso por 5 s. icon: glifo Segoe MDL2.</summary>
    public void Notify(string title, string text, string icon, Action? undo = null)
    {
        _undo.Set(undo);
        TUndo.Visibility = undo != null ? Visibility.Visible : Visibility.Collapsed;
        TTitle.Text = title;
        TText.Text = text;
        TIcon.Text = icon;
        if (!IsVisible) Show();
        Place(); // já visível também: a barra de tarefas ou a resolução podem ter mudado desde o último aviso
        Fade(1, null);
        _hide.Stop();
        _hide.Start();
    }

    private void Place()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 16;
        Top = wa.Bottom - ActualHeight - 16;
    }

    private void Fade(double to, Action? done)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(180));
        if (done != null) anim.Completed += (_, _) => done();
        BeginAnimation(OpacityProperty, anim);
    }
}
