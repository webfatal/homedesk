using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using RemoteDesk.Shared.Protocol;

namespace RemoteDesk.Host.UI;

/// <summary>
/// Settings window for configuring server port, FPS, video quality, and codec.
/// </summary>
public partial class SettingsWindow : Window
{
    public int ServerPort { get; private set; } = 8443;
    public int Fps { get; private set; } = 15;
    public string Quality { get; private set; } = "Medium";
    public VideoCodec SelectedCodec { get; private set; } = VideoCodec.Vp8;

    public SettingsWindow()
    {
        InitializeComponent();
        UpdateStatusDisplay();
    }

    private void OnFpsSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FpsLabel != null)
        {
            FpsLabel.Text = ((int)e.NewValue).ToString();
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortInput.Text, out var port) || port < 1 || port > 65535)
        {
            MessageBox.Show("Bitte geben Sie einen gültigen Port ein (1–65535).",
                "Ungültiger Port", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ServerPort = port;
        Fps = (int)FpsSlider.Value;

        if (QualityLow.IsChecked == true) Quality = "Low";
        else if (QualityHigh.IsChecked == true) Quality = "High";
        else Quality = "Medium";

        var selectedTag = ((ComboBoxItem)CodecSelector.SelectedItem).Tag.ToString();
        SelectedCodec = selectedTag == "Av1" ? VideoCodec.Av1 : VideoCodec.Vp8;

        UpdateStatusDisplay();
        DialogResult = true;
        Close();
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
