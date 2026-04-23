using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Everything_To_IMU_SlimeVR.UI.Behaviors;

/// <summary>
/// Re-raises MouseWheel events from child controls (DataGrid, ComboBox, custom controls) up
/// to their parent so an outer ScrollViewer can handle them. Canonical fix for WPF's
/// "nested ScrollViewers swallow wheel" behaviour (dotnet/wpf#8353).
///
/// Register once at app startup for every type that eats wheel events.
/// </summary>
public static class ScrollViewerHelper
{
    // Kept for XAML compatibility — no-op now; the global class handler does the work.
    public static readonly DependencyProperty CaptureMouseWheelProperty =
        DependencyProperty.RegisterAttached(
            "CaptureMouseWheel",
            typeof(bool),
            typeof(ScrollViewerHelper),
            new PropertyMetadata(false));

    public static void SetCaptureMouseWheel(DependencyObject d, bool value) => d.SetValue(CaptureMouseWheelProperty, value);
    public static bool GetCaptureMouseWheel(DependencyObject d) => (bool)d.GetValue(CaptureMouseWheelProperty);

    public static void RegisterGlobal()
    {
        EventManager.RegisterClassHandler(typeof(DataGrid),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnChildPreviewMouseWheel));
        EventManager.RegisterClassHandler(typeof(ComboBox),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnChildPreviewMouseWheel));
        EventManager.RegisterClassHandler(typeof(Controls.LiveChart),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnChildPreviewMouseWheel));
    }

    private static void OnChildPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not UIElement element) return;

        // If the element is an open ComboBox popup (user is scrolling its items), let it work.
        if (sender is ComboBox cb && cb.IsDropDownOpen) return;

        // Forward the event to the parent so an outer ScrollViewer can receive it.
        e.Handled = true;
        var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender
        };
        (VisualTreeHelper.GetParent(element) as UIElement)?.RaiseEvent(args);
    }
}
