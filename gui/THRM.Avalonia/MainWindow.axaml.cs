using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FluentIcons.Avalonia.Fluent;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Media.Animation;
using FluentAvalonia.UI.Navigation;

namespace THRM.Avalonia;

internal static class FanRatedRpm
{
    public const int FallbackRpm = 4000;

    public static int Resolve(FanDataSnapshot? fanData, string? model)
    {
        if (IsFixed3300Model(model))
        {
            return 3300;
        }

        if (fanData?.GearSettings is { } gearSettings
            && FromGearSettings(gearSettings) is { } ratedRpm)
        {
            return ratedRpm;
        }

        return FromMaxGearText(fanData?.MaxGear) ?? FallbackRpm;
    }

    public static int? FromGearSettings(int gearSettings) =>
        FromGearCode((gearSettings >> 4) & 0x0F);

    public static int? FromGearCode(int gearCode) => gearCode switch
    {
        0x2 or 0x3 or 0xA => 2760,
        0x4 or 0xC => 3300,
        0x6 or 0xE => 4000,
        _ => null,
    };

    public static int? FromMaxGearText(string? maxGear) => maxGear switch
    {
        "静音" => 1900,
        "标准" => 2700,
        "强劲" => 3300,
        "超频" => 4000,
        _ => null,
    };

    public static void SelfCheck()
    {
        if (FromGearCode(0x6) != 4000 || FromGearCode(0x4) != 3300 || FromGearCode(0x2) != 2760
            || Resolve(new FanDataSnapshot { GearSettings = 0x60 }, "other") != 4000
            || Resolve(new FanDataSnapshot { GearSettings = 0x40 }, "other") != 3300
            || Resolve(new FanDataSnapshot { GearSettings = 0x20 }, "other") != 2760
            || Resolve(new FanDataSnapshot { GearSettings = 0x10, MaxGear = "强劲" }, "other") != 3300
            || Resolve(new FanDataSnapshot { MaxGear = "超频" }, "other") != 4000
            || Resolve(null, "BS2") != 3300 || Resolve(null, "other") != FallbackRpm)
        {
            throw new InvalidOperationException("Fan rated RPM mapping check failed.");
        }
    }

    private static bool IsFixed3300Model(string? model) =>
        string.Equals(model, "BS1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(model, "BS2", StringComparison.OrdinalIgnoreCase)
        || string.Equals(model, "BS3", StringComparison.OrdinalIgnoreCase);
}

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
    private Type? _currentPageType;
    private readonly List<FanCurvePoint> _fanCurve = [];
    private readonly List<FanCurveProfileSnapshot> _fanCurveProfiles = [];
    private string? _activeFanCurveProfileId;
    private bool _fanCurveKnown;
    private bool _fanCurveLoading;
    private bool _fanCurveLearningEnabled;
    private string _fanCurveLearningBias = "balanced";
    private readonly List<int> _learnedFanCurveOffsets = [];
    private readonly List<TemperatureHistoryPointSnapshot> _temperatureHistory = [];
    private bool _temperatureHistoryKnown;
    private bool _temperatureHistoryLoading;
    private bool _temperatureHistoryEnabled;
    private int _temperatureHistoryRetentionHours = 1;
    private bool _updatingTemperatureHistoryControls;
    private int _deviceFanMaximumRpm = FanRatedRpm.FallbackRpm;

    private static readonly string[] ManualGearValues = ["静音", "标准", "强劲", "超频"];
    private static readonly string[] ManualLevelValues = ["低", "中", "高"];
    private static readonly string[] SmartStartStopValues = ["off", "immediate", "delayed"];
    private static readonly int[] TemperatureHistoryRetentionOptions = [1, 2, 3, 6, 12, 24];

    public MainWindow()
    {
        InitializeComponent();
        using (var iconStream = AssetLoader.Open(new Uri("avares://THRM.Avalonia/Assets/thrm.png")))
        {
            Icon = new WindowIcon(new Bitmap(iconStream));
        }
        PageDefinitions.Children.Clear();
        PageFrame.NavigationPageFactory = new MainWindowPageFactory(this);
        NavigateToPage(typeof(StatusRoute));
        NavigationView.SelectedItem = StatusNavigationItem;
        _ipc.ConnectionChanged += IpcConnectionChanged;
        _ipc.EventReceived += CoreEventReceived;
        FanCurvePreview.PointDragged += FanCurvePreviewPointDragged;
        TemperatureHistoryPreview.HoverChanged += HistoryPreviewHoverChanged;
        PowerHistoryPreview.HoverChanged += HistoryPreviewHoverChanged;
        Opened += WindowOpened;
        Closing += WindowClosing;
    }

