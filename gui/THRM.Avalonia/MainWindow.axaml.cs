using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FluentIcons.Avalonia.Fluent;
using FluentAvalonia.UI.Controls;

namespace THRM.Avalonia;

public partial class MainWindow : Window
{
    private readonly ThrmIpcClient _ipc = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _recoveryLoop;
    private WindowNotificationManager? _notifications;
    private bool _deviceConnected;
    private bool _deviceStateKnown;
    private bool _autoControl;
    private bool _customSpeedEnabled;
    private bool _configKnown;
    private bool _writeInProgress;
    private string? _deviceModel;
    private string? _manualGear;
    private string? _manualLevel;
    private Control? _currentPage;

    private static readonly string[] ManualGearValues = ["静音", "标准", "强劲", "超频"];
    private static readonly string[] ManualLevelValues = ["低", "中", "高"];

    public MainWindow()
    {
        InitializeComponent();
        PageCache.Children.Clear();
        PageHost.Content = StatusPage;
        _currentPage = StatusPage;
        NavigationView.SelectedItem = StatusNavigationItem;
        _ipc.ConnectionChanged += IpcConnectionChanged;
        _ipc.EventReceived += CoreEventReceived;
        Opened += WindowOpened;
        Closing += WindowClosing;
    }

    private void NavigationViewSelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is not FANavigationViewItem item || item.Tag is not string page)
        {
            return;
        }

        var nextPage = page switch
        {
            "status" => StatusPage,
            "fan-control" => FanControlPage,
            "core-device" => CoreDevicePage,
            "about" => AboutPage,
            _ => null,
        };

        if (nextPage is null || ReferenceEquals(nextPage, _currentPage))
        {
            return;
        }

        PageHost.Content = nextPage;
        _currentPage = nextPage;
    }

    private async void WindowOpened(object? sender, EventArgs e)
    {
        if (_recoveryLoop is not null)
        {
            return;
        }

        _notifications = new WindowNotificationManager(this)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 1,
        };

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
            SetActivity($"{status} Waiting for IPC.", FAInfoBarSeverity.Informational);
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
                    Dispatcher.UIThread.Post(() => SetActivity(error ?? "THRM Core reported an unspecified device error.", FAInfoBarSeverity.Error));
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
            SetActivity("Choose a gear and level before applying.", FAInfoBarSeverity.Warning);
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
            SetActivity("THRM Core window shown.", FAInfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            SetActivity($"Show window failed: {ex.Message}", FAInfoBarSeverity.Error);
        }
    }

    private async void QuitCoreClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _ipc.QuitCoreAsync();
            SetActivity("Quit request sent; no request replay is attempted.", FAInfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            SetActivity($"Quit core failed: {ex.Message}", FAInfoBarSeverity.Error);
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
            SetActivity($"{action} unavailable: THRM Core is not connected; known state retained.", FAInfoBarSeverity.Warning);
            return false;
        }

        _writeInProgress = true;
        SetActionAvailability();
        SetActivity($"{action} in progress...", FAInfoBarSeverity.Informational, TimeSpan.FromSeconds(10));
        try
        {
            if (!await request())
            {
                SetActivity($"{action} was rejected; known state retained.", FAInfoBarSeverity.Warning);
                return false;
            }

            if (await RefreshStateAsync())
            {
                SetActivity($"{action} succeeded; state synchronized.", FAInfoBarSeverity.Success);
                return true;
            }

            SetActivity($"{action} accepted, but state was not synchronized; known state retained.", FAInfoBarSeverity.Warning);
            return false;
        }
        catch (Exception ex)
        {
            SetActivity($"{action} failed: {ex.Message}; known state retained.", FAInfoBarSeverity.Error);
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
            LastUpdateText.Text = $"Last update: {DateTime.Now:HH:mm:ss}";
            return true;
        }
        catch (Exception ex)
        {
            _deviceStateKnown = false;
            _configKnown = false;
            SetActionAvailability();
            SetActivity($"State refresh failed: {ex.Message}", FAInfoBarSeverity.Error);
            return false;
        }
    }

    private void UpdateConnection(bool connected)
    {
        ConnectionInfoBar.IsOpen = !connected;
        _deviceStateKnown = false;
        _configKnown = false;
        SetActionAvailability();
    }

    private void SetActivity(string message, FAInfoBarSeverity severity, TimeSpan? expiration = null)
    {
        _notifications?.Show(new Notification
        {
            Title = "THRM",
            Message = message,
            Type = severity switch
            {
                FAInfoBarSeverity.Success => NotificationType.Success,
                FAInfoBarSeverity.Warning => NotificationType.Warning,
                FAInfoBarSeverity.Error => NotificationType.Error,
                _ => NotificationType.Information,
            },
            Expiration = expiration ?? TimeSpan.FromSeconds(severity is FAInfoBarSeverity.Error or FAInfoBarSeverity.Warning ? 8 : 5),
        });
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
        SelectCoreValue(ManualGearComboBox, ManualGearValues, _manualGear);
        SelectCoreValue(ManualLevelComboBox, ManualLevelValues, _manualLevel);
        UpdateManualAppliedText();
    }

    private void ApplyDeviceStatus(DeviceStatusSnapshot status)
    {
        _deviceConnected = status.Connected;
        _deviceModel = status.Model;
        DeviceText.Text = status.Connected ? "Connected" : "Not connected";
        DeviceStatusIcon.IconSource = new FluentIconSource
        {
            Icon = status.Connected
                ? FluentIcons.Common.Icon.PlugConnected
                : FluentIcons.Common.Icon.PlugDisconnected
        };
        ModelText.Text = EmptyDash(status.Model);
        ProductText.Text = EmptyDash(status.ProductId);
        ModeText.Text = EmptyDash(status.CurrentData?.WorkMode);
        if (status.CurrentData is null)
        {
            FanText.Text = "—";
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
        FanText.Text = $"{fanData.CurrentRpm} RPM → {fanData.TargetRpm} RPM";

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
