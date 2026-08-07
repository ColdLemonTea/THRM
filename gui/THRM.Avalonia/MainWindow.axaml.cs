using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
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

internal readonly record struct LearnedOffsetSummaryEntry(int Temperature, int Offset);

internal static class LearnedOffsetSummary
{
    public const int MaximumEntries = 4;

    public static IReadOnlyList<LearnedOffsetSummaryEntry> Build(
        IReadOnlyList<FanCurvePoint> sourceCurve,
        IReadOnlyList<int>? learnedOffsets,
        string? learningBias)
    {
        return (learnedOffsets ?? [])
            .Select((value, index) => (Offset: Constrain(value, learningBias), Index: index))
            .Where(item => item.Offset != 0 && item.Index < sourceCurve.Count)
            .OrderByDescending(item => Math.Abs(item.Offset))
            .Take(MaximumEntries)
            .Select(item => new LearnedOffsetSummaryEntry(sourceCurve[item.Index].Temperature, item.Offset))
            .ToArray();
    }

    public static int Constrain(int offset, string? learningBias) => learningBias switch
    {
        "cooling" when offset < 0 => 0,
        "quiet" when offset > 0 => 0,
        _ => offset,
    };

    public static void SelfCheck()
    {
        var curve = new[]
        {
            new FanCurvePoint { Temperature = 30 },
            new FanCurvePoint { Temperature = 40 },
            new FanCurvePoint { Temperature = 50 },
            new FanCurvePoint { Temperature = 60 },
            new FanCurvePoint { Temperature = 70 },
        };
        var summary = Build(curve, [10, -250, 0, 40, 30, 500], "balanced");
        var quiet = Build(curve, [10, -250, 0, 40], "quiet");
        if (summary.Count != MaximumEntries
            || summary[0] != new LearnedOffsetSummaryEntry(40, -250)
            || summary[1] != new LearnedOffsetSummaryEntry(60, 40)
            || summary[2] != new LearnedOffsetSummaryEntry(70, 30)
            || summary[3] != new LearnedOffsetSummaryEntry(30, 10)
            || quiet.Any(item => item.Offset > 0))
        {
            throw new InvalidOperationException("Learned offset summary check failed.");
        }
    }
}

internal static class TimeCurveScheduleState
{
    public static bool Matches(TimeCurveScheduleSnapshot? left, TimeCurveScheduleSnapshot? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Enabled == right.Enabled
            && left.Rules.Count == right.Rules.Count
            && left.Rules.Zip(right.Rules).All(pair => RulesMatch(pair.First, pair.Second));
    }

    public static void SelfCheck()
    {
        var first = new TimeCurveScheduleSnapshot
        {
            Enabled = true,
            Rules =
            [
                new TimeCurveScheduleRuleSnapshot
                {
                    Id = "rule-1",
                    Name = "Quiet hours",
                    Enabled = true,
                    Weekdays = [1, 2, 3],
                    StartTime = "22:00",
                    EndTime = "06:00",
                    CurveProfileId = "quiet",
                },
            ],
        };
        var equivalent = new TimeCurveScheduleSnapshot
        {
            Enabled = true,
            Rules =
            [
                new TimeCurveScheduleRuleSnapshot
                {
                    Id = "rule-1",
                    Name = "Quiet hours",
                    Enabled = true,
                    Weekdays = [3, 1, 2],
                    StartTime = "22:00",
                    EndTime = "06:00",
                    CurveProfileId = "quiet",
                },
            ],
        };
        var changed = new TimeCurveScheduleSnapshot
        {
            Enabled = true,
            Rules =
            [
                new TimeCurveScheduleRuleSnapshot
                {
                    Id = "rule-1",
                    Name = "Quiet hours",
                    Enabled = true,
                    Weekdays = [1, 2, 3],
                    StartTime = "21:00",
                    EndTime = "06:00",
                    CurveProfileId = "quiet",
                },
            ],
        };

        if (!Matches(first, equivalent) || Matches(first, changed) || Matches(first, null))
        {
            throw new InvalidOperationException("Time curve schedule state check failed.");
        }
    }

    private static bool RulesMatch(TimeCurveScheduleRuleSnapshot left, TimeCurveScheduleRuleSnapshot right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal)
        && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && left.Enabled == right.Enabled
        && string.Equals(left.StartTime, right.StartTime, StringComparison.Ordinal)
        && string.Equals(left.EndTime, right.EndTime, StringComparison.Ordinal)
        && string.Equals(left.CurveProfileId, right.CurveProfileId, StringComparison.Ordinal)
        && left.Weekdays.Distinct().OrderBy(day => day).SequenceEqual(right.Weekdays.Distinct().OrderBy(day => day));
}

internal static class LightStripLogic
{
    public static readonly string[] ModeValues =
    [
        "off",
        "smart_temp",
        "static_single",
        "static_multi",
        "rotation",
        "flowing",
        "breathing",
    ];

    public static readonly string[] SpeedValues = ["fast", "medium", "slow"];

    public static int RequiredColorCount(string? mode) => mode switch
    {
        "off" or "smart_temp" or "flowing" => 0,
        "static_single" => 1,
        "static_multi" or "rotation" or "breathing" => 3,
        _ => 3,
    };

    public static bool IsAnimated(string? mode) =>
        mode is "rotation" or "flowing" or "breathing";

    public static LightStripSnapshot Normalize(LightStripSnapshot? config)
    {
        var defaults = Default();
        if (config is null)
        {
            return defaults;
        }

        var colors = config.Colors is { Count: > 0 }
            ? config.Colors.Select(CloneColor).ToList()
            : defaults.Colors.Select(CloneColor).ToList();
        while (colors.Count < 3)
        {
            colors.Add(CloneColor(defaults.Colors[colors.Count]));
        }

        return new LightStripSnapshot
        {
            Mode = string.IsNullOrWhiteSpace(config.Mode) ? defaults.Mode : config.Mode,
            Speed = string.IsNullOrWhiteSpace(config.Speed) ? defaults.Speed : config.Speed,
            Brightness = Math.Clamp(config.Brightness, 0, 100),
            Colors = colors,
        };
    }

    public static LightStripSnapshot Default() => new()
    {
        Mode = "smart_temp",
        Speed = "medium",
        Brightness = 100,
        Colors =
        [
            new LightRgbSnapshot { R = 255, G = 0, B = 0 },
            new LightRgbSnapshot { R = 0, G = 255, B = 0 },
            new LightRgbSnapshot { R = 0, G = 128, B = 255 },
        ],
    };

