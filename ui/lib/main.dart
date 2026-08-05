import 'dart:convert';
import 'dart:io';
import 'dart:math' as math;

import 'package:fluent_ui/fluent_ui.dart' as fluent;
import 'package:flutter/foundation.dart'
    show TargetPlatform, defaultTargetPlatform, listEquals, setEquals;
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'app_controller.dart';
import 'smooth_scroll.dart';
import 'temperature_history.dart';

void main() => runApp(const ThrmApp());

fluent.FluentThemeData _fluentTheme(Brightness brightness) {
  final systemMica = defaultTargetPlatform == TargetPlatform.windows;
  return fluent.FluentThemeData(
    brightness: brightness,
    accentColor: fluent.Colors.blue,
    navigationPaneTheme: systemMica
        ? const fluent.NavigationPaneThemeData(
            backgroundColor: Colors.transparent,
          )
        : null,
  );
}

ThemeMode _themeMode(Object? value) => switch (value) {
  'light' => ThemeMode.light,
  'dark' => ThemeMode.dark,
  _ => ThemeMode.system,
};

class ThrmApp extends StatefulWidget {
  const ThrmApp({super.key});

  @override
  State<ThrmApp> createState() => _ThrmAppState();
}

class _ThrmAppState extends State<ThrmApp> {
  late final AppController controller;

  @override
  void initState() {
    super.initState();
    controller = AppController()..start();
  }

  @override
  void dispose() {
    controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: controller,
      builder: (context, _) => fluent.FluentApp(
        title: 'THRM',
        debugShowCheckedModeBanner: false,
        locale: const Locale('zh', 'CN'),
        supportedLocales: const [Locale('zh', 'CN')],
        themeMode: _themeMode(controller.config?['themeMode']),
        theme: _fluentTheme(Brightness.light),
        darkTheme: _fluentTheme(Brightness.dark),
        builder: (_, child) =>
            ScaffoldMessenger(child: child ?? const SizedBox.shrink()),
        home: ThrmShell(controller: controller),
      ),
    );
  }
}

enum ThrmPage { status, curve, control, about }

typedef FanCurvePoint = ({int temperature, int rpm});
typedef FanCurveProfileOption = ({String id, String name});
typedef ManualGearLevel = ({String level, int rpm});
typedef ManualGearPreset = ({String gear, List<ManualGearLevel> levels});

List<FanCurvePoint> readFanCurve(Object? raw) {
  if (raw is! List || raw.length < 2) return const [];
  final points = <FanCurvePoint>[];
  for (final item in raw) {
    if (item is! Map) return const [];
    final temperatureValue = item['temperature'];
    final rpmValue = item['rpm'];
    if (temperatureValue is! num || rpmValue is! num) return const [];
    final temperature = temperatureValue.toInt();
    final rpm = rpmValue.toInt();
    if (temperatureValue != temperature ||
        rpmValue != rpm ||
        temperature > 110 ||
        rpm < 0 ||
        rpm > 4000 ||
        (points.isNotEmpty &&
            (temperature <= points.last.temperature ||
                rpm < points.last.rpm))) {
      return const [];
    }
    points.add((temperature: temperature, rpm: rpm));
  }
  return List.unmodifiable(points);
}

List<FanCurveProfileOption> readFanCurveProfileOptions(Object? raw) {
  if (raw is! List) return const [];
  final profiles = <FanCurveProfileOption>[];
  for (final item in raw) {
    if (item is! Map) continue;
    final id = item['id']?.toString().trim() ?? '';
    if (id.isEmpty) continue;
    final name = item['name']?.toString().trim() ?? '';
    profiles.add((id: id, name: name.isEmpty ? id : name));
  }
  return List.unmodifiable(profiles);
}

List<FanCurvePoint> syncFanCurveRpmAtIndex(
  List<FanCurvePoint> curve,
  int index,
  double targetRpm,
) {
  if (index < 0 || index >= curve.length) return curve;
  final rpm = ((targetRpm / 50).round() * 50).clamp(0, 4000).toInt();
  if (curve[index].rpm == rpm) return curve;

  final next = [...curve];
  next[index] = (temperature: next[index].temperature, rpm: rpm);
  for (var left = index - 1; left >= 0; left--) {
    if (next[left].rpm <= next[left + 1].rpm) break;
    next[left] = (temperature: next[left].temperature, rpm: next[left + 1].rpm);
  }
  for (var right = index + 1; right < next.length; right++) {
    if (next[right].rpm >= next[right - 1].rpm) break;
    next[right] = (
      temperature: next[right].temperature,
      rpm: next[right - 1].rpm,
    );
  }
  return List.unmodifiable(next);
}

List<FanCurvePoint> _learnedFanCurve(
  List<FanCurvePoint> baseCurve,
  Object? rawOffsets,
  Object? rawBias,
) {
  if (baseCurve.isEmpty || rawOffsets is! List) return const [];
  final minRpm = baseCurve.map((point) => point.rpm).reduce(math.min);
  final maxRpm = baseCurve.map((point) => point.rpm).reduce(math.max);
  final bias = rawBias == 'cooling' || rawBias == 'quiet'
      ? rawBias
      : 'balanced';
  var hasOffset = false;
  final learned = <FanCurvePoint>[];
  for (var index = 0; index < baseCurve.length; index++) {
    final offset = constrainLearningOffset(
      index < rawOffsets.length && rawOffsets[index] is num
          ? (rawOffsets[index] as num).toInt()
          : 0,
      bias,
    );
    hasOffset |= offset != 0;
    learned.add((
      temperature: baseCurve[index].temperature,
      rpm: (baseCurve[index].rpm + offset).clamp(minRpm, maxRpm),
    ));
  }
  return hasOffset ? List.unmodifiable(learned) : const [];
}

int constrainLearningOffset(int offset, Object? bias) =>
    (bias == 'cooling' && offset < 0) || (bias == 'quiet' && offset > 0)
    ? 0
    : offset;

List<({int index, int temperature, int rpm})> summarizeLearnedOffsets(
  List<FanCurvePoint> curve,
  Object? rawOffsets,
  Object? bias,
) {
  if (rawOffsets is! List) return const [];
  final offsets = <({int index, int temperature, int rpm})>[];
  for (
    var index = 0;
    index < curve.length && index < rawOffsets.length;
    index++
  ) {
    final raw = rawOffsets[index];
    final rpm = constrainLearningOffset(raw is num ? raw.round() : 0, bias);
    if (rpm != 0) {
      offsets.add((
        index: index,
        temperature: curve[index].temperature,
        rpm: rpm,
      ));
    }
  }
  offsets.sort((left, right) {
    final magnitude = right.rpm.abs().compareTo(left.rpm.abs());
    return magnitude != 0 ? magnitude : left.index.compareTo(right.index);
  });
  return List.unmodifiable(offsets.take(4));
}

const manualGearPresets = <ManualGearPreset>[
  (
    gear: '静音',
    levels: [
      (level: '低', rpm: 1300),
      (level: '中', rpm: 1700),
      (level: '高', rpm: 1900),
    ],
  ),
  (
    gear: '标准',
    levels: [
      (level: '低', rpm: 2100),
      (level: '中', rpm: 2400),
      (level: '高', rpm: 2700),
    ],
  ),
  (
    gear: '强劲',
    levels: [
      (level: '低', rpm: 2800),
      (level: '中', rpm: 3000),
      (level: '高', rpm: 3300),
    ],
  ),
  (
    gear: '超频',
    levels: [
      (level: '低', rpm: 3500),
      (level: '中', rpm: 3700),
      (level: '高', rpm: 4000),
    ],
  ),
];

Map<String, dynamic>? _stringMap(Object? value) =>
    value is Map ? Map<String, dynamic>.from(value) : null;

Map<String, dynamic> normalizeManualGearRpmMap(Object? raw) {
  final source = _stringMap(raw);
  final result = <String, dynamic>{};
  var previous = 0;
  for (final preset in manualGearPresets) {
    final sourceLevels = _stringMap(source?[preset.gear]);
    final levels = <String, int>{};
    for (final level in preset.levels) {
      final value = sourceLevels?[level.level];
      var rpm = value is num ? value.round() : level.rpm;
      rpm = rpm.clamp(800, 4500);
      if (rpm < previous) rpm = previous;
      levels[level.level] = rpm;
      previous = rpm;
    }
    result[preset.gear] = levels;
  }
  return result;
}

List<ManualGearPreset> effectiveManualGearPresets(
  Object? raw, {
  required bool bs1,
}) {
  if (bs1) {
    return [
      for (final preset in manualGearPresets)
        (
          gear: preset.gear,
          levels: [(level: '中', rpm: preset.levels.first.rpm)],
        ),
    ];
  }
  final values = normalizeManualGearRpmMap(raw);
  return [
    for (final preset in manualGearPresets)
      (
        gear: preset.gear,
        levels: [
          for (final level in preset.levels)
            (
              level: level.level,
              rpm: (_stringMap(values[preset.gear])?[level.level] as int),
            ),
        ],
      ),
  ];
}

Map<String, dynamic> readSpeedAvoidance(Object? raw) {
  final source = _stringMap(raw) ?? const <String, dynamic>{};
  int value(String key, int fallback, int min, int max) {
    final rawValue = source[key];
    return (rawValue is num ? rawValue.round() : fallback).clamp(min, max);
  }

  var minRpm = value('minRpm', 1900, 800, 4500);
  var maxRpm = value('maxRpm', 2200, 800, 4500);
  if (minRpm > maxRpm) (minRpm, maxRpm) = (maxRpm, minRpm);
  if (minRpm == maxRpm) {
    if (maxRpm < 4500) {
      maxRpm += 50;
    } else {
      minRpm -= 50;
    }
  }
  return {
    ...source,
    'enabled': source['enabled'] == true,
    'minRpm': minRpm,
    'maxRpm': maxRpm,
    'marginRpm': value('marginRpm', 100, 50, 500),
    'emergencyBypassTemp': value('emergencyBypassTemp', 80, 60, 95),
  };
}

const _weekdaySequence = [1, 2, 3, 4, 5, 6, 0];

List<int> normalizeScheduleWeekdays(Object? raw) {
  if (raw is! List) return List<int>.from(_weekdaySequence);
  final days = <int>{
    for (final day in raw)
      if (day is num && day == day.round() && day >= 0 && day <= 6) day.toInt(),
  };
  return days.isEmpty
      ? List<int>.from(_weekdaySequence)
      : [
          for (final day in _weekdaySequence)
            if (days.contains(day)) day,
        ];
}

String normalizeScheduleClock(Object? raw, String fallback) {
  final match = RegExp(r'^(\d{2}):(\d{2})$').firstMatch(raw?.toString() ?? '');
  if (match == null) return fallback;
  final hour = int.parse(match.group(1)!);
  final minute = int.parse(match.group(2)!);
  return hour <= 23 && minute <= 59
      ? '${hour.toString().padLeft(2, '0')}:${minute.toString().padLeft(2, '0')}'
      : fallback;
}

Map<String, dynamic> readTimeCurveSchedule(
  Object? raw,
  List<FanCurveProfileOption> profiles,
  String? activeProfileId,
) {
  final source = _stringMap(raw) ?? const <String, dynamic>{};
  final fallbackProfile =
      profiles.any((profile) => profile.id == activeProfileId)
      ? activeProfileId!
      : profiles.firstOrNull?.id ?? '';
  final validProfileIds = {for (final profile in profiles) profile.id};
  final rules = <Map<String, dynamic>>[];
  final rawRules = source['rules'];
  if (rawRules is List) {
    for (var index = 0; index < rawRules.length; index++) {
      final rule = _stringMap(rawRules[index]) ?? const <String, dynamic>{};
      final id = rule['id']?.toString().trim() ?? '';
      final name = rule['name']?.toString().trim() ?? '';
      final profileId = rule['curveProfileId']?.toString() ?? '';
      rules.add({
        ...rule,
        'id': id.isEmpty ? 'schedule-${index + 1}' : id,
        'name': name.isEmpty ? '时段 ${index + 1}' : name,
        'enabled': rule['enabled'] != false,
        'weekdays': normalizeScheduleWeekdays(rule['weekdays']),
        'startTime': normalizeScheduleClock(rule['startTime'], '22:00'),
        'endTime': normalizeScheduleClock(rule['endTime'], '07:00'),
        'curveProfileId': validProfileIds.contains(profileId)
            ? profileId
            : fallbackProfile,
      });
    }
  }
  return {...source, 'enabled': source['enabled'] == true, 'rules': rules};
}

bool scheduleRuleMatchesAt(Map<String, dynamic> rule, DateTime now) {
  if (rule['enabled'] == false) return false;
  int? minutes(Object? raw) {
    final value = normalizeScheduleClock(raw, '');
    if (value.isEmpty) return null;
    final parts = value.split(':');
    return int.parse(parts[0]) * 60 + int.parse(parts[1]);
  }

  final start = minutes(rule['startTime']);
  final end = minutes(rule['endTime']);
  if (start == null || end == null) return false;
  final days = normalizeScheduleWeekdays(rule['weekdays']);
  final weekday = now.weekday % 7;
  final previousWeekday = (weekday + 6) % 7;
  final current = now.hour * 60 + now.minute;
  if (start == end) return days.contains(weekday);
  if (start < end) {
    return days.contains(weekday) && current >= start && current < end;
  }
  return current >= start
      ? days.contains(weekday)
      : days.contains(previousWeekday) && current < end;
}

const _pageLabels = ['状态', '曲线', '控制', '关于'];
const _fluentPageIcons = [
  fluent.FluentIcons.view_dashboard,
  fluent.FluentIcons.line_chart,
  fluent.FluentIcons.settings,
  fluent.FluentIcons.info,
];

class ThrmShell extends StatefulWidget {
  const ThrmShell({
    required this.controller,
    this.fetchLatestRelease = _fetchLatestRelease,
    super.key,
  });

  final AppController controller;
  final Future<ReleaseInfo> Function() fetchLatestRelease;

  @override
  State<ThrmShell> createState() => _ThrmShellState();
}

class _ThrmShellState extends State<ThrmShell> {
  ThrmPage page = ThrmPage.status;
  bool paneExpanded = false;
  final statusScrollController = SmoothScrollController();
  final curveScrollController = SmoothScrollController();
  final controlScrollController = SmoothScrollController();
  final aboutScrollController = SmoothScrollController();

  @override
  void dispose() {
    statusScrollController.dispose();
    curveScrollController.dispose();
    controlScrollController.dispose();
    aboutScrollController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final selected = page.index;
    fluent.PaneItem paneItem(int index) => fluent.PaneItem(
      icon: Icon(
        _fluentPageIcons[index],
        key: ValueKey('navigation-item-$index'),
      ),
      title: Text(_pageLabels[index]),
      body: _page(ThrmPage.values[index]),
    );
    final view = fluent.NavigationView(
      pane: fluent.NavigationPane(
        key: const ValueKey('navigation-pane'),
        toggleButton: Transform.translate(
          offset: const Offset(0, 5),
          child: fluent.PaneToggleButton(
            key: const ValueKey('navigation-pane-toggle'),
            onPressed: _togglePane,
          ),
        ),
        toggleButtonPosition: fluent.PaneToggleButtonPreferredPosition.pane,
        selected: selected,
        onChanged: _selectPage,
        displayMode: fluent.PaneDisplayMode.expanded,
        toggleable: false,
        size: fluent.NavigationPaneSize(openWidth: paneExpanded ? 180 : 50),
        items: [for (var index = 0; index < 3; index++) paneItem(index)],
        footerItems: [
          paneItem(ThrmPage.about.index),
          fluent.PaneItemWidgetAdapter(
            key: const ValueKey('navigation-about-gap'),
            applyPadding: false,
            child: const SizedBox(height: 3),
          ),
        ],
      ),
    );
    return Scaffold(backgroundColor: Colors.transparent, body: view);
  }

  void _togglePane() => setState(() => paneExpanded = !paneExpanded);

  void _selectPage(int index) => setState(() => page = ThrmPage.values[index]);

  Widget _page(ThrmPage selected) => switch (selected) {
    ThrmPage.status => StatusPage(
      controller: widget.controller,
      scrollController: statusScrollController,
    ),
    ThrmPage.curve => FanCurvePage(
      controller: widget.controller,
      scrollController: curveScrollController,
    ),
    ThrmPage.control => ControlPage(
      controller: widget.controller,
      scrollController: controlScrollController,
    ),
    ThrmPage.about => AboutPage(
      scrollController: aboutScrollController,
      fetchLatestRelease: widget.fetchLatestRelease,
    ),
  };
}

class StatusPage extends StatelessWidget {
  const StatusPage({
    required this.controller,
    required this.scrollController,
    super.key,
  });

  final AppController controller;
  final ScrollController scrollController;

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: controller,
      builder: (context, _) {
        final temp = controller.temperature;
        final fan = controller.fanData;
        final status = controller.deviceStatus;
        final connected = controller.connection == CoreConnection.connected;
        final autoControl = controller.config?['autoControl'] == true;
        final bridgeFailed = temp?['bridgeOk'] == false;
        final cpuTempError = temp?['cpuTempError']?.toString().trim() ?? '';
        final bridgeMessage = temp?['bridgeMessage']?.toString().trim() ?? '';
        final temperatureWarning = bridgeFailed
            ? (bridgeMessage.isEmpty ? '温度监控暂不可用，Core 将继续自动恢复。' : bridgeMessage)
            : (cpuTempError.isEmpty ? null : cpuTempError);
        final connectionMessage =
            controller.error ??
            (connected
                ? controller.deviceConnected
                      ? '设备已连接，实时事件由 Core 推送'
                      : 'Core 在线，设备当前未连接'
                : '正在自动连接后台服务');
        final typography = fluent.FluentTheme.of(context).typography;
        return fluent.ScaffoldPage.scrollable(
          scrollController: scrollController,
          header: fluent.PageHeader(
            title: const Text('状态'),
            commandBar: fluent.CommandBar(
              mainAxisAlignment: MainAxisAlignment.end,
              primaryItems: [
                fluent.CommandBarButton(
                  icon: const Icon(fluent.FluentIcons.refresh),
                  label: const Text('刷新'),
                  tooltip: '刷新快照',
                  onPressed: connected ? controller.refresh : null,
                ),
              ],
            ),
          ),
          children: [
            fluent.InfoBar(
              title: Text(connected ? '已连接 THRM Core' : '正在等待 THRM Core'),
              content: Text(connectionMessage),
              severity: controller.error != null
                  ? fluent.InfoBarSeverity.error
                  : connected
                  ? fluent.InfoBarSeverity.success
                  : fluent.InfoBarSeverity.warning,
            ),
            const SizedBox(height: 16),
            Text('实时状态', style: typography.subtitle),
            const SizedBox(height: 8),
            GridView.extent(
              maxCrossAxisExtent: 280,
              shrinkWrap: true,
              physics: const NeverScrollableScrollPhysics(),
              childAspectRatio: 1.55,
              mainAxisSpacing: 8,
              crossAxisSpacing: 8,
              children: [
                MetricCard(
                  icon: fluent.WindowsIcons.cpu,
                  label: 'CPU 温度',
                  value: _metric(temp, 'cpuTemp', '°C'),
                ),
                MetricCard(
                  icon: fluent.WindowsIcons.cpu,
                  label: 'GPU 温度',
                  value: _metric(temp, 'gpuTemp', '°C'),
                ),
                MetricCard(
                  icon: fluent.FluentIcons.lightning_bolt,
                  label: 'CPU 功耗',
                  value: _metric(temp, 'cpuPower', 'W'),
                ),
                MetricCard(
                  icon: fluent.FluentIcons.lightning_bolt,
                  label: 'GPU 功耗',
                  value: _metric(temp, 'gpuPower', 'W'),
                ),
                MetricCard(
                  icon: fluent.FluentIcons.speed_high,
                  label: '当前转速',
                  value: _metric(fan, 'currentRpm', ' RPM'),
                ),
                MetricCard(
                  icon: fluent.FluentIcons.speed_high,
                  label: '目标转速',
                  value: _metric(fan, 'targetRpm', ' RPM'),
                ),
              ],
            ),
            if (temperatureWarning != null) ...[
              const SizedBox(height: 16),
              fluent.InfoBar.error(
                title: const Text('温度监控异常'),
                content: Text(temperatureWarning),
              ),
            ],
            const SizedBox(height: 16),
            Text('设备与控制', style: typography.subtitle),
            const SizedBox(height: 8),
            fluent.Card(
              padding: EdgeInsets.zero,
              child: Column(
                children: [
                  fluent.ListTile(
                    leading: Icon(
                      controller.deviceConnected
                          ? fluent.FluentIcons.usb
                          : fluent.WindowsIcons.disconnect_drive,
                    ),
                    title: Text(controller.deviceConnected ? '设备已连接' : '设备未连接'),
                    subtitle: Text(
                      '${status?['model'] ?? '未知型号'} · '
                      '${status?['productId'] ?? '--'}',
                    ),
                  ),
                  const fluent.Divider(),
                  fluent.ListTile(
                    leading: const Icon(fluent.FluentIcons.snowflake),
                    title: const Text('智能控温'),
                    subtitle: Text(
                      controller.updatingAutoControl
                          ? '正在切换…'
                          : '${autoControl ? '已开启' : '已关闭'} · '
                                '最近事件：${controller.lastEvent ?? '--'}',
                    ),
                    trailing: fluent.ToggleSwitch(
                      checked: autoControl,
                      semanticLabel: '智能控温',
                      onChanged:
                          connected &&
                              controller.deviceConnected &&
                              controller.config != null &&
                              !controller.updatingAutoControl
                          ? (enabled) async {
                              await controller.setAutoControl(enabled);
                            }
                          : null,
                    ),
                  ),
                ],
              ),
            ),
          ],
        );
      },
    );
  }

  String _metric(Map<String, dynamic>? data, String key, String suffix) {
    final value = data?[key];
    return value is num ? '$value$suffix' : '--';
  }
}

class MetricCard extends StatelessWidget {
  const MetricCard({
    required this.icon,
    required this.label,
    required this.value,
    super.key,
  });

  final IconData icon;
  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    return fluent.Card(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          Row(children: [Icon(icon), const SizedBox(width: 8), Text(label)]),
          Text(
            value,
            style: fluent.FluentTheme.of(context).typography.subtitle,
          ),
        ],
      ),
    );
  }
}

class FanCurvePage extends StatefulWidget {
  const FanCurvePage({
    required this.controller,
    required this.scrollController,
    super.key,
  });

