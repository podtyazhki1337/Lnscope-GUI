using GUI.Mvvm;

namespace GUI.Motion;

/// <summary>Runtime device link: configuration plus its enabled state.</summary>
public sealed class MotionLink : ViewModelBase
{
    private bool _isEnabled;

    public MotionLink(LinkOptions options)
    {
        Options = options;
        _isEnabled = options.EnabledByDefault;
    }

    public LinkOptions Options { get; }

    public string Id => Options.Id;

    public string Name => Options.Name;

    public string Description =>
        Options.Bidirectional
            ? $"{Options.Source} ⇄ {Options.Target}"
            : $"{Options.Source} → {Options.Target}";

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }
}
