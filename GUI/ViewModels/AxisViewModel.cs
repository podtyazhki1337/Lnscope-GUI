using GUI.Motion;
using GUI.Mvvm;
using LnScope.Core.Devices;

namespace GUI.ViewModels;

/// <summary>One axis in the interface. All movement goes through MotionService, so links apply to buttons and keyboard input alike.</summary>
public sealed class AxisViewModel : ViewModelBase
{
    private readonly MotionService _motion;
    private readonly string _deviceId;
    private readonly Action<string> _log;

    private double _position;
    private double _referencePosition;
    private double _target;
    private double _step = 10;
    private bool _isMoving;
    private bool _stopSwitchActive;
    private AxisLimit _limit;
    private bool _hasPosition;

    public AxisViewModel(string deviceId, AxisInfo info, MotionService motion, Action<string> log)
    {
        _deviceId = deviceId;
        Info = info;
        _motion = motion;
        _log = log;

        GoCommand = new AsyncRelayCommand(GoToTargetAsync) { ErrorHandler = HandleError };
        StepUpCommand = new AsyncRelayCommand(() => StepAsync(+1)) { ErrorHandler = HandleError };
        StepDownCommand = new AsyncRelayCommand(() => StepAsync(-1)) { ErrorHandler = HandleError };
        StopCommand = new AsyncRelayCommand(StopAsync) { ErrorHandler = HandleError };
        ReadCommand = new AsyncRelayCommand(RefreshAsync) { ErrorHandler = HandleError };
    }

    public AxisInfo Info { get; }

    public string Id => Info.Id;

    public string Unit => Info.Unit;
    public double ReferencePosition => _referencePosition;
    public long PositionReadTimeMs { get; private set; }
    public string CounterLabel => Info.DisplayUserCoordinates ? "Counter 2 · panel" : "Counter 1 · reference";
    public string ReferenceText => $"C1: {_referencePosition:0.000} {Unit}";
    public bool AtLimit => StopSwitchActive || _hasPosition && (_referencePosition <= Info.MinPosition || _referencePosition >= Info.MaxPosition);
    public string LimitText => StopSwitchActive ? _limit switch
    {
        AxisLimit.Negative => "HARDWARE LIMIT −",
        AxisLimit.Positive => "HARDWARE LIMIT +",
        _ => "HARDWARE LIMIT"
    } : AtLimit ? "SOFTWARE C1 LIMIT" : "";

    public string LimitsText =>
        Info.MinPosition is null && Info.MaxPosition is null
            ? "no software limits"
            : $"C1 limits: {FormatLimit(Info.MinPosition)} … {FormatLimit(Info.MaxPosition)} {Info.Unit}";

    private static string FormatLimit(double? value) => value?.ToString("0") ?? "—";

    public double Position
    {
        get => _position;
        private set => SetField(ref _position, value);
    }

    public double Target
    {
        get => _target;
        set
        {
            if (!double.IsFinite(value) || Math.Abs(value) > float.MaxValue ||
                (!Info.DisplayUserCoordinates && (value < Info.MinPosition || value > Info.MaxPosition)))
                throw new ArgumentOutOfRangeException(nameof(value), "Enter a finite target within the axis limits.");
            SetField(ref _target, value);
        }
    }

    public double Step
    {
        get => _step;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Enter a positive, finite step.");
            SetField(ref _step, value);
        }
    }

    public bool IsMoving
    {
        get => _isMoving;
        private set => SetField(ref _isMoving, value);
    }

    public bool StopSwitchActive
    {
        get => _stopSwitchActive;
        private set => SetField(ref _stopSwitchActive, value);
    }

    public AsyncRelayCommand GoCommand { get; }

    public AsyncRelayCommand StepUpCommand { get; }

    public AsyncRelayCommand StepDownCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand ReadCommand { get; }

    public void Apply(AxisPosition position)
    {
        PositionReadTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Position = Info.DisplayUserCoordinates ? position.User : position.Reference;
        _referencePosition = position.Reference;
        OnPropertyChanged(nameof(ReferenceText));
        IsMoving = position.IsMoving;
        StopSwitchActive = position.StopSwitchActive;
        _limit = position.Limit; _hasPosition = true;
        OnPropertyChanged(nameof(AtLimit)); OnPropertyChanged(nameof(LimitText));
    }

    private async Task GoToTargetAsync()
    {
        var referenceTarget = Target;
        if (Info.DisplayUserCoordinates)
        {
            // Read the current offset: the operator may have reset counter 2 since the last poll.
            referenceTarget = await _motion.ReferenceTargetAsync(_deviceId, Id, Target);
        }
        _log($"{_deviceId}.{Id}: go to {Target:0.###} {Unit} ({CounterLabel}); C1 target {referenceTarget:0.###}");
        await _motion.MoveAbsoluteAsync(_deviceId, Id, referenceTarget, _motion.ManualSpeed);
    }

    private async Task StepAsync(int direction)
    {
        var delta = direction * Step;
        _log($"{_deviceId}.{Id}: step {delta:+0.###;-0.###} {Unit}");
        await _motion.MoveRelativeAsync(_deviceId, Id, delta, _motion.ManualSpeed);
    }

    private async Task StopAsync()
    {
        _log($"{_deviceId}.{Id}: stop");
        await _motion.StopAsync(_deviceId, Id);
    }

    public async Task RefreshAsync()
    {
        var position = await _motion.ReadPositionAsync(_deviceId, Id);
        Apply(position);
    }

    private void HandleError(Exception exception) => _log($"{_deviceId}.{Id}: ERROR — {exception.Message}");
}