  final AppController controller;
  final ScrollController scrollController;

  @override
  State<FanCurvePage> createState() => _FanCurvePageState();
}

enum _PendingCurveAction { discard, save }

enum _ProfileMenuAction { create, rename, delete, export, import }

class _FanCurvePageState extends State<FanCurvePage> {
  List<FanCurvePoint> savedCurve = const [];
  List<FanCurvePoint> draftCurve = const [];
  bool dirty = false;
  int? dragIndex;
  double dragRpm = 0;
  double? targetTempDraft;
  bool avoidanceRevealed = false;
  (_ProfileMenuAction, FanCurveProfileOption?)? pendingProfileAction;

  void _syncExternalCurve(List<FanCurvePoint> external) {
    if (listEquals(savedCurve, external)) return;
    savedCurve = external;
    if (!dirty) draftCurve = external;
  }

  void _startDrag(int index) {
    dragIndex = index;
    dragRpm = draftCurve[index].rpm.toDouble();
  }

  void _dragBy(int index, double rpmDelta) {
    if (dragIndex != index ||
        widget.controller.updatingFanCurve ||
        widget.controller.updatingFanCurveProfile) {
      return;
    }
    dragRpm = (dragRpm + rpmDelta).clamp(0, 4000).toDouble();
    final next = syncFanCurveRpmAtIndex(draftCurve, index, dragRpm);
    if (identical(next, draftCurve)) return;
    setState(() {
      draftCurve = next;
      dirty = !listEquals(draftCurve, savedCurve);
    });
  }

  void _endDrag() => dragIndex = null;

  void _discard() {
    setState(() {
      draftCurve = savedCurve;
      dirty = false;
    });
  }

  Future<bool> _save() async {
    if (!dirty ||
        widget.controller.updatingFanCurve ||
        widget.controller.updatingFanCurveProfile) {
      return false;
    }
    if (!await _confirmLowRpm(draftCurve)) return false;

    final savingCurve = List<FanCurvePoint>.unmodifiable(draftCurve);
    final saved = await widget.controller.setFanCurve(_curveJson(savingCurve));
    if (!mounted) return false;
    if (saved) {
      setState(() {
        savedCurve = savingCurve;
        dirty = !listEquals(draftCurve, savedCurve);
      });
    } else {
      _showError(widget.controller.error ?? 'Core 当前不可用，曲线未保存');
    }
    return saved;
  }

  void _showError(String message) {
    fluent.displayInfoBar(
      context,
      alignment: Alignment.topCenter,
      builder: (_, close) => fluent.InfoBar.error(
        title: const Text('操作失败'),
        content: Text(message),
        action: fluent.IconButton(
          icon: const Icon(fluent.WindowsIcons.chrome_close),
          onPressed: close,
        ),
      ),
    );
  }

  List<Map<String, int>> _curveJson(List<FanCurvePoint> curve) => [
    for (final point in curve)
      {'temperature': point.temperature, 'rpm': point.rpm},
  ];

  Future<bool> _confirmAction({
    required IconData icon,
    required String title,
    required String message,
    required String confirmLabel,
  }) async {
    final confirmed = await fluent.showDialog<bool>(
      context: context,
      builder: (dialogContext) => fluent.ContentDialog(
        title: Row(
          children: [
            Icon(icon, size: 20),
            const SizedBox(width: 10),
            Expanded(child: Text(title)),
          ],
        ),
        content: Text(message),
        actions: [
          fluent.Button(
            onPressed: () => Navigator.pop(dialogContext, true),
            child: Text(confirmLabel),
          ),
          fluent.FilledButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('取消'),
          ),
        ],
      ),
    );
    return mounted && confirmed == true;
  }

  Future<bool> _confirmLowRpm(List<FanCurvePoint> curve) async {
    if (!curve.any((point) => point.rpm < 1000)) return true;
    return _confirmAction(
      icon: fluent.FluentIcons.warning,
      title: '低转速风险',
      message: '曲线中存在低于 1000 RPM 的控制点，风扇可能停转并导致设备过热。确定仍要保存吗？',
      confirmLabel: '仍然保存',
    );
  }

  Future<String?> _promptProfileName({
    required String title,
    required String description,
    required String initialValue,
    required String fallback,
  }) async {
    var input = initialValue;
    String value() {
      final trimmed = input.trim();
      return trimmed.isEmpty ? fallback : trimmed;
    }

    final controller = TextEditingController(text: initialValue);
    final result = await fluent.showDialog<String>(
      context: context,
      builder: (dialogContext) => fluent.ContentDialog(
        title: Text(title),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(description),
            const SizedBox(height: 16),
            fluent.TextBox(
              key: const ValueKey('fan-curve-profile-name-input'),
              controller: controller,
              autofocus: true,
              maxLength: 6,
              textInputAction: TextInputAction.done,
              placeholder: '方案名称',
              onChanged: (text) => input = text,
              onSubmitted: (_) => Navigator.pop(dialogContext, value()),
            ),
          ],
        ),
        actions: [
          fluent.Button(
            onPressed: () => Navigator.pop(dialogContext, value()),
            child: const Text('确定'),
          ),
          fluent.FilledButton(
            onPressed: () => Navigator.pop(dialogContext),
            child: const Text('取消'),
          ),
        ],
      ),
    );
    controller.dispose();
    return result;
  }

  Future<void> _createProfile() async {
    final name = await _promptProfileName(
      title: '新建曲线方案',
      description: '以当前曲线为基础创建并切换到新方案。',
      initialValue: '',
      fallback: '新曲线',
    );
    if (!mounted || name == null || !await _confirmLowRpm(draftCurve)) return;
    final curve = List<FanCurvePoint>.unmodifiable(draftCurve);
    final saved = await widget.controller.saveFanCurveProfile(
      id: '',
      name: name,
      curve: _curveJson(curve),
      setActive: true,
    );
    if (!mounted) return;
    if (!saved) {
      _showError(widget.controller.error ?? '新建曲线方案失败');
      return;
    }
    final activeCurve = readFanCurve(widget.controller.config?['fanCurve']);
    setState(() {
      savedCurve = activeCurve.isEmpty ? curve : activeCurve;
      draftCurve = savedCurve;
      dirty = false;
    });
  }

  Future<void> _renameProfile(FanCurveProfileOption profile) async {
    final name = await _promptProfileName(
      title: '重命名曲线方案',
      description: '方案名称最多 6 个字符。',
      initialValue: profile.name,
      fallback: profile.name,
    );
    if (!mounted || name == null || name == profile.name) return;
    final saved = await widget.controller.saveFanCurveProfile(
      id: profile.id,
      name: name,
      curve: _curveJson(savedCurve),
      setActive: false,
    );
    if (!mounted) return;
    if (!saved) _showError(widget.controller.error ?? '重命名曲线方案失败');
  }

  Future<void> _deleteProfile(FanCurveProfileOption profile) async {
    final confirmed = await _confirmAction(
      icon: fluent.FluentIcons.delete,
      title: '删除曲线方案？',
      message: dirty
          ? '“${profile.name}”还有未保存修改；删除后这些修改也会丢失。'
          : '确定删除“${profile.name}”吗？此操作无法撤销。',
      confirmLabel: '删除',
    );
    if (!confirmed) return;
    final deleted = await widget.controller.deleteFanCurveProfile(profile.id);
    if (!mounted) return;
    if (!deleted) {
      _showError(widget.controller.error ?? '删除曲线方案失败');
      return;
    }
    final activeCurve = readFanCurve(widget.controller.config?['fanCurve']);
    setState(() {
      savedCurve = activeCurve;
      draftCurve = activeCurve;
      dirty = false;
    });
  }

  Future<void> _exportProfiles() async {
    if (dirty && !await _save()) return;
    final code = await widget.controller.exportFanCurveProfiles();
    if (!mounted) return;
    if (code == null) {
      _showError(widget.controller.error ?? '导出曲线方案失败');
      return;
    }
    try {
      await Clipboard.setData(ClipboardData(text: code));
    } catch (caught) {
      if (!mounted) return;
      _showError('复制方案码失败：$caught');
    }
  }

  Future<void> _importProfiles() async {
    String code;
    try {
      code =
          (await Clipboard.getData(Clipboard.kTextPlain))?.text?.trim() ?? '';
    } catch (caught) {
      if (!mounted) return;
      _showError('读取剪贴板失败：$caught');
      return;
    }
    if (!mounted) return;
    if (code.isEmpty) {
      _showError('剪贴板中没有方案码');
      return;
    }
    if (dirty && !await _save()) return;
    final imported = await widget.controller.importFanCurveProfiles(code);
    if (!mounted) return;
    if (!imported) {
      _showError(widget.controller.error ?? '导入曲线方案失败');
      return;
    }
    final activeCurve = readFanCurve(widget.controller.config?['fanCurve']);
    setState(() {
      savedCurve = activeCurve;
      draftCurve = activeCurve;
      dirty = false;
    });
  }

  Future<bool> _saveConfig(Map<String, dynamic> patch, String action) async {
    final saved = await widget.controller.updateConfig(patch, action: action);
    if (!mounted) return false;
    if (!saved) _showError(widget.controller.error ?? '$action失败');
    return saved;
  }

  Future<void> _setAutoControl(bool enabled) async {
    await widget.controller.setAutoControl(enabled);
    if (!mounted) return;
    if (widget.controller.error != null) _showError(widget.controller.error!);
  }

  Future<void> _updateSmartControl(
    Map<String, dynamic> current,
    Map<String, dynamic> patch,
  ) async {
    await _saveConfig({
      'smartControl': {...current, ...patch},
    }, '保存学习设置');
  }

  Future<void> _resetLearnedOffsets() async {
    final reset = await widget.controller.resetLearnedOffsets();
    if (!mounted) return;
    if (!reset) _showError(widget.controller.error ?? '重置学习偏移失败');
  }

  Future<void> _setManualGear(String gear, String level) async {
    final changed = await widget.controller.setManualGear(gear, level);
    if (!mounted) return;
    if (!changed) _showError(widget.controller.error ?? '切换手动挡位失败');
  }

  Future<void> _editManualGearRpm(
    Object? current,
    String gear,
    String level,
  ) async {
    final values = await fluent.showDialog<Map<String, dynamic>>(
      context: context,
      builder: (_) => _ManualGearRpmDialog(initial: current),
    );
    if (!mounted || values == null) return;
    if (!await _saveConfig({'manualGearRpm': values}, '保存挡位转速')) return;
    await _setManualGear(gear, level);
  }

  Future<void> _revealAvoidance() async {
    final confirmed = await _confirmAction(
      icon: fluent.FluentIcons.warning,
      title: '展开避噪转速设置？',
      message: '这些参数需要根据设备实测噪音调整；设置不当可能影响正常散热。',
      confirmLabel: '我已了解',
    );
    if (confirmed) setState(() => avoidanceRevealed = true);
  }

  Future<void> _updateSpeedAvoidance(
    Map<String, dynamic> current,
    Map<String, dynamic> patch,
  ) async {
    await _saveConfig({
      'speedAvoidance': readSpeedAvoidance({...current, ...patch}),
    }, '保存避噪转速设置');
  }

  Future<void> _updateSchedule(
    Map<String, dynamic> current,
    List<FanCurveProfileOption> profiles,
    String? activeProfileId,
    Map<String, dynamic> patch,
  ) async {
    final next = readTimeCurveSchedule(
      {...current, ...patch},
      profiles,
      activeProfileId,
    );
    await _saveConfig({'timeCurveSchedule': next}, '保存分时曲线');
  }

  Future<void> _addScheduleRule(
    Map<String, dynamic> schedule,
    List<FanCurveProfileOption> profiles,
    String? activeProfileId,
  ) async {
    if (profiles.isEmpty) return;
    final rules = [
      for (final raw in schedule['rules'] as List)
        Map<String, dynamic>.from(raw as Map),
    ];
    rules.add({
      'id':
          'schedule-${DateTime.now().microsecondsSinceEpoch.toRadixString(36)}',
      'name': '新时段',
      'enabled': true,
      'weekdays': List<int>.from(_weekdaySequence),
      'startTime': '22:00',
      'endTime': '07:00',
      'curveProfileId': activeProfileId ?? profiles.first.id,
    });
    await _updateSchedule(schedule, profiles, activeProfileId, {
      'rules': rules,
    });
  }

  Future<void> _updateScheduleRule(
    Map<String, dynamic> schedule,
    List<FanCurveProfileOption> profiles,
    String? activeProfileId,
    String id,
    Map<String, dynamic> patch,
  ) async {
    final rules = [
      for (final raw in schedule['rules'] as List)
        if ((raw as Map)['id'] == id)
          {...Map<String, dynamic>.from(raw), ...patch}
        else
          Map<String, dynamic>.from(raw),
    ];
    await _updateSchedule(schedule, profiles, activeProfileId, {
      'rules': rules,
    });
  }

  Future<void> _deleteScheduleRule(
    Map<String, dynamic> schedule,
    List<FanCurveProfileOption> profiles,
    String? activeProfileId,
    String id,
  ) async {
    final rules = [
      for (final raw in schedule['rules'] as List)
        if ((raw as Map)['id'] != id) Map<String, dynamic>.from(raw),
    ];
    await _updateSchedule(schedule, profiles, activeProfileId, {
      'rules': rules,
    });
  }

  Future<void> _setTemperatureHistoryEnabled(bool enabled) async {
    if (!enabled &&
        !await _confirmAction(
          icon: fluent.FluentIcons.delete_rows,
          title: '关闭后台温度记录？',
          message: 'Core 会立即清空已保存的温度历史；重新开启后只会记录新的采样。',
          confirmLabel: '关闭并清空',
        )) {
      return;
    }
    final saved = await widget.controller.setTemperatureHistoryEnabled(enabled);
    if (!mounted) return;
    if (!saved) _showError(widget.controller.error ?? '设置温度历史记录失败');
  }

  Future<void> _setTemperatureHistoryRetentionHours(int hours) async {
    final saved = await widget.controller.setTemperatureHistoryRetentionHours(
      hours,
    );
    if (!mounted) return;
    if (!saved) _showError(widget.controller.error ?? '设置温度历史保留时长失败');
  }

  Future<void> _handleProfileAction(
    _ProfileMenuAction action,
    FanCurveProfileOption? profile,
  ) async {
    switch (action) {
      case _ProfileMenuAction.create:
        await _createProfile();
      case _ProfileMenuAction.rename:
        if (profile != null) await _renameProfile(profile);
      case _ProfileMenuAction.delete:
        if (profile != null) await _deleteProfile(profile);
      case _ProfileMenuAction.export:
        await _exportProfiles();
      case _ProfileMenuAction.import:
        await _importProfiles();
    }
  }

  Future<void> _switchProfile(String? id) async {
    final activeId = widget.controller.config?['activeFanCurveProfileId']
        ?.toString();
    if (id == null ||
        id == activeId ||
        widget.controller.updatingFanCurve ||
        widget.controller.updatingFanCurveProfile) {
      return;
    }

    if (dirty) {
      final action = await fluent.showDialog<_PendingCurveAction>(
        context: context,
        builder: (dialogContext) => fluent.ContentDialog(
          title: const Text('曲线尚未保存'),
          content: const Text('切换方案前，要保存当前曲线的修改吗？'),
          actions: [
            fluent.Button(
              onPressed: () => Navigator.pop(dialogContext),
              child: const Text('取消'),
            ),
            fluent.Button(
              onPressed: () =>
                  Navigator.pop(dialogContext, _PendingCurveAction.discard),
              child: const Text('放弃并切换'),
            ),
            fluent.FilledButton(
              onPressed: () =>
                  Navigator.pop(dialogContext, _PendingCurveAction.save),
              child: const Text('保存并切换'),
            ),
          ],
        ),
      );
      if (!mounted || action == null) return;
      if (action == _PendingCurveAction.save) {
        if (!await _save()) return;
      } else {
        _discard();
      }
    }

    final switched = await widget.controller.setActiveFanCurveProfile(id);
    if (!mounted || switched) return;
    _showError(widget.controller.error ?? '曲线方案切换失败');
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: widget.controller,
      builder: (context, _) {
        final config = widget.controller.config ?? const <String, dynamic>{};
        _syncExternalCurve(readFanCurve(config['fanCurve']));
        final points = draftCurve;
        final profiles = readFanCurveProfileOptions(config['fanCurveProfiles']);
        final activeProfileId = config['activeFanCurveProfileId']?.toString();
        final selectedProfileIndex = profiles.indexWhere(
          (profile) => profile.id == activeProfileId,
        );
        final selectedProfile = profiles.isEmpty
            ? null
            : profiles[selectedProfileIndex >= 0 ? selectedProfileIndex : 0];
        final selectedProfileId = selectedProfile?.id;
        final busy =
            widget.controller.updatingFanCurve ||
            widget.controller.updatingFanCurveProfile ||
            widget.controller.updatingFanFeatures ||
            widget.controller.updatingManualGear;
        final controlsEnabled =
            widget.controller.connection == CoreConnection.connected &&
            widget.controller.config != null;
        final autoControl = config['autoControl'] == true;
        final temperature = widget.controller.temperature?['controlTemp'] is num
            ? (widget.controller.temperature!['controlTemp'] as num).toDouble()
            : widget.controller.temperature?['maxTemp'] is num
            ? (widget.controller.temperature!['maxTemp'] as num).toDouble()
            : 0.0;
        final targetRpm = widget.controller.fanData?['targetRpm'];
        final smartControl = <String, dynamic>{
          'learning': true,
          'predictiveBoost': true,
          'learningBias': 'balanced',
          'filterTransientSpike': true,
          'laptopFanGuard': true,
          'targetTemp': 68,
          ...?_stringMap(config['smartControl']),
        };
        if (!const {
          'balanced',
          'cooling',
          'quiet',
        }.contains(smartControl['learningBias'])) {
          smartControl['learningBias'] = 'balanced';
        }
        final configuredTargetTemp = smartControl['targetTemp'];
        smartControl['targetTemp'] =
            (configuredTargetTemp is num ? configuredTargetTemp.round() : 68)
                .clamp(45, 90);
        final learnedPoints = autoControl && smartControl['learning'] == true
            ? _learnedFanCurve(
                points,
                smartControl['learnedOffsets'],
                smartControl['learningBias'],
              )
            : const <FanCurvePoint>[];
        final speedAvoidance = readSpeedAvoidance(config['speedAvoidance']);
        if (speedAvoidance['enabled'] == true) avoidanceRevealed = true;
        final schedule = readTimeCurveSchedule(
          config['timeCurveSchedule'],
          profiles,
          activeProfileId,
        );
        final scheduleRules = (schedule['rules'] as List)
            .cast<Map<String, dynamic>>();
        final currentScheduleRule = schedule['enabled'] == true
            ? scheduleRules.cast<Map<String, dynamic>?>().firstWhere(
                (rule) => scheduleRuleMatchesAt(rule!, DateTime.now()),
                orElse: () => null,
              )
            : null;
        final deviceModel = widget.controller.deviceStatus?['model']
            ?.toString();
        final manualPresets = effectiveManualGearPresets(
          config['manualGearRpm'],
          bs1: deviceModel == 'BS1',
        );
        final manualGear = config['manualGear']?.toString() ?? '标准';
        final manualLevel = config['manualLevel']?.toString() ?? '中';
        final theme = fluent.FluentTheme.of(context);
        return fluent.ScaffoldPage.scrollable(
          scrollController: widget.scrollController,
          header: const fluent.PageHeader(title: Text('风扇曲线')),
          children: [
            Wrap(
              alignment: WrapAlignment.spaceBetween,
              crossAxisAlignment: WrapCrossAlignment.center,
              spacing: 12,
              runSpacing: 8,
              children: [
                Text(
                  points.isEmpty
                      ? '等待 Core 返回有效曲线'
                      : dirty
                      ? '${points.length} 个控制点 · 有未保存修改'
                      : '${points.length} 个控制点 · 当前显示 Core 生效曲线',
                ),
                if (points.isNotEmpty)
                  Wrap(
                    spacing: 8,
                    crossAxisAlignment: WrapCrossAlignment.center,
                    children: [
                      if (profiles.isNotEmpty)
                        Semantics(
                          label: '当前风扇曲线方案',
                          child: SizedBox(
                            width: 128,
                            height: 34,
                            child: fluent.ComboBox<String>(
                              key: const ValueKey('fan-curve-profile-selector'),
                              value: selectedProfileId,
                              isExpanded: true,
                              iconSize: _comboBoxIconSize,
                              items: [
                                for (final profile in profiles)
                                  fluent.ComboBoxItem(
                                    value: profile.id,
                                    child: Text(profile.name),
                                  ),
                              ],
                              onChanged:
                                  widget.controller.connection ==
                                          CoreConnection.connected &&
                                      profiles.length > 1 &&
                                      !busy
                                  ? _switchProfile
                                  : null,
                            ),
                          ),
                        ),
                      SizedBox(
                        height: 34,
                        child: fluent.DropDownButton(
                          key: const ValueKey('fan-curve-profile-menu'),
                          disabled:
                              widget.controller.connection !=
                                  CoreConnection.connected ||
                              busy,
                          leading: const Icon(fluent.FluentIcons.more),
                          title: const Text('管理'),
                          onClose: () async {
                            final pending = pendingProfileAction;
                            pendingProfileAction = null;
                            if (pending != null) {
                              await _handleProfileAction(
                                pending.$1,
                                pending.$2,
                              );
                            }
                          },
                          items: [
                            fluent.MenuFlyoutItem(
                              leading: const Icon(fluent.FluentIcons.add),
                              text: const Text('新建方案'),
                              onPressed: () => pendingProfileAction = (
                                _ProfileMenuAction.create,
                                selectedProfile,
                              ),
                            ),
                            fluent.MenuFlyoutItem(
                              leading: const Icon(fluent.FluentIcons.edit),
                              text: const Text('重命名'),
                              onPressed: selectedProfile == null
                                  ? null
                                  : () => pendingProfileAction = (
                                      _ProfileMenuAction.rename,
                                      selectedProfile,
                                    ),
                            ),
                            fluent.MenuFlyoutItem(
                              leading: const Icon(fluent.FluentIcons.delete),
                              text: const Text('删除方案'),
                              onPressed:
                                  selectedProfile != null && profiles.length > 1
                                  ? () => pendingProfileAction = (
                                      _ProfileMenuAction.delete,
                                      selectedProfile,
                                    )
                                  : null,
                            ),
                            const fluent.MenuFlyoutSeparator(),
                            fluent.MenuFlyoutItem(
                              leading: const Icon(fluent.FluentIcons.copy),
                              text: const Text('导出并复制方案码'),
                              onPressed: () => pendingProfileAction = (
                                _ProfileMenuAction.export,
                                selectedProfile,
                              ),
                            ),
                            fluent.MenuFlyoutItem(
                              leading: const Icon(fluent.FluentIcons.paste),
                              text: const Text('从剪贴板导入方案码'),
                              onPressed: () => pendingProfileAction = (
                                _ProfileMenuAction.import,
                                selectedProfile,
                              ),
                            ),
                          ],
                        ),
                      ),
                      SizedBox(
                        height: 34,
                        child: fluent.Button(
                          key: const ValueKey('fan-curve-discard'),
                          onPressed: dirty && !busy ? _discard : null,
                          child: const Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              Icon(fluent.FluentIcons.undo, size: 16),
                              SizedBox(width: 8),
                              Text('放弃修改'),
                            ],
                          ),
                        ),
                      ),
                      SizedBox(
                        height: 34,
                        child: fluent.FilledButton(
                          key: const ValueKey('fan-curve-save'),
                          onPressed:
                              dirty &&
                                  widget.controller.connection ==
                                      CoreConnection.connected &&
                                  !busy
                              ? _save
                              : null,
                          child: Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              const Icon(fluent.FluentIcons.save, size: 16),
                              const SizedBox(width: 8),
                              Text(busy ? '处理中…' : '保存'),
                            ],
                          ),
                        ),
                      ),
                    ],
                  ),
              ],
            ),
            const SizedBox(height: 16),
            if (points.isEmpty)
              const fluent.InfoBar(
                title: Text('暂无可用曲线'),
                content: Text('曲线至少需要两个温度递增、转速非递减的控制点。'),
              )
            else
              fluent.Card(
                padding: const EdgeInsets.all(16),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Wrap(
                      spacing: 16,
                      runSpacing: 8,
                      children: [
                        if (temperature > 0)
                          Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              const Icon(
                                fluent.FluentIcons.snowflake,
                                size: 16,
                              ),
                              const SizedBox(width: 6),
                              Text('控温 ${temperature.toStringAsFixed(0)}°C'),
                            ],
                          ),
                        if (targetRpm is num)
                          Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              const Icon(
                                fluent.FluentIcons.speed_high,
                                size: 16,
                              ),
                              const SizedBox(width: 6),
                              Text('目标 ${targetRpm.toInt()} RPM'),
                            ],
                          ),
                      ],
                    ),
                    const SizedBox(height: 12),
                    Semantics(
                      label: '风扇曲线，${points.length} 个可上下拖动的控制点',
                      child: SizedBox(
                        height: 320,
                        width: double.infinity,
                        child: _FanCurveChart(
                          key: const ValueKey('fan-curve-chart'),
                          points: points,
                          learnedPoints: learnedPoints,
                          currentTemperature: temperature,
                          curveColor: theme.accentColor.defaultBrushFor(
                            theme.brightness,
                          ),
                          learnedColor: fluent.Colors.purple.defaultBrushFor(
                            theme.brightness,
                          ),
                          markerColor: theme.resources.systemFillColorCaution,
                          gridColor: theme.resources.dividerStrokeColorDefault,
                          labelColor: theme.resources.textFillColorSecondary,
                          editable: !busy,
                          onDragStart: _startDrag,
                          onDragUpdate: _dragBy,
                          onDragEnd: _endDrag,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            const SizedBox(height: 16),
            _AutoControlCard(
              enabled: autoControl,
              busy: widget.controller.updatingAutoControl,
              controlsEnabled:
                  controlsEnabled && widget.controller.deviceConnected,
              onChanged: _setAutoControl,
            ),
            if (!autoControl &&
                controlsEnabled &&
                widget.controller.deviceConnected) ...[
              const SizedBox(height: 16),
              _ManualGearCard(
                presets: manualPresets,
                selectedGear: manualGear,
                selectedLevel: manualLevel,
                busy: busy,
                onGearChanged: (gear) {
                  final remembered = _stringMap(
                    config['manualGearLevels'],
                  )?[gear]?.toString();
                  final nextLevel = deviceModel == 'BS1'
                      ? '中'
                      : const {'低', '中', '高'}.contains(remembered)
                      ? remembered!
                      : manualLevel;
                  _setManualGear(gear, nextLevel);
                },
                onLevelChanged: (level) => _setManualGear(manualGear, level),
                onCustomize: deviceModel == 'BS1'
                    ? null
                    : () => _editManualGearRpm(
                        config['manualGearRpm'],
                        manualGear,
                        manualLevel,
                      ),
              ),
            ],
            const SizedBox(height: 16),
            _LearningCard(
              config: smartControl,
              points: points,
              hasLaptopFan:
                  (widget.controller.temperature?['cpuFanRpm'] as num? ?? 0) >
                      0 ||
                  (widget.controller.temperature?['gpuFanRpm'] as num? ?? 0) >
                      0,
              busy: widget.controller.updatingFanFeatures,
              controlsEnabled: controlsEnabled,
              targetTemp:
                  targetTempDraft ??
                  (smartControl['targetTemp'] as int).toDouble(),
              onUpdate: (patch) => _updateSmartControl(smartControl, patch),
              onTargetChanged: (value) =>
                  setState(() => targetTempDraft = value),
              onTargetCommitted: (value) async {
                await _updateSmartControl(smartControl, {
                  'targetTemp': value.round().clamp(45, 90),
                });
                if (mounted) setState(() => targetTempDraft = null);
              },
              onReset: _resetLearnedOffsets,
            ),
            const SizedBox(height: 16),
            _SpeedAvoidanceCard(
              config: speedAvoidance,
              revealed: avoidanceRevealed,
              busy: widget.controller.updatingFanFeatures,
              controlsEnabled: controlsEnabled,
              autoControl: autoControl,
              targetRpm: targetRpm is num ? targetRpm.toInt() : null,
              onReveal: _revealAvoidance,
              onUpdate: (patch) => _updateSpeedAvoidance(speedAvoidance, patch),
            ),
            const SizedBox(height: 16),
            _ScheduleCard(
              config: schedule,
              profiles: profiles,
              currentRule: currentScheduleRule,
              busy: widget.controller.updatingFanFeatures,
              controlsEnabled: controlsEnabled,
              onEnabledChanged: (enabled) => _updateSchedule(
                schedule,
                profiles,
                activeProfileId,
                {'enabled': enabled},
              ),
              onAdd: () =>
                  _addScheduleRule(schedule, profiles, activeProfileId),
              onRuleChanged: (id, patch) => _updateScheduleRule(
                schedule,
                profiles,
                activeProfileId,
                id,
                patch,
              ),
              onRuleDeleted: (id) =>
                  _deleteScheduleRule(schedule, profiles, activeProfileId, id),
            ),
            const SizedBox(height: 16),
            _TemperatureHistoryCard(
              snapshot: widget.controller.temperatureHistory,
              busy: widget.controller.updatingTemperatureHistory,
              controlsEnabled:
                  widget.controller.connection == CoreConnection.connected,
              onEnabledChanged: _setTemperatureHistoryEnabled,
              onRetentionChanged: _setTemperatureHistoryRetentionHours,
            ),
          ],
        );
      },
    );
  }
}

