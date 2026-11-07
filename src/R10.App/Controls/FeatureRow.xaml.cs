using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using R10.App.ViewModels;

namespace R10.App.Controls;
public partial class FeatureRow : UserControl
{
    public FeatureRow() => InitializeComponent();
    private void NumberGotFocus(object sender, KeyboardFocusChangedEventArgs e) { if (DataContext is FeatureViewModel vm) vm.SetEditing(true); }
    private void NumberLostFocus(object sender, KeyboardFocusChangedEventArgs e) { if (DataContext is FeatureViewModel vm) vm.SetEditing(false); }
    private void NumberKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not FeatureViewModel vm) return;
        if (e.Key == Key.Enter) { if (vm.ApplyCommand.CanExecute(null)) vm.ApplyCommand.Execute(null); e.Handled = true; }
        if (e.Key == Key.Escape) { vm.CancelDraft(); e.Handled = true; }
    }
}
