import 'dart:math' as math;

import 'package:flutter/foundation.dart' show listEquals;
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';

import 'app_controller.dart';
import 'smooth_scroll.dart';

void main() => runApp(const ThrmApp());

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
    return MaterialApp(
      title: 'THRM',
      debugShowCheckedModeBanner: false,
      locale: const Locale('zh', 'CN'),
      localizationsDelegates: GlobalMaterialLocalizations.delegates,
      supportedLocales: const [Locale('zh', 'CN')],
      themeMode: ThemeMode.system,
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xff6750a4)),
        useMaterial3: true,
      ),
      darkTheme: ThemeData(
        colorScheme: ColorScheme.fromSeed(
          seedColor: const Color(0xffb69df8),
          brightness: Brightness.dark,
        ),
        useMaterial3: true,
      ),
      home: ThrmShell(controller: controller),
    );
  }
}

enum ThrmPage { status, curve, control, about }

typedef FanCurvePoint = ({int temperature, int rpm});
typedef FanCurveProfileOption = ({String id, String name});

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

const _pageLabels = ['状态', '曲线', '控制', '关于'];
const _pageIcons = [
  Icons.dashboard_outlined,
  Icons.show_chart,
  Icons.tune,
  Icons.info_outline,
];
const _selectedPageIcons = [
  Icons.dashboard,
  Icons.show_chart,
  Icons.tune,
  Icons.info,
];

class ThrmShell extends StatefulWidget {
  const ThrmShell({required this.controller, super.key});

  final AppController controller;

  @override
  State<ThrmShell> createState() => _ThrmShellState();
}

class _ThrmShellState extends State<ThrmShell> {
  ThrmPage page = ThrmPage.status;
  final statusScrollController = SmoothScrollController();
  final curveScrollController = SmoothScrollController();
  final aboutScrollController = SmoothScrollController();

