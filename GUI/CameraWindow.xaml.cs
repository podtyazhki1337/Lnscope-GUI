using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using GUI.Motion;
using GUI.ViewModels;

namespace GUI;

public partial class CameraWindow : Window
{
    private readonly CameraViewModel _camera;
    private readonly JogController _jog;

    public CameraWindow(CameraViewModel camera, JogController jog, Window owner)
    {
        InitializeComponent();

        _camera = camera;
        _jog = jog;

        DataContext = camera;
        Owner = owner;
        Title = $"Camera — {camera.Id}";

        camera.PropertyChanged += OnCameraPropertyChanged;
        Loaded += (_, _) => FitIfTooLarge();
        Closed += (_, _) => camera.PropertyChanged -= OnCameraPropertyChanged;
    }

    private void OnCameraPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CameraViewModel.CurrentMode))
        {
            Dispatcher.InvokeAsync(FitIfTooLarge);
        }
    }

    private void FitIfTooLarge()
    {
        var work = SystemParameters.WorkArea;

        if (_camera.DisplayWidth > work.Width - 40 || _camera.DisplayHeight > work.Height - 80)
        {
            _camera.FitTo(work.Width - 40, work.Height - 80);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_jog.HandleKeyDown(e))
        {
            e.Handled = true;
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (_jog.HandleKeyUp(e))
        {
            e.Handled = true;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e) => _jog.ReleaseAll();
}
