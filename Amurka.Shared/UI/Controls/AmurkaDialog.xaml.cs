using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Amurka.Shared.UI.Controls;

public partial class AmurkaDialog : Window
{
    private static readonly SolidColorBrush DarkTextBrush = CreateFrozenBrush(Color.FromRgb(10, 10, 10));
    private static readonly SolidColorBrush DefaultAccentBrush = CreateFrozenBrush(Color.FromRgb(0x00, 0xFF, 0x88));
    private static readonly SolidColorBrush DefaultDangerBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0x47, 0x57));

    public AmurkaDialog(
        string message,
        string title,
        AmurkaDialogButtons buttons,
        AmurkaDialogIcon icon,
        string? confirmText = null,
        string? cancelText = null,
        bool isDanger = false)
    {
        InitializeComponent();

        TitleText.Text = title;
        MessageText.Text = message;

        ApplyIconAndTheme(icon);
        ConfigureButtons(buttons, confirmText, cancelText, isDanger);
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void ApplyIconAndTheme(AmurkaDialogIcon icon)
    {
        (Color primaryColor, string glyph) = icon switch
        {
            AmurkaDialogIcon.Warning => (Color.FromRgb(0xFD, 0xCB, 0x6E), "⚠"),
            AmurkaDialogIcon.Question => (Color.FromRgb(0x6C, 0x5C, 0xE7), "?"),
            AmurkaDialogIcon.Error => (Color.FromRgb(0xFF, 0x47, 0x57), "✕"),
            AmurkaDialogIcon.Success => (Color.FromRgb(0x00, 0xFF, 0x88), "✓"),
            _ => (Color.FromRgb(0x00, 0xFF, 0x88), "ℹ")
        };

        SolidColorBrush brush = CreateFrozenBrush(primaryColor);
        SolidColorBrush dimBrush = CreateFrozenBrush(Color.FromArgb(40, primaryColor.R, primaryColor.G, primaryColor.B));
        SolidColorBrush borderBrush = CreateFrozenBrush(Color.FromArgb(80, primaryColor.R, primaryColor.G, primaryColor.B));

        IconGlyph.Text = glyph;
        IconGlyph.Foreground = brush;
        IconBadge.Background = dimBrush;
        IconBadge.BorderBrush = borderBrush;

        OuterBorder.BorderBrush = borderBrush;
        GlowEffect.Color = primaryColor;
    }

    private void ConfigureButtons(AmurkaDialogButtons buttons, string? confirmText, string? cancelText, bool isDanger)
    {
        string defaultConfirm = buttons == AmurkaDialogButtons.YesNo ? "Да" : "ОК";
        string defaultCancel = buttons == AmurkaDialogButtons.YesNo ? "Нет" : "Отмена";

        ConfirmBtnText.Text = confirmText ?? defaultConfirm;
        CancelBtnText.Text = cancelText ?? defaultCancel;
        CancelBtn.Visibility = buttons == AmurkaDialogButtons.Ok ? Visibility.Collapsed : Visibility.Visible;

        if (isDanger)
        {
            ConfirmBtn.Background = TryFindResource("DangerBrush") as Brush ?? DefaultDangerBrush;
            ConfirmBtnText.Foreground = Brushes.White;
        }
        else
        {
            ConfirmBtn.Background = TryFindResource("AccentGradientBrush") as Brush ?? DefaultAccentBrush;
            ConfirmBtnText.Foreground = DarkTextBrush;
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void ConfirmBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogResult = true;
            Close();
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }
}