  @override
  void dispose() {
    statusScrollController.dispose();
    curveScrollController.dispose();
    aboutScrollController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final selected = page.index;
    return Scaffold(
      appBar: AppBar(
        title: Text('THRM · ${_pageLabels[selected]}'),
        actions: [
          AnimatedBuilder(
            animation: widget.controller,
            builder: (_, _) {
              final online =
                  widget.controller.connection == CoreConnection.connected;
              return Padding(
                padding: const EdgeInsets.only(right: 16),
                child: Chip(
                  avatar: Icon(online ? Icons.link : Icons.link_off, size: 18),
                  label: Text(online ? 'Core 在线' : 'Core 离线'),
                ),
              );
            },
          ),
        ],
      ),
      body: LayoutBuilder(
        builder: (context, constraints) {
          final content = _page(page);
          if (constraints.maxWidth < 820) return content;
          return Row(
            children: [
              NavigationRail(
                selectedIndex: selected,
                labelType: NavigationRailLabelType.all,
                onDestinationSelected: _selectPage,
                destinations: List.generate(
                  ThrmPage.values.length,
                  (index) => NavigationRailDestination(
                    icon: Icon(_pageIcons[index]),
                    selectedIcon: Icon(_selectedPageIcons[index]),
                    label: Text(_pageLabels[index]),
                  ),
                ),
              ),
              const VerticalDivider(width: 1),
              Expanded(child: content),
            ],
          );
        },
      ),
      bottomNavigationBar: MediaQuery.sizeOf(context).width < 820
          ? NavigationBar(
              selectedIndex: selected,
              onDestinationSelected: _selectPage,
              destinations: List.generate(
                ThrmPage.values.length,
                (index) => NavigationDestination(
                  icon: Icon(_pageIcons[index]),
                  selectedIcon: Icon(_selectedPageIcons[index]),
                  label: _pageLabels[index],
                ),
              ),
            )
          : null,
    );
  }

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
    ThrmPage.control => const PlaceholderPage(
      icon: Icons.tune,
      title: '控制页',
      message: '硬件控制仍由现有 Go Core 负责。',
    ),
    ThrmPage.about => RenderingLabPage(scrollController: aboutScrollController),
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
        return RefreshIndicator(
          onRefresh: controller.refresh,
          child: ListView(
            controller: scrollController,
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.all(20),
            children: [
              Card(
                color: connected
                    ? Theme.of(context).colorScheme.primaryContainer
                    : Theme.of(context).colorScheme.errorContainer,
                child: ListTile(
                  leading: Icon(
                    connected ? Icons.cloud_done : Icons.cloud_off,
                    size: 32,
                  ),
                  title: Text(connected ? '已连接 THRM Core' : '正在等待 THRM Core'),
                  subtitle: Text(
                    controller.error ??
                        (controller.deviceConnected
                            ? '设备已连接，实时事件由 Core 推送'
                            : 'Core 在线，设备当前未连接'),
                  ),
                  trailing: IconButton(
                    tooltip: '刷新快照',
                    onPressed: connected ? controller.refresh : null,
                    icon: const Icon(Icons.refresh),
                  ),
                ),
              ),
              const SizedBox(height: 16),
              Text('实时状态', style: Theme.of(context).textTheme.titleLarge),
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
                    icon: Icons.memory,
                    label: 'CPU 温度',
                    value: _metric(temp, 'cpuTemp', '°C'),
                  ),
                  MetricCard(
                    icon: Icons.videogame_asset_outlined,
                    label: 'GPU 温度',
                    value: _metric(temp, 'gpuTemp', '°C'),
                  ),
                  MetricCard(
                    icon: Icons.electric_bolt,
                    label: 'CPU 功耗',
                    value: _metric(temp, 'cpuPower', 'W'),
                  ),
                  MetricCard(
                    icon: Icons.bolt,
                    label: 'GPU 功耗',
                    value: _metric(temp, 'gpuPower', 'W'),
                  ),
                  MetricCard(
                    icon: Icons.air,
                    label: '当前转速',
                    value: _metric(fan, 'currentRpm', ' RPM'),
                  ),
                  MetricCard(
                    icon: Icons.speed,
                    label: '目标转速',
                    value: _metric(fan, 'targetRpm', ' RPM'),
                  ),
                ],
              ),
              if (temperatureWarning != null) ...[
                const SizedBox(height: 16),
                Card(
                  color: Theme.of(context).colorScheme.errorContainer,
                  child: ListTile(
                    leading: const Icon(Icons.warning_amber),
                    title: const Text('温度监控异常'),
                    subtitle: Text(temperatureWarning),
                  ),
                ),
              ],
              const SizedBox(height: 16),
              Card(
                child: Column(
                  children: [
                    ListTile(
                      leading: Icon(
                        controller.deviceConnected ? Icons.usb : Icons.usb_off,
                      ),
                      title: Text(
                        controller.deviceConnected ? '设备已连接' : '设备未连接',
                      ),
                      subtitle: Text(
                        '${status?['model'] ?? '未知型号'} · '
                        '${status?['productId'] ?? '--'}',
                      ),
                    ),
                    const Divider(height: 1),
                    SwitchListTile(
                      secondary: const Icon(Icons.auto_awesome),
                      title: const Text('智能控温'),
                      subtitle: Text(
                        controller.updatingAutoControl
                            ? '正在切换…'
                            : '${autoControl ? '已开启' : '已关闭'} · '
                                  '最近事件：${controller.lastEvent ?? '--'}',
                      ),
                      value: autoControl,
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
                  ],
                ),
              ),
            ],
          ),
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
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            Row(children: [Icon(icon), const SizedBox(width: 8), Text(label)]),
            Text(value, style: Theme.of(context).textTheme.headlineSmall),
          ],
        ),
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