    public static void SelfCheck()
    {
        if (RequiredColorCount("off") != 0
            || RequiredColorCount("smart_temp") != 0
            || RequiredColorCount("flowing") != 0
            || RequiredColorCount("static_single") != 1
            || RequiredColorCount("static_multi") != 3
            || RequiredColorCount("rotation") != 3
            || RequiredColorCount("breathing") != 3
            || !IsAnimated("rotation")
            || IsAnimated("static_single"))
        {
            throw new InvalidOperationException("Light strip mode check failed.");
        }

        var normalized = Normalize(new LightStripSnapshot
        {
            Mode = "static_single",
            Speed = "fast",
            Brightness = 140,
            Colors = [new LightRgbSnapshot { R = 1, G = 2, B = 3 }],
        });
        if (normalized.Brightness != 100
            || normalized.Colors.Count != 3
            || normalized.Colors[0].R != 1
            || normalized.Colors[1].G != 255)
        {
            throw new InvalidOperationException("Light strip normalization check failed.");
        }
    }

    private static LightRgbSnapshot CloneColor(LightRgbSnapshot color) => new()
    {
        R = color.R,
        G = color.G,
        B = color.B,
    };
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
    private LightStripSnapshot _lightStrip = LightStripLogic.Default();
    private bool _systemAutoStartEnabled;
    private bool _systemAutoStartAdmin;
    private string _systemAutoStartMethod = "none";
    private bool _systemAutoStartKnown;
    private bool _systemAutoStartLoading;
    private string? _systemAutoStartError;
    private bool _updatingSystemAutoStartControls;
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
    private TimeCurveScheduleSnapshot _timeCurveSchedule = new();
    private bool _timeCurveScheduleSupported;
    private TimeCurveScheduleSnapshot? _renderedTimeCurveSchedule;
    private bool? _renderedTimeCurveScheduleSupported;
    private bool _suppressTimeCurveScheduleVisualSync;
    private bool _fanCurveKnown;
    private bool _fanCurveLoading;
    private bool _fanCurveLearningEnabled;
    private string _fanCurveLearningBias = "balanced";
    private readonly List<int> _learnedFanCurveOffsets = [];
    private readonly List<TemperatureHistoryPointSnapshot> _temperatureHistory = [];
    private readonly List<TimelineEventSnapshot> _timelineEvents = [];
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
    private static readonly (int Value, string Label)[] TimeCurveScheduleWeekdays =
    [
        (1, "Mon"),
        (2, "Tue"),
        (3, "Wed"),
        (4, "Thu"),
        (5, "Fri"),
        (6, "Sat"),
        (0, "Sun"),
    ];

    private readonly record struct ScheduleDayTag(string RuleId, int Day);

    public MainWindow()
    {
        InitializeComponent();
        foreach (var comboBox in new[]
                 {
                     FanCurveProfileComboBox,
                     TemperatureHistoryRetentionComboBox,
                     ManualGearComboBox,
                     ManualLevelComboBox,
                     LightModeComboBox,
                     LightSpeedComboBox,
                     SmartStartStopComboBox,
                 })
        {
            AttachComboBoxOpeningAnimation(comboBox);
        }
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
        TemperatureHistoryPreview.ZoomChanged += HistoryPreviewZoomChanged;
        PowerHistoryPreview.ZoomChanged += HistoryPreviewZoomChanged;
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
            "system" => typeof(SystemRoute),
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

        if (nextPageType == typeof(SystemRoute))
        {
            _ = RefreshSystemAutoStartAsync();
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
    private sealed class SystemRoute { }
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
            var type when type == typeof(SystemRoute) => owner.SystemPage,
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

                if (_currentPageType == typeof(SystemRoute))
                {
                    _ = RefreshSystemAutoStartAsync();
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
            case "timeline-event":
                if (e.TryGetData<TimelineEventSnapshot>(out var timelineEvent) && timelineEvent is not null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        MergeTimelineEvents([timelineEvent]);
                        UpdateTemperatureHistoryPreviews();
                    });
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

    private void LightModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingConfigControls)
        {
            return;
        }

        var mode = SelectedCoreValue(LightModeComboBox, LightStripLogic.ModeValues);
        if (mode is null || !CanChangeDeviceFeatures() || IsBs1)
        {
            ApplyLightStripControls();
            return;
        }

        _lightStrip = new LightStripSnapshot
        {
            Mode = mode,
            Speed = _lightStrip.Speed,
            Brightness = _lightStrip.Brightness,
            Colors = _lightStrip.Colors.Select(CloneLightColor).ToList(),
        };
        ApplyLightStripControls();
    }

