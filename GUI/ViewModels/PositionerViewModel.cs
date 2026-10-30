using System.Collections.ObjectModel;
using System.Windows.Threading;
using GUI.Motion;
using GUI.Mvvm;
using LnScope.Core.Devices;

namespace GUI.ViewModels;

public sealed class PositionerViewModel : ViewModelBase
{
    private readonly IPositioner _positioner;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _log;

    private DeviceState _state;
    private bool _memoryBusy;
    private string _memoryStatus = "Controller slots 1–5. Save replaces the selected slot.";
    public string MemoryStatus { get => _memoryStatus; private set => SetField(ref _memoryStatus, value); }
    public bool SupportsPositionMemory { get; }
    public bool CanUsePositionMemory => SupportsPositionMemory && IsConnected && !_memoryBusy;
    public IReadOnlyList<PositionSlotViewModel> PositionSlots { get; }
    public IReadOnlyList<int> PresetVelocities { get; } = Enumerable.Range(1, 15).ToArray();
    public int PresetVelocity { get; set; } = 1;

    public PositionerViewModel(IPositioner positioner, MotionService motion, Dispatcher dispatcher,
        Action<string> log)
    {
        _positioner = positioner;
        _dispatcher = dispatcher;
        _log = log;
        _state = positioner.State;
        SupportsPositionMemory = motion.SupportsPositionMemory(positioner.Id);
        PositionSlots = Enumerable.Range(1, 5).Select(slot => new PositionSlotViewModel(slot,
            () => UseMemoryAsync(slot, false, motion), () => UseMemoryAsync(slot, true, motion))).ToArray();

        Axes = new ObservableCollection<AxisViewModel>(
            positioner.Axes.Select(axis => new AxisViewModel(positioner.Id, axis, motion, log)));

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => State is DeviceState.Disconnected or DeviceState.Faulted) { ErrorHandler = HandleError };
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => State is DeviceState.Ready or DeviceState.Faulted) { ErrorHandler = HandleError };

        positioner.StateChanged += OnStateChanged;
        positioner.PositionChanged += OnPositionChanged;
    }

    public string Id => _positioner.Id;

    public ObservableCollection<AxisViewModel> Axes { get; }

    public AsyncRelayCommand ConnectCommand { get; }

    public AsyncRelayCommand DisconnectCommand { get; }

    public DeviceState State
    {
        get => _state;
        private set
        {
            if (!SetField(ref _state, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(CanUsePositionMemory));
            ConnectCommand.RaiseCanExecuteChanged();
            DisconnectCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsConnected => State == DeviceState.Ready;

    private async Task UseMemoryAsync(int slot, bool recall, MotionService motion)
    {
        if (!CanUsePositionMemory) return;
        _memoryBusy = true;
        OnPropertyChanged(nameof(CanUsePositionMemory));
        try
        {
            if (recall) await motion.GotoPositionAsync(Id, slot, PresetVelocity);
            else await motion.SavePositionAsync(Id, slot);
            MemoryStatus = $"{(recall ? "Go" : "Save")} {slot} sent · no controller ACK.";
            _log($"{Id}: {MemoryStatus}");
        }
        catch (Exception error)
        {
            MemoryStatus = error.Message;
            HandleError(error);
        }
        finally
        {
            _memoryBusy = false;
            OnPropertyChanged(nameof(CanUsePositionMemory));
        }
    }

    private async Task ConnectAsync()
    {
        _log($"{Id}: connecting…");
        await _positioner.ConnectAsync();
        _log($"{Id}: connected");

        foreach (var axis in Axes)
        {
            await axis.RefreshAsync();
        }
    }

    private async Task DisconnectAsync()
    {
        await _positioner.DisconnectAsync();
        _log($"{Id}: disconnected");
    }

    public async Task ShutdownAsync()
    {
        _positioner.StateChanged -= OnStateChanged;
        _positioner.PositionChanged -= OnPositionChanged;
        await _positioner.DisposeAsync();
    }

    // Driver messages arrive on a background reader thread; update bindings on the UI thread.
    private void OnStateChanged(object? sender, DeviceState state)
    {
        if (_dispatcher.CheckAccess()) State = state;
        else _dispatcher.InvokeAsync(() => State = state);
    }

    private void OnPositionChanged(object? sender, AxisPosition position) =>
        _dispatcher.InvokeAsync(() =>
        {
            var axis = Axes.FirstOrDefault(a => a.Id == position.AxisId);
            axis?.Apply(position);
        });

    private void HandleError(Exception exception) => _log($"{Id}: ERROR — {exception.Message}");
}

public sealed class PositionSlotViewModel(int number, Func<Task> save, Func<Task> go)
{
    public int Number { get; } = number;
    public AsyncRelayCommand SaveCommand { get; } = new(save);
    public AsyncRelayCommand GoCommand { get; } = new(go);
}