class _FanCurvePageState extends State<FanCurvePage> {
  List<FanCurvePoint> savedCurve = const [];
  List<FanCurvePoint> draftCurve = const [];
  bool dirty = false;
  int? dragIndex;
  double dragRpm = 0;

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
    if (draftCurve.any((point) => point.rpm < 1000)) {
      final confirmed = await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
          icon: const Icon(Icons.warning_amber_rounded),
          title: const Text('低转速风险'),
          content: const Text('曲线中存在低于 1000 RPM 的控制点，风扇可能停转并导致设备过热。确定仍要保存吗？'),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(context, false),
              child: const Text('取消'),
            ),
            FilledButton(
              onPressed: () => Navigator.pop(context, true),
              child: const Text('仍然保存'),
            ),
          ],
        ),
      );
      if (!mounted || confirmed != true) return false;
    }

    final savingCurve = List<FanCurvePoint>.unmodifiable(draftCurve);
    final saved = await widget.controller.setFanCurve([
      for (final point in savingCurve)
        {'temperature': point.temperature, 'rpm': point.rpm},
    ]);
    if (!mounted) return false;
    if (saved) {
      setState(() {
        savedCurve = savingCurve;
        dirty = !listEquals(draftCurve, savedCurve);
      });
    } else {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(widget.controller.error ?? 'Core 当前不可用，曲线未保存')),
      );
    }
    return saved;
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
      final action = await showDialog<_PendingCurveAction>(
        context: context,
        builder: (context) => AlertDialog(
          title: const Text('曲线尚未保存'),
          content: const Text('切换方案前，要保存当前曲线的修改吗？'),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(context),
              child: const Text('取消'),
            ),
            OutlinedButton(
              onPressed: () =>
                  Navigator.pop(context, _PendingCurveAction.discard),
              child: const Text('放弃并切换'),
            ),
            FilledButton(
              onPressed: () => Navigator.pop(context, _PendingCurveAction.save),
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
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(widget.controller.error ?? '曲线方案切换失败')),
    );
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: widget.controller,
      builder: (context, _) {
        _syncExternalCurve(readFanCurve(widget.controller.config?['fanCurve']));
        final points = draftCurve;
        final profiles = readFanCurveProfileOptions(
          widget.controller.config?['fanCurveProfiles'],
        );
        final activeProfileId = widget
            .controller
            .config?['activeFanCurveProfileId']
            ?.toString();
        final selectedProfileId =
            profiles.any((profile) => profile.id == activeProfileId)
            ? activeProfileId
            : profiles.isEmpty
            ? null
            : profiles.first.id;
        final busy =
            widget.controller.updatingFanCurve ||
            widget.controller.updatingFanCurveProfile;
        final temperature = widget.controller.temperature?['controlTemp'] is num
            ? (widget.controller.temperature!['controlTemp'] as num).toDouble()
            : widget.controller.temperature?['maxTemp'] is num
            ? (widget.controller.temperature!['maxTemp'] as num).toDouble()
            : 0.0;
        final targetRpm = widget.controller.fanData?['targetRpm'];
        final colors = Theme.of(context).colorScheme;
        return ListView(
          controller: widget.scrollController,
          padding: const EdgeInsets.all(20),
          children: [
            Text('风扇曲线', style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 4),
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
                          child: DropdownButton<String>(
                            key: const ValueKey('fan-curve-profile-selector'),
                            value: selectedProfileId,
                            items: [
                              for (final profile in profiles)
                                DropdownMenuItem(
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
                      OutlinedButton.icon(
                        key: const ValueKey('fan-curve-discard'),
                        onPressed: dirty && !busy ? _discard : null,
                        icon: const Icon(Icons.undo),
                        label: const Text('放弃修改'),
                      ),
                      FilledButton.icon(
                        key: const ValueKey('fan-curve-save'),
                        onPressed:
                            dirty &&
                                widget.controller.connection ==
                                    CoreConnection.connected &&
                                !busy
                            ? _save
                            : null,
                        icon: const Icon(Icons.save_outlined),
                        label: Text(busy ? '处理中…' : '保存'),
                      ),
                    ],
                  ),
              ],
            ),
            const SizedBox(height: 16),
            if (points.isEmpty)
              const Card(
                child: ListTile(
                  leading: Icon(Icons.show_chart),
                  title: Text('暂无可用曲线'),
                  subtitle: Text('曲线至少需要两个温度递增、转速非递减的控制点。'),
                ),
              )
            else
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Wrap(
                        spacing: 8,
                        runSpacing: 8,
                        children: [
                          if (temperature > 0)
                            Chip(
                              avatar: const Icon(Icons.thermostat, size: 18),
                              label: Text(
                                '控温 ${temperature.toStringAsFixed(0)}°C',
                              ),
                            ),
                          if (targetRpm is num)
                            Chip(
                              avatar: const Icon(Icons.air, size: 18),
                              label: Text('目标 ${targetRpm.toInt()} RPM'),
                            ),
                        ],
                      ),
                      const SizedBox(height: 12),
                      Semantics(
                        label: '风扇曲线，${points.length} 个可上下拖动的控制点',
                        child: RepaintBoundary(
                          child: SizedBox(
                            height: 320,
                            width: double.infinity,
                            child: _FanCurveChart(
                              points: points,
                              currentTemperature: temperature,
                              curveColor: colors.primary,
                              markerColor: colors.tertiary,
                              gridColor: colors.outlineVariant,
                              labelColor: colors.onSurfaceVariant,
                              editable: !busy,
                              onDragStart: _startDrag,
                              onDragUpdate: _dragBy,
                              onDragEnd: _endDrag,
                            ),
                          ),
                        ),
                      ),
                    ],
                  ),
                ),
              ),
          ],
        );
      },
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

