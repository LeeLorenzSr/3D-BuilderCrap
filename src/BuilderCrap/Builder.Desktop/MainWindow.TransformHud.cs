using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Builder.Desktop;

public partial class MainWindow
{
    private void TransformNumberMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox { IsKeyboardFocusWithin: false } field) return;
        e.Handled = true;
        field.Focus();
    }

    private void TransformNumberFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox field) field.SelectAll();
    }

    private void TransformOptionsClick(object sender, RoutedEventArgs e)
    {
        TransformOptionsPanel.Visibility = TransformOptionsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TransformHudVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue) TransformOptionsPanel.Visibility = Visibility.Collapsed;
    }
}