class _CurveFeatureCard extends StatelessWidget {
  const _CurveFeatureCard({
    required this.icon,
    required this.title,
    required this.description,
    required this.trailing,
    this.child,
    this.childPadding = const EdgeInsets.all(16),
  });

  final IconData icon;
  final String title;
  final String description;
  final Widget trailing;
  final Widget? child;
  final EdgeInsetsGeometry childPadding;

  @override
  Widget build(BuildContext context) {
    final theme = fluent.FluentTheme.of(context);
    final accent = theme.accentColor.defaultBrushFor(theme.brightness);
    return fluent.Card(
      padding: EdgeInsets.zero,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.all(16),
            child: Row(
              children: [
                Container(
                  width: 36,
                  height: 36,
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    color: accent.withAlpha(24),
                    borderRadius: BorderRadius.circular(6),
                  ),
                  child: Icon(icon, size: 18, color: accent),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(title, style: theme.typography.bodyStrong),
                      const SizedBox(height: 2),
                      Text(description, style: theme.typography.caption),
                    ],
                  ),
                ),
                const SizedBox(width: 12),
                trailing,
              ],
            ),
          ),
          if (child != null) ...[
            const fluent.Divider(),
            Padding(padding: childPadding, child: child),
          ],
        ],
      ),
    );
  }
}

class _AutoControlCard extends StatelessWidget {
  const _AutoControlCard({
    required this.enabled,
    required this.busy,
    required this.controlsEnabled,
    required this.onChanged,
  });

  final bool enabled;
  final bool busy;
  final bool controlsEnabled;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) => _CurveFeatureCard(
    icon: fluent.FluentIcons.snowflake,
    title: '智能控温',
    description: busy
        ? '正在切换控制模式…'
        : enabled
        ? 'Core 正按当前风扇曲线自动调整转速。'
        : '当前使用手动挡位；曲线设置仍会保留。',
    trailing: fluent.ToggleSwitch(
      key: const ValueKey('fan-curve-auto-control'),
      checked: enabled,
      semanticLabel: '智能控温',
      onChanged: controlsEnabled && !busy ? onChanged : null,
    ),
  );
}

const _settingRowsPadding = EdgeInsets.symmetric(horizontal: 16);
const _settingContentPadding = EdgeInsets.fromLTRB(16, 0, 16, 16);
const _settingControlWidth = 180.0;
const _comboBoxIconSize = 12.0;

class _ManualGearCard extends StatelessWidget {
  const _ManualGearCard({
    required this.presets,
    required this.selectedGear,
    required this.selectedLevel,
    required this.busy,
    required this.onGearChanged,
    required this.onLevelChanged,
    required this.onCustomize,
  });

  final List<ManualGearPreset> presets;
  final String selectedGear;
  final String selectedLevel;
  final bool busy;
  final ValueChanged<String> onGearChanged;
  final ValueChanged<String> onLevelChanged;
  final VoidCallback? onCustomize;

  @override
  Widget build(BuildContext context) {
    final selected = presets.firstWhere(
      (preset) => preset.gear == selectedGear,
      orElse: () => presets[1],
    );
    final level = selected.levels.firstWhere(
      (item) => item.level == selectedLevel,
      orElse: () => selected.levels.first,
    );
    return _CurveFeatureCard(
      icon: fluent.FluentIcons.speed_high,
      title: '手动挡位',
      description: '${selected.gear} · ${level.level} · ${level.rpm} RPM',
      trailing: onCustomize == null
          ? const SizedBox.shrink()
          : fluent.Button(
              onPressed: busy ? null : onCustomize,
              child: const Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(fluent.FluentIcons.edit, size: 16),
                  SizedBox(width: 8),
                  Text('自定义转速'),
                ],
              ),
            ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              for (final preset in presets)
                SizedBox(
                  width: 132,
                  child: fluent.ToggleButton(
                    checked: preset.gear == selected.gear,
                    semanticLabel: '${preset.gear}挡位',
                    onChanged: busy ? null : (_) => onGearChanged(preset.gear),
                    child: Center(
                      child: Column(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Text(preset.gear),
                          const SizedBox(height: 2),
                          Text(
                            preset.levels.length == 1
                                ? '${preset.levels.first.rpm} RPM'
                                : '${preset.levels.first.rpm}–${preset.levels.last.rpm} RPM',
                            style: fluent.FluentTheme.of(
                              context,
                            ).typography.caption,
                          ),
                        ],
                      ),
                    ),
                  ),
                ),
            ],
          ),
          if (selected.levels.length > 1) ...[
            const SizedBox(height: 16),
            Row(
              children: [
                const Text('小挡位'),
                const SizedBox(width: 16),
                for (final item in selected.levels) ...[
                  fluent.ToggleButton(
                    checked: item.level == level.level,
                    semanticLabel: '${item.level}挡 ${item.rpm} RPM',
                    onChanged: busy ? null : (_) => onLevelChanged(item.level),
                    child: Text('${item.level} · ${item.rpm} RPM'),
                  ),
                  const SizedBox(width: 8),
                ],
              ],
            ),
          ],
        ],
      ),
    );
  }
}

class _ManualGearRpmDialog extends StatefulWidget {
  const _ManualGearRpmDialog({required this.initial});

  final Object? initial;

  @override
  State<_ManualGearRpmDialog> createState() => _ManualGearRpmDialogState();
}

class _ManualGearRpmDialogState extends State<_ManualGearRpmDialog> {
  late Map<String, dynamic> values;

  @override
  void initState() {
    super.initState();
    values = normalizeManualGearRpmMap(widget.initial);
  }

  @override
  Widget build(BuildContext context) => fluent.ContentDialog(
    title: const Text('自定义挡位转速'),
    content: SizedBox(
      width: 600,
      height: 390,
      child: SingleChildScrollView(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            const fluent.InfoBar(
              title: Text('转速顺序'),
              content: Text('保存时会将 12 个挡位限制在 800–4500 RPM，并保证从低到高不递减。'),
            ),
            const SizedBox(height: 12),
            for (final preset in manualGearPresets) ...[
              Text(
                preset.gear,
                style: fluent.FluentTheme.of(context).typography.bodyStrong,
              ),
              const SizedBox(height: 6),
              Row(
                children: [
                  for (final item in preset.levels) ...[
                    Expanded(
                      child: fluent.InfoLabel(
                        label: item.level,
                        child: Row(
                          children: [
                            Expanded(
                              child: fluent.NumberBox<int>(
                                value:
                                    (_stringMap(
                                          values[preset.gear],
                                        )?[item.level]
                                        as int),
                                min: 800,
                                max: 4500,
                                smallChange: 50,
                                largeChange: 200,
                                clearButton: false,
                                onChanged: (value) {
                                  if (value == null) return;
                                  setState(() {
                                    values[preset.gear] = {
                                      ...?_stringMap(values[preset.gear]),
                                      item.level: value,
                                    };
                                  });
                                },
                              ),
                            ),
                            const SizedBox(width: 6),
                            const Text('RPM'),
                          ],
                        ),
                      ),
                    ),
                    const SizedBox(width: 12),
                  ],
                ],
              ),
              const SizedBox(height: 16),
            ],
          ],
        ),
      ),
    ),
    actions: [
      fluent.Button(
        onPressed: () => setState(() {
          values = normalizeManualGearRpmMap(null);
        }),
        child: const Text('恢复默认'),
      ),
      fluent.FilledButton(
        onPressed: () =>
            Navigator.pop(context, normalizeManualGearRpmMap(values)),
        child: const Text('保存'),
      ),
      fluent.Button(
        onPressed: () => Navigator.pop(context),
        child: const Text('取消'),
      ),
    ],
  );
}

class _SettingRow extends StatelessWidget {
  const _SettingRow({
    required this.title,
    required this.description,
    required this.trailing,
  });

  final String title;
  final String description;
  final Widget trailing;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 8),
    child: Row(
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(title),
              const SizedBox(height: 2),
              Text(
                description,
                style: fluent.FluentTheme.of(context).typography.caption,
              ),
            ],
          ),
        ),
        const SizedBox(width: 16),
        trailing,
      ],
    ),
  );
}

class _LearningCard extends StatelessWidget {
  const _LearningCard({
    required this.config,
    required this.points,
    required this.hasLaptopFan,
    required this.busy,
    required this.controlsEnabled,
    required this.targetTemp,
    required this.onUpdate,
    required this.onTargetChanged,
    required this.onTargetCommitted,
    required this.onReset,
  });

  final Map<String, dynamic> config;
  final List<FanCurvePoint> points;
  final bool hasLaptopFan;
  final bool busy;
  final bool controlsEnabled;
  final double targetTemp;
  final ValueChanged<Map<String, dynamic>> onUpdate;
  final ValueChanged<double> onTargetChanged;
  final ValueChanged<double> onTargetCommitted;
  final VoidCallback onReset;

  @override
  Widget build(BuildContext context) {
    final learning = config['learning'] == true;
    final bias = config['learningBias'] as String;
    final offsets = summarizeLearnedOffsets(
      points,
      config['learnedOffsets'],
      bias,
    );
    final enabled = controlsEnabled && !busy;
    final biasDescription = switch (bias) {
      'cooling' => '只允许学习增加转速，避免待机温度被学高。',
      'quiet' => '只允许学习降低转速，避免自动把风扇拉高。',
      _ => '允许学习曲线在基础曲线上下微调。',
    };
    return _CurveFeatureCard(
      icon: fluent.FluentIcons.machine_learning,
      title: '自适应学习',
      description: '长期运行后微调各温度点转速，使稳态温度更接近目标。',
      trailing: fluent.ToggleSwitch(
        key: const ValueKey('fan-curve-learning'),
        checked: learning,
        semanticLabel: '自适应学习',
        onChanged: enabled ? (value) => onUpdate({'learning': value}) : null,
      ),
      childPadding: _settingContentPadding,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _SettingRow(
            title: '提前升速',
            description: '功耗突增或温度上升时按预计温度提前升速，只提前升速、不提前降速。',
            trailing: fluent.ToggleSwitch(
              checked: config['predictiveBoost'] == true,
              semanticLabel: '提前升速',
              onChanged: enabled && learning
                  ? (value) => onUpdate({'predictiveBoost': value})
                  : null,
            ),
          ),
          const fluent.Divider(),
          _SettingRow(
            title: '过滤瞬时尖峰',
            description: '忽略孤立温度尖峰，避免短暂读数让风扇频繁升降。',
            trailing: fluent.ToggleSwitch(
              checked: config['filterTransientSpike'] != false,
              semanticLabel: '过滤瞬时温度尖峰',
              onChanged: enabled
                  ? (value) => onUpdate({'filterTransientSpike': value})
                  : null,
            ),
          ),
          if (hasLaptopFan) ...[
            const fluent.Divider(),
            _SettingRow(
              title: '笔记本风扇高转时缓慢降速',
              description: '本机风扇仍接近高转速时限制散热器降速，减少温度和转速来回摆动。',
              trailing: fluent.ToggleSwitch(
                checked: config['laptopFanGuard'] == true,
                semanticLabel: '笔记本风扇高转时缓慢降速',
                onChanged: enabled && learning
                    ? (value) => onUpdate({'laptopFanGuard': value})
                    : null,
              ),
            ),
          ],
          const fluent.Divider(),
          _SettingRow(
            title: '学习倾向',
            description: biasDescription,
            trailing: SizedBox(
              width: _settingControlWidth,
              height: 34,
              child: fluent.ComboBox<String>(
                value: bias,
                isExpanded: true,
                iconSize: _comboBoxIconSize,
                items: const [
                  fluent.ComboBoxItem(value: 'balanced', child: Text('均衡')),
                  fluent.ComboBoxItem(value: 'cooling', child: Text('散热优先')),
                  fluent.ComboBoxItem(value: 'quiet', child: Text('静音优先')),
                ],
                onChanged: enabled
                    ? (value) {
                        if (value != null) onUpdate({'learningBias': value});
                      }
                    : null,
              ),
            ),
          ),
          const fluent.Divider(),
          _SettingRow(
            title: '目标温度',
            description: '学习模式会尽量把稳态温度收敛到这个上限附近。',
            trailing: SizedBox(
              width: 300,
              child: Row(
                children: [
                  Expanded(
                    child: fluent.Slider(
                      value: targetTemp.clamp(45, 90),
                      min: 45,
                      max: 90,
                      divisions: 45,
                      label: '${targetTemp.round()}°C',
                      onChanged: enabled ? onTargetChanged : null,
                      onChangeEnd: enabled ? onTargetCommitted : null,
                    ),
                  ),
                  const SizedBox(width: 12),
                  SizedBox(
                    width: 64,
                    child: fluent.NumberBox<int>(
                      key: const ValueKey('fan-curve-target-temperature'),
                      value: targetTemp.round(),
                      min: 45,
                      max: 90,
                      mode: fluent.SpinButtonPlacementMode.none,
                      clearButton: false,
                      textAlign: TextAlign.center,
                      onChanged: enabled
                          ? (value) {
                              if (value == null) return;
                              onTargetChanged(value.toDouble());
                              onTargetCommitted(value.toDouble());
                            }
                          : null,
                    ),
                  ),
                  const SizedBox(width: 6),
                  const Text('°C'),
                ],
              ),
            ),
          ),
          const fluent.Divider(),
          _SettingRow(
            title: '学习偏移',
            description: '当前学习曲线相对基础曲线的主要 RPM 修正点。',
            trailing: fluent.Button(
              onPressed: enabled && offsets.isNotEmpty ? onReset : null,
              child: const Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(fluent.FluentIcons.reset, size: 16),
                  SizedBox(width: 8),
                  Text('重置学习'),
                ],
              ),
            ),
          ),
          if (offsets.isEmpty)
            Text(
              '暂无学习偏移',
              style: fluent.FluentTheme.of(context).typography.caption,
            )
          else
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (final offset in offsets)
                  _ValueBadge(
                    text:
                        '${offset.temperature}°C  ${offset.rpm > 0 ? '+' : ''}${offset.rpm} RPM',
                  ),
              ],
            ),
        ],
      ),
    );
  }
}

class _ValueBadge extends StatelessWidget {
  const _ValueBadge({required this.text});

  final String text;

  @override
  Widget build(BuildContext context) {
    final theme = fluent.FluentTheme.of(context);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
      decoration: BoxDecoration(
        color: theme.brightness == Brightness.dark
            ? Colors.white.withAlpha(10)
            : Colors.black.withAlpha(6),
        border: Border.all(color: theme.resources.dividerStrokeColorDefault),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Text(text, style: theme.typography.caption),
    );
  }
}

class _NumberSetting extends StatelessWidget {
  const _NumberSetting({
    required this.title,
    required this.description,
    required this.value,
    required this.min,
    required this.max,
    required this.step,
    required this.suffix,
    required this.enabled,
    required this.onChanged,
  });

  final String title;
  final String description;
  final int value;
  final int min;
  final int max;
  final int step;
  final String suffix;
  final bool enabled;
  final ValueChanged<int> onChanged;

