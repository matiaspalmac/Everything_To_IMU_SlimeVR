using System.Windows.Controls;
using Everything_To_IMU_SlimeVR.UI.ViewModels;

namespace Everything_To_IMU_SlimeVR.UI.Views;

public partial class TrackersPage : Page
{
    public TrackersPage()
    {
        InitializeComponent();
        // Tear down the VM's refresh timer on navigate-away. Without this every visit
        // accumulated one DispatcherTimer firing 800 ms ticks against an orphaned VM
        // for the rest of the process lifetime.
        Unloaded += (_, _) => (DataContext as IDisposable)?.Dispose();
    }
}
