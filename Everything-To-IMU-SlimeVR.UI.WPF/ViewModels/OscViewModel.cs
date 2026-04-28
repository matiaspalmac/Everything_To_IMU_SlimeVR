using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everything_To_IMU_SlimeVR.UI.Services;

namespace Everything_To_IMU_SlimeVR.UI.ViewModels;

public partial class OscViewModel : ObservableObject
{
    [ObservableProperty] private string _ipAddress = "127.0.0.1";
    [ObservableProperty] private string _inputPort = "9001";
    [ObservableProperty] private string _newOutputPort = "";
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _hasStatus;

    public ObservableCollection<int> OutputPorts { get; } = new();

    public OscViewModel()
    {
        var cfg = AppServices.Instance.Configuration;
        if (cfg != null)
        {
            IpAddress = cfg.OscIpAddress;
            InputPort = cfg.PortInput;
            foreach (var p in cfg.PortOutputs) OutputPorts.Add(p);
        }
    }

    [RelayCommand]
    private void Apply()
    {
        var cfg = AppServices.Instance.Configuration;
        if (cfg == null) return;
        if (!int.TryParse(InputPort, out var port) || port < 1 || port > 65535)
        {
            SetStatus("Input port must be a number between 1-65535.");
            return;
        }
        cfg.OscIpAddress = IpAddress;
        cfg.PortInput = InputPort;
        cfg.SaveDebounced();
        AppServices.Instance.TrackerManager.RefreshOscPort();
        SetStatus("OSC config applied.");
    }

    [RelayCommand]
    private void AddOutput()
    {
        var cfg = AppServices.Instance.Configuration;
        if (cfg == null) return;
        if (!int.TryParse(NewOutputPort, out var port) || port < 1 || port > 65535)
        {
            SetStatus("Invalid port.");
            return;
        }
        if (port == 9000)
        {
            SetStatus("Port 9000 is reserved (VRChat receive). Use a different port.");
            return;
        }
        if (OutputPorts.Contains(port))
        {
            SetStatus("Port already in list.");
            return;
        }
        OutputPorts.Add(port);
        cfg.PortOutputs.Add(port);
        cfg.SaveDebounced();
        NewOutputPort = "";
        SetStatus($"Output port {port} added.");
    }

    [RelayCommand]
    private void RemoveOutput(int port)
    {
        var cfg = AppServices.Instance.Configuration;
        if (cfg == null) return;
        OutputPorts.Remove(port);
        cfg.PortOutputs.Remove(port);
        cfg.SaveDebounced();
        SetStatus($"Output port {port} removed.");
    }

    private void SetStatus(string msg)
    {
        StatusMessage = msg;
        HasStatus = true;
        _ = HideLater();
    }

    private async Task HideLater()
    {
        await Task.Delay(3500);
        HasStatus = false;
    }
}
