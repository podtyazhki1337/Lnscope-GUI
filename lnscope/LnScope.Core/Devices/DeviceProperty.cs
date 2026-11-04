using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LnScope.Core.Devices;

/// <summary>Driver-defined property with a name, range and unit. The interface creates a control automatically, so new properties do not require XAML changes.</summary>
public abstract class DeviceProperty : INotifyPropertyChanged
{
    protected DeviceProperty(string id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>Stable property key used in configuration and logs.</summary>
    public string Id { get; }

    public string Name { get; }

    public string? Unit { get; init; }

    private bool _isReadOnly;
    public bool IsReadOnly
    {
        get => _isReadOnly;
        set { if (_isReadOnly != value) { _isReadOnly = value; OnPropertyChanged(); } }
    }


    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>Numeric property with bounds, such as exposure, gain or brightness.</summary>
public sealed class NumericProperty : DeviceProperty
{
    private readonly Action<double>? _apply;
    private double _value;

    public NumericProperty(string id, string name, double value, Action<double>? apply = null)
        : base(id, name)
    {
        _value = value;
        _apply = apply;
    }

    public double Minimum { get; init; }

    public double Maximum { get; init; } = 100;

    public double Step { get; init; } = 1;

    public double Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, Minimum, Maximum);

            if (Math.Abs(_value - clamped) < double.Epsilon)
            {
                return;
            }

            if (!double.IsFinite(clamped) || IsReadOnly) return;
            _apply?.Invoke(clamped);
            _value = clamped;
            OnPropertyChanged();
        }
    }

    /// <summary>Apply a value received from hardware without writing it back to the device.</summary>
    public void SetFromDevice(double value)
    {
        _value = value;
        OnPropertyChanged(nameof(Value));
    }
}

/// <summary>Boolean property, such as automatic exposure, mirroring or color mode.</summary>
public sealed class BooleanProperty : DeviceProperty
{
    private readonly Action<bool>? _apply;
    private bool _value;

    public BooleanProperty(string id, string name, bool value, Action<bool>? apply = null)
        : base(id, name)
    {
        _value = value;
        _apply = apply;
    }

    public bool Value
    {
        get => _value;
        set
        {
            if (_value == value)
            {
                return;
            }

            _apply?.Invoke(value);
            _value = value;
            OnPropertyChanged();
        }
    }
}

/// <summary>Choice property, such as bit depth, white balance mode or binning type.</summary>
public sealed class ChoiceProperty : DeviceProperty
{
    private readonly Action<string>? _apply;
    private string _value;

    public ChoiceProperty(string id, string name, IReadOnlyList<string> options, string value,
        Action<string>? apply = null)
        : base(id, name)
    {
        Options = options;
        _value = value;
        _apply = apply;
    }

    public IReadOnlyList<string> Options { get; }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value || !Options.Contains(value))
            {
                return;
            }

            _apply?.Invoke(value);
            _value = value;
            OnPropertyChanged();
        }
    }
}
