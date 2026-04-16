using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using RemoteDesk.Shared.Protocol;

namespace RemoteDesk.Host.UI;

/// <summary>
/// Settings window for configuring server port, FPS, video quality, and codec.
/// Raises <see cref="SettingsApplied"/> when the user commits changes either via
/// "Anwenden" (live apply, window stays open) or "Speichern" (apply + close).
/// </summary>
public partial class SettingsWindow : Window
{
    public int ServerPort { get; private set; } = 8443;

    /// <summary>
    /// Fired whenever the user commits a settings change. The hosting app
    /// subscribes and forwards the new <see cref="SessionSettings"/> to the
    /// <see cref="Host.Session.SessionManager"/> so the running session is
    /// re-configured without forcing viewers to reconnect.
    /// </summary>
    public event EventHandler<SessionSettings>? SettingsApplied;

    public SettingsWindow()
    {
        InitializeComponent();
        UpdateStatusDisplay();
    }

    /// <summary>
    /// Pre-populates the controls with the currently active settings before the
    /// window is shown. Without this the user would always see the static
    /// defaults from XAML, not the real server state.
    /// </summary>
    public void LoadCurrent(int port, SessionSettings settings)
    {
        ServerPort = port;
        PortInput.Text = port.ToString();
        FpsSlider.Value = settings.Fps;
        FpsLabel.Text = settings.Fps.ToString();

        QualityLow.IsChecked = settings.Quality == VideoQuality.Low;
        QualityMedium.IsChecked = settings.Quality == VideoQuality.Medium;
        QualityHigh.IsChecked = settings.Quality == VideoQuality.High;

        for (var i = 0; i < CodecSelector.Items.Count; i++)
        {
            if (CodecSelector.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), settings.Codec.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                CodecSelector.SelectedIndex = i;
                break;
            }
        }

        UpdateStatusDisplay();
    }

    private void OnFpsSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FpsLabel != null)
        {
            FpsLabel.Text = ((int)e.NewValue).ToString();
        }
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        ApplyCurrentValues(closeWindow: false);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ApplyCurrentValues(closeWindow: true);
    }

    private void ApplyCurrentValues(bool closeWindow)
    {
        if (!int.TryParse(PortInput.Text, out var port) || port < 1 || port > 65535)
        {
            MessageBox.Show("Bitte geben Sie einen gültigen Port ein (1–65535).",
                "Ungültiger Port", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ServerPort = port;

        var quality = QualityLow.IsChecked == true ? VideoQuality.Low
            : QualityHigh.IsChecked == true ? VideoQuality.High
            : VideoQuality.Medium;

        var selectedTag = ((ComboBoxItem)CodecSelector.SelectedItem).Tag?.ToString();
        var codec = selectedTag == "Av1" ? VideoCodec.Av1 : VideoCodec.Vp8;

        var settings = new SessionSettings(
            MonitorIndex: 0,
            Fps: (int)FpsSlider.Value,
            Quality: quality,
            Codec: codec);

        SettingsApplied?.Invoke(this, settings);

        UpdateStatusDisplay();

        if (closeWindow)
        {
            DialogResult = true;
            Close();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateStatusDisplay()
    {
        var localIp = GetLocalIpAddress();
        StatusText.Text = $"Status: Bereit auf Port {PortInput.Text}";
        UrlText.Text = $"URL: https://{localIp}:{PortInput.Text}";
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            var endpoint = socket.LocalEndPoint as IPEndPoint;
            return endpoint?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}
