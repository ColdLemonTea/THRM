using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace THRM.Avalonia;

public partial class MainWindow : Window
{
    private readonly ThrmIpcClient _ipc = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _recoveryLoop;

    public MainWindow()
    {
        InitializeComponent();
        _ipc.ConnectionChanged += IpcConnectionChanged;
        _ipc.EventReceived += CoreEventReceived;
        Opened += WindowOpened;
        Closing += WindowClosing;
    }

    private async void WindowOpened(object? sender, EventArgs e)
    {
        if (_recoveryLoop is not null)
        {
            return;
        }

        try
        {
            await _ipc.ConnectAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            CoreProcessLauncher.TryStart(out var status);
            StatusText.Text = $"{status} Waiting for IPC.";
        }

        _recoveryLoop = _ipc.RunRecoveryLoopAsync(_lifetime.Token);
    }

    private void IpcConnectionChanged(object? sender, IpcConnectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateConnection(e.Connected);
            if (e.Connected)
            {
                _ = RefreshStateAsync();
            }
        });
    }

    private void CoreEventReceived(object? sender, IpcEvent e)
    {
        switch (e.Type)
        {
            case "show-window":
                Dispatcher.UIThread.Post(Show);
                break;
            case "quit":
                Dispatcher.UIThread.Post(Close);
                break;
            case "temperature-update":
                if (e.TryGetData<TemperatureSnapshot>(out var temperature) && temperature is not null)
                {
                    Dispatcher.UIThread.Post(() => ApplyTemperature(temperature));
                }
                break;
            case "fan-data-update":
                if (e.TryGetData<FanDataSnapshot>(out var fanData) && fanData is not null)
                {
                    Dispatcher.UIThread.Post(() => ApplyFanData(fanData));
                }
                break;
            case "device-connected":
            case "device-disconnected":
            case "config-update":
                Dispatcher.UIThread.Post(() => _ = RefreshStateAsync());
                break;
            case "device-error":
                if (e.TryGetData<string>(out var error))
                {
                    Dispatcher.UIThread.Post(() => StatusText.Text = error);
                }
                break;
        }
    }

    private async void RefreshClick(object? sender, RoutedEventArgs e) => await RefreshStateAsync();

    private async void ShowWindowClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _ipc.ShowWindowAsync();
            Show();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Show window failed: {ex.Message}";
        }
    }

    private async void QuitCoreClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _ipc.QuitCoreAsync();
            StatusText.Text = "Quit request sent; no request replay is attempted.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Quit core failed: {ex.Message}";
        }
    }

    private async Task RefreshStateAsync()
    {
        if (!_ipc.IsConnected)
        {
            return;
        }

        try
        {
            var pingTask = _ipc.PingAsync();
            var configTask = _ipc.GetConfigAsync();
            var statusTask = _ipc.GetDeviceStatusAsync();
            await Task.WhenAll(pingTask, configTask, statusTask);
            ApplyConfig(configTask.Result);
            ApplyDeviceStatus(statusTask.Result);
            StatusText.Text = "Core state synchronized";
            LastUpdateText.Text = $"Last update: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"State refresh failed: {ex.Message}";
        }
    }

    private void UpdateConnection(bool connected)
    {
        ConnectionText.Text = connected ? "Connected" : "Disconnected — recovering";
        ConnectionDot.Fill = connected ? Brushes.LimeGreen : Brushes.DarkOrange;
        if (!connected)
        {
            DeviceText.Text = "Disconnected";
            StatusText.Text = "Core connection lost; retrying without replaying writes";
        }
    }

    private void ApplyConfig(ConfigSnapshot config) =>
        AutoControlText.Text = config.AutoControl ? "Enabled" : "Disabled";

    private void ApplyDeviceStatus(DeviceStatusSnapshot status)
    {
        DeviceText.Text = status.Connected ? "Connected" : "Disconnected";
        ModelText.Text = $"Model {EmptyDash(status.Model)}";
        ProductText.Text = $"Product ID {EmptyDash(status.ProductId)}";
        ModeText.Text = $"Mode {EmptyDash(status.CurrentData?.WorkMode)}";
        if (status.CurrentData is null)
        {
            FanText.Text = "Fan —";
        }
        else
        {
            ApplyFanData(status.CurrentData);
        }

        if (status.Temperature is not null)
        {
            ApplyTemperature(status.Temperature);
        }
    }

    private void ApplyTemperature(TemperatureSnapshot temperature)
    {
        CpuTempText.Text = $"{temperature.CpuTemp} °C";
        GpuTempText.Text = $"{temperature.GpuTemp} °C";
        MaxTempText.Text = $"{temperature.MaxTemp} °C";
        BridgeText.Text = temperature.BridgeOk
            ? "Temperature bridge OK"
            : $"Temperature bridge: {EmptyDash(temperature.BridgeMessage)}";
        LastUpdateText.Text = $"Temperature update: {DateTime.Now:HH:mm:ss}";
    }

    private void ApplyFanData(FanDataSnapshot fanData) =>
        FanText.Text = $"Fan {fanData.CurrentRpm} RPM → {fanData.TargetRpm} RPM";

    private static string EmptyDash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _lifetime.Cancel();
        _ipc.Dispose();
    }
}