    private void NavigationViewSelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is not FANavigationViewItem item || item.Tag is not string page)
        {
            return;
        }

        var nextPageType = page switch
        {
            "status" => typeof(StatusRoute),
            "fan-curve" => typeof(FanCurveRoute),
            "temperature-history" => typeof(TemperatureHistoryRoute),
            "fan-control" => typeof(FanControlRoute),
            "device-settings" => typeof(DeviceSettingsRoute),
            "about" => typeof(AboutRoute),
            _ => null,
        };

        if (nextPageType is null || nextPageType == _currentPageType)
        {
            return;
        }

        NavigateToPage(nextPageType);
        if (nextPageType == typeof(FanCurveRoute))
        {
            _ = RefreshFanCurveAsync();
        }

        if (nextPageType == typeof(TemperatureHistoryRoute))
        {
            _ = RefreshTemperatureHistoryAsync();
        }
    }

    private void NavigateToPage(Type pageType)
    {
        PageFrame.NavigateToType(
            pageType,
            null,
            new FAFrameNavigationOptions
            {
                IsNavigationStackEnabled = false,
                TransitionInfoOverride = new FAEntranceNavigationTransitionInfo
                {
                    FromVerticalOffset = 24,
                },
            });
        _currentPageType = pageType;
    }

    private sealed class StatusRoute { }
    private sealed class FanCurveRoute { }
    private sealed class TemperatureHistoryRoute { }
    private sealed class FanControlRoute { }
    private sealed class DeviceSettingsRoute { }
    private sealed class AboutRoute { }

    private sealed class MainWindowPageFactory(MainWindow owner) : IFANavigationPageFactory
    {
        public Control? GetPage(Type sourcePageType) => sourcePageType switch
        {
            var type when type == typeof(StatusRoute) => owner.StatusPage,
            var type when type == typeof(FanCurveRoute) => owner.FanCurvePage,
            var type when type == typeof(TemperatureHistoryRoute) => owner.TemperatureHistoryPage,
            var type when type == typeof(FanControlRoute) => owner.FanControlPage,
            var type when type == typeof(DeviceSettingsRoute) => owner.DeviceSettingsPage,
            var type when type == typeof(AboutRoute) => owner.AboutPage,
            _ => null,
        };

        public Control? GetPageFromObject(object target) => null;
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
                if (_currentPageType == typeof(FanCurveRoute))
                {
                    _ = RefreshFanCurveAsync();
                }

                if (_currentPageType == typeof(TemperatureHistoryRoute))
                {
                    _ = RefreshTemperatureHistoryAsync();
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
            case "temperature-history-update":
                if (e.TryGetData<TemperatureHistoryPointSnapshot>(out var historyPoint) && historyPoint is not null)
                {
                    Dispatcher.UIThread.Post(() => AppendTemperatureHistoryPoint(historyPoint));
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

    private async void TemperatureHistoryEnabledClick(object? sender, RoutedEventArgs e)
    {
        if (_temperatureHistoryLoading || _writeInProgress || !_ipc.IsConnected || !_temperatureHistoryKnown)
        {
            TemperatureHistoryEnabledSwitch.IsChecked = _temperatureHistoryEnabled;
            return;
        }

        var enabled = TemperatureHistoryEnabledSwitch.IsChecked == true;
        TemperatureHistoryEnabledSwitch.IsChecked = _temperatureHistoryEnabled;
        if (await RunWriteAsync(
                enabled ? "Enable temperature history" : "Disable temperature history",
                () => _ipc.SetTemperatureHistoryEnabledAsync(enabled, _lifetime.Token)))
        {
            await RefreshTemperatureHistoryAsync();
        }
    }

    private async void TemperatureHistoryRetentionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingTemperatureHistoryControls
            || _temperatureHistoryLoading
            || _writeInProgress
            || !_ipc.IsConnected
            || !_temperatureHistoryKnown)
        {
            SelectTemperatureHistoryRetention();
            return;
        }

        var index = TemperatureHistoryRetentionComboBox.SelectedIndex;
        if (index < 0 || index >= TemperatureHistoryRetentionOptions.Length)
        {
            SelectTemperatureHistoryRetention();
            return;
        }

        var hours = TemperatureHistoryRetentionOptions[index];
        if (hours == _temperatureHistoryRetentionHours)
        {
            return;
        }

        SelectTemperatureHistoryRetention();
        if (await RunWriteAsync(
                "Set temperature history retention",
                () => _ipc.SetTemperatureHistoryRetentionHoursAsync(hours, _lifetime.Token)))
        {
            await RefreshTemperatureHistoryAsync();
        }
    }

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

    private static void FanCurveProfileManageFlyoutOpening(object? sender, EventArgs e)
    {
        if (sender is not FAMenuFlyout flyout || flyout.Popup.Child is not Control presenter)
        {
            return;
        }

        presenter.Transitions = CreateFlyoutTransitions();
        presenter.Opacity = 0;
        presenter.RenderTransform = TransformOperations.Parse("translate(0px, -6px)");
    }

    private static void FanCurveProfileManageFlyoutOpened(object? sender, EventArgs e)
    {
        if (sender is not FAMenuFlyout flyout || flyout.Popup.Child is not Control presenter)
        {
            return;
        }

        presenter.Opacity = 1;
        presenter.RenderTransform = TransformOperations.Identity;
    }

    private static Transitions CreateFlyoutTransitions() =>
        new()
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(150),
                Easing = AnimatedContentClip.PowerToysEaseOutCubic,
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(180),
                Easing = AnimatedContentClip.PowerToysEaseOutCubic,
            },
        };

    private async void NewFanCurveProfileClick(object? sender, RoutedEventArgs e)
    {
        if (!CanEditFanCurve)
        {
            return;
        }

        if (!TryReadFanCurve(out var curve, out var error))
        {
            SetActivity(error, FAInfoBarSeverity.Warning);
            return;
        }

        var name = await PromptFanCurveProfileNameAsync(
            "Create fan curve profile",
            "Save the current curve as a new profile.",
            "Create",
            string.Empty);
        if (name is null)
        {
            return;
        }

        if (await RunWriteAsync(
                "Create fan curve profile",
                async () =>
                {
                    await _ipc.SaveFanCurveProfileAsync(string.Empty, name, curve, true, _lifetime.Token);
                    return true;
                }))
        {
            await RefreshFanCurveAsync();
        }
    }

    private async void RenameFanCurveProfileClick(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveFanCurveProfile();
        if (!CanEditFanCurve || profile is null)
        {
            SetActivity("Choose an active profile before renaming.", FAInfoBarSeverity.Warning);
            return;
        }

        var name = await PromptFanCurveProfileNameAsync(
            "Rename fan curve profile",
            $"Choose a new name for “{profile.Name}”.",
            "Rename",
            profile.Name);
        if (name is null || string.Equals(profile.Name, name, StringComparison.Ordinal))
        {
            return;
        }

        FanCurveProfileSnapshot? renamed = null;
        if (await RunWriteAsync(
                "Rename fan curve profile",
                async () =>
                {
                    renamed = await _ipc.SaveFanCurveProfileAsync(
                        profile.Id,
                        name,
                        profile.Curve,
                        false,
                        _lifetime.Token);
                    return true;
                }))
        {
            if (renamed is not null)
            {
                ReplaceFanCurveProfile(renamed);
            }
        }
    }

    private async void ExportFanCurveProfilesClick(object? sender, RoutedEventArgs e)
    {
        if (!CanEditFanCurve)
        {
            SetActivity("Export unavailable until the active curve is synchronized.", FAInfoBarSeverity.Warning);
            return;
        }

        try
        {
            var code = await _ipc.ExportFanCurveProfilesAsync(_lifetime.Token);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                throw new InvalidOperationException("The system clipboard is unavailable.");
            }

            await clipboard.SetTextAsync(code);
            SetActivity("Saved fan curve profiles copied to the clipboard.", FAInfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            SetActivity($"Export fan curve profiles failed: {ex.Message}", FAInfoBarSeverity.Error);
        }
    }

    private async void ImportFanCurveProfilesFromClipboardClick(object? sender, RoutedEventArgs e)
    {
        if (!CanEditFanCurve)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            SetActivity("The system clipboard is unavailable.", FAInfoBarSeverity.Warning);
            return;
        }

        var code = (await clipboard.TryGetTextAsync())?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            SetActivity("The clipboard does not contain a profile code.", FAInfoBarSeverity.Warning);
            return;
        }

        if (await RunWriteAsync(
                "Import fan curve profiles",
                () => _ipc.ImportFanCurveProfilesAsync(code, _lifetime.Token)))
        {
            await RefreshFanCurveAsync();
        }
    }

    private async void DeleteFanCurveProfileClick(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveFanCurveProfile();
        if (!CanEditFanCurve || profile is null)
        {
            SetActivity("Choose an active profile before deleting.", FAInfoBarSeverity.Warning);
            return;
        }

        if (_fanCurveProfiles.Count <= 1)
        {
            SetActivity("At least one fan curve profile must remain.", FAInfoBarSeverity.Warning);
            return;
        }

        if (!await ConfirmActionAsync(
                "Delete active profile?",
                $"Delete “{profile.Name}”? This cannot be undone.",
                "Delete"))
        {
            return;
        }

        if (await RunWriteAsync(
                "Delete fan curve profile",
                () => _ipc.DeleteFanCurveProfileAsync(profile.Id, _lifetime.Token)))
        {
            await RefreshFanCurveAsync();
        }
    }

    private async void ResetLearnedOffsetsClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmActionAsync(
                "Reset curve learning?",
                "THRM Core will forget its learned RPM corrections. The base fan curve is unchanged.",
                "Reset"))
        {
            return;
        }

        if (await RunWriteAsync(
                "Reset learned offsets",
                async () =>
                {
                    await _ipc.ResetLearnedOffsetsAsync(_lifetime.Token);
                    return true;
                }))
        {
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
        if (!connected)
        {
            _deviceFanMaximumRpm = FanRatedRpm.Resolve(null, _deviceModel);
            UpdateTemperatureHistoryPreviews();
        }
        _fanCurveKnown = connected && _fanCurveKnown;
        _temperatureHistoryKnown = connected && _temperatureHistoryKnown;
        if (!connected)
        {
            TemperatureHistoryStateText.Text = "Temperature history unavailable: THRM Core is not connected.";
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
            _fanCurveLearningEnabled = config.SmartControl?.Learning == true;
            _fanCurveLearningBias = config.SmartControl?.LearningBias ?? "balanced";
            _learnedFanCurveOffsets.Clear();
            _learnedFanCurveOffsets.AddRange(config.SmartControl?.LearnedOffsets ?? []);
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
            UpdateFanCurvePreview();
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
            _deviceFanMaximumRpm = FanRatedRpm.Resolve(null, _deviceModel);
            FanText.Text = "—";
            UpdateTemperatureHistoryPreviews();
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

    private void ApplyFanData(FanDataSnapshot fanData)
    {
        _deviceFanMaximumRpm = FanRatedRpm.Resolve(fanData, _deviceModel);
        FanText.Text = $"{fanData.CurrentRpm} RPM → {fanData.TargetRpm} RPM";
        UpdateTemperatureHistoryPreviews();
    }

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

        var canEditFanCurve = CanEditFanCurve;
        var activeFanCurveProfile = ActiveFanCurveProfile();
        FanCurveProfileComboBox.IsEnabled = canEditFanCurve && _fanCurveProfiles.Count > 1;
        FanCurveProfileManageButton.IsEnabled = canEditFanCurve && _fanCurveProfiles.Count > 0;
        FanCurveNewProfileMenuItem.IsEnabled = canEditFanCurve;
        FanCurveRenameProfileMenuItem.IsEnabled = canEditFanCurve && activeFanCurveProfile is not null;
        FanCurveDeleteProfileMenuItem.IsEnabled = canEditFanCurve
            && activeFanCurveProfile is not null
            && _fanCurveProfiles.Count > 1;
        FanCurveExportProfilesMenuItem.IsEnabled = canEditFanCurve;
        FanCurveImportProfilesMenuItem.IsEnabled = canEditFanCurve;
        ResetLearnedOffsetsButton.IsEnabled = canEditFanCurve;
        ApplyFanCurveButton.IsEnabled = canEditFanCurve && _fanCurve.Count >= 2;
        FanCurvePreview.IsEditable = canEditFanCurve;

        var canManageTemperatureHistory = _ipc.IsConnected
            && _temperatureHistoryKnown
            && !_temperatureHistoryLoading
            && !_writeInProgress;
        TemperatureHistoryEnabledSwitch.IsEnabled = canManageTemperatureHistory;
        TemperatureHistoryRetentionComboBox.IsEnabled = canManageTemperatureHistory;
    }

    private async Task RefreshFanCurveAsync()
    {
        if (!_ipc.IsConnected)
        {
            _fanCurveKnown = false;
            SetActionAvailability();
            return;
        }

        _fanCurveLoading = true;
        SetActionAvailability();
        try
        {
            var curveTask = _ipc.GetFanCurveAsync(_lifetime.Token);
            var profilesTask = _ipc.GetFanCurveProfilesAsync(_lifetime.Token);
            await Task.WhenAll(curveTask, profilesTask);
            ApplyFanCurve(curveTask.Result, profilesTask.Result);
            _fanCurveKnown = _fanCurve.Count >= 2;
        }
        catch (Exception ex)
        {
            _fanCurveKnown = false;
            SetActivity($"Fan curve refresh failed: {ex.Message}", FAInfoBarSeverity.Error);
        }
        finally
        {
            _fanCurveLoading = false;
            SetActionAvailability();
        }
    }

    private async Task RefreshTemperatureHistoryAsync()
    {
        if (_temperatureHistoryLoading)
        {
            return;
        }

        if (!_ipc.IsConnected)
        {
            _temperatureHistoryKnown = false;
            TemperatureHistoryStateText.Text = "Temperature history unavailable: THRM Core is not connected.";
            SetActionAvailability();
            return;
        }

        _temperatureHistoryLoading = true;
        TemperatureHistoryStateText.Text = "Loading temperature history from THRM Core...";
        SetActionAvailability();
        try
        {
            ApplyTemperatureHistory(await _ipc.GetTemperatureHistoryAsync(_lifetime.Token));
        }
        catch (Exception ex)
        {
            _temperatureHistoryKnown = false;
            TemperatureHistoryStateText.Text = "Temperature history could not be loaded; known samples were retained.";
            SetActivity($"Temperature history refresh failed: {ex.Message}", FAInfoBarSeverity.Error);
        }
        finally
        {
            _temperatureHistoryLoading = false;
            SetActionAvailability();
        }
    }

    private void ApplyTemperatureHistory(TemperatureHistorySnapshot history)
    {
        _updatingTemperatureHistoryControls = true;
        try
        {
            _temperatureHistory.Clear();
            _temperatureHistory.AddRange(history.Points.OrderBy(point => point.Timestamp));
            _temperatureHistoryEnabled = history.Enabled;
            _temperatureHistoryRetentionHours = Math.Clamp(history.RetentionHours, 1, 24);
            _temperatureHistoryKnown = true;
            TemperatureHistoryEnabledSwitch.IsChecked = _temperatureHistoryEnabled;
            SelectTemperatureHistoryRetention();
            UpdateTemperatureHistoryPreviews();
            UpdateTemperatureHistorySummary();
            UpdateTemperatureHistoryStateText();
        }
        finally
        {
            _updatingTemperatureHistoryControls = false;
        }
    }

    private void AppendTemperatureHistoryPoint(TemperatureHistoryPointSnapshot point)
    {
        if (!_temperatureHistoryKnown || point.Timestamp <= 0)
        {
            return;
        }

        if (_temperatureHistory.Count > 0 && point.Timestamp <= _temperatureHistory[^1].Timestamp)
        {
            if (point.Timestamp == _temperatureHistory[^1].Timestamp)
            {
                _temperatureHistory[^1] = point;
            }
            else
            {
                return;
            }
        }
        else
        {
            _temperatureHistory.Add(point);
        }

        var cutoff = point.Timestamp - (long)TimeSpan.FromHours(_temperatureHistoryRetentionHours).TotalMilliseconds;
        while (_temperatureHistory.Count > 0 && _temperatureHistory[0].Timestamp < cutoff)
        {
            _temperatureHistory.RemoveAt(0);
        }

        UpdateTemperatureHistoryPreviews();
        UpdateTemperatureHistorySummary();
        UpdateTemperatureHistoryStateText();
    }

    private void SelectTemperatureHistoryRetention()
    {
        var index = Array.IndexOf(TemperatureHistoryRetentionOptions, _temperatureHistoryRetentionHours);
        if (TemperatureHistoryRetentionComboBox.SelectedIndex == index)
        {
            return;
        }

        var wasUpdating = _updatingTemperatureHistoryControls;
        _updatingTemperatureHistoryControls = true;
        try
        {
            TemperatureHistoryRetentionComboBox.SelectedIndex = index;
        }
        finally
        {
            _updatingTemperatureHistoryControls = wasUpdating;
        }
    }

    private void UpdateTemperatureHistoryPreviews()
    {
        var points = _temperatureHistory.ToArray();
        TemperatureHistoryPreview.HistoryWindowHours = _temperatureHistoryRetentionHours;
        PowerHistoryPreview.HistoryWindowHours = _temperatureHistoryRetentionHours;
        TemperatureHistoryPreview.DeviceFanMaximumRpm = _deviceFanMaximumRpm;
        TemperatureHistoryPreview.Points = points;
        PowerHistoryPreview.Points = points;
    }

    private void HistoryPreviewHoverChanged(object? sender, HistoryHoverChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, TemperatureHistoryPreview))
        {
            TemperatureHistoryPreview.SetLinkedHover(e.Timestamp, e.PointerRatio);
        }

        if (!ReferenceEquals(sender, PowerHistoryPreview))
        {
            PowerHistoryPreview.SetLinkedHover(e.Timestamp, e.PointerRatio);
        }
    }

    private void UpdateTemperatureHistoryStateText()
    {
        if (!_temperatureHistoryEnabled)
        {
            TemperatureHistoryStateText.Text = "Background recording is off; THRM Core is not retaining new samples.";
            return;
        }

        var hourLabel = _temperatureHistoryRetentionHours == 1 ? "hour" : "hours";
        TemperatureHistoryStateText.Text = $"Recording is on. {_temperatureHistory.Count:N0} samples are available from the last {_temperatureHistoryRetentionHours} {hourLabel}.";
    }

    private void UpdateTemperatureHistorySummary()
    {
        TemperatureHistoryCpuSummaryText.Text = BuildTemperatureHistorySummary(
            _temperatureHistory.Select(point => point.CpuTemp), "°C", "CPU temperatures");
        TemperatureHistoryGpuSummaryText.Text = BuildTemperatureHistorySummary(
            _temperatureHistory.Select(point => point.GpuTemp), "°C", "GPU temperatures");
        TemperatureHistoryFanSummaryText.Text = BuildTemperatureHistorySummary(
            _temperatureHistory.Select(point => point.FanRpm), "RPM", "fan speeds");

        if (_temperatureHistory.Count == 0)
        {
            TemperatureHistoryLastSampleText.Text = "No recorded samples.";
            return;
        }

        var last = _temperatureHistory[^1];
        TemperatureHistoryLastSampleText.Text = last.Timestamp > 0
            ? $"{DateTimeOffset.FromUnixTimeMilliseconds(last.Timestamp).ToLocalTime():g}"
            : "Timestamp unavailable.";
    }

    private static string BuildTemperatureHistorySummary(IEnumerable<int> values, string unit, string emptyLabel)
    {
        var samples = values.Where(value => value > 0).ToArray();
        return samples.Length == 0
            ? $"No recorded {emptyLabel}."
            : $"Peak {samples.Max():N0} {unit}; average {samples.Average():N0} {unit}.";
    }

    private void ApplyFanCurve(IReadOnlyList<FanCurvePoint> curve, FanCurveProfilesSnapshot profiles)
    {
        _fanCurveLoading = true;
        try
        {
            _fanCurveProfiles.Clear();
            _fanCurveProfiles.AddRange(profiles.Profiles);
            _activeFanCurveProfileId = profiles.ActiveId;
            var activeProfile = ActiveFanCurveProfile();
            FanCurveProfileComboBox.ItemsSource = _fanCurveProfiles.ToArray();
            FanCurveProfileComboBox.SelectedItem = activeProfile;

            _fanCurve.Clear();
            _fanCurve.AddRange(curve.Select(point => new FanCurvePoint
            {
                Temperature = point.Temperature,
                Rpm = point.Rpm,
            }));

            UpdateFanCurvePreview();
        }
        finally
        {
            _fanCurveLoading = false;
        }
    }

    private void FanCurvePreviewPointDragged(object? sender, FanCurvePointDragEventArgs e)
    {
        if (!CanEditFanCurve || e.Index < 0 || e.Index >= _fanCurve.Count)
        {
            return;
        }

        var adjusted = FanCurveEdit.SetRpm(_fanCurve, e.Index, e.Rpm);
        _fanCurve.Clear();
        _fanCurve.AddRange(adjusted);
        UpdateFanCurvePreview();
    }

    private void UpdateFanCurvePreview()
    {
        FanCurvePreview.Points = _fanCurve.ToArray();
        FanCurvePreview.LearnedPoints = BuildLearnedFanCurve();
    }

    private IReadOnlyList<FanCurvePoint>? BuildLearnedFanCurve()
    {
        if (!_autoControl || !_fanCurveLearningEnabled || _fanCurve.Count == 0)
        {
            return null;
        }

        var offsets = _fanCurve.Select((_, index) => ConstrainLearnedOffset(
            index < _learnedFanCurveOffsets.Count ? _learnedFanCurveOffsets[index] : 0)).ToArray();
        if (!offsets.Any(offset => offset != 0))
        {
            return null;
        }

        var minimum = _fanCurve.Min(point => point.Rpm);
        var maximum = _fanCurve.Max(point => point.Rpm);
        return _fanCurve.Select((point, index) => new FanCurvePoint
        {
            Temperature = point.Temperature,
            Rpm = Math.Clamp(point.Rpm + offsets[index], minimum, maximum),
        }).ToArray();
    }

    private int ConstrainLearnedOffset(int offset) => _fanCurveLearningBias switch
    {
        "cooling" when offset < 0 => 0,
        "quiet" when offset > 0 => 0,
        _ => offset,
    };

    private bool CanEditFanCurve =>
        _ipc.IsConnected && _fanCurveKnown && !_fanCurveLoading && !_writeInProgress;

    private FanCurveProfileSnapshot? ActiveFanCurveProfile() =>
        _fanCurveProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, _activeFanCurveProfileId, StringComparison.Ordinal));

    private void ReplaceFanCurveProfile(FanCurveProfileSnapshot profile)
    {
        var index = _fanCurveProfiles.FindIndex(candidate =>
            string.Equals(candidate.Id, profile.Id, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }

        _fanCurveLoading = true;
        try
        {
            _fanCurveProfiles[index] = profile;
            FanCurveProfileComboBox.ItemsSource = _fanCurveProfiles.ToArray();
            FanCurveProfileComboBox.SelectedItem = profile;
        }
        finally
        {
            _fanCurveLoading = false;
        }
    }

    private async Task<bool> ConfirmActionAsync(string title, string message, string primaryButtonText)
    {
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Close,
        };

        return await dialog.ShowAsync(this) == FAContentDialogResult.Primary;
    }

    private async Task<string?> PromptFanCurveProfileNameAsync(
        string title,
        string message,
        string primaryButtonText,
        string initialName)
    {
        var input = new TextBox
        {
            Text = initialName,
            PlaceholderText = "Profile name",
            MaxLength = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    input,
                },
            },
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync(this);
        var name = input.Text?.Trim();
        if (result != FAContentDialogResult.Primary || string.IsNullOrEmpty(name))
        {
            return null;
        }

        return name;
    }

    private bool TryReadFanCurve(out List<FanCurvePoint> curve, out string error)
    {
        curve = [];
        if (_fanCurve.Count < 2)
        {
            error = "A fan curve needs at least two points.";
            return false;
        }

        for (var index = 0; index < _fanCurve.Count; index++)
        {
            var point = _fanCurve[index];
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
