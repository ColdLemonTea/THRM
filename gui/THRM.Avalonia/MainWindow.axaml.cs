using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Avalonia.Layout;
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
    private int _customSpeedRpm = 2000;
    private bool _gearLight;
    private bool _powerOnStart;
    private string _smartStartStop = "off";
    private bool _updatingConfigControls;
    private bool _configKnown;
    private bool _writeInProgress;
    private string? _deviceModel;
    private string? _manualGear;
    private string? _manualLevel;
    private Control? _currentPage;
    private readonly List<(FANumberBox Temperature, FANumberBox Rpm)> _fanCurveEditors = [];
    private readonly List<FanCurveProfileSnapshot> _fanCurveProfiles = [];
    private string? _activeFanCurveProfileId;
    private bool _fanCurveKnown;
    private bool _fanCurveLoading;

    private static readonly string[] ManualGearValues = ["静音", "标准", "强劲", "超频"];
    private static readonly string[] ManualLevelValues = ["低", "中", "高"];
    private static readonly string[] SmartStartStopValues = ["off", "immediate", "delayed"];

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
            "fan-curve" => FanCurvePage,
            "fan-control" => FanControlPage,
            "device-settings" => DeviceSettingsPage,
            "core" => CorePage,
            "about" => AboutPage,
            _ => null,
        };

        if (nextPage is null || ReferenceEquals(nextPage, _currentPage))
        {
            return;
        }

        PageHost.Content = nextPage;
        _currentPage = nextPage;
        if (ReferenceEquals(nextPage, FanCurvePage))
        {
            _ = RefreshFanCurveAsync();
        }
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
                if (ReferenceEquals(_currentPage, FanCurvePage))
                {
                    _ = RefreshFanCurveAsync();
                }
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

    private async void CustomSpeedClick(object? sender, RoutedEventArgs e)
    {
        if (!CanChangeCustomSpeed())
        {
            CustomSpeedSwitch.IsChecked = _customSpeedEnabled;
            return;
        }

        var enabled = CustomSpeedSwitch.IsChecked == true;
        CustomSpeedSwitch.IsChecked = _customSpeedEnabled;
        var rpm = _customSpeedRpm;
        if (enabled && !TryReadCustomSpeed(out rpm, out var error))
        {
            SetActivity(error, FAInfoBarSeverity.Warning);
            return;
        }

        await RunWriteAsync(
            enabled ? "Enable custom fan speed" : "Disable custom fan speed",
            () => _ipc.SetCustomSpeedAsync(enabled, rpm, _lifetime.Token));
    }

    private async void ApplyCustomSpeedClick(object? sender, RoutedEventArgs e)
    {
        if (!_customSpeedEnabled)
        {
            SetActivity("Enable custom speed before applying a target speed.", FAInfoBarSeverity.Warning);
            return;
        }

        if (!TryReadCustomSpeed(out var rpm, out var error))
        {
            SetActivity(error, FAInfoBarSeverity.Warning);
            return;
        }

        await RunWriteAsync("Apply custom fan speed", () => _ipc.SetCustomSpeedAsync(true, rpm, _lifetime.Token));
    }

    private async void GearLightClick(object? sender, RoutedEventArgs e)
    {
        if (IsBs1 || !CanChangeDeviceFeatures())
        {
            GearLightSwitch.IsChecked = _gearLight;
            return;
        }

        var enabled = GearLightSwitch.IsChecked == true;
        GearLightSwitch.IsChecked = _gearLight;
        await RunWriteAsync(
            enabled ? "Enable gear light" : "Disable gear light",
            () => _ipc.SetGearLightAsync(enabled, _lifetime.Token));
    }

    private async void PowerOnStartClick(object? sender, RoutedEventArgs e)
    {
        if (!CanChangeDeviceFeatures())
        {
            PowerOnStartSwitch.IsChecked = _powerOnStart;
            return;
        }

        var enabled = PowerOnStartSwitch.IsChecked == true;
        PowerOnStartSwitch.IsChecked = _powerOnStart;
        await RunWriteAsync(
            enabled ? "Enable power-on start" : "Disable power-on start",
            () => _ipc.SetPowerOnStartAsync(enabled, _lifetime.Token));
    }

    private async void SmartStartStopSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingConfigControls || _writeInProgress || IsBs1 || !CanChangeDeviceFeatures())
        {
            SelectCoreValue(SmartStartStopComboBox, SmartStartStopValues, _smartStartStop);
            return;
        }

        var value = SelectedCoreValue(SmartStartStopComboBox, SmartStartStopValues);
        if (value is null || string.Equals(value, _smartStartStop, StringComparison.Ordinal))
        {
            return;
        }

        SelectCoreValue(SmartStartStopComboBox, SmartStartStopValues, _smartStartStop);
        await RunWriteAsync("Set smart start and stop", () => _ipc.SetSmartStartStopAsync(value, _lifetime.Token));
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

    private async void ReloadFanCurveClick(object? sender, RoutedEventArgs e) => await RefreshFanCurveAsync();

    private async void FanCurveProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_fanCurveLoading
            || _writeInProgress
            || FanCurveProfileComboBox.SelectedItem is not FanCurveProfileSnapshot profile
            || string.Equals(profile.Id, _activeFanCurveProfileId, StringComparison.Ordinal))
        {
            return;
        }

        if (await RunWriteAsync(
                "Switch fan curve profile",
                async () =>
                {
                    await _ipc.SetActiveFanCurveProfileAsync(profile.Id, _lifetime.Token);
                    return true;
                }))
        {
            await RefreshFanCurveAsync();
        }
    }

    private async void SaveFanCurveProfileClick(object? sender, RoutedEventArgs e)
    {
        var name = NewFanCurveProfileNameTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            SetActivity("Enter a profile name before saving.", FAInfoBarSeverity.Warning);
            return;
        }

        if (!TryReadFanCurve(out var curve, out var error))
        {
            SetActivity(error, FAInfoBarSeverity.Warning);
            return;
        }

        if (await RunWriteAsync(
                "Save fan curve profile",
                async () =>
                {
                    await _ipc.SaveFanCurveProfileAsync(string.Empty, name, curve, true, _lifetime.Token);
                    return true;
                }))
        {
            NewFanCurveProfileNameTextBox.Text = string.Empty;
            await RefreshFanCurveAsync();
        }
    }

    private async void ApplyFanCurveClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadFanCurve(out var curve, out var error))
        {
            SetActivity(error, FAInfoBarSeverity.Warning);
            return;
        }

        if (await RunWriteAsync("Apply fan curve", () => _ipc.SetFanCurveAsync(curve, _lifetime.Token)))
        {
            await RefreshFanCurveAsync();
        }
    }

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
        _fanCurveKnown = connected && _fanCurveKnown;
        if (!connected)
        {
            FanCurveStateText.Text = "Fan curve editing unavailable: THRM Core is not connected.";
        }
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
        _updatingConfigControls = true;
        try
        {
            _autoControl = config.AutoControl;
            _customSpeedEnabled = config.CustomSpeedEnabled;
            _customSpeedRpm = config.CustomSpeedRpm is >= 1000 and <= 4000 ? config.CustomSpeedRpm : 2000;
            _gearLight = config.GearLight;
            _powerOnStart = config.PowerOnStart;
            _smartStartStop = SmartStartStopValues.Contains(config.SmartStartStop) ? config.SmartStartStop! : "off";
            if (!string.IsNullOrWhiteSpace(config.ManualGear))
            {
                _manualGear = config.ManualGear;
            }

            if (!string.IsNullOrWhiteSpace(config.ManualLevel))
            {
                _manualLevel = config.ManualLevel;
            }

            AutoControlSwitch.IsChecked = _autoControl;
            CustomSpeedSwitch.IsChecked = _customSpeedEnabled;
            CustomSpeedNumberBox.Value = _customSpeedRpm;
            GearLightSwitch.IsChecked = _gearLight;
            PowerOnStartSwitch.IsChecked = _powerOnStart;
            SelectCoreValue(SmartStartStopComboBox, SmartStartStopValues, _smartStartStop);
            SelectCoreValue(ManualGearComboBox, ManualGearValues, _manualGear);
            SelectCoreValue(ManualLevelComboBox, ManualLevelValues, _manualLevel);
            UpdateManualAppliedText();
        }
        finally
        {
            _updatingConfigControls = false;
        }
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
        AutoControlSwitch.IsEnabled = _ipc.IsConnected && _configKnown && !_writeInProgress && !_customSpeedEnabled;

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

        var canChangeCustomSpeed = CanChangeCustomSpeed();
        CustomSpeedSwitch.IsEnabled = canChangeCustomSpeed;
        CustomSpeedNumberBox.IsEnabled = canChangeCustomSpeed && _customSpeedEnabled;
        ApplyCustomSpeedButton.IsEnabled = canChangeCustomSpeed && _customSpeedEnabled;
        CustomSpeedStatusText.Text = GetCustomSpeedAvailabilityText(canChangeCustomSpeed);

        var canChangeDeviceFeatures = CanChangeDeviceFeatures();
        GearLightDeviceSetting.IsVisible = !IsBs1;
        GearLightSwitch.IsEnabled = canChangeDeviceFeatures && !IsBs1;
        PowerOnStartSwitch.IsEnabled = canChangeDeviceFeatures;
        SmartStartStopDeviceSetting.IsVisible = !IsBs1;
        SmartStartStopComboBox.IsEnabled = canChangeDeviceFeatures && !IsBs1;
        DeviceFeaturesStatusText.Text = GetDeviceFeaturesAvailabilityText(canChangeDeviceFeatures);

        var canEditFanCurve = _ipc.IsConnected && _fanCurveKnown && !_fanCurveLoading && !_writeInProgress;
        FanCurveProfileComboBox.IsEnabled = canEditFanCurve && _fanCurveProfiles.Count > 1;
        NewFanCurveProfileNameTextBox.IsEnabled = canEditFanCurve;
        SaveFanCurveProfileButton.IsEnabled = canEditFanCurve;
        ReloadFanCurveButton.IsEnabled = _ipc.IsConnected && !_fanCurveLoading && !_writeInProgress;
        ApplyFanCurveButton.IsEnabled = canEditFanCurve && _fanCurveEditors.Count >= 2;
    }

    private async Task RefreshFanCurveAsync()
    {
        if (!_ipc.IsConnected)
        {
            _fanCurveKnown = false;
            FanCurveStateText.Text = "Fan curve editing unavailable: THRM Core is not connected.";
            SetActionAvailability();
            return;
        }

        _fanCurveLoading = true;
        FanCurveStateText.Text = "Loading the active curve from THRM Core...";
        SetActionAvailability();
        try
        {
            var curveTask = _ipc.GetFanCurveAsync(_lifetime.Token);
            var profilesTask = _ipc.GetFanCurveProfilesAsync(_lifetime.Token);
            await Task.WhenAll(curveTask, profilesTask);
            ApplyFanCurve(curveTask.Result, profilesTask.Result);
            _fanCurveKnown = _fanCurveEditors.Count >= 2;
            FanCurveStateText.Text = _fanCurveKnown
                ? "Active curve loaded from THRM Core."
                : "THRM Core returned an incomplete curve.";
        }
        catch (Exception ex)
        {
            _fanCurveKnown = false;
            FanCurveStateText.Text = "Fan curve could not be loaded; known values were retained.";
            SetActivity($"Fan curve refresh failed: {ex.Message}", FAInfoBarSeverity.Error);
        }
        finally
        {
            _fanCurveLoading = false;
            SetActionAvailability();
        }
    }

    private void ApplyFanCurve(IReadOnlyList<FanCurvePoint> curve, FanCurveProfilesSnapshot profiles)
    {
        _fanCurveLoading = true;
        try
        {
            _fanCurveProfiles.Clear();
            _fanCurveProfiles.AddRange(profiles.Profiles);
            _activeFanCurveProfileId = profiles.ActiveId;
            FanCurveProfileComboBox.ItemsSource = _fanCurveProfiles.ToArray();
            FanCurveProfileComboBox.SelectedItem = _fanCurveProfiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, _activeFanCurveProfileId, StringComparison.Ordinal));

            _fanCurveEditors.Clear();
            FanCurvePointsPanel.Children.Clear();
            for (var index = 0; index < curve.Count; index++)
            {
                var temperature = new FANumberBox
                {
                    Minimum = 0,
                    Maximum = 110,
                    SmallChange = 1,
                    SpinButtonPlacementMode = FANumberBoxSpinButtonPlacementMode.Compact,
                    Value = curve[index].Temperature,
                };
                var rpm = new FANumberBox
                {
                    Minimum = 0,
                    Maximum = 4000,
                    SmallChange = 100,
                    SpinButtonPlacementMode = FANumberBoxSpinButtonPlacementMode.Compact,
                    Value = curve[index].Rpm,
                };
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("44,*,*"),
                    ColumnSpacing = 12,
                };
                row.Children.Add(new TextBlock
                {
                    Text = (index + 1).ToString(),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(temperature);
                row.Children.Add(rpm);
                Grid.SetColumn(temperature, 1);
                Grid.SetColumn(rpm, 2);
                FanCurvePointsPanel.Children.Add(row);
                _fanCurveEditors.Add((temperature, rpm));
                temperature.PropertyChanged += FanCurveEditorPropertyChanged;
                rpm.PropertyChanged += FanCurveEditorPropertyChanged;
            }

            UpdateFanCurvePreview();
        }
        finally
        {
            _fanCurveLoading = false;
        }
    }

    private void FanCurveEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == FANumberBox.ValueProperty)
        {
            UpdateFanCurvePreview();
        }
    }

    private void UpdateFanCurvePreview()
    {
        FanCurvePreview.Points = _fanCurveEditors
            .Select(editor => new FanCurvePoint
            {
                Temperature = double.IsFinite(editor.Temperature.Value) ? (int)Math.Round(editor.Temperature.Value) : 0,
                Rpm = double.IsFinite(editor.Rpm.Value) ? (int)Math.Round(editor.Rpm.Value) : 0,
            })
            .ToArray();
    }

    private bool TryReadFanCurve(out List<FanCurvePoint> curve, out string error)
    {
        curve = [];
        if (_fanCurveEditors.Count < 2)
        {
            error = "A fan curve needs at least two points.";
            return false;
        }

        for (var index = 0; index < _fanCurveEditors.Count; index++)
        {
            var temperatureValue = _fanCurveEditors[index].Temperature.Value;
            var rpmValue = _fanCurveEditors[index].Rpm.Value;
            if (!double.IsFinite(temperatureValue) || !double.IsFinite(rpmValue)
                || temperatureValue != Math.Truncate(temperatureValue)
                || rpmValue != Math.Truncate(rpmValue))
            {
                error = $"Point {index + 1} must use whole-number temperature and speed values.";
                return false;
            }

            var point = new FanCurvePoint
            {
                Temperature = (int)temperatureValue,
                Rpm = (int)rpmValue,
            };
            if (point.Temperature is < 0 or > 110 || point.Rpm is < 0 or > 4000)
            {
                error = $"Point {index + 1} is outside the supported range.";
                return false;
            }

            if (curve.Count > 0 && point.Temperature <= curve[^1].Temperature)
            {
                error = "Temperature points must increase from top to bottom.";
                return false;
            }

            if (curve.Count > 0 && point.Rpm < curve[^1].Rpm)
            {
                error = "Target speeds cannot decrease from top to bottom.";
                return false;
            }

            curve.Add(point);
        }

        error = string.Empty;
        return true;
    }

    private bool CanChangeManualControl() =>
        _ipc.IsConnected
        && _deviceStateKnown
        && _configKnown
        && _deviceConnected
        && !_writeInProgress
        && !_autoControl
        && !_customSpeedEnabled;

    private bool CanChangeDeviceFeatures() =>
        _ipc.IsConnected
        && _deviceStateKnown
        && _configKnown
        && _deviceConnected
        && !_writeInProgress;

    private bool CanChangeCustomSpeed() => CanChangeDeviceFeatures();

    private bool TryReadCustomSpeed(out int rpm, out string error)
    {
        var value = CustomSpeedNumberBox.Value;
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value is < 1000 or > 4000)
        {
            rpm = 0;
            error = "Custom fan speed must be a whole number from 1,000 to 4,000 RPM.";
            return false;
        }

        rpm = (int)value;
        error = string.Empty;
        return true;
    }

    private string GetCustomSpeedAvailabilityText(bool canChangeCustomSpeed)
    {
        if (!_ipc.IsConnected)
        {
            return "Custom speed unavailable: THRM Core is not connected.";
        }

        if (!_deviceStateKnown || !_configKnown)
        {
            return "Custom speed unavailable: waiting for synchronized device and configuration state.";
        }

        if (!_deviceConnected)
        {
            return "Custom speed unavailable: connect a device first.";
        }

        if (_writeInProgress)
        {
            return "Custom speed unavailable: a write is in progress.";
        }

        return canChangeCustomSpeed
            ? _customSpeedEnabled
                ? $"Custom speed enabled at {_customSpeedRpm} RPM."
                : "Custom speed is disabled."
            : "Custom speed unavailable: current state does not permit writes.";
    }

    private string GetDeviceFeaturesAvailabilityText(bool canChangeDeviceFeatures)
    {
        if (!_ipc.IsConnected)
        {
            return "Device features unavailable: THRM Core is not connected.";
        }

        if (!_deviceStateKnown || !_configKnown)
        {
            return "Device features unavailable: waiting for synchronized device and configuration state.";
        }

        if (!_deviceConnected)
        {
            return "Device features unavailable: connect a device first.";
        }

        if (_writeInProgress)
        {
            return "Device features unavailable: a write is in progress.";
        }

        return canChangeDeviceFeatures
            ? IsBs1
                ? "This device supports power-on start."
                : "Device features are ready."
            : "Device features unavailable: current state does not permit writes.";
    }

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