  @override
  Widget build(BuildContext context) {
    final theme = fluent.FluentTheme.of(context);
    return Container(
      width: 210,
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        border: Border.all(color: theme.resources.dividerStrokeColorDefault),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(title, style: theme.typography.bodyStrong),
          const SizedBox(height: 4),
          SizedBox(
            height: 34,
            child: Text(description, style: theme.typography.caption),
          ),
          const SizedBox(height: 8),
          Row(
            children: [
              Expanded(
                child: fluent.NumberBox<int>(
                  value: value,
                  min: min,
                  max: max,
                  smallChange: step,
                  largeChange: step * 4,
                  clearButton: false,
                  onChanged: enabled
                      ? (value) {
                          if (value != null) onChanged(value);
                        }
                      : null,
                ),
              ),
              const SizedBox(width: 6),
              Text(suffix),
            ],
          ),
        ],
      ),
    );
  }
}

class _SpeedAvoidanceCard extends StatelessWidget {
  const _SpeedAvoidanceCard({
    required this.config,
    required this.revealed,
    required this.busy,
    required this.controlsEnabled,
    required this.autoControl,
    required this.targetRpm,
    required this.onReveal,
    required this.onUpdate,
  });

  final Map<String, dynamic> config;
  final bool revealed;
  final bool busy;
  final bool controlsEnabled;
  final bool autoControl;
  final int? targetRpm;
  final VoidCallback onReveal;
  final ValueChanged<Map<String, dynamic>> onUpdate;

  @override
  Widget build(BuildContext context) {
    final enabled = controlsEnabled && !busy;
    final minRpm = config['minRpm'] as int;
    final maxRpm = config['maxRpm'] as int;
    final active =
        config['enabled'] == true &&
        targetRpm != null &&
        targetRpm! >= minRpm &&
        targetRpm! <= maxRpm;
    return _CurveFeatureCard(
      icon: fluent.FluentIcons.warning,
      title: '避噪转速区间',
      description: '自动控温目标落入噪音敏感区间时跳到区间外，减少轴噪概率。',
      trailing: revealed
          ? fluent.ToggleSwitch(
              key: const ValueKey('fan-curve-speed-avoidance'),
              checked: config['enabled'] == true,
              semanticLabel: '避噪转速区间',
              onChanged: enabled
                  ? (value) => onUpdate({'enabled': value})
                  : null,
            )
          : fluent.Button(
              onPressed: enabled ? onReveal : null,
              child: const Text('调参'),
            ),
      child: revealed
          ? Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Wrap(
                  spacing: 10,
                  runSpacing: 10,
                  children: [
                    _NumberSetting(
                      title: '区间起点',
                      description: '噪音区间的较低转速边界。',
                      value: minRpm,
                      min: 800,
                      max: 4500,
                      step: 50,
                      suffix: 'RPM',
                      enabled: enabled,
                      onChanged: (value) => onUpdate({'minRpm': value}),
                    ),
                    _NumberSetting(
                      title: '区间终点',
                      description: '噪音区间的较高转速边界。',
                      value: maxRpm,
                      min: 800,
                      max: 4500,
                      step: 50,
                      suffix: 'RPM',
                      enabled: enabled,
                      onChanged: (value) => onUpdate({'maxRpm': value}),
                    ),
                    _NumberSetting(
                      title: '避让余量',
                      description: '额外向区间外跳开的转速余量。',
                      value: config['marginRpm'] as int,
                      min: 50,
                      max: 500,
                      step: 50,
                      suffix: 'RPM',
                      enabled: enabled,
                      onChanged: (value) => onUpdate({'marginRpm': value}),
                    ),
                    _NumberSetting(
                      title: '高温旁路',
                      description: '达到该温度时优先散热，不再避让。',
                      value: config['emergencyBypassTemp'] as int,
                      min: 60,
                      max: 95,
                      step: 1,
                      suffix: '°C',
                      enabled: enabled,
                      onChanged: (value) =>
                          onUpdate({'emergencyBypassTemp': value}),
                    ),
                  ],
                ),
                const SizedBox(height: 12),
                Wrap(
                  spacing: 8,
                  runSpacing: 8,
                  children: [
                    _ValueBadge(
                      text:
                          '避让区间 $minRpm–$maxRpm RPM，余量 ${config['marginRpm']} RPM',
                    ),
                    if (active) _ValueBadge(text: '当前目标 $targetRpm RPM 处于敏感区间'),
                    if (!autoControl) const _ValueBadge(text: '仅在智能控温模式下生效'),
                  ],
                ),
              ],
            )
          : const fluent.InfoBar(
              title: Text('高级设置'),
              content: Text('参数需要结合设备实测噪音调整；不清楚需求时请保持关闭。'),
              severity: fluent.InfoBarSeverity.warning,
            ),
    );
  }
}

typedef _ScheduleRuleChanged =
    void Function(String id, Map<String, dynamic> patch);

class _ScheduleCard extends StatelessWidget {
  const _ScheduleCard({
    required this.config,
    required this.profiles,
    required this.currentRule,
    required this.busy,
    required this.controlsEnabled,
    required this.onEnabledChanged,
    required this.onAdd,
    required this.onRuleChanged,
    required this.onRuleDeleted,
  });

  final Map<String, dynamic> config;
  final List<FanCurveProfileOption> profiles;
  final Map<String, dynamic>? currentRule;
  final bool busy;
  final bool controlsEnabled;
  final ValueChanged<bool> onEnabledChanged;
  final VoidCallback onAdd;
  final _ScheduleRuleChanged onRuleChanged;
  final ValueChanged<String> onRuleDeleted;

  @override
  Widget build(BuildContext context) {
    final enabled = controlsEnabled && !busy;
    final rules = (config['rules'] as List).cast<Map<String, dynamic>>();
    return _CurveFeatureCard(
      icon: fluent.FluentIcons.clock,
      title: '分时曲线',
      description: '按时间段自动切换曲线方案，适合白天性能、夜间静音等场景。',
      trailing: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          fluent.Button(
            key: const ValueKey('fan-curve-schedule-add'),
            onPressed: enabled && profiles.isNotEmpty ? onAdd : null,
            child: const Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(fluent.FluentIcons.add, size: 16),
                SizedBox(width: 8),
                Text('新增规则'),
              ],
            ),
          ),
          const SizedBox(width: 12),
          fluent.ToggleSwitch(
            key: const ValueKey('fan-curve-schedule-enabled'),
            checked: config['enabled'] == true,
            semanticLabel: '分时曲线',
            onChanged: enabled ? onEnabledChanged : null,
          ),
        ],
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          fluent.InfoBar(
            title: Text(
              currentRule == null
                  ? '当前时间没有命中任何规则'
                  : '当前规则：${currentRule!['name']}',
            ),
            content: const Text('分时计划只切换活动曲线方案，不会强制开启智能控温。'),
            severity: currentRule == null
                ? fluent.InfoBarSeverity.info
                : fluent.InfoBarSeverity.success,
          ),
          const SizedBox(height: 12),
          if (rules.isEmpty)
            Text(
              '还没有分时规则，点击右上角新增。',
              style: fluent.FluentTheme.of(context).typography.caption,
            )
          else
            for (var index = 0; index < rules.length; index++) ...[
              _ScheduleRuleCard(
                key: ValueKey(rules[index]['id']),
                rule: rules[index],
                profiles: profiles,
                busy: !enabled,
                onChanged: onRuleChanged,
                onDeleted: onRuleDeleted,
              ),
              if (index != rules.length - 1) const SizedBox(height: 10),
            ],
        ],
      ),
    );
  }
}

class _ScheduleRuleCard extends StatefulWidget {
  const _ScheduleRuleCard({
    required this.rule,
    required this.profiles,
    required this.busy,
    required this.onChanged,
    required this.onDeleted,
    super.key,
  });

  final Map<String, dynamic> rule;
  final List<FanCurveProfileOption> profiles;
  final bool busy;
  final _ScheduleRuleChanged onChanged;
  final ValueChanged<String> onDeleted;

  @override
  State<_ScheduleRuleCard> createState() => _ScheduleRuleCardState();
}

class _ScheduleRuleCardState extends State<_ScheduleRuleCard> {
  late final TextEditingController nameController;
  late final FocusNode nameFocusNode;

  String get id => widget.rule['id'] as String;

  @override
  void initState() {
    super.initState();
    nameController = TextEditingController(text: widget.rule['name'] as String);
    nameFocusNode = FocusNode();
  }

  @override
  void didUpdateWidget(_ScheduleRuleCard oldWidget) {
    super.didUpdateWidget(oldWidget);
    final name = widget.rule['name'] as String;
    if (!nameFocusNode.hasFocus && nameController.text != name) {
      nameController.text = name;
    }
  }

  @override
  void dispose() {
    nameController.dispose();
    nameFocusNode.dispose();
    super.dispose();
  }

  void _commitName() {
    final fallback = widget.rule['name'] as String;
    final value = nameController.text.trim();
    if (value.isEmpty) {
      nameController.text = fallback;
    } else if (value != fallback) {
      widget.onChanged(id, {'name': value});
    }
  }

  DateTime _time(String key) {
    final parts = (widget.rule[key] as String).split(':');
    return DateTime(2000, 1, 1, int.parse(parts[0]), int.parse(parts[1]));
  }

  String _formatTime(DateTime value) =>
      '${value.hour.toString().padLeft(2, '0')}:${value.minute.toString().padLeft(2, '0')}';

  @override
  Widget build(BuildContext context) {
    final theme = fluent.FluentTheme.of(context);
    final weekdays = (widget.rule['weekdays'] as List).cast<int>();
    const weekdayLabels = {
      1: '一',
      2: '二',
      3: '三',
      4: '四',
      5: '五',
      6: '六',
      0: '日',
    };
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        border: Border.all(color: theme.resources.dividerStrokeColorDefault),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: fluent.InfoLabel(
                  label: '规则名称',
                  child: fluent.TextBox(
                    controller: nameController,
                    focusNode: nameFocusNode,
                    enabled: !widget.busy,
                    placeholder: '例如：深夜静音',
                    textInputAction: TextInputAction.done,
                    suffix: fluent.Tooltip(
                      message: '保存名称',
                      child: fluent.IconButton(
                        icon: const Icon(fluent.FluentIcons.accept, size: 14),
                        onPressed: widget.busy ? null : _commitName,
                      ),
                    ),
                    onSubmitted: (_) => _commitName(),
                    onTapOutside: (_) => nameFocusNode.unfocus(),
                  ),
                ),
              ),
              const SizedBox(width: 12),
              fluent.ToggleSwitch(
                checked: widget.rule['enabled'] == true,
                semanticLabel: '启用 ${widget.rule['name']}',
                onChanged: widget.busy
                    ? null
                    : (value) => widget.onChanged(id, {'enabled': value}),
              ),
              const SizedBox(width: 8),
              fluent.IconButton(
                icon: const Icon(fluent.FluentIcons.delete),
                onPressed: widget.busy ? null : () => widget.onDeleted(id),
              ),
            ],
          ),
          const SizedBox(height: 12),
          Wrap(
            spacing: 12,
            runSpacing: 12,
            crossAxisAlignment: WrapCrossAlignment.end,
            children: [
              SizedBox(
                width: 190,
                child: fluent.InfoLabel(
                  label: '曲线方案',
                  child: fluent.ComboBox<String>(
                    value: widget.rule['curveProfileId'] as String,
                    isExpanded: true,
                    iconSize: _comboBoxIconSize,
                    items: [
                      for (final profile in widget.profiles)
                        fluent.ComboBoxItem(
                          value: profile.id,
                          child: Text(profile.name),
                        ),
                    ],
                    onChanged: widget.busy
                        ? null
                        : (value) {
                            if (value != null) {
                              widget.onChanged(id, {'curveProfileId': value});
                            }
                          },
                  ),
                ),
              ),
              fluent.TimePicker(
                selected: _time('startTime'),
                header: '开始时间',
                hourFormat: fluent.HourFormat.HH,
                onChanged: widget.busy
                    ? null
                    : (value) => widget.onChanged(id, {
                        'startTime': _formatTime(value),
                      }),
              ),
              fluent.TimePicker(
                selected: _time('endTime'),
                header: '结束时间',
                hourFormat: fluent.HourFormat.HH,
                onChanged: widget.busy
                    ? null
                    : (value) =>
                          widget.onChanged(id, {'endTime': _formatTime(value)}),
              ),
            ],
          ),
          const SizedBox(height: 12),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              const Padding(
                padding: EdgeInsets.only(right: 4),
                child: Text('生效日期'),
              ),
              for (final day in _weekdaySequence)
                fluent.ToggleButton(
                  checked: weekdays.contains(day),
                  semanticLabel: '星期${weekdayLabels[day]}',
                  onChanged: widget.busy
                      ? null
                      : (_) {
                          final next = weekdays.contains(day)
                              ? weekdays.where((item) => item != day).toList()
                              : [...weekdays, day];
                          if (next.isNotEmpty) {
                            widget.onChanged(id, {
                              'weekdays': normalizeScheduleWeekdays(next),
                            });
                          }
                        },
                  child: Text(weekdayLabels[day]!),
                ),
            ],
          ),
        ],
      ),
    );
  }
}

Rect _fanCurveChartRect(Size size) =>
    Rect.fromLTRB(48, 16, size.width - 12, size.height - 32);

Offset _fanCurvePointPosition(
  Rect chart,
  List<FanCurvePoint> points,
  FanCurvePoint point,
) => Offset(
  chart.left +
      chart.width *
          (point.temperature - points.first.temperature) /
          (points.last.temperature - points.first.temperature),
  chart.bottom - chart.height * point.rpm / 4000,
);

class _FanCurveChart extends StatefulWidget {
  const _FanCurveChart({
    required this.points,
    required this.learnedPoints,
    required this.currentTemperature,
    required this.curveColor,
    required this.learnedColor,
    required this.markerColor,
    required this.gridColor,
    required this.labelColor,
    required this.editable,
    required this.onDragStart,
    required this.onDragUpdate,
    required this.onDragEnd,
    super.key,
  });

  final List<FanCurvePoint> points;
  final List<FanCurvePoint> learnedPoints;
  final double currentTemperature;
  final Color curveColor;
  final Color learnedColor;
  final Color markerColor;
  final Color gridColor;
  final Color labelColor;
  final bool editable;
  final void Function(int index) onDragStart;
  final void Function(int index, double rpmDelta) onDragUpdate;
  final VoidCallback onDragEnd;

  @override
  State<_FanCurveChart> createState() => _FanCurveChartState();
}

class _FanCurveChartState extends State<_FanCurveChart> {
  int? selectedIndex;
  Offset? pointerPosition;

  @override
  void didUpdateWidget(_FanCurveChart oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (selectedIndex != null && selectedIndex! >= widget.points.length) {
      selectedIndex = null;
    }
  }

  void _selectAt(Offset position, Rect chart) {
    int? next;
    Offset? nextPosition;
    if (chart.contains(position)) {
      nextPosition = position;
      var closestDistance = double.infinity;
      for (var index = 0; index < widget.points.length; index++) {
        final distance =
            (_fanCurvePointPosition(
                      chart,
                      widget.points,
                      widget.points[index],
                    ).dx -
                    position.dx)
                .abs();
        if (distance < closestDistance) {
          closestDistance = distance;
          next = index;
        }
      }
    }
    if (next != selectedIndex || nextPosition != pointerPosition) {
      setState(() {
        selectedIndex = next;
        pointerPosition = nextPosition;
      });
    }
  }

  void _clearSelection() {
    if (selectedIndex != null || pointerPosition != null) {
      setState(() {
        selectedIndex = null;
        pointerPosition = null;
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        final size = Size(constraints.maxWidth, constraints.maxHeight);
        final chart = _fanCurveChartRect(size);
        final handleBorder = fluent.FluentTheme.of(context).cardColor;
        final index = selectedIndex;
        final selected = index == null ? null : widget.points[index];
        const tooltipWidth = 224.0;
        const tooltipHeight = 96.0;
        final pointer = pointerPosition;
        var tooltipLeft = (pointer?.dx ?? 0) + 14;
        if (tooltipLeft + tooltipWidth > size.width) {
          tooltipLeft = (pointer?.dx ?? 0) - tooltipWidth - 14;
        }
        tooltipLeft = tooltipLeft
            .clamp(4.0, math.max(4.0, size.width - tooltipWidth - 4))
            .toDouble();
        var tooltipTop = (pointer?.dy ?? 0) + 14;
        if (tooltipTop + tooltipHeight > size.height) {
          tooltipTop = (pointer?.dy ?? 0) - tooltipHeight - 14;
        }
        tooltipTop = tooltipTop
            .clamp(4.0, math.max(4.0, size.height - tooltipHeight - 4))
            .toDouble();
        return MouseRegion(
          onHover: (event) => _selectAt(event.localPosition, chart),
          onExit: (_) => _clearSelection(),
          child: Stack(
            children: [
              RepaintBoundary(
                child: CustomPaint(
                  size: size,
                  painter: FanCurvePainter(
                    points: widget.points,
                    learnedPoints: widget.learnedPoints,
                    currentTemperature: widget.currentTemperature,
                    curveColor: widget.curveColor,
                    learnedColor: widget.learnedColor,
                    markerColor: widget.markerColor,
                    gridColor: widget.gridColor,
                    labelColor: widget.labelColor,
                    selectedIndex: selectedIndex,
                  ),
                ),
              ),
              if (chart.width > 0 && chart.height > 0)
                for (var index = 0; index < widget.points.length; index++)
                  Positioned(
                    left:
                        _fanCurvePointPosition(
                          chart,
                          widget.points,
                          widget.points[index],
                        ).dx -
                        12,
                    top:
                        _fanCurvePointPosition(
                          chart,
                          widget.points,
                          widget.points[index],
                        ).dy -
                        12,
                    width: 24,
                    height: 24,
                    child: Semantics(
                      slider: true,
                      label: '${widget.points[index].temperature}°C 控制点',
                      value: '${widget.points[index].rpm} RPM',
                      increasedValue:
                          '${(widget.points[index].rpm + 50).clamp(0, 4000)} RPM',
                      decreasedValue:
                          '${(widget.points[index].rpm - 50).clamp(0, 4000)} RPM',
                      onIncrease: widget.editable
                          ? () {
                              widget.onDragStart(index);
                              widget.onDragUpdate(index, 50);
                              widget.onDragEnd();
                            }
                          : null,
                      onDecrease: widget.editable
                          ? () {
                              widget.onDragStart(index);
                              widget.onDragUpdate(index, -50);
                              widget.onDragEnd();
                            }
                          : null,
                      child: MouseRegion(
                        cursor: widget.editable
                            ? SystemMouseCursors.resizeUpDown
                            : MouseCursor.defer,
                        child: GestureDetector(
                          key: ValueKey('fan-curve-point-$index'),
                          behavior: HitTestBehavior.opaque,
                          onVerticalDragStart: widget.editable
                              ? (_) => widget.onDragStart(index)
                              : null,
                          onVerticalDragUpdate: widget.editable
                              ? (details) => widget.onDragUpdate(
                                  index,
                                  -(details.primaryDelta ?? 0) *
                                      4000 /
                                      chart.height,
                                )
                              : null,
                          onVerticalDragEnd: widget.editable
                              ? (_) => widget.onDragEnd()
                              : null,
                          onVerticalDragCancel: widget.editable
                              ? widget.onDragEnd
                              : null,
                          child: Center(
                            child: Container(
                              width: 12,
                              height: 12,
                              decoration: BoxDecoration(
                                color: widget.curveColor,
                                shape: BoxShape.circle,
                                border: Border.all(
                                  color: handleBorder,
                                  width: 2,
                                ),
                              ),
                            ),
                          ),
                        ),
                      ),
                    ),
                  ),
              if (selected != null)
                AnimatedPositioned(
                  key: const ValueKey('fan-curve-tooltip-position'),
                  duration: const Duration(milliseconds: 90),
                  curve: Curves.easeOutCubic,
                  left: tooltipLeft,
                  top: tooltipTop,
                  width: tooltipWidth,
                  child: IgnorePointer(child: _curveTooltip(context, index!)),
                ),
            ],
          ),
        );
      },
    );
  }

  Widget _curveTooltip(BuildContext context, int index) {
    final theme = fluent.FluentTheme.of(context);
    final point = widget.points[index];
    return fluent.FlyoutContent(
      key: const ValueKey('fan-curve-tooltip'),
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            '温度：${point.temperature} °C',
            style: theme.typography.bodyStrong,
          ),
          _curveTooltipRow(
            context,
            widget.curveColor,
            '基础曲线',
            '${point.rpm} RPM',
          ),
          if (widget.learnedPoints.length == widget.points.length)
            _curveTooltipRow(
              context,
              widget.learnedColor,
              '学习曲线',
              '${widget.learnedPoints[index].rpm} RPM',
            ),
        ],
      ),
    );
  }

  Widget _curveTooltipRow(
    BuildContext context,
    Color color,
    String label,
    String value,
  ) => Padding(
    padding: const EdgeInsets.only(top: 4),
    child: Row(
      children: [
        Container(
          width: 8,
          height: 8,
          decoration: BoxDecoration(color: color, shape: BoxShape.circle),
        ),
        const SizedBox(width: 7),
        Text(label),
        const Spacer(),
        Text(
          value,
          style: fluent.FluentTheme.of(context).typography.bodyStrong,
        ),
      ],
    ),
  );
}

class FanCurvePainter extends CustomPainter {
  const FanCurvePainter({
    required this.points,
    required this.learnedPoints,
    required this.currentTemperature,
    required this.curveColor,
    required this.learnedColor,
    required this.markerColor,
    required this.gridColor,
    required this.labelColor,
    required this.selectedIndex,
  });

  final List<FanCurvePoint> points;
  final List<FanCurvePoint> learnedPoints;
  final double currentTemperature;
  final Color curveColor;
  final Color learnedColor;
  final Color markerColor;
  final Color gridColor;
  final Color labelColor;
  final int? selectedIndex;