class _FanCurveChart extends StatelessWidget {
  const _FanCurveChart({
    required this.points,
    required this.currentTemperature,
    required this.curveColor,
    required this.markerColor,
    required this.gridColor,
    required this.labelColor,
    required this.editable,
    required this.onDragStart,
    required this.onDragUpdate,
    required this.onDragEnd,
  });

  final List<FanCurvePoint> points;
  final double currentTemperature;
  final Color curveColor;
  final Color markerColor;
  final Color gridColor;
  final Color labelColor;
  final bool editable;
  final void Function(int index) onDragStart;
  final void Function(int index, double rpmDelta) onDragUpdate;
  final VoidCallback onDragEnd;

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        final size = Size(constraints.maxWidth, constraints.maxHeight);
        final chart = _fanCurveChartRect(size);
        final handleBorder = Theme.of(context).colorScheme.surface;
        return Stack(
          children: [
            CustomPaint(
              size: size,
              painter: FanCurvePainter(
                points: points,
                currentTemperature: currentTemperature,
                curveColor: curveColor,
                markerColor: markerColor,
                gridColor: gridColor,
                labelColor: labelColor,
              ),
            ),
            if (chart.width > 0 && chart.height > 0)
              for (var index = 0; index < points.length; index++)
                Positioned(
                  left:
                      _fanCurvePointPosition(chart, points, points[index]).dx -
                      12,
                  top:
                      _fanCurvePointPosition(chart, points, points[index]).dy -
                      12,
                  width: 24,
                  height: 24,
                  child: Semantics(
                    slider: true,
                    label: '${points[index].temperature}°C 控制点',
                    value: '${points[index].rpm} RPM',
                    increasedValue:
                        '${(points[index].rpm + 50).clamp(0, 4000)} RPM',
                    decreasedValue:
                        '${(points[index].rpm - 50).clamp(0, 4000)} RPM',
                    onIncrease: editable
                        ? () {
                            onDragStart(index);
                            onDragUpdate(index, 50);
                            onDragEnd();
                          }
                        : null,
                    onDecrease: editable
                        ? () {
                            onDragStart(index);
                            onDragUpdate(index, -50);
                            onDragEnd();
                          }
                        : null,
                    child: Tooltip(
                      message:
                          '${points[index].temperature}°C · ${points[index].rpm} RPM',
                      child: MouseRegion(
                        cursor: editable
                            ? SystemMouseCursors.resizeUpDown
                            : MouseCursor.defer,
                        child: GestureDetector(
                          key: ValueKey('fan-curve-point-$index'),
                          behavior: HitTestBehavior.opaque,
                          onVerticalDragStart: editable
                              ? (_) => onDragStart(index)
                              : null,
                          onVerticalDragUpdate: editable
                              ? (details) => onDragUpdate(
                                  index,
                                  -(details.primaryDelta ?? 0) *
                                      4000 /
                                      chart.height,
                                )
                              : null,
                          onVerticalDragEnd: editable
                              ? (_) => onDragEnd()
                              : null,
                          onVerticalDragCancel: editable ? onDragEnd : null,
                          child: Center(
                            child: Container(
                              width: 12,
                              height: 12,
                              decoration: BoxDecoration(
                                color: curveColor,
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
                ),
          ],
        );
      },
    );
  }
}

class FanCurvePainter extends CustomPainter {
  const FanCurvePainter({
    required this.points,
    required this.currentTemperature,
    required this.curveColor,
    required this.markerColor,
    required this.gridColor,
    required this.labelColor,
  });

  final List<FanCurvePoint> points;
  final double currentTemperature;
  final Color curveColor;
  final Color markerColor;
  final Color gridColor;
  final Color labelColor;

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
      _label(
        canvas,
        '${(4000 * (1 - fraction)).round()}',
        Offset(chart.left - 8, y),
        alignRight: true,
      );
      final temperature =
          points.first.temperature +
          (points.last.temperature - points.first.temperature) * fraction;
      _label(
        canvas,
        '${temperature.round()}°',
        Offset(x, chart.bottom + 8),
        centered: true,
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

  void _label(
    Canvas canvas,
    String text,
    Offset offset, {
    bool alignRight = false,
    bool centered = false,
  }) {
    final painter = TextPainter(
      text: TextSpan(
        text: text,
        style: TextStyle(color: labelColor, fontSize: 11),
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
        offset.dy - (alignRight ? painter.height / 2 : 0),
      ),
    );
  }

  @override
  bool shouldRepaint(FanCurvePainter oldDelegate) =>
      oldDelegate.points != points ||
      oldDelegate.currentTemperature != currentTemperature ||
      oldDelegate.curveColor != curveColor ||
      oldDelegate.markerColor != markerColor ||
      oldDelegate.gridColor != gridColor ||
      oldDelegate.labelColor != labelColor;
}

class PlaceholderPage extends StatelessWidget {
  const PlaceholderPage({
    required this.icon,
    required this.title,
    required this.message,
    super.key,
  });

  final IconData icon;
  final String title;
  final String message;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 56),
            const SizedBox(height: 16),
            Text(title, style: Theme.of(context).textTheme.headlineSmall),
            const SizedBox(height: 8),
            Text(message, textAlign: TextAlign.center),
          ],
        ),
      ),
    );
  }
}

