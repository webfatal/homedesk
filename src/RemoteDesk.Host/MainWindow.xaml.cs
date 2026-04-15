using System.Windows;
using System.Windows.Input;
using RemoteDesk.Host.UI;

namespace RemoteDesk.Host;

/// <summary>
/// Invisible main window that hosts the system tray icon.
/// </summary>
public partial class MainWindow : Window
{
    public static readonly RoutedUICommand OpenSettingsCommand =
        new("Open Settings", "OpenSettings", typeof(MainWindow));

    private SettingsWindow? _settingsWindow;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        ShowSettings();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        TrayIcon.Dispose();
        Application.Current.Shutdown();
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow();
        _settingsWindow.ShowDialog();
    }

    protected override void OnClosed(EventArgs e)
    {
        TrayIcon.Dispose();
        base.OnClosed(e);
    }
}