  @override
  void paint(Canvas canvas, Size size) {
    final chart = _fanCurveChartRect(size);
    if (chart.width <= 0 || chart.height <= 0 || points.length < 2) return;

    final grid = Paint()
      ..color = gridColor
      ..strokeWidth = 1;
    for (var index = 0; index <= 4; index++) {
      final fraction = index / 4;
      final x = chart.left + chart.width * fraction;
      final y = chart.top + chart.height * fraction;
      canvas.drawLine(Offset(x, chart.top), Offset(x, chart.bottom), grid);
      canvas.drawLine(Offset(chart.left, y), Offset(chart.right, y), grid);
      _paintChartLabel(
        canvas,
        '${(4000 * (1 - fraction)).round()}',
        Offset(chart.left - 8, y),
        labelColor,
        alignRight: true,
        centerVertically: true,
      );
      final temperature =
          points.first.temperature +
          (points.last.temperature - points.first.temperature) * fraction;
      _paintChartLabel(
        canvas,
        '${temperature.round()}°',
        Offset(x, chart.bottom + 8),
        labelColor,
        centered: true,
      );
    }

    if (learnedPoints.length == points.length) {
      final learnedPath = Path();
      for (var index = 0; index < learnedPoints.length; index++) {
        final offset = _fanCurvePointPosition(
          chart,
          learnedPoints,
          learnedPoints[index],
        );
        if (index == 0) {
          learnedPath.moveTo(offset.dx, offset.dy);
        } else {
          learnedPath.lineTo(offset.dx, offset.dy);
        }
      }
      _drawDashedPath(
        canvas,
        learnedPath,
        Paint()
          ..color = learnedColor
          ..strokeWidth = 2
          ..style = PaintingStyle.stroke,
      );
    }

    final path = Path();
    for (var index = 0; index < points.length; index++) {
      final offset = _fanCurvePointPosition(chart, points, points[index]);
      if (index == 0) {
        path.moveTo(offset.dx, offset.dy);
      } else {
        path.lineTo(offset.dx, offset.dy);
      }
    }
    canvas.drawPath(
      path,
      Paint()
        ..color = curveColor
        ..strokeWidth = 3
        ..style = PaintingStyle.stroke,
    );
    final pointPaint = Paint()..color = curveColor;
    for (final point in points) {
      canvas.drawCircle(
        _fanCurvePointPosition(chart, points, point),
        4,
        pointPaint,
      );
    }

    final activeIndex = selectedIndex;
    if (activeIndex != null && activeIndex < points.length) {
      canvas.drawCircle(
        _fanCurvePointPosition(chart, points, points[activeIndex]),
        8,
        Paint()
          ..color = curveColor
          ..strokeWidth = 2
          ..style = PaintingStyle.stroke,
      );
    }

    if (currentTemperature >= points.first.temperature &&
        currentTemperature <= points.last.temperature) {
      final x =
          chart.left +
          chart.width *
              (currentTemperature - points.first.temperature) /
              (points.last.temperature - points.first.temperature);
      canvas.drawLine(
        Offset(x, chart.top),
        Offset(x, chart.bottom),
        Paint()
          ..color = markerColor
          ..strokeWidth = 2,
      );
    }
  }

  @override
  bool shouldRepaint(FanCurvePainter oldDelegate) =>
      !listEquals(oldDelegate.points, points) ||
      !listEquals(oldDelegate.learnedPoints, learnedPoints) ||
      oldDelegate.currentTemperature != currentTemperature ||
      oldDelegate.curveColor != curveColor ||
      oldDelegate.learnedColor != learnedColor ||
      oldDelegate.markerColor != markerColor ||
      oldDelegate.gridColor != gridColor ||
      oldDelegate.labelColor != labelColor ||
      oldDelegate.selectedIndex != selectedIndex;
}

void _drawDashedPath(Canvas canvas, Path path, Paint paint) {
  for (final metric in path.computeMetrics()) {
    for (var distance = 0.0; distance < metric.length; distance += 10) {
      canvas.drawPath(
        metric.extractPath(distance, math.min(distance + 6, metric.length)),
        paint,
      );
    }
  }
}

class _TemperatureHistoryCard extends StatefulWidget {
  const _TemperatureHistoryCard({
    required this.snapshot,
    required this.busy,
    required this.controlsEnabled,
    required this.onEnabledChanged,
    required this.onRetentionChanged,
  });

  final TemperatureHistorySnapshot snapshot;
  final bool busy;
  final bool controlsEnabled;
  final ValueChanged<bool> onEnabledChanged;
  final ValueChanged<int> onRetentionChanged;

  @override
  State<_TemperatureHistoryCard> createState() =>
      _TemperatureHistoryCardState();
}

class _TemperatureHistoryCardState extends State<_TemperatureHistoryCard> {
  static const drawPointLimit = 1500;

  late List<TemperatureHistoryPoint> points;
  late int gapThreshold;

  @override
  void initState() {
    super.initState();
    _syncPoints();
  }

  @override
  void didUpdateWidget(_TemperatureHistoryCard oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!identical(oldWidget.snapshot.points, widget.snapshot.points) ||
        oldWidget.snapshot.sampleIntervalSeconds !=
            widget.snapshot.sampleIntervalSeconds) {
      _syncPoints();
    }
  }

  void _syncPoints() {
    points = downsampleTemperatureHistory(
      widget.snapshot.points,
      drawPointLimit,
      widget.snapshot.sampleIntervalSeconds,
    );
    gapThreshold = temperatureHistoryGapThreshold(
      points,
      widget.snapshot.sampleIntervalSeconds,
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = fluent.FluentTheme.of(context);
    final dark = theme.brightness == Brightness.dark;
    final cpuColor = dark ? const Color(0xffffb74d) : const Color(0xffe65100);
    final gpuColor = dark ? const Color(0xff64b5f6) : const Color(0xff1565c0);
    final fanColor = dark ? const Color(0xff81c784) : const Color(0xff2e7d32);
    final cpuPowerColor = dark
        ? const Color(0xffb39ddb)
        : const Color(0xff6a1b9a);
    final gpuPowerColor = dark
        ? const Color(0xfff48fb1)
        : const Color(0xffad1457);
    final hasPower = points.any(
      (point) => point.cpuPower > 0 || point.gpuPower > 0,
    );
    final snapshot = widget.snapshot;
    final retentionOptions = {
      1,
      2,
      3,
      6,
      12,
      24,
      snapshot.retentionHours,
    }.toList()..sort();
    final controlsEnabled = widget.controlsEnabled && !widget.busy;
    return fluent.Card(
      key: const ValueKey('temperature-history-card'),
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(fluent.FluentIcons.history),
              const SizedBox(width: 8),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text('硬件历史', style: theme.typography.subtitle),
                    Text(
                      snapshot.enabled
                          ? '${snapshot.points.length} 个采样 · 后台保留 ${snapshot.retentionHours} 小时'
                          : '后台记录已关闭',
                      style: theme.typography.caption,
                    ),
                  ],
                ),
              ),
              if (widget.busy) ...[
                const SizedBox(
                  width: 18,
                  height: 18,
                  child: fluent.ProgressRing(strokeWidth: 2),
                ),
                const SizedBox(width: 8),
              ],
              const Text('后台记录'),
              const SizedBox(width: 8),
              fluent.ToggleSwitch(
                key: const ValueKey('temperature-history-enabled'),
                checked: snapshot.enabled,
                semanticLabel: '后台温度记录',
                onChanged: controlsEnabled ? widget.onEnabledChanged : null,
              ),
            ],
          ),
          const SizedBox(height: 12),
          Wrap(
            alignment: WrapAlignment.spaceBetween,
            spacing: 24,
            runSpacing: 8,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              Wrap(
                spacing: 12,
                runSpacing: 8,
                children: [
                  _historyLegend(cpuColor, 'CPU 温度'),
                  _historyLegend(gpuColor, 'GPU 温度'),
                  _historyLegend(fanColor, '散热器转速'),
                  if (hasPower) _historyLegend(cpuPowerColor, 'CPU 功耗'),
                  if (hasPower) _historyLegend(gpuPowerColor, 'GPU 功耗'),
                ],
              ),
              Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  const Text('保留'),
                  const SizedBox(width: 6),
                  SizedBox(
                    width: 96,
                    child: fluent.ComboBox<int>(
                      key: const ValueKey('temperature-history-retention'),
                      value: snapshot.retentionHours,
                      isExpanded: true,
                      iconSize: _comboBoxIconSize,
                      items: [
                        for (final hours in retentionOptions)
                          fluent.ComboBoxItem(
                            value: hours,
                            child: Text('$hours 小时'),
                          ),
                      ],
                      onChanged: controlsEnabled
                          ? (hours) {
                              if (hours != null) {
                                widget.onRetentionChanged(hours);
                              }
                            }
                          : null,
                    ),
                  ),
                ],
              ),
            ],
          ),
          const SizedBox(height: 12),
          if (points.length < 2)
            const SizedBox(
              height: 160,
              child: Center(child: Text('等待 Core 记录更多硬件采样')),
            )
          else
            Semantics(
              label:
                  '硬件历史图，${snapshot.points.length} 个采样，包含 CPU、GPU 温度、功耗和散热器转速',
              child: _TemperatureHistoryChart(
                key: const ValueKey('temperature-history-chart'),
                points: points,
                gapThreshold: gapThreshold,
                cpuColor: cpuColor,
                gpuColor: gpuColor,
                fanColor: fanColor,
                cpuPowerColor: cpuPowerColor,
                gpuPowerColor: gpuPowerColor,
                gridColor: theme.resources.dividerStrokeColorDefault,
                labelColor: theme.resources.textFillColorSecondary,
              ),
            ),
        ],
      ),
    );
  }

  Widget _historyLegend(Color color, String label) => Row(
    mainAxisSize: MainAxisSize.min,
    children: [
      Container(
        width: 10,
        height: 3,
        decoration: BoxDecoration(
          color: color,
          borderRadius: BorderRadius.circular(2),
        ),
      ),
      const SizedBox(width: 5),
      Text(label),
    ],
  );
}

Rect _temperatureHistoryChartRect(Size size) =>
    Rect.fromLTRB(48, 12, size.width - 52, size.height - 30);

class _TemperatureHistoryChart extends StatefulWidget {
  const _TemperatureHistoryChart({
    required this.points,
    required this.gapThreshold,
    required this.cpuColor,
    required this.gpuColor,
    required this.fanColor,
    required this.cpuPowerColor,
    required this.gpuPowerColor,
    required this.gridColor,
    required this.labelColor,
    super.key,
  });

  final List<TemperatureHistoryPoint> points;
  final int gapThreshold;
  final Color cpuColor;
  final Color gpuColor;
  final Color fanColor;
  final Color cpuPowerColor;
  final Color gpuPowerColor;
  final Color gridColor;
  final Color labelColor;

  @override
  State<_TemperatureHistoryChart> createState() =>
      _TemperatureHistoryChartState();
}

class _TemperatureHistoryChartState extends State<_TemperatureHistoryChart> {
  int? selectedIndex;
  Offset? pointerPosition;
  ({int start, int end})? zoomDomain;
  List<TemperatureHistoryPoint>? zoomPoints;
  double? dragStartX;
  double? dragCurrentX;

  List<TemperatureHistoryPoint> get _displayPoints =>
      zoomPoints ?? widget.points;

  @override
  void didUpdateWidget(_TemperatureHistoryChart oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!identical(oldWidget.points, widget.points)) {
      final domain = zoomDomain;
      if (domain != null) {
        final points = [
          for (final point in widget.points)
            if (point.timestamp >= domain.start &&
                point.timestamp <= domain.end)
              point,
        ];
        if (points.length >= 2) {
          zoomPoints = List.unmodifiable(points);
        } else {
          zoomDomain = null;
          zoomPoints = null;
        }
      }
      if (selectedIndex != null && selectedIndex! >= _displayPoints.length) {
        selectedIndex = null;
        pointerPosition = null;
      }
    }
  }

  int _pointIndexAt(
    double x,
    Rect chart,
    List<TemperatureHistoryPoint> points,
  ) {
    final first = points.first.timestamp;
    final span = math.max(1, points.last.timestamp - first);
    final target = first + span * (x - chart.left) / chart.width;
    var low = 0;
    var high = points.length - 1;
    while (low < high) {
      final middle = (low + high) ~/ 2;
      if (points[middle].timestamp < target) {
        low = middle + 1;
      } else {
        high = middle;
      }
    }
    if (low > 0 &&
        (points[low - 1].timestamp - target).abs() <
            (points[low].timestamp - target).abs()) {
      return low - 1;
    }
    return low;
  }

  void _selectAt(
    Offset position,
    Size size,
    List<TemperatureHistoryPoint> points,
  ) {
    if (dragStartX != null) return;
    final chart = _temperatureHistoryChartRect(size);
    int? next;
    Offset? nextPosition;
    if (chart.contains(position)) {
      next = _pointIndexAt(position.dx, chart, points);
      nextPosition = position;
    }
    if (next != selectedIndex || nextPosition != pointerPosition) {
      setState(() {
        selectedIndex = next;
        pointerPosition = nextPosition;
      });
    }
  }

  void _clearSelection() {
    if (selectedIndex != null || pointerPosition != null) {
      setState(() {
        selectedIndex = null;
        pointerPosition = null;
      });
    }
  }

  void _startZoom(Offset position, Rect chart) {
    if (!chart.contains(position)) return;
    setState(() {
      dragStartX = position.dx;
      dragCurrentX = position.dx;
      selectedIndex = null;
      pointerPosition = null;
    });
  }

  void _updateZoom(double x, Rect chart) {
    if (dragStartX == null) return;
    setState(() {
      dragCurrentX = x.clamp(chart.left, chart.right).toDouble();
    });
  }

  void _cancelZoomSelection() {
    if (dragStartX == null && dragCurrentX == null) return;
    setState(() {
      dragStartX = null;
      dragCurrentX = null;
    });
  }

  void _finishZoom(Rect chart, List<TemperatureHistoryPoint> points) {
    final startX = dragStartX;
    final endX = dragCurrentX;
    if (startX == null || endX == null) return;
    if ((endX - startX).abs() < 12) {
      _cancelZoomSelection();
      return;
    }
    final firstIndex = _pointIndexAt(startX, chart, points);
    final lastIndex = _pointIndexAt(endX, chart, points);
    if (firstIndex == lastIndex) {
      _cancelZoomSelection();
      return;
    }
    final start = math.min(firstIndex, lastIndex);
    final end = math.max(firstIndex, lastIndex);
    setState(() {
      dragStartX = null;
      dragCurrentX = null;
      selectedIndex = null;
      if (start > 0 || end < points.length - 1) {
        zoomDomain = (
          start: points[start].timestamp,
          end: points[end].timestamp,
        );
        zoomPoints = List.unmodifiable(points.sublist(start, end + 1));
      }
    });
  }

  void _resetZoom() {
    if (zoomDomain == null) return;
    setState(() {
      zoomDomain = null;
      zoomPoints = null;
      selectedIndex = null;
      pointerPosition = null;
    });
  }

  @override
  Widget build(BuildContext context) {
    final hasPower = widget.points.any(
      (point) => point.cpuPower > 0 || point.gpuPower > 0,
    );
    return MouseRegion(
      onExit: (_) => _clearSelection(),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            '温度与转速趋势',
            style: fluent.FluentTheme.of(context).typography.caption,
          ),
          const SizedBox(height: 4),
          _buildPlot(context, power: false),
          if (hasPower) ...[
            const SizedBox(height: 12),
            const fluent.Divider(),
            const SizedBox(height: 12),
            Text(
              '功耗趋势',
              style: fluent.FluentTheme.of(context).typography.caption,
            ),
            const SizedBox(height: 4),
            _buildPlot(context, power: true),
          ],
        ],
      ),
    );
  }

  Widget _buildPlot(BuildContext context, {required bool power}) => SizedBox(
    height: power ? 200 : 260,
    child: LayoutBuilder(
      builder: (context, constraints) {
        final size = Size(constraints.maxWidth, constraints.maxHeight);
        final theme = fluent.FluentTheme.of(context);
        final accent = theme.accentColor.defaultBrushFor(theme.brightness);
        final points = _displayPoints;
        final index = selectedIndex;
        final selected = index == null ? null : points[index];
        final chart = _temperatureHistoryChartRect(size);
        final dragStart = dragStartX;
        final dragEnd = dragCurrentX;
        const tooltipWidth = 208.0;
        final tooltipHeight = power ? 88.0 : 112.0;
        final pointer = pointerPosition;
        var tooltipLeft = (pointer?.dx ?? 0) + 14;
        if (tooltipLeft + tooltipWidth > size.width) {
          tooltipLeft = (pointer?.dx ?? 0) - tooltipWidth - 14;
        }
        tooltipLeft = tooltipLeft
            .clamp(4.0, math.max(4.0, size.width - tooltipWidth - 4))
            .toDouble();
        var tooltipTop = (pointer?.dy ?? 0) + 14;
        if (tooltipTop + tooltipHeight > size.height) {
          tooltipTop = (pointer?.dy ?? 0) - tooltipHeight - 14;
        }
        tooltipTop = tooltipTop
            .clamp(4.0, math.max(4.0, size.height - tooltipHeight - 4))
            .toDouble();
        return MouseRegion(
          key: ValueKey(
            power
                ? 'temperature-history-power-chart'
                : 'temperature-history-temperature-chart',
          ),
          cursor: SystemMouseCursors.precise,
          onHover: (event) => _selectAt(event.localPosition, size, points),
          child: Stack(
            children: [
              GestureDetector(
                behavior: HitTestBehavior.opaque,
                onTapDown: (details) =>
                    _selectAt(details.localPosition, size, points),
                onHorizontalDragStart: (details) =>
                    _startZoom(details.localPosition, chart),
                onHorizontalDragUpdate: (details) =>
                    _updateZoom(details.localPosition.dx, chart),
                onHorizontalDragEnd: (_) => _finishZoom(chart, points),
                onHorizontalDragCancel: _cancelZoomSelection,
                onDoubleTap: _resetZoom,
                child: Stack(
                  children: [
                    RepaintBoundary(
                      child: CustomPaint(
                        key: ValueKey(
                          power
                              ? 'temperature-history-power-paint'
                              : 'temperature-history-paint',
                        ),
                        size: size,
                        painter: _TemperatureHistoryPainter(
                          points: points,
                          gapThreshold: widget.gapThreshold,
                          kind: power
                              ? _HistoryPlotKind.power
                              : _HistoryPlotKind.temperature,
                          cpuColor: power
                              ? widget.cpuPowerColor
                              : widget.cpuColor,
                          gpuColor: power
                              ? widget.gpuPowerColor
                              : widget.gpuColor,
                          fanColor: widget.fanColor,
                          gridColor: widget.gridColor,
                          labelColor: widget.labelColor,
                          selectedIndex: selectedIndex,
                        ),
                      ),
                    ),
                    if (dragStart != null && dragEnd != null)
                      Positioned(
                        left: math.min(dragStart, dragEnd),
                        top: chart.top,
                        width: (dragEnd - dragStart).abs(),
                        height: chart.height,
                        child: IgnorePointer(
                          child: DecoratedBox(
                            decoration: BoxDecoration(
                              color: accent.withAlpha(38),
                              border: Border.all(color: accent.withAlpha(120)),
                            ),
                          ),
                        ),
                      ),
                    if (selected != null)
                      AnimatedPositioned(
                        key: ValueKey(
                          power
                              ? 'temperature-history-power-tooltip-position'
                              : 'temperature-history-tooltip-position',
                        ),
                        duration: const Duration(milliseconds: 90),
                        curve: Curves.easeOutCubic,
                        left: tooltipLeft,
                        top: tooltipTop,
                        width: tooltipWidth,
                        child: IgnorePointer(
                          child: _historyTooltip(context, selected, power),
                        ),
                      ),
                  ],
                ),
              ),
              if (zoomDomain != null && !power)
                Positioned(
                  top: chart.top + 4,
                  right: size.width - chart.right + 4,
                  child: fluent.Button(
                    key: const ValueKey('temperature-history-reset-zoom'),
                    onPressed: _resetZoom,
                    child: const Text('重置缩放'),
                  ),
                ),
            ],
          ),
        );
      },
    ),
  );

  Widget _historyTooltip(
    BuildContext context,
    TemperatureHistoryPoint point,
    bool power,
  ) {
    final theme = fluent.FluentTheme.of(context);
    return fluent.FlyoutContent(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            _historyDateTime(point.timestamp),
            style: theme.typography.bodyStrong,
          ),
          if (!power && point.cpuTemp > 0)
            _tooltipRow(widget.cpuColor, 'CPU', '${point.cpuTemp} °C'),
          if (!power && point.gpuTemp > 0)
            _tooltipRow(widget.gpuColor, 'GPU', '${point.gpuTemp} °C'),
          if (!power && point.fanRpm > 0)
            _tooltipRow(widget.fanColor, '散热器', '${point.fanRpm} RPM'),
          if (power && point.cpuPower > 0)
            _tooltipRow(
              widget.cpuPowerColor,
              'CPU',
              '${point.cpuPower.toStringAsFixed(1)} W',
            ),
          if (power && point.gpuPower > 0)
            _tooltipRow(
              widget.gpuPowerColor,
              'GPU',
              '${point.gpuPower.toStringAsFixed(1)} W',
            ),
        ],
      ),
    );
  }

  Widget _tooltipRow(Color color, String label, String value) => Padding(
    padding: const EdgeInsets.only(top: 4),
    child: Row(
      children: [
        Container(
          width: 8,
          height: 8,
          decoration: BoxDecoration(color: color, shape: BoxShape.circle),
        ),
        const SizedBox(width: 7),
        Text(label),
        const Spacer(),
        Text(
          value,
          style: fluent.FluentTheme.of(context).typography.bodyStrong,
        ),
      ],
    ),
  );
}

enum _HistoryPlotKind { temperature, power }

class _TemperatureHistoryPainter extends CustomPainter {
  const _TemperatureHistoryPainter({
    required this.points,
    required this.gapThreshold,
    required this.kind,
    required this.cpuColor,
    required this.gpuColor,
    required this.fanColor,
    required this.gridColor,
    required this.labelColor,
    required this.selectedIndex,
  });

  final List<TemperatureHistoryPoint> points;
  final int gapThreshold;
  final _HistoryPlotKind kind;
  final Color cpuColor;
  final Color gpuColor;
  final Color fanColor;
  final Color gridColor;
  final Color labelColor;
  final int? selectedIndex;

