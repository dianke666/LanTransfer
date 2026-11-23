using System.Windows;
using System.Windows.Controls;
using LanTransfer.App.ViewModels;

namespace LanTransfer.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnAutoAcceptClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is TrustedDeviceItemVm item && DataContext is SettingsViewModel vm)
        {
            vm.ToggleAutoAccept(item, cb.IsChecked == true);
        }
    }
}
