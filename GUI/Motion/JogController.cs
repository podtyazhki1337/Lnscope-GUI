using System.Windows.Input;
using System.Windows.Threading;
using LnScope.Core.Devices;

namespace GUI.Motion;

/// <summary>Keyboard input with configurable bindings. Simultaneous keys are combined per axis; W+A produces diagonal motion and W+S cancel each other.</summary>
public sealed class JogController
{
    private readonly MotionService _motion;
    private readonly JogOptions _options;
    private readonly Action<string> _log;
    private readonly Dictionary<Key, JogBinding> _bindings = new();
    private readonly HashSet<Key> _pressed = [];
    private readonly HashSet<Key> _suppressed = [];
    private readonly HashSet<string> _busy = [];
    private readonly DispatcherTimer _timer;

    public JogController(MotionService motion, JogOptions options, Dispatcher dispatcher, Action<string> log)
    {
        _motion = motion;
        _options = options;
        _log = log;
        _motion.MotionHalted += _ => dispatcher.InvokeAsync(() => ReleaseAll());

        Step = options.StepUm;
        Rebuild(options.Bindings);

        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(Math.Max(20, options.IntervalMs)),
            DispatcherPriority.Input,
            (_, _) => Tick(),
            dispatcher);
    }

    /// <summary>Distance per tick; editable while the application is running.</summary>
    public double Step { get; set; }
    public string Rate { get; set; } = "L";
    public double StepMultiplier => _motion.JogProfile(Rate).Multiplier;
    public MoveSpeed Speed => _motion.JogProfile(Rate).Speed;

    private bool _enabled = true;
    public bool IsEnabled { get => _enabled; set { _enabled = value; if (!value) ReleaseAll(); } }

    /// <summary>Rebuild the key lookup after editing bindings.</summary>
    public void Rebuild(IEnumerable<JogBinding> bindings)
    {
        ReleaseAll();
        _bindings.Clear();
        _pressed.Clear();

        foreach (var binding in bindings)
        {
            if (Enum.TryParse<Key>(binding.Key, ignoreCase: true, out var key))
            {
                _bindings[key] = binding;
            }
            else
            {
                _log($"keymap: unknown key '{binding.Key}'");
            }
        }
    }

    public bool HandleKeyDown(KeyEventArgs e)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (!_bindings.ContainsKey(key))
        {
            return false;
        }

        if (e.IsRepeat && (_suppressed.Contains(key) || !_pressed.Contains(key))) return true;
        if (!e.IsRepeat) _suppressed.Remove(key);
        if (_pressed.Add(key) && !_timer.IsEnabled)
        {
            _timer.Start();
            Tick(); // Send the first step immediately without waiting for a timer tick.
        }

        return true;
    }

    public bool HandleKeyUp(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        _suppressed.Remove(key);

        if (!_pressed.Remove(key))
        {
            return false;
        }

        if (_pressed.Count == 0)
        {
            _timer.Stop();
        }

        _ = _motion.CancelHeldMotionAsync();
        return true;
    }

    /// <summary>Clear held keys when the window loses focus, since the corresponding KeyUp may never arrive.</summary>
    public void ReleaseAll()
    {
        _suppressed.UnionWith(_pressed);
        _pressed.Clear();
        _timer?.Stop();
        _ = _motion.CancelHeldMotionAsync();
    }

    private void Tick()
    {
        if (_motion.IsStopping) return;
        if (_pressed.Count == 0)
        {
            _timer.Stop();
            return;
        }

        if (_motion.UsesPython)
        {
            // WPF supplies key state only. Python aggregates axes and chooses step/speed.
            if (!_busy.Add("python-tick")) return;
            var bindings = _pressed.Where(_bindings.ContainsKey).Select(k => _bindings[k]).ToArray();
            _ = PythonTickAsync(bindings);
            return;
        }

        var requests = new Dictionary<(string Device, string Axis), double>();

        foreach (var key in _pressed)
        {
            if (!_bindings.TryGetValue(key, out var binding))
            {
                continue;
            }

            var token = (binding.Device, binding.Axis);
            requests[token] = requests.GetValueOrDefault(token) + binding.Direction;
        }

        foreach (var ((device, axis), direction) in requests)
        {
            if (_motion.IsAxisBusy(device, axis)) continue;
            if (Math.Abs(direction) < double.Epsilon)
            {
                continue;
            }

            var token = $"{device}.{axis}";

            // Discard new requests while the previous request for this axis is in flight, preventing queued movement after key release.
            lock (_busy)
            {
                if (!_busy.Add(token))
                {
                    continue;
                }
            }

            _ = MoveAsync(device, axis, Math.Sign(direction) * Step * StepMultiplier, token);
        }
    }

    private async Task MoveAsync(string device, string axis, double delta, string token)
    {
        try
        {
            await _motion.MoveRelativeAsync(device, axis, delta, Speed);
        }
        catch (Exception exception)
        {
            _log($"{device}.{axis}: ERROR — {exception.Message}");
            ReleaseAll();
        }
        finally
        {
            lock (_busy)
            {
                _busy.Remove(token);
            }
        }
    }

    private async Task PythonTickAsync(JogBinding[] bindings)
    {
        try { await _motion.JogAsync(bindings, Step, Rate); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _log("Jog: " + exception.Message); ReleaseAll(); }
        finally { _busy.Remove("python-tick"); }
    }
}