  @override
  void paint(Canvas canvas, Size size) {
    final chart = _temperatureHistoryChartRect(size);
    if (chart.width <= 0 || chart.height <= 0 || points.length < 2) return;
    final values = kind == _HistoryPlotKind.temperature
        ? <double>[
            for (final point in points)
              if (point.cpuTemp > 0) point.cpuTemp.toDouble(),
            for (final point in points)
              if (point.gpuTemp > 0) point.gpuTemp.toDouble(),
          ]
        : <double>[
            for (final point in points)
              if (point.cpuPower > 0) point.cpuPower,
            for (final point in points)
              if (point.gpuPower > 0) point.gpuPower,
          ];
    if (values.isEmpty) return;
    final minimum = values.reduce((left, right) => math.min(left, right));
    final maximum = values.reduce((left, right) => math.max(left, right));
    final axisMin = kind == _HistoryPlotKind.temperature
        ? math.max(0, minimum - 5).toDouble()
        : 0.0;
    var axisMax = kind == _HistoryPlotKind.temperature
        ? maximum + 5
        : math.max(20, ((maximum + 10) / 10).ceil() * 10).toDouble();
    if (axisMax - axisMin < 10) axisMax = axisMin + 10;
    final fanPeak = points.fold<int>(
      0,
      (peak, point) => math.max(peak, point.fanRpm),
    );
    final secondaryMax = kind == _HistoryPlotKind.temperature
        ? math.max(1000, ((fanPeak + 999) ~/ 1000) * 1000).toDouble()
        : axisMax;
    final firstTimestamp = points.first.timestamp;
    final span = math.max(1, points.last.timestamp - firstTimestamp);
    final grid = Paint()
      ..color = gridColor
      ..strokeWidth = 1;
    for (var index = 0; index <= 4; index++) {
      final fraction = index / 4;
      final x = chart.left + chart.width * fraction;
      final y = chart.top + chart.height * fraction;
      canvas.drawLine(Offset(x, chart.top), Offset(x, chart.bottom), grid);
      canvas.drawLine(Offset(chart.left, y), Offset(chart.right, y), grid);
      _paintChartLabel(
        canvas,
        kind == _HistoryPlotKind.temperature
            ? '${(axisMax - (axisMax - axisMin) * fraction).round()}°'
            : '${(axisMax * (1 - fraction)).round()} W',
        Offset(chart.left - 8, y),
        labelColor,
        alignRight: true,
        centerVertically: true,
      );
      _paintChartLabel(
        canvas,
        kind == _HistoryPlotKind.temperature
            ? '${(secondaryMax * (1 - fraction)).round()}'
            : '${(axisMax * (1 - fraction)).round()}',
        Offset(chart.right + 8, y),
        labelColor,
        centerVertically: true,
      );
      _paintChartLabel(
        canvas,
        _historyTime((firstTimestamp + span * fraction).round()),
        Offset(x, chart.bottom + 8),
        labelColor,
        centered: true,
      );
    }

    Offset position(
      TemperatureHistoryPoint point,
      double value,
      bool secondary,
    ) => Offset(
      chart.left + chart.width * (point.timestamp - firstTimestamp) / span,
      chart.bottom -
          chart.height *
              (secondary
                  ? value / secondaryMax
                  : (value - axisMin) / (axisMax - axisMin)),
    );

    void drawSeries(
      double Function(TemperatureHistoryPoint point) valueOf,
      Color color, {
      bool secondary = false,
    }) {
      final path = Path();
      TemperatureHistoryPoint? previous;
      var drawing = false;
      for (final point in points) {
        final value = valueOf(point);
        if (value <= 0 ||
            (previous != null &&
                point.timestamp - previous.timestamp > gapThreshold)) {
          drawing = false;
        }
        if (value > 0) {
          final offset = position(point, value, secondary);
          if (drawing) {
            path.lineTo(offset.dx, offset.dy);
          } else {
            path.moveTo(offset.dx, offset.dy);
            drawing = true;
          }
        }
        previous = point;
      }
      canvas.drawPath(
        path,
        Paint()
          ..color = color
          ..strokeWidth = 2
          ..style = PaintingStyle.stroke,
      );
    }

    if (kind == _HistoryPlotKind.temperature) {
      drawSeries((point) => point.cpuTemp.toDouble(), cpuColor);
      drawSeries((point) => point.gpuTemp.toDouble(), gpuColor);
      drawSeries((point) => point.fanRpm.toDouble(), fanColor, secondary: true);
    } else {
      drawSeries((point) => point.cpuPower, cpuColor);
      drawSeries((point) => point.gpuPower, gpuColor);
    }

    final index = selectedIndex;
    if (index != null && index >= 0 && index < points.length) {
      final point = points[index];
      final x =
          chart.left + chart.width * (point.timestamp - firstTimestamp) / span;
      canvas.drawLine(
        Offset(x, chart.top),
        Offset(x, chart.bottom),
        Paint()
          ..color = labelColor.withAlpha(150)
          ..strokeWidth = 1,
      );
      void marker(double value, Color color, {bool secondary = false}) {
        if (value > 0) {
          canvas.drawCircle(
            position(point, value, secondary),
            4,
            Paint()..color = color,
          );
        }
      }

      if (kind == _HistoryPlotKind.temperature) {
        marker(point.cpuTemp.toDouble(), cpuColor);
        marker(point.gpuTemp.toDouble(), gpuColor);
        marker(point.fanRpm.toDouble(), fanColor, secondary: true);
      } else {
        marker(point.cpuPower, cpuColor);
        marker(point.gpuPower, gpuColor);
      }
    }
  }

  @override
  bool shouldRepaint(_TemperatureHistoryPainter oldDelegate) =>
      oldDelegate.points != points ||
      oldDelegate.gapThreshold != gapThreshold ||
      oldDelegate.kind != kind ||
      oldDelegate.cpuColor != cpuColor ||
      oldDelegate.gpuColor != gpuColor ||
      oldDelegate.fanColor != fanColor ||
      oldDelegate.gridColor != gridColor ||
      oldDelegate.labelColor != labelColor ||
      oldDelegate.selectedIndex != selectedIndex;
}

String _historyTime(int timestamp) {
  final time = DateTime.fromMillisecondsSinceEpoch(timestamp);
  return '${time.hour.toString().padLeft(2, '0')}:${time.minute.toString().padLeft(2, '0')}';
}

String _historyDateTime(int timestamp) {
  final time = DateTime.fromMillisecondsSinceEpoch(timestamp);
  return '${_historyTime(timestamp)}:${time.second.toString().padLeft(2, '0')}';
}

void _paintChartLabel(
  Canvas canvas,
  String text,
  Offset offset,
  Color color, {
  bool alignRight = false,
  bool centered = false,
  bool centerVertically = false,
}) {
  final painter = TextPainter(
    text: TextSpan(
      text: text,
      style: TextStyle(color: color, fontSize: 11),
    ),
    textDirection: TextDirection.ltr,
  )..layout();
  painter.paint(
    canvas,
    Offset(
      offset.dx -
          (alignRight
              ? painter.width
              : centered
              ? painter.width / 2
              : 0),
      offset.dy - (centerVertically ? painter.height / 2 : 0),
    ),
  );
}

const _lightModes = <({String value, String label})>[
  (value: 'off', label: '关闭'),
  (value: 'smart_temp', label: '智能温度'),
  (value: 'static_single', label: '单色常亮'),
  (value: 'static_multi', label: '多色常亮'),
  (value: 'rotation', label: '旋转'),
  (value: 'flowing', label: '流水'),
  (value: 'breathing', label: '呼吸'),
];

const _lightSpeeds = <({String value, String label})>[
  (value: 'fast', label: '快速'),
  (value: 'medium', label: '中速'),
  (value: 'slow', label: '慢速'),
];

const _defaultLightColors = <Map<String, int>>[
  {'r': 255, 'g': 0, 'b': 0},
  {'r': 0, 'g': 255, 'b': 0},
  {'r': 0, 'g': 128, 'b': 255},
];

Map<String, dynamic> normalizeLightStripConfig(Object? raw) {
  final source = _stringMap(raw) ?? const <String, dynamic>{};
  final mode = source['mode']?.toString() ?? 'smart_temp';
  final speed = source['speed']?.toString() ?? 'medium';
  final colors = <Map<String, int>>[];
  final rawColors = source['colors'];
  if (rawColors is List) {
    for (final rawColor in rawColors.take(3)) {
      final color = _stringMap(rawColor);
      if (color == null) continue;
      int channel(String key) =>
          (color[key] is num ? (color[key] as num).round() : 0).clamp(0, 255);
      colors.add({'r': channel('r'), 'g': channel('g'), 'b': channel('b')});
    }
  }
  while (colors.length < 3) {
    colors.add(Map<String, int>.from(_defaultLightColors[colors.length]));
  }
  return {
    'mode': _lightModes.any((item) => item.value == mode) ? mode : 'smart_temp',
    'speed': _lightSpeeds.any((item) => item.value == speed) ? speed : 'medium',
    'brightness':
        (source['brightness'] is num
                ? (source['brightness'] as num).round()
                : 100)
            .clamp(0, 100),
    'colors': colors,
  };
}

int requiredLightColorCount(String mode) => switch (mode) {
  'off' || 'smart_temp' || 'flowing' => 0,
  'static_single' => 1,
  _ => 3,
};

String? normalizeHotkey(String raw) {
  final value = raw.trim();
  if (value.isEmpty) return '';
  final parts = value.split('+').map((part) => part.trim()).toList();
  if (parts.length < 2) return null;
  const modifierOrder = ['Ctrl', 'Alt', 'Shift', 'Win'];
  final modifiers = <String>{};
  for (final rawModifier in parts.take(parts.length - 1)) {
    final modifier = modifierOrder.firstWhere(
      (item) => item.toLowerCase() == rawModifier.toLowerCase(),
      orElse: () => '',
    );
    if (modifier.isEmpty || !modifiers.add(modifier)) return null;
  }
  final key = parts.last.toUpperCase();
  final validKey = RegExp(r'^(?:[A-Z0-9]|F(?:[1-9]|1[0-2]))$').hasMatch(key);
  if (!validKey) return null;
  return [
    for (final modifier in modifierOrder)
      if (modifiers.contains(modifier)) modifier,
    key,
  ].join('+');
}

int? parseDeviceDebugCommand(String input) {
  final parts = input
      .trim()
      .split(RegExp(r'[^0-9a-fA-F]+'))
      .where((part) => part.isNotEmpty)
      .toList();
  if (parts.isEmpty) return null;
  final bytes = <int>[];
  for (final part in parts) {
    final value = int.tryParse(part, radix: 16);
    if (value == null || value > 0xff) return null;
    bytes.add(value);
  }
  return bytes.length >= 3 && bytes[0] == 0x5a && bytes[1] == 0xa5
      ? bytes[2]
      : bytes.first;
}

bool isLatestVersion(String currentVersion, String latestVersion) {
  String normalize(String value) => value
      .trim()
      .replaceFirst(RegExp(r'^v', caseSensitive: false), '')
      .toLowerCase();
  final current = normalize(currentVersion);
  final latest = normalize(latestVersion);
  if (current.isEmpty || latest.isEmpty || current == latest) return true;
  int? nightly(String value) => int.tryParse(
    RegExp(r'^nightly[-.]?(\d{8})$').firstMatch(value)?.group(1) ?? '',
  );
  final currentNightly = nightly(current);
  final latestNightly = nightly(latest);
  if (currentNightly != null && latestNightly != null) {
    return latestNightly <= currentNightly;
  }
  List<int>? semver(String value) {
    final base = value.split('-').first.split('+').first;
    if (!RegExp(r'^\d+(?:\.\d+){0,3}$').hasMatch(base)) return null;
    return base.split('.').map(int.parse).toList();
  }

  final currentParts = semver(current);
  final latestParts = semver(latest);
  if (currentParts == null || latestParts == null) return false;
  final length = math.max(currentParts.length, latestParts.length);
  for (var index = 0; index < length; index++) {
    final currentPart = index < currentParts.length ? currentParts[index] : 0;
    final latestPart = index < latestParts.length ? latestParts[index] : 0;
    if (latestPart != currentPart) return latestPart < currentPart;
  }
  return true;
}

class ControlPage extends StatefulWidget {
  const ControlPage({
    required this.controller,
    required this.scrollController,
    super.key,
  });

  final AppController controller;
  final ScrollController scrollController;

  @override
  State<ControlPage> createState() => _ControlPageState();
}

class _ControlPageState extends State<ControlPage> {
  Map<String, dynamic> lightDraft = normalizeLightStripConfig(null);
  Object? lightSource;
  Object? configSource;
  int customRpm = 2000;
  final manualHotkeyController = TextEditingController();
  final autoHotkeyController = TextEditingController();
  final curveHotkeyController = TextEditingController();
  final debugCommandController = TextEditingController(text: '27');
  final cpuSensorFlyoutController = fluent.FlyoutController();
  Object? debugInfo;
  Object? debugResult;

  @override
  void dispose() {
    manualHotkeyController.dispose();
    autoHotkeyController.dispose();
    curveHotkeyController.dispose();
    debugCommandController.dispose();
    cpuSensorFlyoutController.dispose();
    super.dispose();
  }

  void _syncDrafts(Map<String, dynamic> config) {
    final nextLightSource = config['lightStrip'];
    if (!identical(lightSource, nextLightSource)) {
      lightSource = nextLightSource;
      lightDraft = normalizeLightStripConfig(nextLightSource);
    }
    if (!identical(configSource, config)) {
      configSource = config;
      customRpm =
          (config['customSpeedRPM'] is num
                  ? (config['customSpeedRPM'] as num).round()
                  : 2000)
              .clamp(1000, 4000);
      manualHotkeyController.text =
          config['manualGearToggleHotkey']?.toString() ?? '';
      autoHotkeyController.text =
          config['autoControlToggleHotkey']?.toString() ?? '';
      curveHotkeyController.text =
          config['curveProfileToggleHotkey']?.toString() ?? '';
    }
  }

  void _showError([String? fallback]) {
    if (!mounted) return;
    fluent.displayInfoBar(
      context,
      alignment: Alignment.topCenter,
      builder: (_, close) => fluent.InfoBar.error(
        title: const Text('操作失败'),
        content: Text(widget.controller.error ?? fallback ?? 'Core 未完成该操作'),
        action: fluent.IconButton(
          icon: const Icon(fluent.WindowsIcons.chrome_close),
          onPressed: close,
        ),
      ),
    );
  }

  Future<bool> _saveConfig(Map<String, dynamic> patch, String action) async {
    final saved = await widget.controller.updateConfig(patch, action: action);
    if (!saved) _showError('$action失败');
    return saved;
  }

