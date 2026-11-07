using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AmurkaLauncher.Controls;

public partial class AmurkaDialog : Window
{
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

    private void ApplyIconAndTheme(AmurkaDialogIcon icon)
    {
        Color primaryColor;
        string glyph;

        switch (icon)
        {
            case AmurkaDialogIcon.Warning:
                primaryColor = Color.FromRgb(0xFD, 0xCB, 0x6E); // #FDCB6E
                glyph = "⚠";
                break;
            case AmurkaDialogIcon.Question:
                primaryColor = Color.FromRgb(0x6C, 0x5C, 0xE7); // #6C5CE7
                glyph = "?";
                break;
            case AmurkaDialogIcon.Error:
                primaryColor = Color.FromRgb(0xFF, 0x47, 0x57); // #FF4757
                glyph = "✕";
                break;
            case AmurkaDialogIcon.Success:
                primaryColor = Color.FromRgb(0x00, 0xFF, 0x88); // #00FF88
                glyph = "✓";
                break;
            case AmurkaDialogIcon.Info:
            default:
                primaryColor = Color.FromRgb(0x00, 0xFF, 0x88); // #00FF88
                glyph = "ℹ";
                break;
        }

        var brush = new SolidColorBrush(primaryColor);
        brush.Freeze();

        var dimBrush = new SolidColorBrush(Color.FromArgb(40, primaryColor.R, primaryColor.G, primaryColor.B));
        dimBrush.Freeze();

        var borderBrush = new SolidColorBrush(Color.FromArgb(80, primaryColor.R, primaryColor.G, primaryColor.B));
        borderBrush.Freeze();

        IconGlyph.Text = glyph;
        IconGlyph.Foreground = brush;
        IconBadge.Background = dimBrush;
        IconBadge.BorderBrush = borderBrush;

        OuterBorder.BorderBrush = borderBrush;
        GlowEffect.Color = primaryColor;
    }

    private void ConfigureButtons(AmurkaDialogButtons buttons, string? confirmText, string? cancelText, bool isDanger)
    {
        string defaultConfirm = buttons switch
        {
            AmurkaDialogButtons.YesNo => "Да",
            _ => "ОК"
        };

        string defaultCancel = buttons switch
        {
            AmurkaDialogButtons.YesNo => "Нет",
            _ => "Отмена"
        };

        ConfirmBtnText.Text = confirmText ?? defaultConfirm;
        CancelBtnText.Text = cancelText ?? defaultCancel;

        if (buttons == AmurkaDialogButtons.Ok)
        {
            CancelBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            CancelBtn.Visibility = Visibility.Visible;
        }

        if (isDanger)
        {
            ConfirmBtn.Background = (Brush)FindResource("DangerBrush");
            ConfirmBtnText.Foreground = Brushes.White;
        }
        else
        {
            ConfirmBtn.Background = (Brush)FindResource("AccentGradientBrush");
            ConfirmBtnText.Foreground = new SolidColorBrush(Color.FromRgb(10, 10, 10));
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
