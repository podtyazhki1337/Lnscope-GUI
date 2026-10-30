using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GUI.Behaviors;

/// <summary>Captures a key name instead of typed text, so users can edit bindings without knowing WPF key names.</summary>
public static class KeyCaptureBox
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(KeyCaptureBox), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        box.PreviewKeyDown -= OnPreviewKeyDown;

        if (e.NewValue is true)
        {
            box.IsReadOnly = true;
            box.PreviewKeyDown += OnPreviewKeyDown;
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.Tab or Key.Escape)
        {
            return;
        }

        box.Text = key.ToString();

        // Update the binding explicitly because the text was assigned by code.
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
}
