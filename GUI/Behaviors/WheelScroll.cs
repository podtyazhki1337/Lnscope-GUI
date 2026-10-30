using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GUI.Behaviors;

/// <summary>Changes slider values with the mouse wheel. A shared style enables this for new driver properties without additional UI code.</summary>
public static class WheelScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(WheelScroll), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider)
        {
            return;
        }

        slider.PreviewMouseWheel -= OnMouseWheel;

        if (e.NewValue is true)
        {
            slider.PreviewMouseWheel += OnMouseWheel;
        }
    }

    private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider slider)
        {
            return;
        }

        var step = slider.TickFrequency > 0 ? slider.TickFrequency : slider.SmallChange;

        if (step <= 0)
        {
            step = (slider.Maximum - slider.Minimum) / 100;
        }

        // Ctrl selects a larger step to traverse the exposure range quickly.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            step *= 10;
        }

        slider.Value = Math.Clamp(slider.Value + Math.Sign(e.Delta) * step, slider.Minimum, slider.Maximum);
        e.Handled = true;
    }
}
