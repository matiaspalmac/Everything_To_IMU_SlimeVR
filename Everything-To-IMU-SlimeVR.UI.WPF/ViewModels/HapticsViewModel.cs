using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everything_To_IMU_SlimeVR.Tracking;
using Everything_To_IMU_SlimeVR.UI.Services;

namespace Everything_To_IMU_SlimeVR.UI.ViewModels;

public partial class HapticsViewModel : ObservableObject
{
    [ObservableProperty] private string _newIp = "";
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _hasStatus;

    public ObservableCollection<string> HapticDevices { get; } = new();

    private MidiHapticsPlayer? _player;

    public HapticsViewModel()
    {
        RefreshList();
    }

    private void RefreshList()
    {
        HapticDevices.Clear();
        foreach (var kv in GenericTrackerManager.TrackersUdpHapticDevice)
            HapticDevices.Add(kv.Key);
    }

    [RelayCommand]
    private void Add()
    {
        if (!IPAddress.TryParse(NewIp, out _))
        {
            SetStatus("Invalid IP address.");
            return;
        }
        AppServices.Instance.TrackerManager.AddRemoteHapticDevice(NewIp, "");
        // SaveDebounced now actually writes (commit 3bb9d5d) — using it instead of
        // SaveConfig keeps the click handler off a synchronous disk write on the UI thread.
        AppServices.Instance.Configuration?.SaveDebounced();
        RefreshList();
        NewIp = "";
        SetStatus("Haptic device added.");
    }

    [RelayCommand]
    private void TestPulse()
    {
        try { AppServices.Instance.TrackerManager.HapticTest(); }
        catch (Exception ex) { SetStatus($"Error: {ex.Message}"); }
    }

    [RelayCommand]
    private void LoadMidi()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "MIDI files (*.mid;*.midi)|*.mid;*.midi|All files|*.*",
            Title = "Select a MIDI file"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            // Tear down any previously playing instance — without this, repeated Load MIDI
            // clicks accumulated parallel NAudio playback streams + background threads.
            try { _player?.Stop(); } catch { }
            try { (_player as IDisposable)?.Dispose(); } catch { }
            _player = new MidiHapticsPlayer(GenericTrackerManager.SnapshotUdpHaptic().Select(kv => kv.Value));
            _player.Load(dlg.FileName);
            _player.Play();
            SetStatus($"Playing MIDI: {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            SetStatus($"MIDI error: {ex.Message}");
        }
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
