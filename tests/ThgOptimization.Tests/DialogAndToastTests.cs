using System.Windows;

namespace ThgOptimization.Tests;

public class DialogAndToastTests
{
    private static Window Plain() => new() { WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Width = 10, Height = 10, Left = -32000, Top = -32000 };

    [Fact]
    public void Dialog_owner_must_be_visible_and_not_the_dialog_itself() => Sta.Run(() =>
    {
        var dialog = Plain();
        var other = Plain();
        Assert.False(ThgDialog.CanOwn(other, dialog)); // invisível

        other.Show();
        dialog.Show();
        try
        {
            Assert.True(ThgDialog.CanOwn(other, dialog));
            Assert.False(ThgDialog.CanOwn(dialog, dialog));
        }
        finally
        {
            dialog.Close();
            other.Close();
        }
    });

    [Fact]
    public void Toast_never_owns_a_dialog() => Sta.Run(() =>
    {
        var toast = new ToastWindow();
        var dialog = Plain();
        toast.Notify("Título", "Texto", "");
        try
        {
            Assert.True(toast.IsVisible);
            Assert.False(ThgDialog.CanOwn(toast, dialog));
        }
        finally { toast.Close(); }
    });

    /// <summary>A barra de tarefas pode ter mudado entre dois avisos: o segundo também volta ao canto certo.</summary>
    [Fact]
    public void Toast_is_placed_again_while_already_visible() => Sta.Run(() =>
    {
        var toast = new ToastWindow();
        toast.Notify("Título", "Texto", "");
        try
        {
            toast.Left = 0;
            toast.Top = 0;
            toast.Notify("Outro título", "Outro texto", "");
            var area = SystemParameters.WorkArea;
            Assert.Equal(area.Right - toast.ActualWidth - 16, toast.Left, 3);
            Assert.Equal(area.Bottom - toast.ActualHeight - 16, toast.Top, 3);
        }
        finally { toast.Close(); }
    });
}