  Future<void> _showCpuSensorPicker(
    List<Map<String, dynamic>> sensors,
    Set<String> selected,
  ) async {
    final draft = {...selected};
    await cpuSensorFlyoutController.showFlyout<void>(
      barrierColor: Colors.transparent,
      autoModeConfiguration: fluent.FlyoutAutoConfiguration(
        preferredMode: fluent.FlyoutPlacementMode.bottomRight,
      ),
      builder: (context) => fluent.FlyoutContent(
        constraints: const BoxConstraints(
          minWidth: 280,
          maxWidth: 360,
          maxHeight: 360,
        ),
        child: StatefulBuilder(
          builder: (context, setFlyoutState) => SingleChildScrollView(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                fluent.Checkbox(
                  key: const ValueKey('control-cpu-sensor-auto'),
                  checked: draft.isEmpty,
                  content: const Text('自动选择（推荐）'),
                  onChanged: (_) => setFlyoutState(draft.clear),
                ),
                const Padding(
                  padding: EdgeInsets.symmetric(vertical: 8),
                  child: fluent.Divider(),
                ),
                for (final sensor in sensors)
                  Padding(
                    padding: const EdgeInsets.symmetric(vertical: 5),
                    child: fluent.Checkbox(
                      key: ValueKey('control-cpu-sensor-${sensor['key']}'),
                      checked: draft.contains(sensor['key'].toString()),
                      content: Text('${sensor['name']} (${sensor['value']}°C)'),
                      onChanged: (checked) => setFlyoutState(() {
                        final key = sensor['key'].toString();
                        checked == true ? draft.add(key) : draft.remove(key);
                      }),
                    ),
                  ),
              ],
            ),
          ),
        ),
      ),
    );
    if (!mounted || setEquals(draft, selected)) return;
    await _saveConfig({
      'cpuSensors': [
        for (final sensor in sensors)
          if (draft.contains(sensor['key'].toString()))
            sensor['key'].toString(),
      ],
    }, '设置 CPU 传感器');
  }

  Future<bool> _runControl(
    String request, {
    required Object data,
    required Map<String, dynamic> patch,
    required String action,
  }) async {
    final result = await widget.controller.runControlRequest(
      request,
      data: data,
      configPatch: patch,
      action: action,
    );
    if (result == null) _showError('$action失败');
    return result != null;
  }

  Color _lightColor(int index) {
    final colors = lightDraft['colors'] as List;
    final color = Map<String, dynamic>.from(colors[index] as Map);
    return Color.fromARGB(
      255,
      color['r'] as int,
      color['g'] as int,
      color['b'] as int,
    );
  }

  Future<void> _pickLightColor(int index) async {
    var selected = _lightColor(index);
    final result = await fluent.showDialog<Color>(
      context: context,
      builder: (dialogContext) => StatefulBuilder(
        builder: (context, setDialogState) => fluent.ContentDialog(
          title: Text('颜色 ${index + 1}'),
          content: SizedBox(
            width: 420,
            child: fluent.ColorPicker(
              color: selected,
              isAlphaEnabled: false,
              isAlphaSliderVisible: false,
              isAlphaTextInputVisible: false,
              onChanged: (color) => setDialogState(() => selected = color),
            ),
          ),
          actions: [
            fluent.FilledButton(
              onPressed: () => Navigator.pop(dialogContext, selected),
              child: const Text('确定'),
            ),
            fluent.Button(
              onPressed: () => Navigator.pop(dialogContext),
              child: const Text('取消'),
            ),
          ],
        ),
      ),
    );
    if (result == null || !mounted) return;
    final value = result.toARGB32();
    final colors = [
      for (final color in lightDraft['colors'] as List)
        Map<String, int>.from(color as Map),
    ];
    colors[index] = {
      'r': (value >> 16) & 0xff,
      'g': (value >> 8) & 0xff,
      'b': value & 0xff,
    };
    setState(() => lightDraft = {...lightDraft, 'colors': colors});
  }

  Future<void> _applyLightStrip() async {
    await _runControl(
      'SetLightStrip',
      data: {'config': lightDraft},
      patch: {'lightStrip': lightDraft},
      action: '应用灯效',
    );
  }

  Future<void> _setCustomSpeed(bool enabled) async {
    if (enabled && widget.controller.config?['customSpeedEnabled'] != true) {
      final confirmed = await fluent.showDialog<bool>(
        context: context,
        builder: (dialogContext) => fluent.ContentDialog(
          title: const Text('启用固定转速'),
          content: Text(
            '固定转速会关闭智能控温，并让散热器持续运行在 $customRpm RPM。'
            '温度变化时不会自动提速，确定继续吗？',
          ),
          actions: [
            fluent.FilledButton(
              onPressed: () => Navigator.pop(dialogContext, true),
              child: const Text('启用'),
            ),
            fluent.Button(
              onPressed: () => Navigator.pop(dialogContext, false),
              child: const Text('取消'),
            ),
          ],
        ),
      );
      if (confirmed != true || !mounted) return;
    }
    await _runControl(
      'SetCustomSpeed',
      data: {'enabled': enabled, 'rpm': customRpm},
      patch: {
        'customSpeedEnabled': enabled,
        'customSpeedRPM': customRpm,
        if (enabled) 'autoControl': false,
      },
      action: enabled ? '启用固定转速' : '关闭固定转速',
    );
  }

  Future<void> _saveHotkeys() async {
    final values = [
      normalizeHotkey(manualHotkeyController.text),
      normalizeHotkey(autoHotkeyController.text),
      normalizeHotkey(curveHotkeyController.text),
    ];
    if (values.any((value) => value == null)) {
      _showError('快捷键需包含至少一个修饰键，并以字母、数字或 F1–F12 结尾');
      return;
    }
    final nonEmpty = values.whereType<String>().where(
      (value) => value.isNotEmpty,
    );
    if (nonEmpty.toSet().length != nonEmpty.length) {
      _showError('三个快捷键不能重复');
      return;
    }
    final saved = await _saveConfig({
      'manualGearToggleHotkey': values[0],
      'autoControlToggleHotkey': values[1],
      'curveProfileToggleHotkey': values[2],
    }, '保存快捷键');
    if (saved && mounted) {
      manualHotkeyController.text = values[0]!;
      autoHotkeyController.text = values[1]!;
      curveHotkeyController.text = values[2]!;
    }
  }

  Future<void> _loadDebugInfo() async {
    final result = await widget.controller.runControlRequest(
      'GetDebugInfo',
      action: '读取诊断信息',
    );
    if (!mounted) return;
    if (result == null) {
      _showError('读取诊断信息失败');
    } else {
      setState(() => debugInfo = result);
    }
  }

  Future<void> _sendDebugCommand() async {
    final command = debugCommandController.text.trim();
    if (parseDeviceDebugCommand(command) == null) {
      _showError('请输入有效的十六进制命令');
      return;
    }
    final result = await widget.controller.runControlRequest(
      'SendDeviceDebugCommand',
      data: {'hex': command, 'waitMs': 900},
      action: '发送设备调试命令',
    );
    if (!mounted) return;
    if (result == null) {
      _showError('发送设备调试命令失败');
    } else {
      setState(() => debugResult = result);
    }
  }

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: widget.controller,
    builder: (context, _) {
      final config = widget.controller.config;
      final connected =
          widget.controller.connection == CoreConnection.connected;
      if (config == null) {
        return fluent.ScaffoldPage.scrollable(
          scrollController: widget.scrollController,
          header: const fluent.PageHeader(title: Text('控制')),
          children: const [
            fluent.InfoBar.warning(
              title: Text('正在等待 Core 配置'),
              content: Text('连接成功后将在这里显示设备与系统控制。'),
            ),
          ],
        );
      }
      _syncDrafts(config);
      final deviceConnected = connected && widget.controller.deviceConnected;
      final busy =
          widget.controller.updatingControls ||
          widget.controller.updatingFanFeatures;
      final model = widget.controller.deviceStatus?['model']?.toString() ?? '';
      final isBs1 = model == 'BS1';
      final legionSupport = _stringMap(config['legionFnQSupport']);
      return fluent.ScaffoldPage.scrollable(
        scrollController: widget.scrollController,
        header: fluent.PageHeader(
          title: const Text('控制'),
          commandBar: fluent.CommandBar(
            mainAxisAlignment: MainAxisAlignment.end,
            primaryItems: [
              fluent.CommandBarButton(
                icon: const Icon(fluent.FluentIcons.refresh),
                label: const Text('刷新'),
                onPressed: connected ? widget.controller.refresh : null,
              ),
            ],
          ),
        ),
        children: [
          if (widget.controller.error != null) ...[
            fluent.InfoBar.error(
              title: const Text('最近一次操作失败'),
              content: Text(widget.controller.error!),
            ),
            const SizedBox(height: 16),
          ],
          if (!connected || !widget.controller.deviceConnected) ...[
            fluent.InfoBar.warning(
              title: Text(connected ? '设备未连接' : 'Core 未连接'),
              content: const Text('设备相关控件暂不可用，系统与界面设置仍可修改。'),
            ),
            const SizedBox(height: 16),
          ],
          if (!isBs1) ...[
            _buildLightCard(deviceConnected, busy),
            const SizedBox(height: 16),
          ],
          _buildTemperatureCard(config, connected, busy),
          const SizedBox(height: 16),
          _buildCustomSpeedCard(config, deviceConnected, busy),
          const SizedBox(height: 16),
          _buildDeviceCard(config, deviceConnected, busy, isBs1),
          if (legionSupport?['supported'] == true) ...[
            const SizedBox(height: 16),
            _buildLegionCard(config, connected, busy),
          ],
          const SizedBox(height: 16),
          _buildSystemCard(config, connected, busy),
          const SizedBox(height: 16),
          _buildDebugPanel(config, deviceConnected, busy),
        ],
      );
    },
  );

  Widget _buildLightCard(bool enabled, bool busy) {
    final mode = lightDraft['mode'] as String;
    final speed = lightDraft['speed'] as String;
    final brightness = lightDraft['brightness'] as int;
    final colorCount = requiredLightColorCount(mode);
    return _CurveFeatureCard(
      icon: fluent.FluentIcons.color,
      title: '灯光效果',
      description: '设置灯带模式、动画速度、亮度和颜色。',
      trailing: fluent.FilledButton(
        key: const ValueKey('control-light-apply'),
        onPressed: enabled && !busy ? _applyLightStrip : null,
        child: const Text('应用'),
      ),
      childPadding: EdgeInsets.fromLTRB(
        16,
        0,
        16,
        mode == 'smart_temp' || colorCount > 0 ? 16 : 0,
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(0, 8, 0, 16),
            child: Wrap(
              spacing: 12,
              runSpacing: 12,
              children: [
                SizedBox(
                  width: 220,
                  child: fluent.InfoLabel(
                    label: '灯效模式',
                    child: fluent.ComboBox<String>(
                      key: const ValueKey('control-light-mode'),
                      value: mode,
                      isExpanded: true,
                      iconSize: _comboBoxIconSize,
                      items: [
                        for (final option in _lightModes)
                          fluent.ComboBoxItem(
                            value: option.value,
                            child: Text(option.label),
                          ),
                      ],
                      onChanged: busy
                          ? null
                          : (value) {
                              if (value != null) {
                                setState(
                                  () => lightDraft = {
                                    ...lightDraft,
                                    'mode': value,
                                  },
                                );
                              }
                            },
                    ),
                  ),
                ),
                SizedBox(
                  width: 180,
                  child: fluent.InfoLabel(
                    label: '动画速度',
                    child: fluent.ComboBox<String>(
                      value: speed,
                      isExpanded: true,
                      iconSize: _comboBoxIconSize,
                      items: [
                        for (final option in _lightSpeeds)
                          fluent.ComboBoxItem(
                            value: option.value,
                            child: Text(option.label),
                          ),
                      ],
                      onChanged:
                          busy ||
                              const {
                                'off',
                                'smart_temp',
                                'static_single',
                                'static_multi',
                              }.contains(mode)
                          ? null
                          : (value) {
                              if (value != null) {
                                setState(
                                  () => lightDraft = {
                                    ...lightDraft,
                                    'speed': value,
                                  },
                                );
                              }
                            },
                    ),
                  ),
                ),
              ],
            ),
          ),
          const fluent.Divider(),
          _SettingRow(
            title: '灯带亮度',
            description: mode == 'smart_temp'
                ? '智能温度模式由设备自动控制亮度。'
                : '调整灯带整体亮度。',
            trailing: SizedBox(
              width: 300,
              child: Row(
                children: [
                  Expanded(
                    child: fluent.Slider(
                      value: brightness.toDouble(),
                      min: 0,
                      max: 100,
                      divisions: 100,
                      label: '$brightness%',
                      onChanged: busy || mode == 'off' || mode == 'smart_temp'
                          ? null
                          : (value) => setState(
                              () => lightDraft = {
                                ...lightDraft,
                                'brightness': value.round(),
                              },
                            ),
                    ),
                  ),
                  const SizedBox(width: 12),
                  SizedBox(width: 44, child: Text('$brightness%')),
                ],
              ),
            ),
          ),
          if (mode == 'smart_temp') ...[
            const SizedBox(height: 8),
            const fluent.InfoBar.warning(
              title: Text('智能温度灯效'),
              content: Text('该模式由设备根据温度自动控制颜色与亮度。'),
            ),
          ],
          if (colorCount > 0) ...[
            const fluent.Divider(),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (var index = 0; index < colorCount; index++)
                  fluent.Button(
                    onPressed: busy ? null : () => _pickLightColor(index),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Container(
                          width: 18,
                          height: 18,
                          decoration: BoxDecoration(
                            color: _lightColor(index),
                            borderRadius: BorderRadius.circular(4),
                            border: Border.all(color: Colors.black26),
                          ),
                        ),
                        const SizedBox(width: 8),
                        Text('颜色 ${index + 1}'),
                      ],
                    ),
                  ),
              ],
            ),
          ],
        ],
      ),
    );
  }

  Widget _buildTemperatureCard(
    Map<String, dynamic> config,
    bool enabled,
    bool busy,
  ) {
    List<Map<String, dynamic>> maps(Object? raw) => raw is List
        ? [
            for (final item in raw)
              if (item is Map) Map<String, dynamic>.from(item),
          ]
        : const [];
    final temperature = widget.controller.temperature;
    final cpuSensors = maps(temperature?['cpuSensors']);
    final gpuDevices = maps(temperature?['gpuDevices']);
    final fallbackGpuSensors = maps(temperature?['gpuSensors']);
    final cpuSensorKeys = {
      for (final sensor in cpuSensors) sensor['key'].toString(),
    };
    final selectedCpuSensors = config['cpuSensors'] is List
        ? (config['cpuSensors'] as List)
              .map((item) => item.toString())
              .where(cpuSensorKeys.contains)
              .toSet()
        : <String>{};
    final configuredDevice = config['gpuDevice']?.toString() ?? 'auto';
    final selectedDevice =
        configuredDevice == 'auto' ||
            gpuDevices.any((device) => device['key'] == configuredDevice)
        ? configuredDevice
        : 'auto';
    final detectedDevice = temperature?['selectedGpuDevice']?.toString();
    final activeDeviceKey = selectedDevice == 'auto'
        ? detectedDevice
        : selectedDevice;
    final activeDevice = gpuDevices.cast<Map<String, dynamic>?>().firstWhere(
      (device) => device?['key'] == activeDeviceKey,
      orElse: () => null,
    );
    final gpuSensors = activeDevice == null
        ? fallbackGpuSensors
        : maps(activeDevice['sensors']);
    final configuredSensor = config['gpuSensor']?.toString() ?? 'auto';
    final selectedGpuSensor =
        gpuSensors.any((sensor) => sensor['key'] == configuredSensor)
        ? configuredSensor
        : 'auto';
    final gpuEnabled = config['disableGpuMonitoring'] != true;
    final tempSource =
        const {'max', 'cpu', 'gpu'}.contains(config['tempSource'])
        ? config['tempSource'] as String
        : 'max';
    final sampleCount =
        const {1, 2, 3, 5, 10}.contains(config['tempSampleCount'])
        ? config['tempSampleCount'] as int
        : 1;

    return _CurveFeatureCard(
      icon: fluent.FluentIcons.diagnostic,
      title: '温度监测',
      description: '选择自动控温使用的 CPU、GPU 与传感器来源。',
      trailing: const SizedBox.shrink(),
      childPadding: gpuEnabled ? _settingContentPadding : _settingRowsPadding,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _SettingRow(
            title: '控温温度来源',
            description: '选择 CPU、GPU 或二者最高温作为风扇曲线基准。',
            trailing: SizedBox(
              width: _settingControlWidth,
              child: fluent.ComboBox<String>(
                key: const ValueKey('control-temperature-source'),
                value: tempSource,
                isExpanded: true,
                iconSize: _comboBoxIconSize,
                items: const [
                  fluent.ComboBoxItem(
                    value: 'max',
                    child: Text('CPU / GPU 最高温'),
                  ),
                  fluent.ComboBoxItem(value: 'cpu', child: Text('仅 CPU')),
                  fluent.ComboBoxItem(value: 'gpu', child: Text('仅 GPU')),
                ],
                onChanged: enabled && !busy
                    ? (value) {
                        if (value != null) {
                          _saveConfig({'tempSource': value}, '切换控温温度来源');
                        }
                      }
                    : null,
              ),
            ),
          ),
          const fluent.Divider(),
          _SettingRow(
            title: '温度平滑',
            description: '采样数越高越平稳，但对温度突变的反应会稍慢。',
            trailing: SizedBox(
              width: _settingControlWidth,
              child: fluent.ComboBox<int>(
                value: sampleCount,
                isExpanded: true,
                iconSize: _comboBoxIconSize,
                items: const [
                  fluent.ComboBoxItem(value: 1, child: Text('即时')),
                  fluent.ComboBoxItem(value: 2, child: Text('灵敏')),
                  fluent.ComboBoxItem(value: 3, child: Text('均衡')),
                  fluent.ComboBoxItem(value: 5, child: Text('平稳')),
                  fluent.ComboBoxItem(value: 10, child: Text('最平稳')),
                ],
                onChanged: enabled && !busy
                    ? (value) {
                        if (value != null) {
                          _saveConfig({'tempSampleCount': value}, '设置温度平滑');
                        }
                      }
                    : null,
              ),
            ),
          ),
          const fluent.Divider(),
          _SettingRow(
            title: 'CPU 温度传感器',
            description: cpuSensors.isEmpty
                ? '暂未发现可选传感器，将由 Core 自动选择。'
                : temperature?['cpuModel']?.toString().trim().isNotEmpty == true
                ? temperature!['cpuModel'].toString()
                : '选择参与 CPU 温度计算的传感器。',
            trailing: SizedBox(
              width: _settingControlWidth,
              child: fluent.FlyoutTarget(
                controller: cpuSensorFlyoutController,
                child: fluent.Button(
                  key: const ValueKey('control-cpu-sensors'),
                  onPressed: enabled && !busy && cpuSensors.isNotEmpty
                      ? () =>
                            _showCpuSensorPicker(cpuSensors, selectedCpuSensors)
                      : null,
                  child: Row(
                    children: [
                      Expanded(
                        child: Text(
                          selectedCpuSensors.isEmpty
                              ? '自动选择'
                              : selectedCpuSensors.length == 1
                              ? cpuSensors
                                    .firstWhere(
                                      (sensor) => selectedCpuSensors.contains(
                                        sensor['key'].toString(),
                                      ),
                                    )['name']
                                    .toString()
                              : '已选 ${selectedCpuSensors.length} 项',
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                      const SizedBox(width: 8),
                      const fluent.WindowsIcon(
                        fluent.WindowsIcons.chevron_down,
                        size: _comboBoxIconSize,
                      ),
                    ],
                  ),
                ),
              ),
            ),
          ),
          const fluent.Divider(),
          _SettingRow(
            title: 'GPU 温度监测',
            description: '关闭后不再轮询 GPU，可避免混合显卡设备唤醒独显。',
            trailing: fluent.ToggleSwitch(
              key: const ValueKey('control-gpu-monitoring'),
              checked: gpuEnabled,
              semanticLabel: 'GPU 温度监测',
              onChanged: enabled && !busy
                  ? (value) => _saveConfig({
                      'disableGpuMonitoring': !value,
                    }, value ? '启用 GPU 温度监测' : '停用 GPU 温度监测')
                  : null,
            ),
          ),
          if (gpuEnabled) ...[
            const SizedBox(height: 8),
            Wrap(
              spacing: 12,
              runSpacing: 12,
              children: [
                SizedBox(
                  width: 280,
                  child: fluent.InfoLabel(
                    label: 'GPU 设备',
                    child: fluent.ComboBox<String>(
                      value: selectedDevice,
                      isExpanded: true,
                      iconSize: _comboBoxIconSize,
                      items: [
                        const fluent.ComboBoxItem(
                          value: 'auto',
                          child: Text('自动选择'),
                        ),
                        for (final device in gpuDevices)
                          fluent.ComboBoxItem(
                            value: device['key'].toString(),
                            child: Text(
                              '${device['vendor']?.toString().toUpperCase() ?? ''}'
                              '${device['vendor']?.toString().isNotEmpty == true ? ' · ' : ''}'
                              '${device['name']}',
                            ),
                          ),
                      ],
                      onChanged: enabled && !busy && gpuDevices.isNotEmpty
                          ? (value) {
                              if (value != null) {
                                _saveConfig({
                                  'gpuDevice': value,
                                  'gpuSensor': 'auto',
                                }, '选择 GPU 设备');
                              }
                            }
                          : null,
                    ),
                  ),
                ),
                SizedBox(
                  width: 280,
                  child: fluent.InfoLabel(
                    label: 'GPU 传感器',
                    child: fluent.ComboBox<String>(
                      value: selectedGpuSensor,
                      isExpanded: true,
                      iconSize: _comboBoxIconSize,
                      items: [
                        const fluent.ComboBoxItem(
                          value: 'auto',
                          child: Text('自动选择'),
                        ),
                        for (final sensor in gpuSensors)
                          fluent.ComboBoxItem(
                            value: sensor['key'].toString(),
                            child: Text(
                              '${sensor['name']} (${sensor['value']}°C)',
                            ),
                          ),
                      ],
                      onChanged: enabled && !busy && gpuSensors.isNotEmpty
                          ? (value) {
                              if (value != null) {
                                _saveConfig({'gpuSensor': value}, '选择 GPU 传感器');
                              }
                            }
                          : null,
                    ),
                  ),
                ),
              ],
            ),
          ],
        ],
      ),
    );
  }

  Widget _buildCustomSpeedCard(
    Map<String, dynamic> config,
    bool enabled,
    bool busy,
  ) {
    final active = config['customSpeedEnabled'] == true;
    return _CurveFeatureCard(
      icon: fluent.FluentIcons.speed_high,
      title: '固定转速',
      description: active
          ? '当前持续运行在 $customRpm RPM，智能控温已暂停。'
          : '调试散热效果时可暂时绕过自动曲线。',
      trailing: fluent.ToggleSwitch(
        key: const ValueKey('control-custom-speed-enabled'),
        checked: active,
        semanticLabel: '固定转速',
        onChanged: enabled && !busy ? _setCustomSpeed : null,
      ),
      child: Row(
        children: [
          SizedBox(
            width: 120,
            child: fluent.NumberBox<int>(
              key: const ValueKey('control-custom-speed-rpm'),
              value: customRpm,
              min: 1000,
              max: 4000,
              smallChange: 50,
              largeChange: 200,
              mode: fluent.SpinButtonPlacementMode.none,
              clearButton: false,
              textAlign: TextAlign.center,
              onChanged: busy
                  ? null
                  : (value) {
                      if (value != null) {
                        setState(() => customRpm = value.clamp(1000, 4000));
                      }
                    },
            ),
          ),
          const SizedBox(width: 8),
          const Text('RPM'),
          const Spacer(),
          fluent.FilledButton(
            key: const ValueKey('control-custom-speed-apply'),
            onPressed: active && enabled && !busy
                ? () => _setCustomSpeed(true)
                : null,
            child: const Text('应用转速'),
          ),
        ],
      ),
    );
  }

  Widget _buildDeviceCard(
    Map<String, dynamic> config,
    bool enabled,
    bool busy,
    bool isBs1,
  ) {
    final smartStartStop =
        const {'off', 'immediate', 'delayed'}.contains(config['smartStartStop'])
        ? config['smartStartStop'] as String
        : 'off';
    return _CurveFeatureCard(
      icon: fluent.FluentIcons.devices3,
      title: '设备设置',
      description: '这些选项会立即写入散热器固件。',
      trailing: const SizedBox.shrink(),
      childPadding: _settingRowsPadding,
      child: Column(
        children: [
          if (!isBs1) ...[
            _SettingRow(
              title: '挡位指示灯',
              description: '控制设备上的挡位状态灯。',
              trailing: fluent.ToggleSwitch(
                key: const ValueKey('control-gear-light'),
                checked: config['gearLight'] == true,
                semanticLabel: '挡位指示灯',
                onChanged: enabled && !busy
                    ? (value) => _runControl(
                        'SetGearLight',
                        data: {'enabled': value},
                        patch: {'gearLight': value},
                        action: '设置挡位指示灯',
                      )
                    : null,
              ),
            ),
            const fluent.Divider(),
          ],
          _SettingRow(
            title: '通电自启动',
            description: '接通电源后让散热器自动开始工作。',
            trailing: fluent.ToggleSwitch(
              key: const ValueKey('control-power-on-start'),
              checked: config['powerOnStart'] == true,
              semanticLabel: '通电自启动',
              onChanged: enabled && !busy
                  ? (value) => _runControl(
                      'SetPowerOnStart',
                      data: {'enabled': value},
                      patch: {'powerOnStart': value},
                      action: '设置通电自启动',
                    )
                  : null,
            ),
          ),
          if (!isBs1) ...[
            const fluent.Divider(),
            _SettingRow(
              title: '智能启停',
              description: '选择设备检测到笔记本后的启动策略。',
              trailing: SizedBox(
                width: _settingControlWidth,
                child: fluent.ComboBox<String>(
                  key: const ValueKey('control-smart-start-stop'),
                  value: smartStartStop,
                  isExpanded: true,
                  iconSize: _comboBoxIconSize,
                  items: const [
                    fluent.ComboBoxItem(value: 'off', child: Text('关闭')),
                    fluent.ComboBoxItem(
                      value: 'immediate',
                      child: Text('立即启动'),
                    ),
                    fluent.ComboBoxItem(value: 'delayed', child: Text('延迟启动')),
                  ],
                  onChanged: enabled && !busy
                      ? (value) {
                          if (value != null) {
                            _runControl(
                              'SetSmartStartStop',
                              data: {'value': value},
                              patch: {'smartStartStop': value},
                              action: '设置智能启停',
                            );
                          }
                        }
                      : null,
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }

  Widget _buildLegionCard(
    Map<String, dynamic> config,
    bool enabled,
    bool busy,
  ) {
    const defaults = <String, Map<String, String>>{
      'Quiet': {'gear': '静音', 'level': '中'},
      'Balance': {'gear': '标准', 'level': '中'},
      'Performance': {'gear': '强劲', 'level': '中'},
      'Extreme': {'gear': '超频', 'level': '中'},
      'GodMode': {'gear': '超频', 'level': '高'},
    };
    final raw = _stringMap(config['legionFnQ']) ?? const <String, dynamic>{};
    final rawMappings = _stringMap(raw['modeMapping']);
    final mappings = <String, Map<String, dynamic>>{
      for (final entry in defaults.entries)
        entry.key: {...entry.value, ...?_stringMap(rawMappings?[entry.key])},
    };
    final legionEnabled = raw['enabled'] == true;
    final takeOver = raw['takeOverFan'] == true;

    Future<void> update(Map<String, dynamic> patch) => _saveConfig({
      'legionFnQ': {
        'enabled': legionEnabled,
        'takeOverFan': takeOver,
        'modeMapping': mappings,
        ...patch,
      },
    }, '保存 Legion Fn+Q 设置');

    return _CurveFeatureCard(
      icon: fluent.FluentIcons.lightning_bolt,
      title: 'Legion Fn+Q 联动',
      description: '将联想性能模式映射到散热器手动挡位。',
      trailing: fluent.ToggleSwitch(
        key: const ValueKey('control-legion-enabled'),
        checked: legionEnabled,
        semanticLabel: 'Legion Fn+Q 联动',
        onChanged: enabled && !busy
            ? (value) => update({'enabled': value})
            : null,
      ),
      childPadding: _settingRowsPadding,
      child: Column(
        children: [
          _SettingRow(
            title: '接管散热器挡位',
            description: 'Fn+Q 模式变化时自动切换下方映射的挡位。',
            trailing: fluent.ToggleSwitch(
              checked: takeOver,
              semanticLabel: '接管散热器挡位',
              onChanged: enabled && legionEnabled && !busy
                  ? (value) => update({'takeOverFan': value})
                  : null,
            ),
          ),
          for (final entry in defaults.entries) ...[
            const fluent.Divider(),
            _SettingRow(
              title: switch (entry.key) {
                'Quiet' => '安静模式',
                'Balance' => '均衡模式',
                'Performance' => '性能模式',
                'Extreme' => '极致模式',
                _ => '自定义模式',
              },
              description: entry.key,
              trailing: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  SizedBox(
                    width: 110,
                    child: fluent.ComboBox<String>(
                      value:
                          const {
                            '静音',
                            '标准',
                            '强劲',
                            '超频',
                          }.contains(mappings[entry.key]!['gear'])
                          ? mappings[entry.key]!['gear'] as String
                          : entry.value['gear'],
                      isExpanded: true,
                      iconSize: _comboBoxIconSize,
                      items: const [
                        fluent.ComboBoxItem(value: '静音', child: Text('静音')),
                        fluent.ComboBoxItem(value: '标准', child: Text('标准')),
                        fluent.ComboBoxItem(value: '强劲', child: Text('强劲')),
                        fluent.ComboBoxItem(value: '超频', child: Text('超频')),
                      ],
                      onChanged: enabled && legionEnabled && takeOver && !busy
                          ? (value) {
                              if (value != null) {
                                update({
                                  'modeMapping': {
                                    ...mappings,
                                    entry.key: {
                                      ...mappings[entry.key]!,
                                      'gear': value,
                                    },
                                  },
                                });
                              }
                            }
                          : null,
                    ),
                  ),
                  const SizedBox(width: 8),
                  SizedBox(
                    width: 90,
                    child: fluent.ComboBox<String>(
                      value:
                          const {
                            '低',
                            '中',
                            '高',
                          }.contains(mappings[entry.key]!['level'])
                          ? mappings[entry.key]!['level'] as String
                          : entry.value['level'],
                      isExpanded: true,
                      iconSize: _comboBoxIconSize,
                      items: const [
                        fluent.ComboBoxItem(value: '低', child: Text('低')),
                        fluent.ComboBoxItem(value: '中', child: Text('中')),
                        fluent.ComboBoxItem(value: '高', child: Text('高')),
                      ],
                      onChanged: enabled && legionEnabled && takeOver && !busy
                          ? (value) {
                              if (value != null) {
                                update({
                                  'modeMapping': {
                                    ...mappings,
                                    entry.key: {
                                      ...mappings[entry.key]!,
                                      'level': value,
                                    },
                                  },
                                });
                              }
                            }
                          : null,
                    ),
                  ),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }

  Widget _buildSystemCard(
    Map<String, dynamic> config,
    bool connected,
    bool busy,
  ) {
    final themeMode =
        const {'system', 'light', 'dark'}.contains(config['themeMode'])
        ? config['themeMode'] as String
        : 'system';
    return _CurveFeatureCard(
      icon: fluent.FluentIcons.settings,
      title: '系统设置',
      description: '设置界面主题、开机自启动、重连策略和全局快捷键。',
      trailing: const SizedBox.shrink(),
      childPadding: _settingContentPadding,
      child: Column(
        children: [
          _SettingRow(
            title: '界面主题',
            description: 'Flutter 版本使用系统、浅色或深色 Fluent 主题。',
            trailing: SizedBox(
              width: _settingControlWidth,
              child: fluent.ComboBox<String>(
                key: const ValueKey('control-theme-mode'),
                value: themeMode,
                isExpanded: true,
                iconSize: _comboBoxIconSize,
                items: const [
                  fluent.ComboBoxItem(value: 'system', child: Text('跟随系统')),
                  fluent.ComboBoxItem(value: 'light', child: Text('浅色')),
                  fluent.ComboBoxItem(value: 'dark', child: Text('深色')),
                ],
                onChanged: connected && !busy
                    ? (value) {
                        if (value != null) {
                          _saveConfig({'themeMode': value}, '切换界面主题');
                        }
                      }
                    : null,
              ),
            ),
          ),
          const fluent.Divider(),
          _SettingRow(
            title: Platform.isWindows ? 'Windows 开机自启动' : 'Linux 登录时启动',
            description: Platform.isWindows
                ? '按当前权限使用计划任务或注册表启动 THRM。'
                : '通过 XDG autostart 在登录桌面后启动 THRM。',
            trailing: fluent.ToggleSwitch(
              key: const ValueKey('control-auto-start'),
              checked: config['windowsAutoStart'] == true,
              semanticLabel: '开机自启动',
              onChanged: connected && !busy
                  ? (value) async {
                      final saved = await widget.controller.setAutoStart(value);
                      if (!saved) _showError('设置开机自启动失败');
                    }
                  : null,
            ),
          ),
          const fluent.Divider(),
          _SettingRow(
            title: '断连后保持应用配置',
            description: '设备重新连接时不让固件状态覆盖 THRM 中的设置。',
            trailing: fluent.ToggleSwitch(
              key: const ValueKey('control-reconnect-policy'),
              checked: config['ignoreDeviceOnReconnect'] != false,
              semanticLabel: '断连后保持应用配置',
              onChanged: connected && !busy
                  ? (value) => _saveConfig({
                      'ignoreDeviceOnReconnect': value,
                    }, '保存重连策略')
                  : null,
            ),
          ),
          const fluent.Divider(),
          const SizedBox(height: 12),
          Align(
            alignment: AlignmentDirectional.centerStart,
            child: Text(
              '全局快捷键',
              style: fluent.FluentTheme.of(context).typography.bodyStrong,
            ),
          ),
          const SizedBox(height: 4),
          Align(
            alignment: AlignmentDirectional.centerStart,
            child: Text(
              '点击输入框后直接按组合键；右侧按钮或 Backspace / Delete 可清空。',
              style: fluent.FluentTheme.of(context).typography.caption,
            ),
          ),
          const SizedBox(height: 8),
          _SettingRow(
            title: '切换手动挡位',
            description: '在静音、标准、强劲和超频挡位之间循环。',
            trailing: SizedBox(
              width: 250,
              child: _HotkeyRecorder(
                key: const ValueKey('control-hotkey-manual'),
                controller: manualHotkeyController,
                enabled: connected && !busy,
              ),
            ),
          ),
          const fluent.Divider(),
          _SettingRow(
            title: '开关智能控温',
            description: '快速启用或暂停自动风扇曲线。',
            trailing: SizedBox(
              width: 250,
              child: _HotkeyRecorder(
                key: const ValueKey('control-hotkey-auto'),
                controller: autoHotkeyController,
                enabled: connected && !busy,
              ),
            ),
          ),
          const fluent.Divider(),
          _SettingRow(
            title: '切换曲线方案',
            description: '按顺序切换已保存的风扇曲线方案。',
            trailing: SizedBox(
              width: 250,
              child: _HotkeyRecorder(
                key: const ValueKey('control-hotkey-curve'),
                controller: curveHotkeyController,
                enabled: connected && !busy,
              ),
            ),
          ),
          const SizedBox(height: 8),
          Align(
            alignment: AlignmentDirectional.centerEnd,
            child: fluent.Button(
              key: const ValueKey('control-hotkeys-save'),
              onPressed: connected && !busy ? _saveHotkeys : null,
              child: const Text('保存快捷键'),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildDebugPanel(
    Map<String, dynamic> config,
    bool deviceConnected,
    bool busy,
  ) {
    final commandByte = parseDeviceDebugCommand(debugCommandController.text);
    final dangerous =
        commandByte != null &&
        const {0xed, 0xee, 0xf0, 0xf1, 0xf2}.contains(commandByte);
    final debugMode = config['debugMode'] == true;
    const encoder = JsonEncoder.withIndent('  ');
    return fluent.Expander(
      key: const ValueKey('control-debug-panel'),
      leading: const Icon(fluent.FluentIcons.bug),
      header: const Text('诊断与调试'),
      trailing: fluent.ToggleSwitch(
        key: const ValueKey('control-debug-mode'),
        checked: debugMode,
        semanticLabel: '调试模式',
        onChanged: !busy
            ? (value) => _runControl(
                'SetDebugMode',
                data: {'enabled': value},
                patch: {'debugMode': value},
                action: '切换调试模式',
              )
            : null,
      ),
      content: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              fluent.Button(
                key: const ValueKey('control-debug-refresh'),
                onPressed: busy ? null : _loadDebugInfo,
                child: const Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(fluent.FluentIcons.refresh, size: 16),
                    SizedBox(width: 8),
                    Text('读取诊断信息'),
                  ],
                ),
              ),
              fluent.Button(
                onPressed: debugInfo == null
                    ? null
                    : () => Clipboard.setData(
                        ClipboardData(text: encoder.convert(debugInfo)),
                      ),
                child: const Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(fluent.FluentIcons.copy, size: 16),
                    SizedBox(width: 8),
                    Text('复制诊断信息'),
                  ],
                ),
              ),
            ],
          ),
          if (debugInfo != null) ...[
            const SizedBox(height: 12),
            Container(
              constraints: const BoxConstraints(maxHeight: 300),
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: fluent.FluentTheme.of(
                  context,
                ).resources.controlFillColorDefault,
                borderRadius: BorderRadius.circular(6),
              ),
              child: SingleChildScrollView(
                child: SelectableText(
                  encoder.convert(debugInfo),
                  style: const TextStyle(fontFamily: 'monospace', fontSize: 12),
                ),
              ),
            ),
          ],
          const SizedBox(height: 16),
          fluent.InfoBar(
            title: Text(dangerous ? '高危设备命令' : '原始设备命令'),
            content: Text(
              dangerous
                  ? '0x${commandByte.toRadixString(16).toUpperCase().padLeft(2, '0')} '
                        '会直接操作固件底层寄存器，误用可能导致设备异常甚至变砖。'
                  : '命令会直接下发到设备固件，请仅在明确了解协议时使用。',
            ),
            severity: dangerous
                ? fluent.InfoBarSeverity.error
                : fluent.InfoBarSeverity.warning,
          ),
          const SizedBox(height: 12),
          Row(
            children: [
              Expanded(
                child: fluent.TextBox(
                  key: const ValueKey('control-debug-command'),
                  controller: debugCommandController,
                  placeholder: '27 或 5A A5 27 02 29',
                  enabled: debugMode && deviceConnected && !busy,
                  onChanged: (_) => setState(() {}),
                  onSubmitted: (_) => _sendDebugCommand(),
                ),
              ),
              const SizedBox(width: 8),
              fluent.FilledButton(
                key: const ValueKey('control-debug-send'),
                onPressed:
                    debugMode && deviceConnected && !busy && commandByte != null
                    ? _sendDebugCommand
                    : null,
                child: const Text('发送'),
              ),
            ],
          ),
          if (debugResult != null) ...[
            const SizedBox(height: 12),
            Container(
              constraints: const BoxConstraints(maxHeight: 220),
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: fluent.FluentTheme.of(
                  context,
                ).resources.controlFillColorDefault,
                borderRadius: BorderRadius.circular(6),
              ),
              child: SingleChildScrollView(
                child: SelectableText(
                  encoder.convert(debugResult),
                  style: const TextStyle(fontFamily: 'monospace', fontSize: 12),
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _HotkeyRecorder extends StatefulWidget {
  const _HotkeyRecorder({
    required this.controller,
    required this.enabled,
    super.key,
  });

  final TextEditingController controller;
  final bool enabled;

  @override
  State<_HotkeyRecorder> createState() => _HotkeyRecorderState();
}

class _HotkeyRecorderState extends State<_HotkeyRecorder> {
  late final FocusNode focusNode;
  bool recording = false;

  @override
  void initState() {
    super.initState();
    focusNode = FocusNode(onKeyEvent: _handleKeyEvent)
      ..addListener(_handleFocusChanged);
  }

  @override
  void didUpdateWidget(_HotkeyRecorder oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!widget.enabled && recording) focusNode.unfocus();
  }

  KeyEventResult _handleKeyEvent(FocusNode node, KeyEvent event) {
    if (!recording) return KeyEventResult.ignored;
    if (event is! KeyDownEvent) return KeyEventResult.handled;
    if (event.logicalKey == LogicalKeyboardKey.escape) {
      node.unfocus();
      return KeyEventResult.handled;
    }
    if (event.logicalKey == LogicalKeyboardKey.backspace ||
        event.logicalKey == LogicalKeyboardKey.delete) {
      widget.controller.clear();
      node.unfocus();
      return KeyEventResult.handled;
    }

    final keyboard = HardwareKeyboard.instance;
    final value = normalizeHotkey(
      [
        if (keyboard.isControlPressed) 'Ctrl',
        if (keyboard.isAltPressed) 'Alt',
        if (keyboard.isShiftPressed) 'Shift',
        if (keyboard.isMetaPressed) 'Win',
        event.logicalKey.keyLabel,
      ].join('+'),
    );
    if (value == null || value.isEmpty) return KeyEventResult.handled;
    widget.controller.text = value;
    node.unfocus();
    return KeyEventResult.handled;
  }

  void _handleFocusChanged() {
    if (!focusNode.hasFocus && recording && mounted) {
      setState(() => recording = false);
    }
  }

  @override
  void dispose() {
    focusNode
      ..removeListener(_handleFocusChanged)
      ..dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => fluent.TextBox(
    controller: widget.controller,
    focusNode: focusNode,
    enabled: widget.enabled,
    readOnly: true,
    placeholder: recording ? '请按组合键' : '未设置',
    suffixMode: fluent.OverlayVisibilityMode.editing,
    suffix: fluent.Tooltip(
      message: '清空快捷键',
      child: Padding(
        padding: const EdgeInsetsDirectional.only(end: 4),
        child: SizedBox.square(
          dimension: 24,
          child: fluent.IconButton(
            key: const ValueKey('hotkey-clear'),
            style: const fluent.ButtonStyle(
              padding: WidgetStatePropertyAll(EdgeInsets.all(4)),
            ),
            icon: const Icon(fluent.WindowsIcons.chrome_close, size: 12),
            onPressed: widget.enabled
                ? () {
                    widget.controller.clear();
                    focusNode.unfocus();
                  }
                : null,
          ),
        ),
      ),
    ),
    onTap: () {
      if (widget.enabled && !recording) setState(() => recording = true);
    },
    onTapOutside: (_) => focusNode.unfocus(),
  );
}

const _appVersion = String.fromEnvironment(
  'THRM_VERSION',
  defaultValue: '0.1.0',
);
const _repositoryUrl = 'https://github.com/TIANLI0/THRM';
const _latestReleaseUrl = 'https://github.com/TIANLI0/THRM/releases/latest';
const _latestReleaseApiUrl =
    'https://api.github.com/repos/TIANLI0/THRM/releases/latest';

typedef ReleaseInfo = ({String tag, String url, String body});

Future<ReleaseInfo> _fetchLatestRelease() async {
  final client = HttpClient()..connectionTimeout = const Duration(seconds: 8);
  try {
    final request = await client.getUrl(Uri.parse(_latestReleaseApiUrl));
    request.headers
      ..set(HttpHeaders.acceptHeader, 'application/vnd.github+json')
      ..set(HttpHeaders.userAgentHeader, 'THRM-Flutter/$_appVersion');
    final response = await request.close().timeout(const Duration(seconds: 10));
    final body = await response.transform(utf8.decoder).join();
    if (response.statusCode != HttpStatus.ok) {
      throw HttpException('GitHub API 返回 HTTP ${response.statusCode}');
    }
    final decoded = jsonDecode(body);
    if (decoded is! Map) throw const FormatException('GitHub 返回格式无效');
    final tag = decoded['tag_name']?.toString().trim() ?? '';
    if (tag.isEmpty) throw const FormatException('GitHub 发布版本缺少标签');
    final rawUrl = decoded['html_url']?.toString() ?? '';
    final uri = Uri.tryParse(rawUrl);
    final url =
        uri?.scheme == 'https' &&
            uri?.host == 'github.com' &&
            uri!.path.startsWith('/TIANLI0/THRM/releases/')
        ? rawUrl
        : _latestReleaseUrl;
    return (tag: tag, url: url, body: decoded['body']?.toString().trim() ?? '');
  } finally {
    client.close(force: true);
  }
}

Future<bool> _openExternal(String value) async {
  final uri = Uri.tryParse(value);
  if (uri == null || (uri.scheme != 'https' && uri.scheme != 'mailto')) {
    return false;
  }
  try {
    if (Platform.isWindows) {
      await Process.start('rundll32.exe', [
        'url.dll,FileProtocolHandler',
        value,
      ], mode: ProcessStartMode.detached);
    } else if (Platform.isLinux) {
      await Process.start('xdg-open', [value], mode: ProcessStartMode.detached);
    } else {
      return false;
    }
    return true;
  } catch (_) {
    return false;
  }
}

class AboutPage extends StatefulWidget {
  const AboutPage({
    required this.scrollController,
    this.fetchLatestRelease = _fetchLatestRelease,
    super.key,
  });

  final ScrollController scrollController;
  final Future<ReleaseInfo> Function() fetchLatestRelease;

  @override
  State<AboutPage> createState() => _AboutPageState();
}

class _AboutPageState extends State<AboutPage> {
  ReleaseInfo? release;
  bool checking = false;
  String? releaseError;

  @override
  void initState() {
    super.initState();
    _checkRelease();
  }

  Future<void> _checkRelease() async {
    setState(() {
      checking = true;
      releaseError = null;
    });
    try {
      final next = await widget.fetchLatestRelease();
      if (mounted) setState(() => release = next);
    } catch (error) {
      if (mounted) setState(() => releaseError = error.toString());
    } finally {
      if (mounted) setState(() => checking = false);
    }
  }

  Future<void> _open(String url) async {
    if (await _openExternal(url) || !mounted) return;
    fluent.displayInfoBar(
      context,
      alignment: Alignment.topCenter,
      builder: (_, close) => fluent.InfoBar.error(
        title: const Text('无法打开链接'),
        content: Text(url),
        action: fluent.IconButton(
          icon: const Icon(fluent.WindowsIcons.chrome_close),
          onPressed: close,
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final hasUpdate =
        release != null && !isLatestVersion(_appVersion, release!.tag);
    final theme = fluent.FluentTheme.of(context);
    return fluent.ScaffoldPage.scrollable(
      scrollController: widget.scrollController,
      header: const fluent.PageHeader(title: Text('关于')),
      children: [
        fluent.Card(
          padding: const EdgeInsets.all(24),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Container(
                width: 80,
                height: 80,
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: theme.resources.controlFillColorDefault,
                  borderRadius: BorderRadius.circular(16),
                  border: Border.all(
                    color: theme.resources.dividerStrokeColorDefault,
                  ),
                ),
                child: Image.asset('assets/brand/appicon.png'),
              ),
              const SizedBox(width: 20),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text('THRM', style: theme.typography.title),
                    const SizedBox(height: 8),
                    Text(
                      '面向飞智笔记本压风散热器系列设备的第三方驱动与控制工具。'
                      'Flutter 前端只负责交互，设备通信与温控仍由独立 Go Core 执行。',
                      style: theme.typography.body,
                    ),
                    const SizedBox(height: 16),
                    const Wrap(
                      spacing: 8,
                      runSpacing: 8,
                      children: [
                        _ValueBadge(text: 'MIT License'),
                        _ValueBadge(text: 'Windows / Linux'),
                        _ValueBadge(text: 'BS1 — BS3 Pro'),
                        _ValueBadge(text: 'FluentUI'),
                      ],
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
        const SizedBox(height: 16),
        _CurveFeatureCard(
          icon: fluent.FluentIcons.refresh,
          title: '版本与更新',
          description: '从 GitHub Releases 检查稳定版本；安装仍由发布页完成。',
          trailing: fluent.FilledButton(
            key: const ValueKey('about-check-update'),
            onPressed: checking ? null : _checkRelease,
            child: Text(checking ? '检查中…' : '检查更新'),
          ),
          childPadding: _settingContentPadding,
          child: Column(
            children: [
              _SettingRow(
                title: '当前版本',
                description: 'Flutter UI · IPC 协议 3.0',
                trailing: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text('v$_appVersion'),
                    const SizedBox(width: 4),
                    fluent.IconButton(
                      icon: const Icon(fluent.FluentIcons.copy, size: 16),
                      onPressed: () => Clipboard.setData(
                        const ClipboardData(text: 'v$_appVersion'),
                      ),
                    ),
                  ],
                ),
              ),
              const fluent.Divider(),
              _SettingRow(
                title: '最新稳定版',
                description: releaseError != null
                    ? '检查失败：$releaseError'
                    : hasUpdate
                    ? '发现新版本，可前往发布页查看更新说明。'
                    : release == null
                    ? '尚未检查'
                    : '当前已是最新版本。',
                trailing: Text(release?.tag ?? '--'),
              ),
              if (release?.body.isNotEmpty == true) ...[
                const fluent.Divider(),
                Container(
                  width: double.infinity,
                  constraints: const BoxConstraints(maxHeight: 220),
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(
                    color: theme.resources.controlFillColorDefault,
                    borderRadius: BorderRadius.circular(6),
                  ),
                  child: SingleChildScrollView(
                    child: SelectableText(release!.body),
                  ),
                ),
              ],
              const SizedBox(height: 8),
              Align(
                alignment: AlignmentDirectional.centerEnd,
                child: fluent.Button(
                  onPressed: () => _open(release?.url ?? _latestReleaseUrl),
                  child: const Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Icon(fluent.FluentIcons.open_in_new_window, size: 16),
                      SizedBox(width: 8),
                      Text('打开发布页'),
                    ],
                  ),
                ),
              ),
            ],
          ),
        ),
        const SizedBox(height: 16),
        _CurveFeatureCard(
          icon: fluent.FluentIcons.contact,
          title: '项目与反馈',
          description: '查看源代码、提交问题或联系项目作者。',
          trailing: const SizedBox.shrink(),
          child: Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              fluent.Button(
                onPressed: () => _open(_repositoryUrl),
                child: const Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(fluent.FluentIcons.repo, size: 16),
                    SizedBox(width: 8),
                    Text('GitHub 仓库'),
                  ],
                ),
              ),
              fluent.Button(
                onPressed: () => _open('$_repositoryUrl/issues'),
                child: const Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(fluent.FluentIcons.feedback, size: 16),
                    SizedBox(width: 8),
                    Text('GitHub Issues'),
                  ],
                ),
              ),
              fluent.Button(
                onPressed: () => _open('mailto:wutianli@tianli0.top'),
                child: const Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(fluent.FluentIcons.mail, size: 16),
                    SizedBox(width: 8),
                    Text('联系作者'),
                  ],
                ),
              ),
            ],
          ),
        ),
        const SizedBox(height: 16),
        const _AboutFaq(),
        const SizedBox(height: 16),
        _CurveFeatureCard(
          icon: fluent.FluentIcons.code,
          title: '技术与致谢',
          description: '感谢所有开源项目、贡献者与测试用户。',
          trailing: const SizedBox.shrink(),
          child: const Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              _ValueBadge(text: 'Flutter'),
              _ValueBadge(text: 'fluent_ui'),
              _ValueBadge(text: 'dart_ipc'),
              _ValueBadge(text: 'Go'),
              _ValueBadge(text: 'hidapi'),
              _ValueBadge(text: 'LibreHardwareMonitor'),
            ],
          ),
        ),
      ],
    );
  }
}

class _AboutFaq extends StatelessWidget {
  const _AboutFaq();

  static const items = <({String question, String answer})>[
    (
      question: '关闭窗口后温控还会继续吗？',
      answer: '会。设备通信与温控由独立 THRM Core 负责，Flutter 窗口关闭或重启不会中断 Core。',
    ),
    (
      question: '支持哪些系统和设备？',
      answer:
          'Flutter 前端仅支持 Windows 与 Linux；设备范围为飞智 BS1、BS2、BS2 Pro、BS3 与 BS3 Pro。',
    ),
    (
      question: '为什么某些传感器或灯效不可用？',
      answer: '硬件能力取决于设备型号、固件和系统监控接口。THRM 会隐藏或禁用当前设备不支持的控件。',
    ),
    (question: '在哪里反馈问题？', answer: '请在 GitHub Issues 附上系统版本、设备型号、复现步骤和诊断信息。'),
  ];

  @override
  Widget build(BuildContext context) => Column(
    children: [
      for (var index = 0; index < items.length; index++) ...[
        fluent.Expander(
          initiallyExpanded: index == 0,
          leading: const Icon(fluent.FluentIcons.info),
          header: Text(items[index].question),
          content: Align(
            alignment: AlignmentDirectional.centerStart,
            child: Text(items[index].answer),
          ),
        ),
        if (index != items.length - 1) const SizedBox(height: 8),
      ],
    ],
  );
}