class RenderingLabPage extends StatefulWidget {
  const RenderingLabPage({required this.scrollController, super.key});

  final ScrollController scrollController;

  @override
  State<RenderingLabPage> createState() => _RenderingLabPageState();
}

class _RenderingLabPageState extends State<RenderingLabPage>
    with SingleTickerProviderStateMixin {
  late final AnimationController animation;

  @override
  void initState() {
    super.initState();
    animation = AnimationController(
      vsync: this,
      duration: const Duration(seconds: 4),
    )..repeat();
  }

  @override
  void dispose() {
    animation.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) => ListView(
        controller: widget.scrollController,
        padding: const EdgeInsets.all(20),
        children: [
          Text('Flutter 3 渲染实验', style: Theme.of(context).textTheme.titleLarge),
          const SizedBox(height: 4),
          Text(
            '窗口 ${constraints.maxWidth.toStringAsFixed(0)} × '
            '${constraints.maxHeight.toStringAsFixed(0)} · 协议 3.0',
          ),
          const SizedBox(height: 16),
          Semantics(
            label: '持续更新的八条折线渲染压力图',
            child: RepaintBoundary(
              child: SizedBox(
                height: 280,
                child: AnimatedBuilder(
                  animation: animation,
                  builder: (_, _) =>
                      CustomPaint(painter: StressChartPainter(animation.value)),
                ),
              ),
            ),
          ),
          const SizedBox(height: 12),
          for (var index = 0; index < 24; index++)
            Card(
              child: ListTile(
                leading: CircleAvatar(child: Text('${index + 1}')),
                title: Text('滚动与合成测试项 ${index + 1}'),
                subtitle: LinearProgressIndicator(
                  value: ((index * 37) % 100) / 100,
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class StressChartPainter extends CustomPainter {
  const StressChartPainter(this.phase);

  final double phase;

  @override
  void paint(Canvas canvas, Size size) {
    canvas.drawRect(
      Offset.zero & size,
      Paint()..color = const Color(0xff111318),
    );
    final grid = Paint()
      ..color = Colors.white12
      ..strokeWidth = 1;
    for (var index = 1; index < 8; index++) {
      final y = size.height * index / 8;
      canvas.drawLine(Offset(0, y), Offset(size.width, y), grid);
    }
    for (var series = 0; series < 8; series++) {
      final path = Path();
      for (var sample = 0; sample < 320; sample++) {
        final x = size.width * sample / 319;
        final wave = math.sin(sample * 0.055 + phase * math.pi * 2 + series);
        final ripple = math.cos(sample * 0.017 - phase * math.pi * 4 + series);
        final y = size.height * (0.5 + wave * 0.28 + ripple * 0.08);
        if (sample == 0) {
          path.moveTo(x, y);
        } else {
          path.lineTo(x, y);
        }
      }
      canvas.drawPath(
        path,
        Paint()
          ..color = HSVColor.fromAHSV(0.85, series * 45, 0.7, 1).toColor()
          ..strokeWidth = 1.5
          ..style = PaintingStyle.stroke,
      );
    }
  }

  @override
  bool shouldRepaint(StressChartPainter oldDelegate) =>
      phase != oldDelegate.phase;
}
