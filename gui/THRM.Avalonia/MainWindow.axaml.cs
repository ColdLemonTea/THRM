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
    private bool _deviceConnected;
    private bool _deviceStateKnown;
    private bool _autoControl;
    private bool _customSpeedEnabled;
    private bool _configKnown;
    private bool _writeInProgress;
    private string? _deviceModel;
    private string? _manualGear;
    private string? _manualLevel;

    private static readonly string[] ManualGearValues = ["静音", "标准", "强劲", "超频"];
    private static readonly string[] ManualLevelValues = ["低", "中", "高"];

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

    private async void ConnectDeviceClick(object? sender, RoutedEventArgs e) =>
        await RunWriteAsync("Connect device", () => _ipc.ConnectDeviceAsync(_lifetime.Token));

    private async void DisconnectDeviceClick(object? sender, RoutedEventArgs e) =>
        await RunWriteAsync("Disconnect device", () => _ipc.DisconnectDeviceAsync(_lifetime.Token));

    private async void AutoControlClick(object? sender, RoutedEventArgs e)
    {
        if (_writeInProgress || !_ipc.IsConnected || !_configKnown)
        {
            AutoControlSwitch.IsChecked = _autoControl;
            return;
        }

        var enabled = AutoControlSwitch.IsChecked == true;
        AutoControlSwitch.IsChecked = _autoControl;
        var action = enabled ? "Enable auto control" : "Disable auto control";
        await RunWriteAsync(action, () => _ipc.SetAutoControlAsync(enabled, _lifetime.Token));
    }

    private async void ApplyManualGearClick(object? sender, RoutedEventArgs e)
    {
        if (!CanChangeManualControl())
        {
            SetActionAvailability();
            return;
        }

        var gear = SelectedCoreValue(ManualGearComboBox, ManualGearValues);
        var level = IsBs1
            ? "中"
            : SelectedCoreValue(ManualLevelComboBox, ManualLevelValues);
        if (gear is null || level is null)
        {
            StatusText.Text = "Choose a gear and level before applying.";
            return;
        }

        var synchronized = await RunWriteAsync(
            "Apply manual fan preset",
            () => _ipc.SetManualGearAsync(gear, level, _lifetime.Token));
        if (!synchronized)
        {
            RestoreManualSelection();
        }
    }

    private void ManualSelectionChanged(object? sender, SelectionChangedEventArgs e) => SetActionAvailability();

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

    private async Task<bool> RunWriteAsync(string action, Func<Task<bool>> request)
    {
        if (_writeInProgress)
        {
            return false;
        }

        if (!_ipc.IsConnected)
        {
            StatusText.Text = $"{action} unavailable: THRM Core is not connected; known state retained.";
            return false;
        }

        _writeInProgress = true;
        SetActionAvailability();
        StatusText.Text = $"{action} in progress...";
        try
        {
            if (!await request())
            {
                StatusText.Text = $"{action} was rejected; known state retained.";
                return false;
            }

            if (await RefreshStateAsync())
            {
                StatusText.Text = $"{action} succeeded; state synchronized.";
                return true;
            }

            StatusText.Text = $"{action} accepted, but state was not synchronized; known state retained.";
            return false;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{action} failed: {ex.Message}; known state retained.";
            return false;
        }
        finally
        {
            _writeInProgress = false;
            SetActionAvailability();
        }
    }

    private async Task<bool> RefreshStateAsync()
    {
        if (!_ipc.IsConnected)
        {
            _deviceStateKnown = false;
            _configKnown = false;
            SetActionAvailability();
            return false;
        }

        try
        {
            var pingTask = _ipc.PingAsync();
            var configTask = _ipc.GetConfigAsync();
            var statusTask = _ipc.GetDeviceStatusAsync();
            await Task.WhenAll(pingTask, configTask, statusTask);
            ApplyConfig(configTask.Result);
            ApplyDeviceStatus(statusTask.Result);
            _configKnown = true;
            _deviceStateKnown = true;
            SetActionAvailability();
            StatusText.Text = "Core state synchronized";
            LastUpdateText.Text = $"Last update: {DateTime.Now:HH:mm:ss}";
            return true;
        }
        catch (Exception ex)
        {
            _deviceStateKnown = false;
            _configKnown = false;
            SetActionAvailability();
            StatusText.Text = $"State refresh failed: {ex.Message}";
            return false;
        }
    }

    private void UpdateConnection(bool connected)
    {
        ConnectionText.Text = connected ? "Connected" : "Disconnected — recovering";
        ConnectionDot.Fill = connected ? Brushes.LimeGreen : Brushes.DarkOrange;
        _deviceStateKnown = false;
        _configKnown = false;
        if (!connected)
        {
            StatusText.Text = "Core connection lost; retrying without replaying writes";
        }

        SetActionAvailability();
    }

    private void ApplyConfig(ConfigSnapshot config)
    {
        _autoControl = config.AutoControl;
        _customSpeedEnabled = config.CustomSpeedEnabled;
        if (!string.IsNullOrWhiteSpace(config.ManualGear))
        {
            _manualGear = config.ManualGear;
        }

        if (!string.IsNullOrWhiteSpace(config.ManualLevel))
        {
            _manualLevel = config.ManualLevel;
        }

        AutoControlSwitch.IsChecked = _autoControl;
        AutoControlText.Text = _autoControl ? "Enabled" : "Disabled";
        SelectCoreValue(ManualGearComboBox, ManualGearValues, _manualGear);
        SelectCoreValue(ManualLevelComboBox, ManualLevelValues, _manualLevel);
        UpdateManualAppliedText();
    }

    private void ApplyDeviceStatus(DeviceStatusSnapshot status)
    {
        _deviceConnected = status.Connected;
        _deviceModel = status.Model;
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

        UpdateManualAppliedText();
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

    private void SetActionAvailability()
    {
        var canChangeDevice = _ipc.IsConnected && _deviceStateKnown && !_writeInProgress;
        ConnectDeviceButton.IsEnabled = canChangeDevice && !_deviceConnected;
        DisconnectDeviceButton.IsEnabled = canChangeDevice && _deviceConnected;
        AutoControlSwitch.IsEnabled = _ipc.IsConnected && _configKnown && !_writeInProgress;

        var canChangeManual = CanChangeManualControl();
        ManualGearComboBox.IsEnabled = canChangeManual;
        ManualLevelLabel.IsVisible = !IsBs1;
        ManualLevelComboBox.IsVisible = !IsBs1;
        ManualLevelComboBox.IsEnabled = canChangeManual && !IsBs1;
        ManualLevelInfo.IsVisible = IsBs1;
        ApplyManualGearButton.IsEnabled = canChangeManual
            && SelectedCoreValue(ManualGearComboBox, ManualGearValues) is not null
            && (IsBs1 || SelectedCoreValue(ManualLevelComboBox, ManualLevelValues) is not null);
        ManualControlAvailabilityText.Text = GetManualControlAvailabilityText(canChangeManual);
    }

    private bool CanChangeManualControl() =>
        _ipc.IsConnected
        && _deviceStateKnown
        && _configKnown
        && _deviceConnected
        && !_writeInProgress
        && !_autoControl
        && !_customSpeedEnabled;

    private string GetManualControlAvailabilityText(bool canChangeManual)
    {
        if (!_ipc.IsConnected)
        {
            return "Manual control unavailable: THRM Core is not connected.";
        }

        if (!_deviceStateKnown || !_configKnown)
        {
            return "Manual control unavailable: waiting for synchronized device and configuration state.";
        }

        if (!_deviceConnected)
        {
            return "Manual control unavailable: connect a device first.";
        }

        if (_writeInProgress)
        {
            return "Manual control unavailable: a write is in progress.";
        }

        if (_autoControl)
        {
            return "Manual control unavailable: Auto control is enabled.";
        }

        if (_customSpeedEnabled)
        {
            return "Manual control unavailable: Custom speed is enabled.";
        }

        return canChangeManual
            ? IsBs1
                ? "Ready. BS1 uses fixed presets; level not applicable."
                : "Ready. Choose a preset and select Apply."
            : "Manual control unavailable: current state does not permit writes.";
    }

    private bool IsBs1 => string.Equals(_deviceModel, "BS1", StringComparison.OrdinalIgnoreCase);

    private void RestoreManualSelection()
    {
        SelectCoreValue(ManualGearComboBox, ManualGearValues, _manualGear);
        SelectCoreValue(ManualLevelComboBox, ManualLevelValues, _manualLevel);
    }

    private void UpdateManualAppliedText()
    {
        if (string.IsNullOrWhiteSpace(_manualGear))
        {
            ManualAppliedText.Text = "Applied preset: —";
            return;
        }

        var gear = ManualGearLabel(_manualGear);
        ManualAppliedText.Text = IsBs1
            ? $"Applied preset: {gear}; fixed presets (level not applicable)."
            : $"Applied preset: {gear} / {ManualLevelDisplay(_manualLevel)}";
    }

    private static void SelectCoreValue(ComboBox comboBox, string[] values, string? value) =>
        comboBox.SelectedIndex = value is null ? -1 : Array.IndexOf(values, value);

    private static string? SelectedCoreValue(ComboBox comboBox, string[] values)
    {
        var index = comboBox.SelectedIndex;
        return index >= 0 && index < values.Length ? values[index] : null;
    }

    private static string ManualGearLabel(string? value) => value switch
    {
        "静音" => "Quiet",
        "标准" => "Standard",
        "强劲" => "Strong",
        "超频" => "Overclock",
        _ => "—",
    };

    private static string ManualLevelDisplay(string? value) => value switch
    {
        "低" => "Low",
        "中" => "Medium",
        "高" => "High",
        _ => "—",
    };

    private static string EmptyDash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _lifetime.Cancel();
        _ipc.Dispose();
    }
}