    private void LightSpeedSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingConfigControls)
        {
            return;
        }

        var speed = SelectedCoreValue(LightSpeedComboBox, LightStripLogic.SpeedValues);
        if (speed is null || !CanChangeDeviceFeatures() || IsBs1)
        {
            ApplyLightStripControls();
            return;
        }

        _lightStrip = new LightStripSnapshot
        {
            Mode = _lightStrip.Mode,
            Speed = speed,
            Brightness = _lightStrip.Brightness,
            Colors = _lightStrip.Colors.Select(CloneLightColor).ToList(),
        };
    }

    private void LightBrightnessValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingConfigControls)
        {
            return;
        }

        if (!CanChangeDeviceFeatures() || IsBs1)
        {
            ApplyLightStripControls();
            return;
        }

        _lightStrip = new LightStripSnapshot
        {
            Mode = _lightStrip.Mode,
            Speed = _lightStrip.Speed,
            Brightness = (int)Math.Round(Math.Clamp(e.NewValue, 0, 100)),
            Colors = _lightStrip.Colors.Select(CloneLightColor).ToList(),
        };
        LightBrightnessValueText.Text = $"{_lightStrip.Brightness}%";
    }

    private void LightColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (_updatingConfigControls || !CanChangeDeviceFeatures() || IsBs1)
        {
            return;
        }

        var index = sender switch
        {
            ColorPicker picker when ReferenceEquals(picker, LightColorPicker0) => 0,
            ColorPicker picker when ReferenceEquals(picker, LightColorPicker1) => 1,
            ColorPicker picker when ReferenceEquals(picker, LightColorPicker2) => 2,
            _ => -1,
        };
        if (index < 0)
        {
            return;
        }

        var colors = LightStripLogic.Normalize(_lightStrip).Colors
            .Select(CloneLightColor)
            .ToList();
        colors[index] = new LightRgbSnapshot
        {
            R = e.NewColor.R,
            G = e.NewColor.G,
            B = e.NewColor.B,
        };
        _lightStrip = new LightStripSnapshot
        {
            Mode = _lightStrip.Mode,
            Speed = _lightStrip.Speed,
            Brightness = _lightStrip.Brightness,
            Colors = colors,
        };
    }

    private void LightPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string preset }
            || !CanChangeDeviceFeatures()
            || IsBs1)
        {
            return;
        }

        List<LightRgbSnapshot>? colors = preset switch
        {
            "neon" =>
            [
                new LightRgbSnapshot { R = 255, G = 0, B = 128 },
                new LightRgbSnapshot { R = 0, G = 255, B = 255 },
                new LightRgbSnapshot { R = 128, G = 0, B = 255 },
            ],
            "forest" =>
            [
                new LightRgbSnapshot { R = 86, G = 169, B = 84 },
                new LightRgbSnapshot { R = 161, G = 210, B = 106 },
                new LightRgbSnapshot { R = 44, G = 120, B = 115 },
            ],
            "glacier" =>
            [
                new LightRgbSnapshot { R = 80, G = 170, B = 255 },
                new LightRgbSnapshot { R = 116, G = 214, B = 255 },
                new LightRgbSnapshot { R = 200, G = 240, B = 255 },
            ],
            _ => null,
        };
        if (colors is null)
        {
            return;
        }

        _lightStrip = new LightStripSnapshot
        {
            Mode = _lightStrip.Mode,
            Speed = _lightStrip.Speed,
            Brightness = _lightStrip.Brightness,
            Colors = colors,
        };
        ApplyLightStripControls();
    }

    private async void ApplyLightStripClick(object? sender, RoutedEventArgs e)
    {
        if (!CanChangeDeviceFeatures() || IsBs1)
        {
            SetActionAvailability();
            return;
        }

        var config = LightStripLogic.Normalize(_lightStrip);
        await RunWriteAsync("Apply lighting", () => _ipc.SetLightStripAsync(config, _lifetime.Token));
    }

    private void ApplyLightStripControls()
    {
        var wasUpdating = _updatingConfigControls;
        _updatingConfigControls = true;
        try
        {
            SelectCoreValue(LightModeComboBox, LightStripLogic.ModeValues, _lightStrip.Mode);
            SelectCoreValue(LightSpeedComboBox, LightStripLogic.SpeedValues, _lightStrip.Speed);
            LightBrightnessSlider.Value = _lightStrip.Brightness;
            LightBrightnessValueText.Text = $"{_lightStrip.Brightness}%";
            LightSmartTemperatureDeviceSetting.IsVisible = _lightStrip.Mode == "smart_temp";
            LightSmartTemperatureInfoBar.IsOpen = _lightStrip.Mode == "smart_temp";

            var requiredColorCount = LightStripLogic.RequiredColorCount(_lightStrip.Mode);
            LightColorsDeviceSetting.IsVisible = requiredColorCount > 0;
            LightColorSlot0.IsVisible = requiredColorCount >= 1;
            LightColorSlot1.IsVisible = requiredColorCount >= 2;
            LightColorSlot2.IsVisible = requiredColorCount >= 3;

            var colors = LightStripLogic.Normalize(_lightStrip).Colors;
            LightColorPicker0.Color = ToAvaloniaColor(colors[0]);
            LightColorPicker1.Color = ToAvaloniaColor(colors[1]);
            LightColorPicker2.Color = ToAvaloniaColor(colors[2]);
        }
        finally
        {
            _updatingConfigControls = wasUpdating;
        }

        SetActionAvailability();
    }

    private static Color ToAvaloniaColor(LightRgbSnapshot color) =>
        Color.FromRgb(color.R, color.G, color.B);

    private static LightRgbSnapshot CloneLightColor(LightRgbSnapshot color) => new()
    {
        R = color.R,
        G = color.G,
        B = color.B,
    };

    private async Task RefreshSystemAutoStartAsync()
    {
        if (!_ipc.IsConnected)
        {
            _systemAutoStartKnown = false;
            _systemAutoStartLoading = false;
            _systemAutoStartError = null;
            ApplySystemAutoStartPresentation();
            SetActionAvailability();
            return;
        }

        if (_systemAutoStartLoading)
        {
            return;
        }

        _systemAutoStartLoading = true;
        _systemAutoStartError = null;
        ApplySystemAutoStartPresentation();
        SetActionAvailability();
        try
        {
            var enabledTask = _ipc.CheckWindowsAutoStartAsync(_lifetime.Token);
            var methodTask = _ipc.GetAutoStartMethodAsync(_lifetime.Token);
            var adminTask = _ipc.IsRunningAsAdminAsync(_lifetime.Token);
            await Task.WhenAll(enabledTask, methodTask, adminTask);
            if (!_ipc.IsConnected)
            {
                _systemAutoStartKnown = false;
                return;
            }

            _systemAutoStartEnabled = enabledTask.Result;
            _systemAutoStartMethod = NormalizeAutoStartMethod(methodTask.Result);
            _systemAutoStartAdmin = adminTask.Result;
            _systemAutoStartKnown = true;
        }
        catch (Exception ex)
        {
            _systemAutoStartKnown = false;
            _systemAutoStartError = ex.Message;
        }
        finally
        {
            _systemAutoStartLoading = false;
            ApplySystemAutoStartPresentation();
            SetActionAvailability();
        }
    }

    private async void SystemAutoStartClick(object? sender, RoutedEventArgs e)
    {
        if (_updatingSystemAutoStartControls)
        {
            return;
        }

        if (!CanChangeSystemAutoStart())
        {
            ApplySystemAutoStartPresentation();
            SetActionAvailability();
            return;
        }

        var enable = SystemAutoStartSwitch.IsChecked == true;
        var method = enable ? PreferredAutoStartMethod() : string.Empty;
        SystemAutoStartSwitch.IsChecked = _systemAutoStartEnabled;
        _systemAutoStartError = null;
        var acceptedButUnsynchronized = false;
        var synchronized = await RunWriteAsync(
            enable ? "Enable startup" : "Disable startup",
            () => _ipc.SetAutoStartWithMethodAsync(enable, method, _lifetime.Token),
            onAcceptedButUnsynchronized: () => acceptedButUnsynchronized = true);
        if (synchronized)
        {
            await RefreshSystemAutoStartAsync();
        }
        else if (acceptedButUnsynchronized)
        {
            _systemAutoStartKnown = false;
            _systemAutoStartError = "The startup change could not be confirmed; refresh later to verify the current state.";
            ApplySystemAutoStartPresentation();
            SetActionAvailability();
        }
        else
        {
            ApplySystemAutoStartPresentation();
            SetActionAvailability();
        }
    }

    private bool CanChangeSystemAutoStart() =>
        _ipc.IsConnected
        && _systemAutoStartKnown
        && !_systemAutoStartLoading
        && !_writeInProgress;

    private string PreferredAutoStartMethod() =>
        OperatingSystem.IsWindows()
            ? _systemAutoStartAdmin ? "task_scheduler" : "registry"
            : "desktop";

    private void ApplySystemAutoStartPresentation()
    {
        var wasUpdating = _updatingSystemAutoStartControls;
        _updatingSystemAutoStartControls = true;
        try
        {
            SystemAutoStartSwitch.IsChecked = _systemAutoStartEnabled;
        }
        finally
        {
            _updatingSystemAutoStartControls = wasUpdating;
        }

        if (!_ipc.IsConnected)
        {
            SystemAutoStartStateText.Text = "Unavailable: THRM Core is not connected.";
            SystemAutoStartMethodText.Text = "Current method: —";
            ShowSystemAutoStartInfo(
                FAInfoBarSeverity.Warning,
                "Startup unavailable",
                "Connect to THRM Core to read or change the startup setting.");
            return;
        }

        if (_systemAutoStartLoading)
        {
            SystemAutoStartStateText.Text = "Loading startup state from THRM Core...";
            SystemAutoStartMethodText.Text = "Current method: —";
            SystemAutoStartInfoBar.IsOpen = false;
            SystemAutoStartInfoSetting.IsVisible = false;
            return;
        }

        if (!_systemAutoStartKnown)
        {
            SystemAutoStartStateText.Text = "Startup state is unavailable.";
            SystemAutoStartMethodText.Text = "Current method: —";
            ShowSystemAutoStartInfo(
                FAInfoBarSeverity.Error,
                "Startup state unavailable",
                _systemAutoStartError ?? "THRM Core did not return the startup state.");
            return;
        }

        SystemAutoStartStateText.Text = _systemAutoStartEnabled
            ? "Enabled. THRM will start when you sign in."
            : "Disabled. THRM will not start automatically.";
        SystemAutoStartMethodText.Text = $"Current method: {AutoStartMethodLabel(_systemAutoStartMethod)}";
        if (!string.IsNullOrWhiteSpace(_systemAutoStartError))
        {
            ShowSystemAutoStartInfo(FAInfoBarSeverity.Error, "Startup update failed", _systemAutoStartError);
        }
        else if (_systemAutoStartMethod == "task_scheduler" && !_systemAutoStartAdmin)
        {
            ShowSystemAutoStartInfo(
                FAInfoBarSeverity.Warning,
                "Administrator permission",
                "The current Task Scheduler entry requires administrator permission to manage.");
        }
        else
        {
            SystemAutoStartInfoBar.IsOpen = false;
            SystemAutoStartInfoSetting.IsVisible = false;
        }
    }

    private void ShowSystemAutoStartInfo(FAInfoBarSeverity severity, string title, string message)
    {
        SystemAutoStartInfoSetting.IsVisible = true;
        SystemAutoStartInfoBar.Severity = severity;
        SystemAutoStartInfoBar.Title = title;
        SystemAutoStartInfoBar.Message = message;
        SystemAutoStartInfoBar.IsOpen = true;
    }

    private static string NormalizeAutoStartMethod(string? method) => method switch
    {
        "task_scheduler" or "registry" or "desktop" or "none" => method,
        _ => "none",
    };

    private static string AutoStartMethodLabel(string method) => method switch
    {
        "task_scheduler" => "Task Scheduler",
        "registry" => "Registry",
        "desktop" => "Desktop autostart",
        _ => "Not enabled",
    };

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

    private static void AttachComboBoxOpeningAnimation(FAComboBox comboBox)
    {
        comboBox.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<Popup>("Popup") is not { Child: Control presenter } popup)
            {
                return;
            }

            PrepareComboBoxPopup(presenter);
            popup.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (!popup.IsOpen)
                {
                    return;
                }

                presenter.Opacity = 1;
                presenter.RenderTransform = TransformOperations.Identity;
            }, DispatcherPriority.Background);
            popup.Closed += (_, _) => PrepareComboBoxPopup(presenter);
        };
    }

    private static void PrepareComboBoxPopup(Control presenter)
    {
        presenter.Transitions = null;
        presenter.Opacity = 0;
        presenter.RenderTransform = TransformOperations.Parse("translate(0px, -4px)");
        presenter.Transitions = CreateFlyoutTransitions();
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

    private async Task<bool> RunWriteAsync(
        string action,
        Func<Task<bool>> request,
        Action? onAcceptedButUnsynchronized = null)
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

            onAcceptedButUnsynchronized?.Invoke();
            SetActivity(
                onAcceptedButUnsynchronized is null
                    ? $"{action} accepted, but state was not synchronized; known state retained."
                    : $"{action} was accepted, but current state could not be confirmed; refresh later.",
                FAInfoBarSeverity.Warning);
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
            var configTask = _ipc.GetConfigJsonAsync();
            var statusTask = _ipc.GetDeviceStatusAsync();
            await Task.WhenAll(pingTask, configTask, statusTask);
            var configJson = configTask.Result;
            if (configJson.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("GetConfig response was not a JSON object.");
            }

            var config = configJson.Deserialize<ConfigSnapshot>(ThrmIpcClient.JsonOptions)
                ?? throw new JsonException("GetConfig response contained no configuration.");
            ApplyConfig(config, configJson.TryGetProperty("timeCurveSchedule", out _));
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
            _systemAutoStartKnown = false;
            _systemAutoStartLoading = false;
            _systemAutoStartError = null;
            ApplySystemAutoStartPresentation();
        }
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

    private void ApplyConfig(ConfigSnapshot config, bool timeCurveSchedulePropertyPresent)
    {
        _updatingConfigControls = true;
        try
        {
            _autoControl = config.AutoControl;
            _customSpeedEnabled = config.CustomSpeedEnabled;
            _customSpeedRpm = config.CustomSpeedRpm is >= 1000 and <= 4000 ? config.CustomSpeedRpm : 2000;
            _gearLight = config.GearLight;
            _lightStrip = LightStripLogic.Normalize(config.LightStrip);
            _powerOnStart = config.PowerOnStart;
            _smartStartStop = SmartStartStopValues.Contains(config.SmartStartStop) ? config.SmartStartStop! : "off";
            _fanCurveLearningEnabled = config.SmartControl?.Learning == true;
            _fanCurveLearningBias = config.SmartControl?.LearningBias ?? "balanced";
            _timeCurveScheduleSupported = timeCurveSchedulePropertyPresent;
            if (config.TimeCurveSchedule is not null)
            {
                _timeCurveSchedule = CloneTimeCurveSchedule(config.TimeCurveSchedule);
            }
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
            ApplyLightStripControls();
            UpdateManualAppliedText();
            UpdateFanCurvePreview();
            ApplyTimeCurveScheduleControls();
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
        ManualLevelSetting.IsVisible = !IsBs1;
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
        var canChangeLighting = canChangeDeviceFeatures && !IsBs1;
        LightingDeviceExpander.IsVisible = !IsBs1;
        LightModeComboBox.IsEnabled = canChangeLighting;
        LightSpeedComboBox.IsEnabled = canChangeLighting && LightStripLogic.IsAnimated(_lightStrip.Mode);
        LightBrightnessSlider.IsEnabled = canChangeLighting
            && _lightStrip.Mode is not "off" and not "smart_temp";
        LightColorPresetPanel.IsEnabled = canChangeLighting;
        LightColorPicker0.IsEnabled = canChangeLighting;
        LightColorPicker1.IsEnabled = canChangeLighting;
        LightColorPicker2.IsEnabled = canChangeLighting;
        ApplyLightStripButton.IsEnabled = canChangeLighting;
        SystemAutoStartSwitch.IsEnabled = CanChangeSystemAutoStart();
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
        var canEditTimeCurveSchedule = canEditFanCurve && _timeCurveScheduleSupported;
        TimeCurveScheduleEnabledSwitch.IsEnabled = canEditTimeCurveSchedule;
        AddTimeCurveScheduleRuleButton.IsEnabled = canEditTimeCurveSchedule && _fanCurveProfiles.Count > 0;
        TimeCurveScheduleRulesPanel.IsEnabled = !_timeCurveScheduleSupported || canEditFanCurve;

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
            MergeTimelineEvents(history.Events);
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

    private void MergeTimelineEvents(IEnumerable<TimelineEventSnapshot>? incoming)
    {
        var merged = TimelineEventLogic.Merge(_timelineEvents, incoming);
        _timelineEvents.Clear();
        _timelineEvents.AddRange(merged);
        TrimTimelineEvents();
    }

    private void TrimTimelineEvents()
    {
        if (_temperatureHistory.Count == 0)
        {
            return;
        }

        var newestTimestamp = _temperatureHistory[^1].Timestamp;
        var cutoff = newestTimestamp - (long)TimeSpan.FromHours(_temperatureHistoryRetentionHours).TotalMilliseconds;
        _timelineEvents.RemoveAll(item => item.Timestamp < cutoff);
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

        TrimTimelineEvents();
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
        TemperatureHistoryPreview.TimelineEvents = _timelineEvents.ToArray();
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

    private void HistoryPreviewZoomChanged(object? sender, HistoryZoomChangedEventArgs e)
    {
        TemperatureHistoryPreview.ZoomDomain = e.Domain;
        PowerHistoryPreview.ZoomDomain = e.Domain;
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
            ApplyTimeCurveScheduleControls();
        }
        finally
        {
            _fanCurveLoading = false;
        }
    }

    private void ApplyTimeCurveScheduleControls(bool force = false)
    {
        TimeCurveScheduleEnabledSwitch.IsChecked = _timeCurveSchedule.Enabled;

        var supported = _timeCurveScheduleSupported;
        var needsVisualSync = force
            || _renderedTimeCurveScheduleSupported != supported
            || !TimeCurveScheduleState.Matches(_renderedTimeCurveSchedule, _timeCurveSchedule);
        if (!_suppressTimeCurveScheduleVisualSync && needsVisualSync)
        {
            TimeCurveScheduleRulesPanel.Children.Clear();
            TimeCurveScheduleRulesHost.IsVisible = !supported || _timeCurveSchedule.Rules.Count > 0;
            TimeCurveScheduleSupportText.IsVisible = !supported;
            if (!supported)
            {
                TimeCurveScheduleRulesPanel.Children.Add(TimeCurveScheduleSupportText);
            }
            else
            {
                var ruleIndex = 0;
                foreach (var rule in _timeCurveSchedule.Rules)
                {
                    if (ruleIndex++ > 0)
                    {
                        TimeCurveScheduleRulesPanel.Children.Add(new Separator());
                    }

                    TimeCurveScheduleRulesPanel.Children.Add(BuildTimeCurveScheduleRuleItem(rule));
                }
            }

            _renderedTimeCurveSchedule = CloneTimeCurveSchedule(_timeCurveSchedule);
            _renderedTimeCurveScheduleSupported = supported;
        }

        ApplyTimeCurveScheduleAvailability(supported);
    }

    private void ApplyTimeCurveScheduleAvailability(bool supported)
    {
        var canEdit = CanEditFanCurve && supported;
        TimeCurveScheduleEnabledSwitch.IsEnabled = canEdit;
        AddTimeCurveScheduleRuleButton.IsEnabled = canEdit && _fanCurveProfiles.Count > 0;
        TimeCurveScheduleRulesPanel.IsEnabled = !supported || canEdit;
    }

    private Control BuildTimeCurveScheduleRuleItem(TimeCurveScheduleRuleSnapshot rule, bool animateEntry = false)
    {
        var nameBox = new TextBox
        {
            Text = rule.Name,
            Width = 150,
            MaxLength = 40,
        };
        AutomationProperties.SetName(nameBox, $"Schedule rule name for {rule.Name}");
        nameBox.LostFocus += (_, _) => _ = CommitTimeCurveScheduleNameAsync(rule.Id, nameBox);

        var profileBox = new FAComboBox
        {
            Width = 144,
            MaxDropDownHeight = 288,
            ItemsSource = _fanCurveProfiles.ToArray(),
            SelectedItem = _fanCurveProfiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, rule.CurveProfileId, StringComparison.Ordinal)),
        };
        AttachComboBoxOpeningAnimation(profileBox);
        AutomationProperties.SetName(profileBox, $"Curve profile for {rule.Name}");
        profileBox.SelectionChanged += (_, _) => _ = SaveTimeCurveScheduleProfileAsync(rule.Id, profileBox);

        var startPicker = CreateScheduleTimePicker(rule, rule.StartTime, "Start time");
        var endPicker = CreateScheduleTimePicker(rule, rule.EndTime, "End time");
        startPicker.SelectedTimeChanged += (_, _) => _ = SaveTimeCurveScheduleTimeAsync(rule.Id, "startTime", startPicker);
        endPicker.SelectedTimeChanged += (_, _) => _ = SaveTimeCurveScheduleTimeAsync(rule.Id, "endTime", endPicker);

        var enabledSwitch = new ToggleSwitch
        {
            IsChecked = rule.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(enabledSwitch, $"Enable {rule.Name}");
        enabledSwitch.Click += (_, _) =>
        {
            _ = SaveTimeCurveScheduleRuleEnabledAsync(rule.Id, enabledSwitch);
        };

        var deleteButton = new Button { Content = "Delete" };
        AutomationProperties.SetName(deleteButton, $"Delete {rule.Name}");
        deleteButton.Click += (_, _) => _ = DeleteTimeCurveScheduleRuleAsync(rule.Id);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                enabledSwitch,
                deleteButton,
            },
        };

        var weekdays = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 6,
            LineSpacing = 6,
        };
        var selectedDays = NormalizeScheduleWeekdays(rule.Weekdays);
        foreach (var (day, label) in TimeCurveScheduleWeekdays)
        {
            var toggle = new ToggleButton
            {
                Content = label,
                IsChecked = selectedDays.Contains(day),
                MinWidth = 34,
                Tag = new ScheduleDayTag(rule.Id, day),
            };
            AutomationProperties.SetName(toggle, $"{label} for {rule.Name}");
            toggle.Click += ScheduleWeekdayClick;
            weekdays.Children.Add(toggle);
        }

        var timeControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                startPicker,
                new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center },
                endPicker,
            },
        };

        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
            RowDefinitions = new RowDefinitions
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
            ColumnSpacing = 12,
            RowSpacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        AddScheduleFormRow(form, 0, "Rule name", nameBox);
        AddScheduleFormRow(form, 1, "Profile", profileBox);
        AddScheduleFormRow(form, 2, "Time", timeControls);
        AddScheduleFormRow(form, 3, "Days", weekdays);
        Grid.SetColumn(actions, 1);
        Grid.SetRow(actions, 4);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        form.Children.Add(actions);

        var item = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = rule.Id,
            Children =
            {
                form,
            },
        };
        if (animateEntry)
        {
            AnimateTimeCurveScheduleRuleEntry(item);
        }

        return item;
    }

    private void AppendTimeCurveScheduleRule(TimeCurveScheduleRuleSnapshot rule)
    {
        if (!_timeCurveScheduleSupported
            || TimeCurveScheduleRulesPanel.Children.OfType<Control>().Any(item =>
                item.Tag is string id && string.Equals(id, rule.Id, StringComparison.Ordinal)))
        {
            ApplyTimeCurveScheduleControls(force: true);
            return;
        }

        if (TimeCurveScheduleRulesPanel.Children.Contains(TimeCurveScheduleSupportText))
        {
            TimeCurveScheduleRulesPanel.Children.Clear();
        }

        TimeCurveScheduleRulesHost.IsVisible = true;
        TimeCurveScheduleSupportText.IsVisible = false;
        if (TimeCurveScheduleRulesPanel.Children.Count > 0)
        {
            TimeCurveScheduleRulesPanel.Children.Add(new Separator());
        }

        TimeCurveScheduleRulesPanel.Children.Add(BuildTimeCurveScheduleRuleItem(rule, animateEntry: true));
        _renderedTimeCurveSchedule = CloneTimeCurveSchedule(_timeCurveSchedule);
        _renderedTimeCurveScheduleSupported = true;
        ApplyTimeCurveScheduleAvailability(supported: true);
    }

    private void RemoveTimeCurveScheduleRule(string ruleId)
    {
        var ruleItem = TimeCurveScheduleRulesPanel.Children.OfType<Control>().FirstOrDefault(item =>
            item.Tag is string id && string.Equals(id, ruleId, StringComparison.Ordinal));
        if (ruleItem is null)
        {
            ApplyTimeCurveScheduleControls(force: true);
            return;
        }

        var index = TimeCurveScheduleRulesPanel.Children.IndexOf(ruleItem);
        if (index > 0 && TimeCurveScheduleRulesPanel.Children[index - 1] is Separator)
        {
            TimeCurveScheduleRulesPanel.Children.RemoveAt(index - 1);
            TimeCurveScheduleRulesPanel.Children.Remove(ruleItem);
        }
        else
        {
            TimeCurveScheduleRulesPanel.Children.Remove(ruleItem);
            if (index < TimeCurveScheduleRulesPanel.Children.Count
                && TimeCurveScheduleRulesPanel.Children[index] is Separator)
            {
                TimeCurveScheduleRulesPanel.Children.RemoveAt(index);
            }
        }

        TimeCurveScheduleRulesHost.IsVisible = _timeCurveSchedule.Rules.Count > 0;
        _renderedTimeCurveSchedule = CloneTimeCurveSchedule(_timeCurveSchedule);
        _renderedTimeCurveScheduleSupported = _timeCurveScheduleSupported;
        ApplyTimeCurveScheduleAvailability(_timeCurveScheduleSupported);
    }

    private static void AnimateTimeCurveScheduleRuleEntry(Control item)
    {
        item.Opacity = 0;
        item.RenderTransform = TransformOperations.Parse("translate(0px, -6px)");
        item.Transitions = CreateFlyoutTransitions();
        var started = false;
        item.AttachedToVisualTree += (_, _) =>
        {
            if (started)
            {
                return;
            }

            started = true;
            Dispatcher.UIThread.Post(() =>
            {
                item.Opacity = 1;
                item.RenderTransform = TransformOperations.Identity;
            }, DispatcherPriority.Render);
        };
    }

    private static void AddScheduleFormRow(Grid form, int row, string label, Control control)
    {
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(labelText, 0);
        Grid.SetRow(labelText, row);
        form.Children.Add(labelText);

        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(control, 1);
        Grid.SetRow(control, row);
        form.Children.Add(control);
    }

    private static TimePicker CreateScheduleTimePicker(
        TimeCurveScheduleRuleSnapshot rule,
        string value,
        string label)
    {
        var picker = new TimePicker
        {
            Width = 108,
            SelectedTime = ParseScheduleTime(value),
            MinuteIncrement = 15,
            ClockIdentifier = "24HourClock",
            UseSeconds = false,
        };
        AutomationProperties.SetName(picker, $"{label} for {rule.Name}");
        return picker;
    }

    private async void TimeCurveScheduleEnabledClick(object? sender, RoutedEventArgs e)
    {
        if (_updatingConfigControls || !CanEditFanCurve || !_timeCurveScheduleSupported)
        {
            TimeCurveScheduleEnabledSwitch.IsChecked = _timeCurveSchedule.Enabled;
            return;
        }

        var next = CloneTimeCurveSchedule(_timeCurveSchedule);
        next = new TimeCurveScheduleSnapshot
        {
            Enabled = TimeCurveScheduleEnabledSwitch.IsChecked == true,
            Rules = next.Rules,
        };
        await SaveTimeCurveScheduleAsync(next);
    }

    private async void AddTimeCurveScheduleRuleClick(object? sender, RoutedEventArgs e)
    {
        if (!CanEditFanCurve || !_timeCurveScheduleSupported || _fanCurveProfiles.Count == 0)
        {
            return;
        }

        var profileId = _activeFanCurveProfileId ?? _fanCurveProfiles[0].Id;
        var next = CloneTimeCurveSchedule(_timeCurveSchedule);
        var rule = new TimeCurveScheduleRuleSnapshot
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = $"Rule {next.Rules.Count + 1}",
            Enabled = true,
            Weekdays = [1, 2, 3, 4, 5, 6, 0],
            StartTime = "22:00",
            EndTime = "06:00",
            CurveProfileId = profileId,
        };
        next.Rules.Add(rule);
        if (await SaveTimeCurveScheduleAsync(next, deferVisualSync: true))
        {
            var confirmedRule = _timeCurveSchedule.Rules.FirstOrDefault(item =>
                string.Equals(item.Id, rule.Id, StringComparison.Ordinal));
            if (confirmedRule is null)
            {
                ApplyTimeCurveScheduleControls(force: true);
                return;
            }

            AppendTimeCurveScheduleRule(confirmedRule);
        }
    }

    private async Task CommitTimeCurveScheduleNameAsync(string ruleId, TextBox nameBox)
    {
        if (_updatingConfigControls || !CanEditFanCurve || !_timeCurveScheduleSupported)
        {
            return;
        }

        var current = _timeCurveSchedule.Rules.FirstOrDefault(rule => rule.Id == ruleId);
        if (current is null)
        {
            return;
        }

        var name = nameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, current.Name, StringComparison.Ordinal))
        {
            nameBox.Text = current.Name;
            return;
        }

        var next = UpdateTimeCurveScheduleRule(ruleId, rule => CloneTimeCurveScheduleRule(rule, name: name));
        await SaveTimeCurveScheduleAsync(next);
    }

    private async Task SaveTimeCurveScheduleProfileAsync(string ruleId, FAComboBox profileBox)
    {
        if (_updatingConfigControls || !CanEditFanCurve || !_timeCurveScheduleSupported)
        {
            return;
        }

        if (profileBox.SelectedItem is not FanCurveProfileSnapshot profile)
        {
            ApplyTimeCurveScheduleControls(force: true);
            return;
        }

        var next = UpdateTimeCurveScheduleRule(ruleId, rule => CloneTimeCurveScheduleRule(rule, curveProfileId: profile.Id));
        await SaveTimeCurveScheduleAsync(next);
    }

    private async Task SaveTimeCurveScheduleTimeAsync(string ruleId, string field, TimePicker picker)
    {
        if (_updatingConfigControls || !CanEditFanCurve || !_timeCurveScheduleSupported || picker.SelectedTime is null)
        {
            return;
        }

        var next = UpdateTimeCurveScheduleRule(
            ruleId,
            rule => field == "startTime"
                ? CloneTimeCurveScheduleRule(rule, startTime: FormatScheduleTime(picker.SelectedTime))
                : CloneTimeCurveScheduleRule(rule, endTime: FormatScheduleTime(picker.SelectedTime)));
        await SaveTimeCurveScheduleAsync(next);
    }

    private async Task SaveTimeCurveScheduleRuleEnabledAsync(string ruleId, ToggleSwitch enabledSwitch)
    {
        if (_updatingConfigControls || !CanEditFanCurve || !_timeCurveScheduleSupported)
        {
            return;
        }

        var next = UpdateTimeCurveScheduleRule(ruleId, rule => CloneTimeCurveScheduleRule(
            rule,
            enabled: enabledSwitch.IsChecked == true));
        await SaveTimeCurveScheduleAsync(next);
    }

    private async void ScheduleWeekdayClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle || toggle.Tag is not ScheduleDayTag tag)
        {
            return;
        }

        if (_updatingConfigControls || !CanEditFanCurve || !_timeCurveScheduleSupported)
        {
            ApplyTimeCurveScheduleControls(force: true);
            return;
        }

        var current = _timeCurveSchedule.Rules.FirstOrDefault(rule => rule.Id == tag.RuleId);
        if (current is null)
        {
            return;
        }

        var days = NormalizeScheduleWeekdays(current.Weekdays);
        if (toggle.IsChecked != true && days.Count <= 1)
        {
            toggle.IsChecked = true;
            SetActivity("Keep at least one weekday selected.", FAInfoBarSeverity.Warning);
            return;
        }

        if (toggle.IsChecked == true)
        {
            if (!days.Contains(tag.Day))
            {
                days.Add(tag.Day);
            }
        }
        else
        {
            days.Remove(tag.Day);
        }

        days.Sort();
        var next = UpdateTimeCurveScheduleRule(tag.RuleId, rule => CloneTimeCurveScheduleRule(rule, weekdays: days));
        await SaveTimeCurveScheduleAsync(next);
    }

    private async Task DeleteTimeCurveScheduleRuleAsync(string ruleId)
    {
        if (!CanEditFanCurve || !_timeCurveScheduleSupported)
        {
            return;
        }

        var next = CloneTimeCurveSchedule(_timeCurveSchedule);
        next.Rules.RemoveAll(rule => rule.Id == ruleId);
        if (await SaveTimeCurveScheduleAsync(next, deferVisualSync: true))
        {
            RemoveTimeCurveScheduleRule(ruleId);
        }
    }

    private async Task<bool> SaveTimeCurveScheduleAsync(
        TimeCurveScheduleSnapshot schedule,
        bool deferVisualSync = false)
    {
        _suppressTimeCurveScheduleVisualSync = true;
        var saved = false;
        try
        {
            saved = await RunWriteAsync(
                "Save time curve schedule",
                () => SaveTimeCurveScheduleViaConfigUpdateAsync(schedule));
            if (saved)
            {
                await RefreshFanCurveAsync();
            }
        }
        finally
        {
            _suppressTimeCurveScheduleVisualSync = false;
        }

        if (!saved)
        {
            ApplyTimeCurveScheduleControls(force: true);
            return false;
        }

        if (!deferVisualSync)
        {
            if (TimeCurveScheduleState.Matches(_timeCurveSchedule, schedule))
            {
                _renderedTimeCurveSchedule = CloneTimeCurveSchedule(_timeCurveSchedule);
                _renderedTimeCurveScheduleSupported = _timeCurveScheduleSupported;
            }
            else
            {
                ApplyTimeCurveScheduleControls(force: true);
            }
        }

        return true;
    }

    private async Task<bool> SaveTimeCurveScheduleViaConfigUpdateAsync(TimeCurveScheduleSnapshot schedule)
    {
        var config = await GetFreshConfigJsonForScheduleAsync();
        var updatedConfig = TimeCurveScheduleConfigJson.ReplaceTimeCurveSchedule(config, schedule);
        return await _ipc.UpdateConfigAsync(updatedConfig, _lifetime.Token);
    }

    private async Task<JsonElement> GetFreshConfigJsonForScheduleAsync()
    {
        var configJson = await _ipc.GetConfigJsonAsync(_lifetime.Token);
        if (configJson.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("GetConfig response was not a JSON object.");
        }

        if (!configJson.TryGetProperty("timeCurveSchedule", out _))
        {
            throw new InvalidOperationException("Time curve schedules are not available in the current Core configuration.");
        }

        return configJson.Clone();
    }

    private TimeCurveScheduleSnapshot UpdateTimeCurveScheduleRule(
        string ruleId,
        Func<TimeCurveScheduleRuleSnapshot, TimeCurveScheduleRuleSnapshot> update)
    {
        var next = CloneTimeCurveSchedule(_timeCurveSchedule);
        var index = next.Rules.FindIndex(rule => rule.Id == ruleId);
        if (index >= 0)
        {
            next.Rules[index] = update(next.Rules[index]);
        }

        return next;
    }

    private static TimeCurveScheduleSnapshot CloneTimeCurveSchedule(TimeCurveScheduleSnapshot? schedule) => new()
    {
        Enabled = schedule?.Enabled == true,
        Rules = schedule?.Rules.Select(rule => CloneTimeCurveScheduleRule(rule)).ToList() ?? [],
    };

    private static TimeCurveScheduleRuleSnapshot CloneTimeCurveScheduleRule(
        TimeCurveScheduleRuleSnapshot rule,
        string? name = null,
        bool? enabled = null,
        IReadOnlyList<int>? weekdays = null,
        string? startTime = null,
        string? endTime = null,
        string? curveProfileId = null) => new()
    {
        Id = rule.Id,
        Name = name ?? rule.Name,
        Enabled = enabled ?? rule.Enabled,
        Weekdays = (weekdays ?? rule.Weekdays).Distinct().OrderBy(day => day).ToList(),
        StartTime = startTime ?? rule.StartTime,
        EndTime = endTime ?? rule.EndTime,
        CurveProfileId = curveProfileId ?? rule.CurveProfileId,
    };

    private static List<int> NormalizeScheduleWeekdays(IEnumerable<int>? weekdays)
    {
        var normalized = weekdays?.Where(day => day is >= 0 and <= 6).Distinct().OrderBy(day => day).ToList() ?? [];
        return normalized.Count > 0 ? normalized : [0, 1, 2, 3, 4, 5, 6];
    }

    private static TimeSpan ParseScheduleTime(string? value) =>
        TimeSpan.TryParseExact(value, [@"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out var parsed)
            && parsed >= TimeSpan.Zero
            && parsed < TimeSpan.FromDays(1)
            ? parsed
            : TimeSpan.Zero;

    private static string FormatScheduleTime(TimeSpan? value)
    {
        var minutes = Math.Clamp((int)(value?.TotalMinutes ?? 0), 0, 23 * 60 + 59);
        return $"{minutes / 60:D2}:{minutes % 60:D2}";
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
        UpdateLearnedOffsetSummary();
    }

    private void UpdateLearnedOffsetSummary()
    {
        var summary = LearnedOffsetSummary.Build(_fanCurve, _learnedFanCurveOffsets, _fanCurveLearningBias);
        LearnedOffsetsSummaryPanel.Children.Clear();
        if (summary.Count == 0)
        {
            LearnedOffsetsSummaryPanel.Children.Add(new TextBlock
            {
                Text = "No learned offsets for the active curve.",
                Opacity = 0.78,
            });
            return;
        }

        foreach (var entry in summary)
        {
            LearnedOffsetsSummaryPanel.Children.Add(new TextBlock
            {
                Text = $"{entry.Temperature} °C  {entry.Offset:+#;-#;0} RPM",
                FontWeight = FontWeight.SemiBold,
            });
        }
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

    private int ConstrainLearnedOffset(int offset) => LearnedOffsetSummary.Constrain(offset, _fanCurveLearningBias);

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

    private static void SelectCoreValue(FAComboBox comboBox, string[] values, string? value) =>
        comboBox.SelectedIndex = value is null ? -1 : Array.IndexOf(values, value);

    private static string? SelectedCoreValue(ComboBox comboBox, string[] values)
    {
        var index = comboBox.SelectedIndex;
        return index >= 0 && index < values.Length ? values[index] : null;
    }

    private static string? SelectedCoreValue(FAComboBox comboBox, string[] values)
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
