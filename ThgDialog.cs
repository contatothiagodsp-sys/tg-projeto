using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace ThgOptimization;

/// <summary>
/// Diálogo do tHg no lugar da caixa padrão do Windows (MessageBox): escurece a janela do app por trás e mostra um cartão
/// no tema atual (escuro/claro), com ícone, título, texto e botões com rótulos naturais ("Aplicar", "Agora não").
/// Mesma assinatura do MessageBox.Show para trocar sem mexer na lógica: devolve MessageBoxResult.
/// </summary>
public static class ThgDialog
{
    public static MessageBoxResult Show(string text, string caption = "tHg Optimization",
                                        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
        => Show(null, text, caption, buttons, icon);

    public static MessageBoxResult Show(Window? owner, string text, string caption = "tHg Optimization",
                                        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
    {
        var (yes, no) = buttons switch
        {
            MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel => ("Sim", "Não"),
            MessageBoxButton.OKCancel => ("OK", "Cancelar"),
            _ => ("OK", (string?)null),
        };
        bool ok = Run(owner, caption, text, yes, no, icon);
        return buttons switch
        {
            MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel => ok ? MessageBoxResult.Yes : MessageBoxResult.No,
            MessageBoxButton.OKCancel => ok ? MessageBoxResult.OK : MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK,
        };
    }

    /// <summary>Pergunta com rótulos próprios. true = botão principal.</summary>
    public static bool Confirm(string title, string text, string yes, string no = "Cancelar", MessageBoxImage icon = MessageBoxImage.Question)
        => Run(null, title, text, yes, no, icon);

    /// <summary>Aviso com um botão só.</summary>
    public static void Info(string title, string text, MessageBoxImage icon = MessageBoxImage.Information, string ok = "Entendi")
        => Run(null, title, text, ok, null, icon);

    // ------------------------------------------------------------------

    private static Brush B(string key) => (Brush)Application.Current!.FindResource(key);

    private static bool Run(Window? owner, string title, string text, string yes, string? no, MessageBoxImage icon)
    {
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        bool result = false;

        // Janela transparente do tamanho da janela do app: o fundo escurecido é o próprio "modal".
        var win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, Title = title,
            Owner = owner is { IsLoaded: true, IsVisible: true } ? owner : null,
            WindowStartupLocation = WindowStartupLocation.Manual, Topmost = owner == null,
        };
        PlaceOver(win, owner);

        var shade = new Border { Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), Opacity = 0 };
        var card = new Border
        {
            Background = B("Card"), BorderBrush = B("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18),
            Padding = new Thickness(26, 22, 26, 20), MaxWidth = 520, MinWidth = 380,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.96, 0.96), Opacity = 0,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Opacity = 0.45, Color = Colors.Black },
        };

        var stack = new StackPanel();

        // Cabeçalho: marca + título
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        try
        {
            var logo = new Image
            {
                Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")) // o mesmo símbolo nos dois temas,
            };
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            header.Children.Add(logo);
        }
        catch { }
        header.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = B("Muted"),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        stack.Children.Add(header);

        // Ícone + texto
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        string? glyph = icon switch
        {
            MessageBoxImage.Question => "",
            MessageBoxImage.Warning => "",
            MessageBoxImage.Error => "",
            MessageBoxImage.Information => "",
            _ => null,
        };
        if (glyph != null)
        {
            var circle = new Border
            {
                Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top,
                Background = icon is MessageBoxImage.Warning or MessageBoxImage.Error ? B("Fg") : B("Hover"),
                Child = new TextBlock
                {
                    Text = glyph, FontFamily = (FontFamily)Application.Current!.FindResource("Icons"), FontSize = 18,
                    Foreground = icon is MessageBoxImage.Warning or MessageBoxImage.Error ? B("Ink") : B("Fg"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            body.Children.Add(circle);
        }
        var textBlock = new TextBlock
        {
            Text = text.Trim(), TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 20, Foreground = B("Fg"),
        };
        var scroll = new ScrollViewer { Content = textBlock, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 2, 6, 0) };
        Grid.SetColumn(scroll, 1);
        body.Children.Add(scroll);
        stack.Children.Add(body);

        // Botões
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        bool closing = false;
        void Close(bool r)
        {
            if (closing) return;
            closing = true;
            result = r;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
            fade.Completed += (_, _) => win.Close();
            shade.BeginAnimation(UIElement.OpacityProperty, fade);
            card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(110)));
        }
        if (no != null)
        {
            var bNo = new Button { Content = no.ToUpperInvariant(), Style = (Style)Application.Current!.FindResource("OutlineButton"),
                                   Padding = new Thickness(18, 9, 18, 9), FontSize = 12, Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
            bNo.Click += (_, _) => Close(false);
            buttons.Children.Add(bNo);
        }
        var bYes = new Button { Content = yes.ToUpperInvariant(), Style = (Style)Application.Current!.FindResource("PrimaryButton"),
                                Padding = new Thickness(22, 9, 22, 9), FontSize = 12, IsDefault = true, IsCancel = no == null };
        bYes.Click += (_, _) => Close(true);
        buttons.Children.Add(bYes);
        stack.Children.Add(buttons);

        card.Child = stack;
        var root = new Grid();
        root.Children.Add(shade);
        root.Children.Add(card);
        win.Content = root;
        win.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(no == null); e.Handled = true; } };
        shade.MouseLeftButtonDown += (_, _) => { if (no == null) Close(true); }; // clicar fora fecha avisos, nunca perguntas

        win.Loaded += (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var t = TimeSpan.FromMilliseconds(170);
            shade.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, t));
            card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, t));
            var st = (ScaleTransform)card.RenderTransform;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, t) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, t) { EasingFunction = ease });
            bYes.Focus();
        };
        win.ShowDialog();
        return result;
    }

    /// <summary>Cobre exatamente a área da janela do app (também maximizada e com escala de DPI).</summary>
    private static void PlaceOver(Window win, Window? owner)
    {
        if (owner == null || !owner.IsVisible || owner.WindowState == WindowState.Minimized)
        {
            win.Width = 560; win.Height = 420; win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }
        var h = new WindowInteropHelper(owner).Handle;
        if (h != IntPtr.Zero && GetWindowRect(h, out var r))
        {
            var dpi = VisualTreeHelper.GetDpi(owner);
            win.Left = r.Left / dpi.DpiScaleX; win.Top = r.Top / dpi.DpiScaleY;
            win.Width = (r.Right - r.Left) / dpi.DpiScaleX; win.Height = (r.Bottom - r.Top) / dpi.DpiScaleY;
        }
        else { win.Left = owner.Left; win.Top = owner.Top; win.Width = owner.ActualWidth; win.Height = owner.ActualHeight; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
}
