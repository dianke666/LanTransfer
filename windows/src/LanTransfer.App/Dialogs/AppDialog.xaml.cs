using System.Windows;
using System.Windows.Media;
using LanTransfer.App.Themes;

namespace LanTransfer.App.Dialogs;

public enum DialogTone
{
    Info,
    Warning,
    Danger,
}

public enum DialogOutcome
{
    Cancel,
    Primary,
    Secondary,
}

/// <summary>自定义中文模态对话框（设计 C8.3：不使用系统 MessageBox）。</summary>
public partial class AppDialog : Window
{
    public AppDialog()
    {
        InitializeComponent();
        ThemeManager.ApplyTitleBar(this);
    }

    public DialogOutcome Outcome { get; private set; } = DialogOutcome.Cancel;

    public static DialogOutcome ShowDialog(
        Window? owner,
        string title,
        string message,
        string primaryText,
        string? secondaryText = null,
        string? cancelText = null,
        DialogTone tone = DialogTone.Info)
    {
        var dlg = new AppDialog
        {
            Owner = owner,
            Title = title,
        };
        dlg.TitleText.Text = title;
        dlg.BodyText.Text = message;
        dlg.PrimaryButton.Content = primaryText;

        if (!string.IsNullOrEmpty(secondaryText))
        {
            dlg.SecondaryButton.Content = secondaryText;
            dlg.SecondaryButton.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrEmpty(cancelText))
        {
            dlg.CancelButton.Content = cancelText;
            dlg.CancelButton.Visibility = Visibility.Visible;
        }

        if (tone == DialogTone.Danger)
        {
            dlg.BadgeBorder.SetResourceReference(BackgroundProperty, "DangerSoftBrush");
            dlg.BadgeIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "DangerBrush");
            dlg.BadgeIcon.Data = Application.Current.TryFindResource("IconWarning") as Geometry;
            dlg.PrimaryButton.Style = Application.Current.TryFindResource("DangerButtonStyle") as Style;
        }
        else if (tone == DialogTone.Warning)
        {
            dlg.BadgeBorder.SetResourceReference(BackgroundProperty, "WarningSoftBrush");
            dlg.BadgeIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "WarningBrush");
            dlg.BadgeIcon.Data = Application.Current.TryFindResource("IconWarning") as Geometry;
        }

        dlg.ShowDialog();
        return dlg.Outcome;
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        Outcome = DialogOutcome.Primary;
        Close();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        Outcome = DialogOutcome.Secondary;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Outcome = DialogOutcome.Cancel;
        Close();
    }
}
