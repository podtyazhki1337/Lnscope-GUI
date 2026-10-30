using GUI.Motion;
using GUI.Mvvm;

namespace GUI.ViewModels;

/// <summary>One row in the keyboard binding editor.</summary>
public sealed class JogBindingViewModel : ViewModelBase
{
    public JogBindingViewModel(JogBinding binding) => Binding = binding;

    public JogBinding Binding { get; }

    public string Key
    {
        get => Binding.Key;
        set
        {
            if (Binding.Key == value)
            {
                return;
            }

            Binding.Key = value;
            OnPropertyChanged();
        }
    }

    public string Device
    {
        get => Binding.Device;
        set
        {
            if (Binding.Device == value)
            {
                return;
            }

            Binding.Device = value;
            OnPropertyChanged();
        }
    }

    public string Axis
    {
        get => Binding.Axis;
        set
        {
            if (Binding.Axis == value)
            {
                return;
            }

            Binding.Axis = value;
            OnPropertyChanged();
        }
    }

    public double Direction
    {
        get => Binding.Direction;
        set
        {
            if (Math.Abs(Binding.Direction - value) < double.Epsilon)
            {
                return;
            }

            Binding.Direction = value;
            OnPropertyChanged();
        }
    }
}
