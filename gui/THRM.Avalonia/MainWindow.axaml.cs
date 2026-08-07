using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
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

internal static class DiagnosticArchive
{
    private const int MaximumLogsPerDirectory = 8;
    private const long MaximumLogBytes = 2L * 1024 * 1024;
    private static readonly JsonSerializerOptions ArchiveJsonOptions = new(ThrmIpcClient.JsonOptions)
    {
        WriteIndented = true,
    };

    public static void Write(
        Stream destination,
        JsonElement config,
        JsonElement debug,
        DeviceStatusSnapshot status)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        var manifest = archive.CreateEntry("diagnostics.json", CompressionLevel.Fastest);
        using (var manifestStream = manifest.Open())
        {
            JsonSerializer.Serialize(manifestStream, new
            {
                createdAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                app = "THRM",
                gui = "Avalonia",
                protocol = ThrmIpcClient.ProtocolVersion,
                os = RuntimeInformation.OSDescription,
                arch = RuntimeInformation.ProcessArchitecture.ToString(),
                numCpu = Environment.ProcessorCount,
                hardware = status.Temperature,
                device = status,
                debug,
                config,
            }, ArchiveJsonOptions);
        }

        AddRecentLogs(archive, Path.Combine(AppContext.BaseDirectory, "logs"), "app");
        AddRecentLogs(archive, Path.Combine(AppContext.BaseDirectory, "bridge", "logs"), "bridge");
        if (OperatingSystem.IsLinux())
        {
            AddRecentLogs(archive, LinuxFallbackLogDirectory(), "fallback");
        }
    }

    public static void SelfCheck()
    {
        using var config = JsonDocument.Parse("{\"autoControl\":true}");
        using var debug = JsonDocument.Parse("{\"debugMode\":false}");
        using var output = new MemoryStream();
        Write(output, config.RootElement, debug.RootElement, new DeviceStatusSnapshot
        {
            Connected = true,
            Model = "BS3",
        });
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
        using var manifest = JsonDocument.Parse(archive.GetEntry("diagnostics.json")?.Open()
            ?? throw new InvalidOperationException("Diagnostic archive manifest was not written."));
        if (manifest.RootElement.GetProperty("app").GetString() != "THRM"
            || !manifest.RootElement.GetProperty("config").GetProperty("autoControl").GetBoolean()
            || !manifest.RootElement.GetProperty("device").GetProperty("connected").GetBoolean())
        {
            throw new InvalidOperationException("Diagnostic archive self-check failed.");
        }
    }

    private static void AddRecentLogs(ZipArchive archive, string directory, string prefix)
    {
        try
        {
            foreach (var file in new DirectoryInfo(directory)
                         .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(item => item.LastWriteTimeUtc)
                         .Take(MaximumLogsPerDirectory))
            {
                try
                {
                    var entry = archive.CreateEntry($"logs/{prefix}-{file.Name}", CompressionLevel.Fastest);
                    using var destination = entry.Open();
                    using var source = new FileStream(
                        file.FullName,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    CopyAtMost(source, destination, MaximumLogBytes);
                }
                catch (IOException)
                {
                    // A rolling log can disappear or be replaced while the archive is built.
                }
                catch (UnauthorizedAccessException)
                {
                    // Keep the archive useful even when one optional log is unavailable.
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Logs are optional.
        }
        catch (UnauthorizedAccessException)
        {
            // Logs are optional.
        }
    }

    private static void CopyAtMost(Stream source, Stream destination, long maximumBytes)
    {
        var buffer = new byte[81920];
        while (maximumBytes > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, maximumBytes));
            if (read == 0)
            {
                return;
            }

            destination.Write(buffer, 0, read);
            maximumBytes -= read;
        }
    }

    private static string LinuxFallbackLogDirectory()
    {
        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (Path.IsPathRooted(stateHome))
        {
            return Path.Combine(stateHome, "thrm", "logs");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "state",
            "thrm",
            "logs");
    }
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
    private string _tempSource = "max";
    private int _tempSampleCount = 1;
    private bool _customSpeedEnabled;
    private int _customSpeedRpm = 2000;
    private Dictionary<string, Dictionary<string, int>> _manualGearRpm = [];
    private string _manualGearRpmGear = "标准";
    private string _manualGearRpmLevel = "中";
    private bool _updatingManualGearRpmControls;
    private LegionFnQConfigSnapshot _legionFnQ = new();
    private bool _legionFnQSupported;
    private bool _updatingLegionFnQControls;
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
    private bool _updatingMonitoringControls;
    private bool _updatingRtssControls;
    private bool _updatingThemeControls;
    private bool _updatingTemperatureControlControls;
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
    private bool _fanCurvePredictiveBoost = true;
    private bool _fanCurveLaptopFanGuard = true;
    private int _fanCurveTargetTemp = 68;
    private bool _updatingCurveLearningControls;
    private readonly List<int> _learnedFanCurveOffsets = [];
    private SpeedAvoidanceSnapshot _speedAvoidance = new();
    private bool _updatingSpeedAvoidanceControls;
    private readonly List<TemperatureHistoryPointSnapshot> _temperatureHistory = [];
    private readonly List<TimelineEventSnapshot> _timelineEvents = [];
    private bool _temperatureHistoryKnown;
    private bool _temperatureHistoryLoading;
    private bool _temperatureHistoryEnabled;
    private int _temperatureHistoryRetentionHours = 1;
    private bool _updatingTemperatureHistoryControls;
    private int _deviceFanMaximumRpm = FanRatedRpm.FallbackRpm;
    private TemperatureSnapshot? _latestTemperature;
    private readonly HashSet<string> _selectedCpuSensors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cpuSensorSelectionDraft = new(StringComparer.Ordinal);
    private bool _cpuSensorSelectionFlyoutOpen;
    private string _selectedGpuDevice = "auto";
    private string _selectedGpuSensor = "auto";
    private bool _disableGpuMonitoring;
    private bool _ignoreDeviceOnReconnect = true;
    private MonitoringSelectionOption[] _renderedCpuSensorOptions = [];
    private MonitoringSelectionOption[] _renderedGpuDeviceOptions = [];
    private MonitoringSelectionOption[] _renderedGpuSensorOptions = [];
    private bool _rtssSupported;
    private bool _rtssEnabled;
    private int _rtssUpdateIntervalMs = 1000;
    private string _rtssPositionMode = "anchor";
    private int _rtssPositionX;
    private int _rtssPositionY;
    private bool _rtssPreviewInProgress;
    private RtssOverlayLayoutStatus? _rtssAnchorLayout;
    private bool _rtssAnchorBusy;
    private string? _rtssAnchorError;
    private string _themeMode = "system";
    private bool _debugMode;
    private bool _diagnosticsLoading;
    private bool _diagnosticsExporting;
    private bool _deviceDebugCommandInProgress;
    private bool _updatingHotkeyControls;
    private string _manualGearToggleHotkey = string.Empty;
    private string _autoControlToggleHotkey = string.Empty;
    private string _curveProfileToggleHotkey = string.Empty;

    private static readonly string[] ManualGearValues = ["静音", "标准", "强劲", "超频"];
    private static readonly string[] ManualLevelValues = ["低", "中", "高"];
    private static readonly string[] LegionFnQPowerModeValues = ["Quiet", "Balance", "Performance", "Extreme", "GodMode"];
    private static readonly string[] LearningBiasValues = ["balanced", "cooling", "quiet"];
    private static readonly string[] TemperatureSourceValues = ["max", "cpu", "gpu"];
    private static readonly int[] TemperatureSampleCountValues = [1, 2, 3, 5, 10];
    private static readonly string[] SmartStartStopValues = ["off", "immediate", "delayed"];
    private static readonly int[] TemperatureHistoryRetentionOptions = [1, 2, 3, 6, 12, 24];
    private static readonly int[] RtssUpdateIntervalOptions = [250, 500, 1000, 2000];
    private static readonly string[] ThemeModeValues = ["system", "light", "dark"];
    private static readonly object CpuSensorAutomaticTag = new();
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
    private readonly record struct MonitoringSelectionOption(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    public MainWindow()
    {
        InitializeComponent();
        foreach (var comboBox in new[]
                 {
                     FanCurveProfileComboBox,
                      TemperatureHistoryRetentionComboBox,
                      ManualGearComboBox,
                      ManualLevelComboBox,
                       FanCurveLearningBiasComboBox,
                      LegionFnQQuietGearComboBox,
                      LegionFnQQuietLevelComboBox,
                      LegionFnQBalanceGearComboBox,
                      LegionFnQBalanceLevelComboBox,
                      LegionFnQPerformanceGearComboBox,
                      LegionFnQPerformanceLevelComboBox,
                      LegionFnQExtremeGearComboBox,
                      LegionFnQExtremeLevelComboBox,
                      LegionFnQGodModeGearComboBox,
                      LegionFnQGodModeLevelComboBox,
                      LightModeComboBox,
                     LightSpeedComboBox,
                     SmartStartStopComboBox,
                     TempSourceComboBox,
                     TempSampleCountComboBox,
                     GpuDeviceComboBox,
                     GpuSensorComboBox,
                     RtssIntervalComboBox,
                     RtssPositionModeComboBox,
                     ThemeModeComboBox,
                 })
        {
            AttachComboBoxOpeningAnimation(comboBox);
        }
        var assemblyName = typeof(MainWindow).Assembly.GetName().Name!;
        using (var iconStream = AssetLoader.Open(new Uri($"avares://{assemblyName}/Assets/thrm.png")))
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
            "diagnostics" => typeof(DiagnosticsRoute),
            "rtss-overlay" => typeof(RtssOverlayRoute),
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

        if (nextPageType == typeof(DiagnosticsRoute))
        {
            _ = RefreshDiagnosticsAsync();
        }

        if (nextPageType == typeof(RtssOverlayRoute))
        {
            _ = RefreshStateAsync();
            _ = RefreshRtssAnchorAsync();
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
    private sealed class DiagnosticsRoute { }
    private sealed class RtssOverlayRoute { }
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
            var type when type == typeof(DiagnosticsRoute) => owner.DiagnosticsPage,
            var type when type == typeof(RtssOverlayRoute) => owner.RtssOverlayPage,
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
            case "hotkey-triggered":
                if (e.TryGetData<HotkeyTriggeredSnapshot>(out var hotkey) && hotkey is not null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var action = string.IsNullOrWhiteSpace(hotkey.Action) ? "Global shortcut" : hotkey.Action;
                        var shortcut = string.IsNullOrWhiteSpace(hotkey.Shortcut) ? string.Empty : $" ({hotkey.Shortcut})";
                        var message = string.IsNullOrWhiteSpace(hotkey.Message)
                            ? $"{action}{shortcut}"
                            : $"{action}{shortcut}: {hotkey.Message}";
                        SetActivity(
                            message,
                            hotkey.Success ? FAInfoBarSeverity.Success : FAInfoBarSeverity.Error);
                    });
                }
                break;
            case "device-connected":
            case "device-disconnected":
            case "config-update":
            case "legion-fnq-support-update":
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

    private async void DebugModeClick(object? sender, RoutedEventArgs e)
    {
        if (_updatingConfigControls || !CanChangeDiagnostics())
        {
            ApplyDiagnosticsControls();
            return;
        }

        var enabled = DebugModeSwitch.IsChecked == true;
        if (enabled == _debugMode)
        {
            return;
        }

        DebugModeSwitch.IsChecked = _debugMode;
        if (!await RunWriteAsync(
                enabled ? "Enable diagnostic logging" : "Disable diagnostic logging",
                () => _ipc.SetDebugModeAsync(enabled, _lifetime.Token)))
        {
            ApplyDiagnosticsControls();
        }
    }

    private async void RefreshDiagnosticsClick(object? sender, RoutedEventArgs e) =>
        await RefreshDiagnosticsAsync();

    private async void ExportDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        if (!CanExportDiagnostics())
        {
            return;
        }

        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null || !storageProvider.CanSave)
        {
            SetActivity("Diagnostic export is unavailable because this platform cannot save files.", FAInfoBarSeverity.Error);
            return;
        }

        _diagnosticsExporting = true;
        SetActionAvailability();
        try
        {
            var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export THRM diagnostics",
                SuggestedFileName = $"THRM-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                DefaultExtension = "zip",
                FileTypeChoices =
                [
                    new FilePickerFileType("ZIP archive") { Patterns = ["*.zip"] },
                ],
            });
            if (file is null)
            {
                return;
            }

            SetActivity("Exporting diagnostic archive...", FAInfoBarSeverity.Informational, TimeSpan.FromSeconds(15));
            var configTask = _ipc.GetConfigJsonAsync(_lifetime.Token);
            var debugTask = _ipc.GetDebugInfoAsync(_lifetime.Token);
            var statusTask = _ipc.GetDeviceStatusAsync(_lifetime.Token);
            await Task.WhenAll(configTask, debugTask, statusTask);
            await using var output = await file.OpenWriteAsync();
            await Task.Run(
                () => DiagnosticArchive.Write(output, configTask.Result, debugTask.Result, statusTask.Result),
                _lifetime.Token);
            SetActivity("Diagnostic archive exported.", FAInfoBarSeverity.Success);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The window is closing.
        }
        catch (Exception ex)
        {
            SetActivity($"Diagnostic export failed: {ex.Message}", FAInfoBarSeverity.Error);
        }
        finally
        {
            _diagnosticsExporting = false;
            SetActionAvailability();
        }
    }

    private async Task RefreshDiagnosticsAsync()
    {
        if (!_ipc.IsConnected || _diagnosticsLoading || _writeInProgress)
        {
            return;
        }

        _diagnosticsLoading = true;
        SetActionAvailability();
        try
        {
            var snapshot = await _ipc.GetDebugInfoAsync(_lifetime.Token);
            DiagnosticsSnapshotTextBox.Text = JsonSerializer.Serialize(
                snapshot,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            SetActivity($"Diagnostics refresh failed: {ex.Message}", FAInfoBarSeverity.Error);
        }
        finally
        {
            _diagnosticsLoading = false;
            SetActionAvailability();
        }
    }

    private async void SendDeviceDebugCommandClick(object? sender, RoutedEventArgs e)
    {
        if (!CanSendDeviceDebugCommand())
        {
            return;
        }

        var command = DeviceDebugCommandTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            SetActivity("Enter a hexadecimal device command first.", FAInfoBarSeverity.Warning);
            return;
        }

        _deviceDebugCommandInProgress = true;
        SetActionAvailability();
        try
        {
            var result = await _ipc.SendDeviceDebugCommandAsync(command, 900, _lifetime.Token);
            DeviceDebugResultTextBox.Text = JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true });
            DeviceDebugResultSetting.IsVisible = true;
            DeviceDebugCommandSetting.Classes.Remove("last-visible-setting");
            SetActivity("Device command completed.", FAInfoBarSeverity.Success);
            await RefreshStateAsync();
        }
        catch (Exception ex)
        {
            SetActivity($"Device command failed: {ex.Message}", FAInfoBarSeverity.Error);
        }
        finally
        {
            _deviceDebugCommandInProgress = false;
            SetActionAvailability();
        }
    }

    private async void TempSourceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingTemperatureControlControls)
        {
            return;
        }

        var source = SelectedCoreValue(TempSourceComboBox, TemperatureSourceValues);
        if (source is null || !CanChangeMonitoringSources())
        {
            ApplyTemperatureControlControls();
            return;
        }

        if (string.Equals(source, _tempSource, StringComparison.Ordinal))
        {
            return;
        }

        if (!await RunWriteAsync(
                "Set control temperature source",
                () => PatchConfigAsync(root => root["tempSource"] = source)))
        {
            ApplyTemperatureControlControls();
        }
    }

    private async void TempSampleCountSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingTemperatureControlControls)
        {
            return;
        }

        var index = TempSampleCountComboBox.SelectedIndex;
        if (!CanChangeMonitoringSources()
            || index < 0
            || index >= TemperatureSampleCountValues.Length)
        {
            ApplyTemperatureControlControls();
            return;
        }

        var count = TemperatureSampleCountValues[index];
        if (count == _tempSampleCount)
        {
            return;
        }

        if (!await RunWriteAsync(
                "Set temperature smoothing",
                () => PatchConfigAsync(root => root["tempSampleCount"] = count)))
        {
            ApplyTemperatureControlControls();
        }
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
        LightBrightnessValueText.Text = _lightStrip.Brightness.ToString();
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
            LightBrightnessValueText.Text = _lightStrip.Brightness.ToString();
            LightSmartTemperatureDeviceSetting.IsVisible = _lightStrip.Mode == "smart_temp";
            LightSmartTemperatureInfoBar.IsOpen = _lightStrip.Mode == "smart_temp";

            var requiredColorCount = LightStripLogic.RequiredColorCount(_lightStrip.Mode);
            LightColorPresetSetting.IsVisible = requiredColorCount > 0;
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

    private async void IgnoreDeviceOnReconnectClick(object? sender, RoutedEventArgs e)
    {
        if (_updatingConfigControls || !CanChangeMonitoringSources())
        {
            IgnoreDeviceOnReconnectSwitch.IsChecked = _ignoreDeviceOnReconnect;
            return;
        }

        var enabled = IgnoreDeviceOnReconnectSwitch.IsChecked == true;
        if (enabled == _ignoreDeviceOnReconnect)
        {
            return;
        }

        IgnoreDeviceOnReconnectSwitch.IsChecked = _ignoreDeviceOnReconnect;
        if (!await RunWriteAsync(
                enabled ? "Keep configuration after reconnect" : "Use device state after reconnect",
                () => PatchConfigAsync(root => root["ignoreDeviceOnReconnect"] = enabled)))
        {
            IgnoreDeviceOnReconnectSwitch.IsChecked = _ignoreDeviceOnReconnect;
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

    private void CpuSensorMenuFlyoutOpening(object? sender, EventArgs e)
    {
        MenuFlyoutOpening(sender, e);
        _cpuSensorSelectionFlyoutOpen = true;
        _cpuSensorSelectionDraft.Clear();
        _cpuSensorSelectionDraft.UnionWith(_selectedCpuSensors);
        _cpuSensorSelectionDraft.IntersectWith(BuildMonitoringOptions(_latestTemperature?.CpuSensors).Select(option => option.Key));

        if (sender is FAMenuFlyout { Popup.Child: Control presenter })
        {
            presenter.MinWidth = 280;
            presenter.MaxWidth = 360;
            presenter.MaxHeight = 360;
        }

        ApplyMonitoringControls();
    }

    private async void CpuSensorMenuFlyoutClosed(object? sender, EventArgs e)
    {
        if (!_cpuSensorSelectionFlyoutOpen)
        {
            return;
        }

        _cpuSensorSelectionFlyoutOpen = false;
        var selection = BuildMonitoringOptions(_latestTemperature?.CpuSensors)
            .Where(option => _cpuSensorSelectionDraft.Contains(option.Key))
            .Select(option => option.Key)
            .ToArray();
        _cpuSensorSelectionDraft.Clear();
        if (selection.SequenceEqual(_selectedCpuSensors.OrderBy(sensor => sensor, StringComparer.Ordinal)))
        {
            ApplyMonitoringControls();
            return;
        }

        if (!CanChangeMonitoringSources())
        {
            ApplyMonitoringControls();
            return;
        }

        if (!await RunWriteAsync(
                "Set CPU temperature sensors",
                () => PatchConfigAsync(root => root["cpuSensors"] = JsonSerializer.SerializeToNode(
                    selection,
                    ThrmIpcClient.JsonOptions))))
        {
            ApplyMonitoringControls();
        }
    }

    private void CpuSensorSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not FAToggleMenuFlyoutItem menuItem
            || _updatingMonitoringControls
            || !_cpuSensorSelectionFlyoutOpen)
        {
            return;
        }

        if (ReferenceEquals(menuItem.Tag, CpuSensorAutomaticTag))
        {
            _cpuSensorSelectionDraft.Clear();
        }
        else if (menuItem.Tag is string key && !_cpuSensorSelectionDraft.Add(key))
        {
            _cpuSensorSelectionDraft.Remove(key);
        }

        Dispatcher.UIThread.Post(ApplyMonitoringControls, DispatcherPriority.Background);
    }

    private async void GpuMonitoringClick(object? sender, RoutedEventArgs e)
    {
        if (_updatingMonitoringControls || !CanChangeMonitoringSources())
        {
            ApplyMonitoringControls();
            return;
        }

        var disabled = GpuMonitoringSwitch.IsChecked != true;
        if (disabled == _disableGpuMonitoring)
        {
            return;
        }

        GpuMonitoringSwitch.IsChecked = !_disableGpuMonitoring;
        if (!await RunWriteAsync(
                disabled ? "Disable GPU monitoring" : "Enable GPU monitoring",
                () => PatchConfigAsync(root => root["disableGpuMonitoring"] = disabled)))
        {
            ApplyMonitoringControls();
        }
    }

    private async void GpuDeviceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingMonitoringControls)
        {
            return;
        }

        if (GpuDeviceComboBox.SelectedItem is not MonitoringSelectionOption option
            || !CanChangeMonitoringSources())
        {
            ApplyMonitoringControls();
            return;
        }

        if (string.Equals(option.Key, _selectedGpuDevice, StringComparison.Ordinal))
        {
            return;
        }

        if (!await RunWriteAsync(
                "Set GPU monitoring device",
                () => PatchConfigAsync(root => root["gpuDevice"] = option.Key)))
        {
            ApplyMonitoringControls();
        }
    }

    private async void GpuSensorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingMonitoringControls)
        {
            return;
        }

        if (GpuSensorComboBox.SelectedItem is not MonitoringSelectionOption option
            || !CanChangeMonitoringSources())
        {
            ApplyMonitoringControls();
            return;
        }

        if (string.Equals(option.Key, _selectedGpuSensor, StringComparison.Ordinal))
        {
            return;
        }

        if (!await RunWriteAsync(
                "Set GPU temperature sensor",
                () => PatchConfigAsync(root => root["gpuSensor"] = option.Key)))
        {
            ApplyMonitoringControls();
        }
    }

    private bool CanChangeMonitoringSources() =>
        _ipc.IsConnected
        && _configKnown
        && !_writeInProgress;

    private async Task<bool> PatchConfigAsync(Action<JsonObject> patch)
    {
        var config = await _ipc.GetConfigJsonAsync(_lifetime.Token);
        if (config.ValueKind != JsonValueKind.Object
            || JsonNode.Parse(config.GetRawText()) is not JsonObject root)
        {
            throw new JsonException("Core configuration must be a JSON object.");
        }

        patch(root);
        using var document = JsonDocument.Parse(root.ToJsonString(ThrmIpcClient.JsonOptions));
        return await _ipc.UpdateConfigAsync(document.RootElement.Clone(), _lifetime.Token);
    }

    private async void RtssEnabledClick(object? sender, RoutedEventArgs e)
    {
        if (_updatingRtssControls || !CanChangeRtss())
        {
            ApplyRtssControls();
            return;
        }

        var enabled = RtssEnabledSwitch.IsChecked == true;
        if (enabled == _rtssEnabled)
        {
            return;
        }

        RtssEnabledSwitch.IsChecked = _rtssEnabled;
        if (!await RunWriteAsync(
                enabled ? "Enable RTSS overlay" : "Disable RTSS overlay",
                () => PatchRtssConfigAsync(rtss => rtss["enabled"] = enabled)))
        {
            ApplyRtssControls();
        }
    }

    private async void RtssIntervalSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingRtssControls)
        {
            return;
        }

        var index = RtssIntervalComboBox.SelectedIndex;
        if (!CanChangeRtss() || index < 0 || index >= RtssUpdateIntervalOptions.Length)
        {
            ApplyRtssControls();
            return;
        }

        var interval = RtssUpdateIntervalOptions[index];
        if (interval == _rtssUpdateIntervalMs)
        {
            return;
        }

        if (!await RunWriteAsync(
                "Set RTSS update interval",
                () => PatchRtssConfigAsync(rtss => rtss["updateIntervalMs"] = interval)))
        {
            ApplyRtssControls();
        }
    }

    private async void RtssPositionModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingRtssControls)
        {
            return;
        }

        var mode = RtssPositionModeComboBox.SelectedIndex == 1 ? "custom" : "anchor";
        if (!CanChangeRtss())
        {
            ApplyRtssControls();
            return;
        }

        if (string.Equals(mode, _rtssPositionMode, StringComparison.Ordinal))
        {
            return;
        }

        if (!await RunWriteAsync(
                mode == "custom" ? "Use custom RTSS position" : "Use RTSS OverlayEditor position",
                () => PatchRtssConfigAsync(rtss => rtss["positionMode"] = mode)))
        {
            ApplyRtssControls();
        }
    }

    private async void RtssPreviewPositionClick(object? sender, RoutedEventArgs e)
    {
        if (!CanChangeRtss() || _rtssPositionMode != "custom" || _rtssPreviewInProgress)
        {
            ApplyRtssControls();
            return;
        }

        if (!TryReadRtssPosition(out var x, out var y, out var error))
        {
            SetActivity(error, FAInfoBarSeverity.Warning);
            return;
        }

        _rtssPreviewInProgress = true;
        SetActionAvailability();
        try
        {
            if (!await _ipc.PreviewRtssPositionAsync("custom", x, y, _lifetime.Token))
            {
                SetActivity("RTSS position preview was rejected; the saved position is unchanged.", FAInfoBarSeverity.Warning);
                return;
            }

            RtssPositionStatusText.Text = $"Previewing X {x}, Y {y}. Select Apply to save this offset.";
            SetActivity("RTSS position preview updated; changes are not saved.", FAInfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            SetActivity($"RTSS position preview failed: {ex.Message}", FAInfoBarSeverity.Error);
        }
        finally
        {
            _rtssPreviewInProgress = false;
            SetActionAvailability();
        }
    }

    private async void RefreshRtssAnchorClick(object? sender, RoutedEventArgs e) =>
        await RefreshRtssAnchorAsync();

    private async void CreateRtssAnchorClick(object? sender, RoutedEventArgs e)
    {
        var layout = _rtssAnchorLayout;
        if (_rtssAnchorBusy
            || !_rtssSupported
            || _rtssPositionMode != "anchor"
            || layout is not { Supported: true, Installed: true }
            || string.IsNullOrWhiteSpace(layout.LayoutPath)
            || string.Equals(layout.AnchorState, "confirmed", StringComparison.Ordinal))
        {
            return;
        }

        if (!await ConfirmActionAsync(
                "Set up RTSS anchor?",
                "THRM will back up the active OverlayEditor layout, then add or update an empty one-percent anchor at its bottom.",
                "Set up anchor"))
        {
            return;
        }

        _rtssAnchorBusy = true;
        _rtssAnchorError = null;
        ApplyRtssControls();
        try
        {
            _rtssAnchorLayout = await Task.Run(RtssOverlayLayout.CreateAnchor, _lifetime.Token);
            var backup = _rtssAnchorLayout.BackupPath;
            SetActivity(
                string.IsNullOrWhiteSpace(backup)
                    ? "RTSS anchor is ready."
                    : $"RTSS anchor is ready. Backup: {Path.GetFileName(backup)}",
                FAInfoBarSeverity.Success);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The window is closing.
        }
        catch (Exception ex)
        {
            _rtssAnchorError = ex.Message;
            SetActivity($"RTSS anchor setup failed: {ex.Message}", FAInfoBarSeverity.Error);
        }
        finally
        {
            _rtssAnchorBusy = false;
            ApplyRtssControls();
        }
    }

    private async void RtssApplyPositionClick(object? sender, RoutedEventArgs e)
    {
        if (!CanChangeRtss() || _rtssPositionMode != "custom")
        {
            ApplyRtssControls();
            return;
        }

        if (!TryReadRtssPosition(out var x, out var y, out var error))
        {
            SetActivity(error, FAInfoBarSeverity.Warning);
            return;
        }

        if (!await RunWriteAsync(
                "Apply RTSS custom position",
                () => PatchRtssConfigAsync(rtss =>
                {
                    rtss["positionMode"] = "custom";
                    rtss["positionX"] = x;
                    rtss["positionY"] = y;
                })))
        {
            ApplyRtssControls();
        }
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

    private async void ThemeModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingThemeControls)
        {
            return;
        }

        var mode = SelectedCoreValue(ThemeModeComboBox, ThemeModeValues);
        if (mode is null || !CanChangeTheme())
        {
            ApplyThemeControls();
            return;
        }

        if (string.Equals(mode, _themeMode, StringComparison.Ordinal))
        {
            return;
        }

        var previousMode = _themeMode;
        _themeMode = mode;
        ApplyThemeControls();
        if (!await RunWriteAsync(
                mode == "system" ? "Use system theme" : $"Use {mode} theme",
                () => PatchConfigAsync(root => root["themeMode"] = mode)))
        {
            _themeMode = previousMode;
            ApplyThemeControls();
        }
    }

    private void HotkeyTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        e.Handled = true;
        if (_updatingHotkeyControls)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            ApplyHotkeyControls();
            return;
        }

        if (!CanChangeHotkeys())
        {
            ApplyHotkeyControls();
            return;
        }

        if (e.Key is Key.Back or Key.Delete)
        {
            textBox.Text = string.Empty;
            return;
        }

        var shortcut = FormatHotkey(e.Key, e.KeyModifiers);
        if (shortcut is null)
        {
            SetActivity(
                "Use Ctrl, Alt, Shift, or Win with A-Z, 0-9, or F1-F12.",
                FAInfoBarSeverity.Warning);
            return;
        }

        textBox.Text = shortcut;
    }

    private async void HotkeyTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_updatingHotkeyControls)
        {
            return;
        }

        if (!CanChangeHotkeys())
        {
            ApplyHotkeyControls();
            return;
        }

        await SaveHotkeysAsync(
            ManualGearHotkeyTextBox.Text,
            AutoControlHotkeyTextBox.Text,
            CurveProfileHotkeyTextBox.Text);
    }

    private async Task SaveHotkeysAsync(string? manual, string? auto, string? curve)
    {
        manual = NormalizeHotkeyValue(manual);
        auto = NormalizeHotkeyValue(auto);
        curve = NormalizeHotkeyValue(curve);
        if (string.Equals(manual, _manualGearToggleHotkey, StringComparison.Ordinal)
            && string.Equals(auto, _autoControlToggleHotkey, StringComparison.Ordinal)
            && string.Equals(curve, _curveProfileToggleHotkey, StringComparison.Ordinal))
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shortcut in new[] { manual, auto, curve })
        {
            if (!string.IsNullOrEmpty(shortcut) && !seen.Add(shortcut))
            {
                ApplyHotkeyControls();
                SetActivity("Each non-empty global shortcut must be unique.", FAInfoBarSeverity.Warning);
                return;
            }
        }

        if (!await RunWriteAsync(
                "Update keyboard shortcuts",
                () => PatchConfigAsync(root =>
                {
                    root["manualGearToggleHotkey"] = manual;
                    root["autoControlToggleHotkey"] = auto;
                    root["curveProfileToggleHotkey"] = curve;
                })))
        {
            ApplyHotkeyControls();
        }
    }

    internal static string? FormatHotkey(Key key, KeyModifiers modifiers)
    {
        var parts = new List<string>(5);
        if ((modifiers & KeyModifiers.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & KeyModifiers.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & KeyModifiers.Meta) != 0)
        {
            parts.Add("Win");
        }

        var mainKey = key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(CultureInfo.InvariantCulture),
            >= Key.NumPad0 and <= Key.NumPad9 => ((int)key - (int)Key.NumPad0).ToString(CultureInfo.InvariantCulture),
            >= Key.F1 and <= Key.F12 => key.ToString(),
            _ => null,
        };
        if (mainKey is null || parts.Count == 0)
        {
            return null;
        }

        parts.Add(mainKey);
        return string.Join("+", parts);
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

        if (!IsBs1)
        {
            if (!TryReadManualGearRpm(gear, level, out var rpm, out var error))
            {
                SetActivity(error, FAInfoBarSeverity.Warning);
                return;
            }

            if (rpm != GetManualGearRpm(gear, level)
                && !await RunWriteAsync(
                    "Update manual preset speed",
                    () => PatchManualGearRpmAsync(gear, level, rpm)))
            {
                ApplyManualGearRpmControls();
                return;
            }
        }

        var synchronized = await RunWriteAsync(
            "Apply manual fan preset",
            () => _ipc.SetManualGearAsync(gear, level, _lifetime.Token));
        if (!synchronized)
        {
            RestoreManualSelection();
        }
    }

    private void ManualSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingConfigControls || _updatingManualGearRpmControls)
        {
            return;
        }

        ApplyManualGearRpmControls();
        SetActionAvailability();
    }

    private Task<bool> PatchManualGearRpmAsync(string gear, string level, int rpm) =>
        PatchConfigAsync(root =>
        {
            if (root["manualGearRpm"] is not JsonObject gearMap)
            {
                gearMap = new JsonObject();
                root["manualGearRpm"] = gearMap;
            }

            if (gearMap[gear] is not JsonObject levelMap)
            {
                levelMap = new JsonObject();
                gearMap[gear] = levelMap;
            }

            levelMap[level] = rpm;
        });

    private async void LegionFnQEnabledClick(object? sender, RoutedEventArgs e)
    {
        if (_updatingLegionFnQControls || !CanChangeLegionFnQ())
        {
            ApplyLegionFnQControls();
            return;
        }

        var enabled = LegionFnQEnabledSwitch.IsChecked == true;
        if (enabled == _legionFnQ.Enabled)
        {
            return;
        }

        if (!await RunWriteAsync(
                enabled ? "Enable Lenovo Fn+Q integration" : "Disable Lenovo Fn+Q integration",
                () => PatchLegionFnQConfigAsync(config => config["enabled"] = enabled)))
        {
            ApplyLegionFnQControls();
        }
    }

    private async void LegionFnQTakeOverFanClick(object? sender, RoutedEventArgs e)
    {
        if (_updatingLegionFnQControls || !CanChangeLegionFnQ() || !_legionFnQ.Enabled)
        {
            ApplyLegionFnQControls();
            return;
        }

        var takeOverFan = LegionFnQTakeOverFanSwitch.IsChecked == true;
        if (takeOverFan == _legionFnQ.TakeOverFan)
        {
            return;
        }

        if (!await RunWriteAsync(
                takeOverFan ? "Enable Fn+Q fan preset takeover" : "Disable Fn+Q fan preset takeover",
                () => PatchLegionFnQConfigAsync(config => config["takeOverFan"] = takeOverFan)))
        {
            ApplyLegionFnQControls();
        }
    }

    private async void LegionFnQMappingSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingLegionFnQControls
            || sender is not FAComboBox comboBox
            || comboBox.Tag is not string tag)
        {
            return;
        }

        var separator = tag.IndexOf(':');
        var mode = separator > 0 ? tag[..separator] : string.Empty;
        var field = separator > 0 ? tag[(separator + 1)..] : string.Empty;
        if (!CanChangeLegionFnQ()
            || !_legionFnQ.Enabled
            || !_legionFnQ.TakeOverFan
            || !LegionFnQPowerModeValues.Contains(mode)
            || (field != "gear" && field != "level"))
        {
            ApplyLegionFnQControls();
            return;
        }

        var values = field == "gear" ? ManualGearValues : ManualLevelValues;
        var value = SelectedCoreValue(comboBox, values);
        if (value is null)
        {
            ApplyLegionFnQControls();
            return;
        }

        var target = GetLegionFnQTarget(mode);
        var current = field == "gear" ? target.Gear : target.Level;
        if (string.Equals(value, current, StringComparison.Ordinal))
        {
            return;
        }

        if (!await RunWriteAsync(
                $"Update Fn+Q {mode} mapping",
                () => PatchLegionFnQConfigAsync(config =>
                {
                    if (config["modeMapping"] is not JsonObject mappings)
                    {
                        mappings = new JsonObject();
                        config["modeMapping"] = mappings;
                    }

                    if (mappings[mode] is not JsonObject mapping)
                    {
                        mapping = JsonSerializer.SerializeToNode(GetLegionFnQTarget(mode), ThrmIpcClient.JsonOptions) as JsonObject
                            ?? new JsonObject();
                        mappings[mode] = mapping;
                    }

                    mapping[field] = value;
                })))
        {
            ApplyLegionFnQControls();
        }
    }

    private Task<bool> PatchLegionFnQConfigAsync(Action<JsonObject> patch) =>
        PatchConfigAsync(root =>
        {
            if (root["legionFnQ"] is not JsonObject config)
            {
                config = JsonSerializer.SerializeToNode(_legionFnQ, ThrmIpcClient.JsonOptions) as JsonObject
                    ?? new JsonObject();
                root["legionFnQ"] = config;
            }

            patch(config);
        });

    private async void FanCurveLearningEnabledClick(object? sender, RoutedEventArgs e)
    {
        if (_updatingCurveLearningControls || !CanChangeCurveLearning())
        {
            ApplyCurveLearningControls();
            return;
        }

        var enabled = FanCurveLearningEnabledSwitch.IsChecked == true;
        if (enabled == _fanCurveLearningEnabled)
        {
            return;
        }

        if (!await RunWriteAsync(
                enabled ? "Enable curve learning" : "Disable curve learning",
                () => PatchSmartControlConfigAsync(config => config["learning"] = enabled)))
        {
            ApplyCurveLearningControls();
        }
    }

    private async void FanCurveLearningBiasSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingCurveLearningControls)
        {
            return;
        }

        var bias = SelectedCoreValue(FanCurveLearningBiasComboBox, LearningBiasValues);
        if (bias is null || !CanChangeCurveLearning())
        {
            ApplyCurveLearningControls();
            return;
        }

        if (string.Equals(bias, _fanCurveLearningBias, StringComparison.Ordinal))
        {
            return;
        }

        if (!await RunWriteAsync(
                "Set curve learning bias",
                () => PatchSmartControlConfigAsync(config => config["learningBias"] = bias)))
        {
            ApplyCurveLearningControls();
        }
    }

    private async void FanCurvePredictiveBoostClick(object? sender, RoutedEventArgs e)
    {
        if (_updatingCurveLearningControls || !CanChangeCurveLearning() || !_fanCurveLearningEnabled)
        {
            ApplyCurveLearningControls();
            return;
        }

        var enabled = FanCurvePredictiveBoostSwitch.IsChecked == true;
        if (enabled == _fanCurvePredictiveBoost)
        {
            return;
        }

        if (!await RunWriteAsync(
                enabled ? "Enable predictive curve learning" : "Disable predictive curve learning",
                () => PatchSmartControlConfigAsync(config => config["predictiveBoost"] = enabled)))
        {
            ApplyCurveLearningControls();
        }
    }

    private async void FanCurveLaptopFanGuardClick(object? sender, RoutedEventArgs e)
    {
        if (_updatingCurveLearningControls
            || !CanChangeCurveLearning()
            || !_fanCurveLearningEnabled
            || !HasLaptopFanTelemetry)
        {
            ApplyCurveLearningControls();
            return;
        }

        var enabled = FanCurveLaptopFanGuardSwitch.IsChecked == true;
        if (enabled == _fanCurveLaptopFanGuard)
        {
            return;
        }

        if (!await RunWriteAsync(
                enabled ? "Enable laptop fan guard" : "Disable laptop fan guard",
                () => PatchSmartControlConfigAsync(config => config["laptopFanGuard"] = enabled)))
        {
            ApplyCurveLearningControls();
        }
    }

    private async void ApplyFanCurveTargetTempClick(object? sender, RoutedEventArgs e)
    {
        if (!CanChangeCurveLearning())
        {
            ApplyCurveLearningControls();
            return;
        }

        var value = FanCurveTargetTempNumberBox.Value;
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value is < 45 or > 90)
        {
            SetActivity("Curve learning target temperature must be a whole number from 45 to 90 °C.", FAInfoBarSeverity.Warning);
            return;
        }

        var targetTemp = (int)value;
        if (targetTemp == _fanCurveTargetTemp)
        {
            return;
        }

        if (!await RunWriteAsync(
                "Set curve learning target temperature",
                () => PatchSmartControlConfigAsync(config => config["targetTemp"] = targetTemp)))
        {
            ApplyCurveLearningControls();
        }
    }

    private Task<bool> PatchSmartControlConfigAsync(Action<JsonObject> patch) =>
        PatchConfigAsync(root =>
        {
            if (root["smartControl"] is not JsonObject config)
            {
                config = new JsonObject
                {
                    ["learning"] = _fanCurveLearningEnabled,
                    ["learningBias"] = _fanCurveLearningBias,
                    ["predictiveBoost"] = _fanCurvePredictiveBoost,
                    ["laptopFanGuard"] = _fanCurveLaptopFanGuard,
                    ["targetTemp"] = _fanCurveTargetTemp,
                };
                root["smartControl"] = config;
            }

            patch(config);
        });

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

    private static void MenuFlyoutOpening(object? sender, EventArgs e)
    {
        if (sender is not FAMenuFlyout flyout || flyout.Popup.Child is not Control presenter)
        {
            return;
        }

        presenter.Transitions = CreateFlyoutTransitions();
        presenter.Opacity = 0;
        presenter.RenderTransform = TransformOperations.Parse("translate(0px, -6px)");
    }

    private static void MenuFlyoutOpened(object? sender, EventArgs e)
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

    private async void SpeedAvoidanceEnabledClick(object? sender, RoutedEventArgs e)
    {
        if (_updatingSpeedAvoidanceControls || !CanChangeMonitoringSources())
        {
            ApplySpeedAvoidanceControls();
            return;
        }

        var enabled = SpeedAvoidanceEnabledSwitch.IsChecked == true;
        if (enabled == _speedAvoidance.Enabled)
        {
            return;
        }

        if (!await RunWriteAsync(
                enabled ? "Enable speed avoidance" : "Disable speed avoidance",
                () => PatchSpeedAvoidanceAsync(new SpeedAvoidanceSnapshot
                {
                    Enabled = enabled,
                    MinRpm = _speedAvoidance.MinRpm,
                    MaxRpm = _speedAvoidance.MaxRpm,
                    MarginRpm = _speedAvoidance.MarginRpm,
                    EmergencyBypassTemp = _speedAvoidance.EmergencyBypassTemp,
                })))
        {
            ApplySpeedAvoidanceControls();
        }
    }

    private async void SpeedAvoidanceValueChanged(FANumberBox sender, FANumberBoxValueChangedEventArgs e)
    {
        if (_updatingSpeedAvoidanceControls || !CanChangeMonitoringSources())
        {
            return;
        }

        if (!TryReadSpeedAvoidance(out var avoidance, out _))
        {
            return;
        }

        if (avoidance.MinRpm == _speedAvoidance.MinRpm
            && avoidance.MaxRpm == _speedAvoidance.MaxRpm
            && avoidance.MarginRpm == _speedAvoidance.MarginRpm
            && avoidance.EmergencyBypassTemp == _speedAvoidance.EmergencyBypassTemp)
        {
            return;
        }

        if (!await RunWriteAsync(
                "Update speed avoidance",
                () => PatchSpeedAvoidanceAsync(avoidance)))
        {
            ApplySpeedAvoidanceControls();
        }
    }

    private Task<bool> PatchSpeedAvoidanceAsync(SpeedAvoidanceSnapshot avoidance) =>
        PatchConfigAsync(root => root["speedAvoidance"] = JsonSerializer.SerializeToNode(avoidance, ThrmIpcClient.JsonOptions));

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
            ApplyConfig(
                config,
                configJson.TryGetProperty("timeCurveSchedule", out _),
                configJson.TryGetProperty("rtss", out _));
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

    private void ApplyConfig(
        ConfigSnapshot config,
        bool timeCurveSchedulePropertyPresent,
        bool rtssPropertyPresent)
    {
        _updatingConfigControls = true;
        try
        {
            _autoControl = config.AutoControl;
            _tempSource = NormalizeTempSource(config.TempSource);
            _tempSampleCount = NormalizeTempSampleCount(config.TempSampleCount);
            _selectedCpuSensors.Clear();
            _selectedCpuSensors.UnionWith((config.CpuSensors ?? []).Where(sensor => !string.IsNullOrWhiteSpace(sensor)));
            _selectedGpuDevice = NormalizeMonitoringSelection(config.GpuDevice);
            _selectedGpuSensor = NormalizeMonitoringSelection(config.GpuSensor);
            _disableGpuMonitoring = config.DisableGpuMonitoring;
            _ignoreDeviceOnReconnect = config.IgnoreDeviceOnReconnect;
            _customSpeedEnabled = config.CustomSpeedEnabled;
            _customSpeedRpm = config.CustomSpeedRpm is >= 1000 and <= 4000 ? config.CustomSpeedRpm : 2000;
            _manualGearRpm = config.ManualGearRpm ?? [];
            _legionFnQ = NormalizeLegionFnQ(config.LegionFnQ);
            _legionFnQSupported = config.LegionFnQSupport?.Supported == true;
            _gearLight = config.GearLight;
            _lightStrip = LightStripLogic.Normalize(config.LightStrip);
            _powerOnStart = config.PowerOnStart;
            _smartStartStop = SmartStartStopValues.Contains(config.SmartStartStop) ? config.SmartStartStop! : "off";
            _themeMode = NormalizeThemeMode(config.ThemeMode);
            _debugMode = config.DebugMode;
            _manualGearToggleHotkey = NormalizeHotkeyValue(config.ManualGearToggleHotkey);
            _autoControlToggleHotkey = NormalizeHotkeyValue(config.AutoControlToggleHotkey);
            _curveProfileToggleHotkey = NormalizeHotkeyValue(config.CurveProfileToggleHotkey);
            _fanCurveLearningEnabled = config.SmartControl?.Learning ?? true;
            _fanCurveLearningBias = NormalizeLearningBias(config.SmartControl?.LearningBias);
            _fanCurvePredictiveBoost = config.SmartControl?.PredictiveBoost ?? true;
            _fanCurveLaptopFanGuard = config.SmartControl?.LaptopFanGuard ?? true;
            _fanCurveTargetTemp = config.SmartControl?.TargetTemp is >= 45 and <= 90
                ? config.SmartControl.TargetTemp
                : 68;
            _speedAvoidance = NormalizeSpeedAvoidance(config.SpeedAvoidance);
            _timeCurveScheduleSupported = timeCurveSchedulePropertyPresent;
            _rtssSupported = rtssPropertyPresent;
            _rtssEnabled = config.Rtss?.Enabled == true;
            _rtssUpdateIntervalMs = RtssUpdateIntervalOptions.Contains(config.Rtss?.UpdateIntervalMs ?? 0)
                ? config.Rtss!.UpdateIntervalMs
                : 1000;
            _rtssPositionMode = string.Equals(config.Rtss?.PositionMode, "custom", StringComparison.Ordinal)
                ? "custom"
                : "anchor";
            _rtssPositionX = Math.Clamp(config.Rtss?.PositionX ?? 0, -1000, 1000);
            _rtssPositionY = Math.Clamp(config.Rtss?.PositionY ?? 0, -1000, 1000);
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
            IgnoreDeviceOnReconnectSwitch.IsChecked = _ignoreDeviceOnReconnect;
            CustomSpeedSwitch.IsChecked = _customSpeedEnabled;
            CustomSpeedNumberBox.Value = _customSpeedRpm;
            GearLightSwitch.IsChecked = _gearLight;
            PowerOnStartSwitch.IsChecked = _powerOnStart;
            SelectCoreValue(SmartStartStopComboBox, SmartStartStopValues, _smartStartStop);
            SelectCoreValue(ManualGearComboBox, ManualGearValues, _manualGear);
            SelectCoreValue(ManualLevelComboBox, ManualLevelValues, _manualLevel);
            ApplyManualGearRpmControls();
            ApplyLegionFnQControls();
            ApplyCurveLearningControls();
            ApplyTemperatureControlControls();
            ApplyLightStripControls();
            UpdateFanCurvePreview();
            ApplyTimeCurveScheduleControls();
            ApplySpeedAvoidanceControls();
            ApplyMonitoringControls();
            ApplyRtssControls();
            ApplyThemeControls();
            ApplyDiagnosticsControls();
            ApplyHotkeyControls();
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

    }

    private void ApplyTemperature(TemperatureSnapshot temperature)
    {
        temperature = TemperatureSnapshot.MergeMetadata(_latestTemperature, temperature);
        _latestTemperature = temperature;
        CpuTempText.Text = $"{temperature.CpuTemp} °C";
        GpuTempText.Text = $"{temperature.GpuTemp} °C";
        MaxTempText.Text = $"{temperature.MaxTemp} °C";
        BridgeText.Text = temperature.BridgeOk
            ? "Temperature bridge OK"
            : $"Temperature bridge: {EmptyDash(temperature.BridgeMessage)}";
        LastUpdateText.Text = $"Temperature update: {DateTime.Now:HH:mm:ss}";
        ApplyMonitoringControls();
        ApplyLaptopFanGuardPresentation();
    }

    private void ApplyMonitoringControls()
    {
        var wasUpdating = _updatingMonitoringControls;
        _updatingMonitoringControls = true;
        try
        {
            var cpuSensorFlyout = CpuSensorSelectionButton.Flyout as FAMenuFlyout
                ?? throw new InvalidOperationException("CPU sensor selector flyout is unavailable.");
            GpuMonitoringSwitch.IsChecked = !_disableGpuMonitoring;

            var cpuOptions = BuildMonitoringOptions(_latestTemperature?.CpuSensors);
            if (!_renderedCpuSensorOptions.SequenceEqual(cpuOptions))
            {
                cpuSensorFlyout.Items.Clear();
                var automatic = new FAToggleMenuFlyoutItem
                {
                    Text = "Automatic (recommended)",
                    Tag = CpuSensorAutomaticTag,
                };
                AutomationProperties.SetName(automatic, "Use automatic CPU temperature sensor selection");
                automatic.Click += CpuSensorSelectionClick;
                cpuSensorFlyout.Items.Add(automatic);
                cpuSensorFlyout.Items.Add(new FAMenuFlyoutSeparator());
                foreach (var option in cpuOptions)
                {
                    var item = new FAToggleMenuFlyoutItem
                    {
                        Text = option.Label,
                        Tag = option.Key,
                        IsChecked = _selectedCpuSensors.Contains(option.Key),
                    };
                    AutomationProperties.SetName(item, $"Use CPU temperature sensor {option.Label}");
                    item.Click += CpuSensorSelectionClick;
                    cpuSensorFlyout.Items.Add(item);
                }

                _renderedCpuSensorOptions = cpuOptions;
            }

            var selectedCpuSensors = _cpuSensorSelectionFlyoutOpen
                ? _cpuSensorSelectionDraft
                : _selectedCpuSensors;
            foreach (var item in cpuSensorFlyout.Items.OfType<FAToggleMenuFlyoutItem>())
            {
                item.IsChecked = ReferenceEquals(item.Tag, CpuSensorAutomaticTag)
                    ? selectedCpuSensors.Count == 0
                    : item.Tag is string key && selectedCpuSensors.Contains(key);
            }

            var selectedCpuSensorCount = cpuOptions.Count(option => selectedCpuSensors.Contains(option.Key));
            CpuSensorSelectionText.Text = selectedCpuSensorCount switch
            {
                0 => "Automatic (recommended)",
                1 => cpuOptions.First(option => selectedCpuSensors.Contains(option.Key)).Label,
                _ => $"{selectedCpuSensorCount} sensors selected",
            };

            var gpuDeviceOptions = BuildGpuDeviceOptions(_latestTemperature?.GpuDevices);
            if (!_renderedGpuDeviceOptions.SequenceEqual(gpuDeviceOptions))
            {
                _renderedGpuDeviceOptions = gpuDeviceOptions;
                GpuDeviceComboBox.ItemsSource = gpuDeviceOptions;
            }

            SetSelectedMonitoringOption(GpuDeviceComboBox, gpuDeviceOptions, _selectedGpuDevice);

            var gpuSensorOptions = BuildGpuSensorOptions();
            if (!_renderedGpuSensorOptions.SequenceEqual(gpuSensorOptions))
            {
                _renderedGpuSensorOptions = gpuSensorOptions;
                GpuSensorComboBox.ItemsSource = gpuSensorOptions;
            }

            SetSelectedMonitoringOption(GpuSensorComboBox, gpuSensorOptions, _selectedGpuSensor);
        }
        finally
        {
            _updatingMonitoringControls = wasUpdating;
        }

        SetActionAvailability();
    }

    private MonitoringSelectionOption[] BuildGpuSensorOptions() =>
        BuildMonitoringOptions(EffectiveGpuSensors(), includeAutomatic: true);

    private IReadOnlyList<TemperatureSensorSnapshot> EffectiveGpuSensors()
    {
        var selectedDevice = (_latestTemperature?.GpuDevices ?? []).FirstOrDefault(device =>
            string.Equals(device.Key, _selectedGpuDevice, StringComparison.Ordinal));
        return selectedDevice is { Sensors.Count: > 0 }
            ? selectedDevice.Sensors
            : _latestTemperature?.GpuSensors ?? [];
    }

    private static MonitoringSelectionOption[] BuildGpuDeviceOptions(
        IEnumerable<TemperatureGpuDeviceSnapshot>? devices)
    {
        var options = new List<MonitoringSelectionOption>
        {
            new("auto", "Automatic (recommended)"),
        };
        options.AddRange((devices ?? [])
            .Where(device => !string.IsNullOrWhiteSpace(device.Key))
            .GroupBy(device => device.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(device => new MonitoringSelectionOption(device.Key, MonitoringOptionLabel(device.Name, device.Key))));
        return options.ToArray();
    }

    private static MonitoringSelectionOption[] BuildMonitoringOptions(
        IEnumerable<TemperatureSensorSnapshot>? sensors,
        bool includeAutomatic = false)
    {
        var options = new List<MonitoringSelectionOption>();
        if (includeAutomatic)
        {
            options.Add(new MonitoringSelectionOption("auto", "Automatic (recommended)"));
        }

        options.AddRange((sensors ?? [])
            .Where(sensor => !string.IsNullOrWhiteSpace(sensor.Key))
            .GroupBy(sensor => sensor.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(sensor => new MonitoringSelectionOption(sensor.Key, MonitoringOptionLabel(sensor.Name, sensor.Key))));
        return options.ToArray();
    }

    private static string MonitoringOptionLabel(string? name, string key) =>
        string.IsNullOrWhiteSpace(name) ? key : name;

    private static string NormalizeMonitoringSelection(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "auto" : value;

    private static void SetSelectedMonitoringOption(
        FAComboBox comboBox,
        IReadOnlyList<MonitoringSelectionOption> options,
        string selectedKey)
    {
        var selected = options.FirstOrDefault(option =>
            string.Equals(option.Key, selectedKey, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(selected.Key))
        {
            selected = options.FirstOrDefault(option => string.Equals(option.Key, "auto", StringComparison.Ordinal));
        }

        if (!Equals(comboBox.SelectedItem, selected))
        {
            comboBox.SelectedItem = selected;
        }
    }

    private void ApplyRtssControls()
    {
        var wasUpdating = _updatingRtssControls;
        _updatingRtssControls = true;
        try
        {
            RtssEnabledSwitch.IsChecked = _rtssEnabled;
            RtssIntervalComboBox.SelectedIndex = Array.IndexOf(RtssUpdateIntervalOptions, _rtssUpdateIntervalMs);
            RtssPositionModeComboBox.SelectedIndex = _rtssPositionMode == "custom" ? 1 : 0;
            RtssPositionXNumberBox.Value = _rtssPositionX;
            RtssPositionYNumberBox.Value = _rtssPositionY;
            RtssAnchorPositionSetting.IsVisible = _rtssSupported && _rtssPositionMode == "anchor";
            RtssCustomPositionSetting.IsVisible = _rtssSupported && _rtssPositionMode == "custom";
            RtssUnavailableSetting.IsVisible = !_rtssSupported;
            RtssUnavailableInfoBar.IsOpen = !_rtssSupported;
            RtssPositionStatusText.Text = _rtssPositionMode == "custom"
                ? $"Current custom offset: X {_rtssPositionX}, Y {_rtssPositionY}. Preview does not save changes."
                : "RTSS OverlayEditor controls the current position.";
            RtssAnchorStatusText.Text = DescribeRtssAnchorStatus();
        }
        finally
        {
            _updatingRtssControls = wasUpdating;
        }

        SetActionAvailability();
    }

    private void ApplyThemeControls()
    {
        var wasUpdating = _updatingThemeControls;
        _updatingThemeControls = true;
        try
        {
            SelectCoreValue(ThemeModeComboBox, ThemeModeValues, _themeMode);
            if (Application.Current is { } application)
            {
                application.RequestedThemeVariant = _themeMode switch
                {
                    "light" => ThemeVariant.Light,
                    "dark" => ThemeVariant.Dark,
                    _ => ThemeVariant.Default,
                };
            }
        }
        finally
        {
            _updatingThemeControls = wasUpdating;
        }
    }

    private void ApplyDiagnosticsControls() => DebugModeSwitch.IsChecked = _debugMode;

    private void ApplyHotkeyControls()
    {
        var wasUpdating = _updatingHotkeyControls;
        _updatingHotkeyControls = true;
        try
        {
            ManualGearHotkeyTextBox.Text = _manualGearToggleHotkey;
            AutoControlHotkeyTextBox.Text = _autoControlToggleHotkey;
            CurveProfileHotkeyTextBox.Text = _curveProfileToggleHotkey;
        }
        finally
        {
            _updatingHotkeyControls = wasUpdating;
        }
    }

    private void ApplyManualGearRpmControls()
    {
        var wasUpdating = _updatingManualGearRpmControls;
        _updatingManualGearRpmControls = true;
        try
        {
            _manualGearRpmGear = SelectedCoreValue(ManualGearComboBox, ManualGearValues)
                ?? (ManualGearValues.Contains(_manualGear) ? _manualGear! : "标准");
            _manualGearRpmLevel = IsBs1
                ? "中"
                : SelectedCoreValue(ManualLevelComboBox, ManualLevelValues)
                    ?? (ManualLevelValues.Contains(_manualLevel) ? _manualLevel! : "中");
            ManualGearRpmNumberBox.Value = GetManualGearRpm(_manualGearRpmGear, _manualGearRpmLevel);
        }
        finally
        {
            _updatingManualGearRpmControls = wasUpdating;
        }

    }

    private int GetManualGearRpm(string gear, string level) => ResolveManualGearRpm(_manualGearRpm, gear, level);

    private static int ResolveManualGearRpm(
        IReadOnlyDictionary<string, Dictionary<string, int>>? values,
        string gear,
        string level) =>
        values is not null
        && values.TryGetValue(gear, out var levels)
        && levels is not null
        && levels.TryGetValue(level, out var rpm)
        && rpm is >= 800 and <= 4500
            ? rpm
            : DefaultManualGearRpm(gear, level);

    private static int DefaultManualGearRpm(string gear, string level) => (gear, level) switch
    {
        ("静音", "低") => 1300,
        ("静音", "中") => 1700,
        ("静音", "高") => 1900,
        ("标准", "低") => 2100,
        ("标准", "中") => 2400,
        ("标准", "高") => 2700,
        ("强劲", "低") => 2800,
        ("强劲", "中") => 3000,
        ("强劲", "高") => 3300,
        ("超频", "低") => 3500,
        ("超频", "中") => 3700,
        ("超频", "高") => 4000,
        _ => 2400,
    };

    private void ApplyLegionFnQControls()
    {
        var wasUpdating = _updatingLegionFnQControls;
        _updatingLegionFnQControls = true;
        try
        {
            LegionFnQExpander.IsVisible = _legionFnQSupported;
            LegionFnQEnabledSwitch.IsChecked = _legionFnQ.Enabled;
            LegionFnQTakeOverFanSwitch.IsChecked = _legionFnQ.TakeOverFan;
            ApplyLegionFnQMapping("Quiet", LegionFnQQuietGearComboBox, LegionFnQQuietLevelComboBox);
            ApplyLegionFnQMapping("Balance", LegionFnQBalanceGearComboBox, LegionFnQBalanceLevelComboBox);
            ApplyLegionFnQMapping("Performance", LegionFnQPerformanceGearComboBox, LegionFnQPerformanceLevelComboBox);
            ApplyLegionFnQMapping("Extreme", LegionFnQExtremeGearComboBox, LegionFnQExtremeLevelComboBox);
            ApplyLegionFnQMapping("GodMode", LegionFnQGodModeGearComboBox, LegionFnQGodModeLevelComboBox);
        }
        finally
        {
            _updatingLegionFnQControls = wasUpdating;
        }
    }

    private void ApplyLegionFnQMapping(string mode, FAComboBox gearComboBox, FAComboBox levelComboBox)
    {
        var target = GetLegionFnQTarget(mode);
        SelectCoreValue(gearComboBox, ManualGearValues, target.Gear);
        SelectCoreValue(levelComboBox, ManualLevelValues, target.Level);
    }

    private FanGearTargetSnapshot GetLegionFnQTarget(string mode) =>
        _legionFnQ.ModeMapping.TryGetValue(mode, out var target)
            ? target
            : DefaultLegionFnQTarget(mode);

    internal static LegionFnQConfigSnapshot NormalizeLegionFnQ(LegionFnQConfigSnapshot? value)
    {
        var modeMapping = new Dictionary<string, FanGearTargetSnapshot>();
        foreach (var mode in LegionFnQPowerModeValues)
        {
            var fallback = DefaultLegionFnQTarget(mode);
            var target = value is not null && value.ModeMapping.TryGetValue(mode, out var configured)
                ? configured
                : null;
            modeMapping[mode] = new FanGearTargetSnapshot
            {
                Gear = ManualGearValues.Contains(target?.Gear ?? string.Empty) ? target!.Gear : fallback.Gear,
                Level = ManualLevelValues.Contains(target?.Level ?? string.Empty) ? target!.Level : fallback.Level,
            };
        }

        return new LegionFnQConfigSnapshot
        {
            Enabled = value?.Enabled == true,
            TakeOverFan = value?.TakeOverFan == true,
            ModeMapping = modeMapping,
        };
    }

    private static FanGearTargetSnapshot DefaultLegionFnQTarget(string mode) => mode switch
    {
        "Quiet" => new FanGearTargetSnapshot { Gear = "静音", Level = "中" },
        "Balance" => new FanGearTargetSnapshot { Gear = "标准", Level = "中" },
        "Performance" => new FanGearTargetSnapshot { Gear = "强劲", Level = "中" },
        "Extreme" => new FanGearTargetSnapshot { Gear = "超频", Level = "中" },
        "GodMode" => new FanGearTargetSnapshot { Gear = "超频", Level = "高" },
        _ => new FanGearTargetSnapshot { Gear = "标准", Level = "中" },
    };

    private void ApplyCurveLearningControls()
    {
        var wasUpdating = _updatingCurveLearningControls;
        _updatingCurveLearningControls = true;
        try
        {
            FanCurveLearningEnabledSwitch.IsChecked = _fanCurveLearningEnabled;
            SelectCoreValue(FanCurveLearningBiasComboBox, LearningBiasValues, _fanCurveLearningBias);
            FanCurvePredictiveBoostSwitch.IsChecked = _fanCurvePredictiveBoost;
            FanCurveLaptopFanGuardSwitch.IsChecked = _fanCurveLaptopFanGuard;
            ApplyLaptopFanGuardPresentation();
            FanCurveTargetTempNumberBox.Value = _fanCurveTargetTemp;
        }
        finally
        {
            _updatingCurveLearningControls = wasUpdating;
        }
    }

    private bool HasLaptopFanTelemetry => _latestTemperature is { CpuFanRpm: > 0 } or { GpuFanRpm: > 0 };

    private void ApplyLaptopFanGuardPresentation()
    {
        FanCurveLaptopFanGuardSetting.IsVisible = HasLaptopFanTelemetry;
        FanCurveLaptopFanGuardSwitch.IsEnabled = CanChangeCurveLearning()
            && _fanCurveLearningEnabled
            && HasLaptopFanTelemetry;
    }

    private void ApplyTemperatureControlControls()
    {
        var wasUpdating = _updatingTemperatureControlControls;
        _updatingTemperatureControlControls = true;
        try
        {
            SelectCoreValue(TempSourceComboBox, TemperatureSourceValues, _tempSource);
            TempSampleCountComboBox.SelectedIndex = Array.IndexOf(TemperatureSampleCountValues, _tempSampleCount);
        }
        finally
        {
            _updatingTemperatureControlControls = wasUpdating;
        }
    }

    private static string NormalizeThemeMode(string? value) => value switch
    {
        "light" => "light",
        "dark" => "dark",
        _ => "system",
    };

    private static string NormalizeTempSource(string? value) => value is "cpu" or "gpu" ? value : "max";

    private static string NormalizeLearningBias(string? value) =>
        LearningBiasValues.Contains(value) ? value! : "balanced";

    private static int NormalizeTempSampleCount(int value) =>
        TemperatureSampleCountValues.Contains(value) ? value : TemperatureSampleCountValues[0];

    private static string NormalizeHotkeyValue(string? value) => value?.Trim() ?? string.Empty;

    private bool CanChangeRtss() =>
        _ipc.IsConnected
        && _configKnown
        && _rtssSupported
        && !_writeInProgress;

    private async Task RefreshRtssAnchorAsync()
    {
        if (_rtssAnchorBusy)
        {
            return;
        }

        _rtssAnchorBusy = true;
        _rtssAnchorError = null;
        ApplyRtssControls();
        try
        {
            _rtssAnchorLayout = await Task.Run(RtssOverlayLayout.Inspect, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The window is closing.
        }
        catch (Exception ex)
        {
            _rtssAnchorError = ex.Message;
            SetActivity($"RTSS layout inspection failed: {ex.Message}", FAInfoBarSeverity.Error);
        }
        finally
        {
            _rtssAnchorBusy = false;
            ApplyRtssControls();
        }
    }

    private string DescribeRtssAnchorStatus()
    {
        if (_rtssAnchorBusy)
        {
            return "Checking the active RTSS layout...";
        }

        if (!string.IsNullOrWhiteSpace(_rtssAnchorError))
        {
            return $"Could not inspect the RTSS layout: {_rtssAnchorError}";
        }

        var layout = _rtssAnchorLayout;
        if (layout is null)
        {
            return "Select Refresh to inspect the active RTSS layout.";
        }

        if (!layout.Supported)
        {
            return "RTSS OverlayEditor anchoring is available on Windows only.";
        }

        if (!layout.Installed)
        {
            return "RTSS was not found. Install RTSS, then select Refresh.";
        }

        if (string.IsNullOrWhiteSpace(layout.LayoutPath))
        {
            return "RTSS is installed, but its active OverlayEditor layout could not be read.";
        }

        return layout.AnchorState switch
        {
            "confirmed" => $"Ready: THRM anchor is the last layer ({layout.AnchorIndex + 1} of {layout.LayerCount}).",
            "candidate" => "An empty one-percent layer is ready to use. Select Set up anchor to mark it for THRM.",
            "needs_last" => "An anchor layer needs to be moved to the bottom. Select Set up anchor to fix it.",
            _ => "No THRM anchor exists yet. Select Set up anchor to add one at the bottom.",
        };
    }

    private Task<bool> PatchRtssConfigAsync(Action<JsonObject> patch) =>
        PatchConfigAsync(root =>
        {
            if (root["rtss"] is not JsonObject rtss)
            {
                rtss = new JsonObject
                {
                    ["enabled"] = _rtssEnabled,
                    ["updateIntervalMs"] = _rtssUpdateIntervalMs,
                    ["positionMode"] = _rtssPositionMode,
                    ["positionX"] = _rtssPositionX,
                    ["positionY"] = _rtssPositionY,
                };
                root["rtss"] = rtss;
            }

            patch(rtss);
        });

    private void ApplySpeedAvoidanceControls()
    {
        var wasUpdating = _updatingSpeedAvoidanceControls;
        _updatingSpeedAvoidanceControls = true;
        try
        {
            SpeedAvoidanceEnabledSwitch.IsChecked = _speedAvoidance.Enabled;
            SpeedAvoidanceMinRpmNumberBox.Value = _speedAvoidance.MinRpm;
            SpeedAvoidanceMaxRpmNumberBox.Value = _speedAvoidance.MaxRpm;
            SpeedAvoidanceMarginRpmNumberBox.Value = _speedAvoidance.MarginRpm;
            SpeedAvoidanceBypassTempNumberBox.Value = _speedAvoidance.EmergencyBypassTemp;
        }
        finally
        {
            _updatingSpeedAvoidanceControls = wasUpdating;
        }

    }

    private static SpeedAvoidanceSnapshot NormalizeSpeedAvoidance(SpeedAvoidanceSnapshot? value)
    {
        var minRpm = value?.MinRpm is >= 800 and <= 4500 ? value.MinRpm : 1900;
        var maxRpm = value?.MaxRpm is >= 800 and <= 4500 ? value.MaxRpm : 2200;
        if (minRpm >= maxRpm)
        {
            minRpm = 1900;
            maxRpm = 2200;
        }

        return new SpeedAvoidanceSnapshot
        {
            Enabled = value?.Enabled == true,
            MinRpm = minRpm,
            MaxRpm = maxRpm,
            MarginRpm = value?.MarginRpm is >= 50 and <= 500 ? value.MarginRpm : 100,
            EmergencyBypassTemp = value?.EmergencyBypassTemp is >= 60 and <= 95 ? value.EmergencyBypassTemp : 80,
        };
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
        ManualFanControlExpander.IsVisible = _ipc.IsConnected
            && _deviceStateKnown
            && _configKnown
            && _deviceConnected
            && !_autoControl
            && !_customSpeedEnabled;
        ManualGearComboBox.IsEnabled = canChangeManual;
        ManualLevelSetting.IsVisible = !IsBs1;
        ManualLevelComboBox.IsEnabled = canChangeManual && !IsBs1;
        ManualGearRpmSetting.IsVisible = !IsBs1;
        ManualGearRpmNumberBox.IsEnabled = canChangeManual && !IsBs1;
        ApplyManualGearButton.IsEnabled = canChangeManual
            && SelectedCoreValue(ManualGearComboBox, ManualGearValues) is not null
            && (IsBs1 || SelectedCoreValue(ManualLevelComboBox, ManualLevelValues) is not null);

        var canChangeLegionFnQ = CanChangeLegionFnQ();
        var canChangeLegionFnQMapping = canChangeLegionFnQ && _legionFnQ.Enabled && _legionFnQ.TakeOverFan;
        LegionFnQExpander.IsVisible = _legionFnQSupported;
        LegionFnQEnabledSwitch.IsEnabled = canChangeLegionFnQ;
        LegionFnQTakeOverFanSwitch.IsEnabled = canChangeLegionFnQ && _legionFnQ.Enabled;
        LegionFnQQuietMappingSetting.IsEnabled = canChangeLegionFnQMapping;
        LegionFnQBalanceMappingSetting.IsEnabled = canChangeLegionFnQMapping;
        LegionFnQPerformanceMappingSetting.IsEnabled = canChangeLegionFnQMapping;
        LegionFnQExtremeMappingSetting.IsEnabled = canChangeLegionFnQMapping;
        LegionFnQGodModeMappingSetting.IsEnabled = canChangeLegionFnQMapping;

        var canChangeCustomSpeed = CanChangeCustomSpeed();
        CustomSpeedSwitch.IsEnabled = canChangeCustomSpeed;
        CustomSpeedNumberBox.IsEnabled = canChangeCustomSpeed && _customSpeedEnabled;
        ApplyCustomSpeedButton.IsEnabled = canChangeCustomSpeed && _customSpeedEnabled;

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
        var canChangeMonitoring = CanChangeMonitoringSources();
        IgnoreDeviceOnReconnectSwitch.IsEnabled = canChangeMonitoring;
        TempSourceComboBox.IsEnabled = canChangeMonitoring;
        TempSampleCountComboBox.IsEnabled = canChangeMonitoring && _autoControl;
        CpuSensorSelectionButton.IsEnabled = canChangeMonitoring && _renderedCpuSensorOptions.Length > 0;
        GpuMonitoringSwitch.IsEnabled = canChangeMonitoring;
        GpuDeviceComboBox.IsEnabled = canChangeMonitoring
            && !_disableGpuMonitoring
            && _renderedGpuDeviceOptions.Length > 1;
        GpuSensorComboBox.IsEnabled = canChangeMonitoring
            && !_disableGpuMonitoring
            && _renderedGpuSensorOptions.Length > 1;
        var canChangeRtss = CanChangeRtss();
        RtssEnabledSwitch.IsEnabled = canChangeRtss;
        RtssIntervalComboBox.IsEnabled = canChangeRtss && _rtssEnabled;
        RtssPositionModeComboBox.IsEnabled = canChangeRtss;
        RtssPositionXNumberBox.IsEnabled = canChangeRtss && _rtssPositionMode == "custom";
        RtssPositionYNumberBox.IsEnabled = canChangeRtss && _rtssPositionMode == "custom";
        RtssPreviewPositionButton.IsEnabled = canChangeRtss
            && _rtssPositionMode == "custom"
            && !_rtssPreviewInProgress;
        RtssApplyPositionButton.IsEnabled = canChangeRtss && _rtssPositionMode == "custom";
        RefreshRtssAnchorButton.IsEnabled = _rtssSupported
            && _rtssPositionMode == "anchor"
            && !_rtssAnchorBusy;
        CreateRtssAnchorButton.IsEnabled = _rtssSupported
            && _rtssPositionMode == "anchor"
            && !_rtssAnchorBusy
            && _rtssAnchorLayout is { Supported: true, Installed: true }
            && !string.IsNullOrWhiteSpace(_rtssAnchorLayout.LayoutPath)
            && !string.Equals(_rtssAnchorLayout.AnchorState, "confirmed", StringComparison.Ordinal);
        GearLightDeviceSetting.IsVisible = !IsBs1;
        GearLightSwitch.IsEnabled = canChangeDeviceFeatures && !IsBs1;
        PowerOnStartSwitch.IsEnabled = canChangeDeviceFeatures;
        SmartStartStopDeviceSetting.IsVisible = !IsBs1;
        SmartStartStopComboBox.IsEnabled = canChangeDeviceFeatures && !IsBs1;

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
        var canChangeCurveLearning = CanChangeCurveLearning();
        FanCurveLearningEnabledSwitch.IsEnabled = canChangeCurveLearning;
        FanCurveLearningBiasComboBox.IsEnabled = canChangeCurveLearning;
        FanCurvePredictiveBoostSwitch.IsEnabled = canChangeCurveLearning && _fanCurveLearningEnabled;
        FanCurveLaptopFanGuardSwitch.IsEnabled = canChangeCurveLearning
            && _fanCurveLearningEnabled
            && HasLaptopFanTelemetry;
        FanCurveTargetTempNumberBox.IsEnabled = canChangeCurveLearning;
        ApplyFanCurveTargetTempButton.IsEnabled = canChangeCurveLearning;
        var canChangeSpeedAvoidance = CanChangeMonitoringSources();
        SpeedAvoidanceEnabledSwitch.IsEnabled = canChangeSpeedAvoidance;
        SpeedAvoidanceMinRpmNumberBox.IsEnabled = canChangeSpeedAvoidance;
        SpeedAvoidanceMaxRpmNumberBox.IsEnabled = canChangeSpeedAvoidance;
        SpeedAvoidanceMarginRpmNumberBox.IsEnabled = canChangeSpeedAvoidance;
        SpeedAvoidanceBypassTempNumberBox.IsEnabled = canChangeSpeedAvoidance;
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
        ThemeModeComboBox.IsEnabled = CanChangeTheme();
        DebugModeSwitch.IsEnabled = CanChangeDiagnostics();
        RefreshDiagnosticsButton.IsEnabled = _ipc.IsConnected && !_diagnosticsLoading && !_diagnosticsExporting && !_writeInProgress;
        ExportDiagnosticsButton.IsEnabled = CanExportDiagnostics();
        var canSendDeviceDebugCommand = CanSendDeviceDebugCommand();
        DeviceDebugExpander.IsEnabled = true;
        DeviceDebugCommandTextBox.IsEnabled = canSendDeviceDebugCommand;
        SendDeviceDebugCommandButton.IsEnabled = canSendDeviceDebugCommand;
        var canChangeHotkeys = CanChangeHotkeys();
        ManualGearHotkeyTextBox.IsEnabled = canChangeHotkeys;
        AutoControlHotkeyTextBox.IsEnabled = canChangeHotkeys;
        CurveProfileHotkeyTextBox.IsEnabled = canChangeHotkeys;
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

    private bool CanChangeCurveLearning() =>
        _ipc.IsConnected && _configKnown && !_writeInProgress;

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

    private bool CanChangeLegionFnQ() =>
        _ipc.IsConnected
        && _configKnown
        && _legionFnQSupported
        && !_writeInProgress;

    private bool CanChangeDeviceFeatures() =>
        _ipc.IsConnected
        && _deviceStateKnown
        && _configKnown
        && _deviceConnected
        && !_writeInProgress;

    private bool CanChangeTheme() =>
        _ipc.IsConnected
        && _configKnown
        && !_writeInProgress;

    private bool CanChangeDiagnostics() => CanChangeTheme() && !_diagnosticsExporting;

    private bool CanExportDiagnostics() =>
        _ipc.IsConnected
        && !_diagnosticsLoading
        && !_diagnosticsExporting
        && !_writeInProgress;

    private bool CanSendDeviceDebugCommand() =>
        _ipc.IsConnected
        && _deviceStateKnown
        && _configKnown
        && _deviceConnected
        && _debugMode
        && !_diagnosticsLoading
        && !_diagnosticsExporting
        && !_deviceDebugCommandInProgress
        && !_writeInProgress;

    private bool CanChangeHotkeys() =>
        _ipc.IsConnected
        && _configKnown
        && !_writeInProgress;

    private bool CanChangeCustomSpeed() => CanChangeDeviceFeatures();

    private bool TryReadSpeedAvoidance(out SpeedAvoidanceSnapshot avoidance, out string error) =>
        TryCreateSpeedAvoidance(
            _speedAvoidance.Enabled,
            SpeedAvoidanceMinRpmNumberBox.Value,
            SpeedAvoidanceMaxRpmNumberBox.Value,
            SpeedAvoidanceMarginRpmNumberBox.Value,
            SpeedAvoidanceBypassTempNumberBox.Value,
            out avoidance,
            out error);

    internal static bool TryCreateSpeedAvoidance(
        bool enabled,
        double minRpmValue,
        double maxRpmValue,
        double marginRpmValue,
        double bypassTempValue,
        out SpeedAvoidanceSnapshot avoidance,
        out string error)
    {
        avoidance = new SpeedAvoidanceSnapshot();
        if (!double.IsFinite(minRpmValue)
            || !double.IsFinite(maxRpmValue)
            || !double.IsFinite(marginRpmValue)
            || !double.IsFinite(bypassTempValue)
            || minRpmValue != Math.Truncate(minRpmValue)
            || maxRpmValue != Math.Truncate(maxRpmValue)
            || marginRpmValue != Math.Truncate(marginRpmValue)
            || bypassTempValue != Math.Truncate(bypassTempValue))
        {
            error = "Speed avoidance values must be whole numbers.";
            return false;
        }

        var minRpm = (int)minRpmValue;
        var maxRpm = (int)maxRpmValue;
        var marginRpm = (int)marginRpmValue;
        var bypassTemp = (int)bypassTempValue;
        if (minRpm is < 800 or > 4500 || maxRpm is < 800 or > 4500)
        {
            error = "Avoided speeds must be between 800 and 4,500 RPM.";
            return false;
        }

        if (minRpm >= maxRpm)
        {
            error = "The minimum avoided speed must be lower than the maximum speed.";
            return false;
        }

        if (marginRpm is < 50 or > 500)
        {
            error = "The safety margin must be between 50 and 500 RPM.";
            return false;
        }

        if (bypassTemp is < 60 or > 95)
        {
            error = "The emergency bypass temperature must be between 60 and 95 °C.";
            return false;
        }

        avoidance = new SpeedAvoidanceSnapshot
        {
            Enabled = enabled,
            MinRpm = minRpm,
            MaxRpm = maxRpm,
            MarginRpm = marginRpm,
            EmergencyBypassTemp = bypassTemp,
        };
        error = string.Empty;
        return true;
    }

    private bool TryReadRtssPosition(out int x, out int y, out string error)
    {
        var xValue = RtssPositionXNumberBox.Value;
        var yValue = RtssPositionYNumberBox.Value;
        if (!double.IsFinite(xValue)
            || !double.IsFinite(yValue)
            || xValue != Math.Truncate(xValue)
            || yValue != Math.Truncate(yValue)
            || xValue is < -1000 or > 1000
            || yValue is < -1000 or > 1000)
        {
            x = 0;
            y = 0;
            error = "RTSS offsets must be whole numbers from -1,000 to 1,000.";
            return false;
        }

        x = (int)xValue;
        y = (int)yValue;
        error = string.Empty;
        return true;
    }

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

    private bool TryReadManualGearRpm(string gear, string level, out int rpm, out string error)
    {
        var value = ManualGearRpmNumberBox.Value;
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value is < 800 or > 4500)
        {
            rpm = 0;
            error = "Manual preset speed must be a whole number from 800 to 4,500 RPM.";
            return false;
        }

        rpm = (int)value;
        if (!IsManualGearRpmOrderValid(_manualGearRpm, gear, level, rpm))
        {
            error = "Manual preset speeds must not decrease from Quiet Low through Overclock High.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    internal static bool IsManualGearRpmOrderValid(
        IReadOnlyDictionary<string, Dictionary<string, int>>? values,
        string editedGear,
        string editedLevel,
        int editedRpm)
    {
        var previous = 0;
        foreach (var gear in ManualGearValues)
        {
            foreach (var level in ManualLevelValues)
            {
                var rpm = gear == editedGear && level == editedLevel
                    ? editedRpm
                    : ResolveManualGearRpm(values, gear, level);
                if (rpm < previous)
                {
                    return false;
                }

                previous = rpm;
            }
        }

        return true;
    }

    private bool IsBs1 => string.Equals(_deviceModel, "BS1", StringComparison.OrdinalIgnoreCase);

    private void RestoreManualSelection()
    {
        SelectCoreValue(ManualGearComboBox, ManualGearValues, _manualGear);
        SelectCoreValue(ManualLevelComboBox, ManualLevelValues, _manualLevel);
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

    private static string EmptyDash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _lifetime.Cancel();
        _ipc.Dispose();
    }
}
