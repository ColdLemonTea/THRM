import 'dart:math' as math;

import 'package:flutter/foundation.dart' show listEquals;
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter/services.dart';

import 'app_controller.dart';
import 'smooth_scroll.dart';
import 'temperature_history.dart';

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

enum _ProfileMenuAction { create, rename, delete, export, import }

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
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(widget.controller.error ?? 'Core 当前不可用，曲线未保存')),
      );
    }
    return saved;
  }

  List<Map<String, int>> _curveJson(List<FanCurvePoint> curve) => [
    for (final point in curve)
      {'temperature': point.temperature, 'rpm': point.rpm},
  ];

  Future<bool> _confirmLowRpm(List<FanCurvePoint> curve) async {
    if (!curve.any((point) => point.rpm < 1000)) return true;
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
    return mounted && confirmed == true;
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

    return showDialog<String>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(title),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(description),
            const SizedBox(height: 16),
            TextFormField(
              key: const ValueKey('fan-curve-profile-name-input'),
              initialValue: initialValue,
              autofocus: true,
              maxLength: 6,
              textInputAction: TextInputAction.done,
              decoration: const InputDecoration(labelText: '方案名称'),
              onChanged: (text) => input = text,
              onFieldSubmitted: (_) => Navigator.pop(dialogContext, value()),
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext),
            child: const Text('取消'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, value()),
            child: const Text('确定'),
          ),
        ],
      ),
    );
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
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(widget.controller.error ?? '新建曲线方案失败')),
      );
      return;
    }
    final activeCurve = readFanCurve(widget.controller.config?['fanCurve']);
    setState(() {
      savedCurve = activeCurve.isEmpty ? curve : activeCurve;
      draftCurve = savedCurve;
      dirty = false;
    });
    ScaffoldMessenger.of(
      context,
    ).showSnackBar(SnackBar(content: Text('已新建曲线方案“$name”')));
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
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(
          saved ? '曲线方案已重命名' : widget.controller.error ?? '重命名曲线方案失败',
        ),
      ),
    );
  }

  Future<void> _deleteProfile(FanCurveProfileOption profile) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        icon: const Icon(Icons.delete_outline),
        title: const Text('删除曲线方案？'),
        content: Text(
          dirty
              ? '“${profile.name}”还有未保存修改；删除后这些修改也会丢失。'
              : '确定删除“${profile.name}”吗？此操作无法撤销。',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('取消'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, true),
            child: const Text('删除'),
          ),
        ],
      ),
    );
    if (!mounted || confirmed != true) return;
    final deleted = await widget.controller.deleteFanCurveProfile(profile.id);
    if (!mounted) return;
    if (!deleted) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(widget.controller.error ?? '删除曲线方案失败')),
      );
      return;
    }
    final activeCurve = readFanCurve(widget.controller.config?['fanCurve']);
    setState(() {
      savedCurve = activeCurve;
      draftCurve = activeCurve;
      dirty = false;
    });
    ScaffoldMessenger.of(
      context,
    ).showSnackBar(const SnackBar(content: Text('曲线方案已删除')));
  }

  Future<void> _exportProfiles() async {
    if (dirty && !await _save()) return;
    final code = await widget.controller.exportFanCurveProfiles();
    if (!mounted) return;
    if (code == null) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(widget.controller.error ?? '导出曲线方案失败')),
      );
      return;
    }
    try {
      await Clipboard.setData(ClipboardData(text: code));
      if (!mounted) return;
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(const SnackBar(content: Text('方案码已复制到剪贴板')));
    } catch (caught) {
      if (!mounted) return;
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text('复制方案码失败：$caught')));
    }
  }

  Future<void> _importProfiles() async {
    String code;
    try {
      code =
          (await Clipboard.getData(Clipboard.kTextPlain))?.text?.trim() ?? '';
    } catch (caught) {
      if (!mounted) return;
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text('读取剪贴板失败：$caught')));
      return;
    }
    if (!mounted) return;
    if (code.isEmpty) {
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(const SnackBar(content: Text('剪贴板中没有方案码')));
      return;
    }
    if (dirty && !await _save()) return;
    final imported = await widget.controller.importFanCurveProfiles(code);
    if (!mounted) return;
    if (!imported) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(widget.controller.error ?? '导入曲线方案失败')),
      );
      return;
    }
    final activeCurve = readFanCurve(widget.controller.config?['fanCurve']);
    setState(() {
      savedCurve = activeCurve;
      draftCurve = activeCurve;
      dirty = false;
    });
    ScaffoldMessenger.of(
      context,
    ).showSnackBar(const SnackBar(content: Text('已导入为新曲线方案')));
  }

  Future<void> _setTemperatureHistoryEnabled(bool enabled) async {
    if (!enabled) {
      final confirmed = await showDialog<bool>(
        context: context,
        builder: (dialogContext) => AlertDialog(
          icon: const Icon(Icons.delete_sweep_outlined),
          title: const Text('关闭后台温度记录？'),
          content: const Text('Core 会立即清空已保存的温度历史；重新开启后只会记录新的采样。'),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(dialogContext, false),
              child: const Text('取消'),
            ),
            FilledButton(
              onPressed: () => Navigator.pop(dialogContext, true),
              child: const Text('关闭并清空'),
            ),
          ],
        ),
      );
      if (!mounted || confirmed != true) return;
    }
    final saved = await widget.controller.setTemperatureHistoryEnabled(enabled);
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(
          saved
              ? enabled
                    ? '后台温度记录已开启'
                    : '后台温度记录已关闭，历史已清空'
              : widget.controller.error ?? '设置温度历史记录失败',
        ),
      ),
    );
  }

  Future<void> _setTemperatureHistoryRetentionHours(int hours) async {
    final saved = await widget.controller.setTemperatureHistoryRetentionHours(
      hours,
    );
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(
          saved
              ? '温度历史保留时长已设为 $hours 小时'
              : widget.controller.error ?? '设置温度历史保留时长失败',
        ),
      ),
    );
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
        final selectedProfileIndex = profiles.indexWhere(
          (profile) => profile.id == activeProfileId,
        );
        final selectedProfile = profiles.isEmpty
            ? null
            : profiles[selectedProfileIndex >= 0 ? selectedProfileIndex : 0];
        final selectedProfileId = selectedProfile?.id;
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
                      PopupMenuButton<_ProfileMenuAction>(
                        key: const ValueKey('fan-curve-profile-menu'),
                        enabled:
                            widget.controller.connection ==
                                CoreConnection.connected &&
                            !busy,
                        tooltip: '管理曲线方案',
                        icon: const Icon(Icons.more_horiz),
                        onSelected: (action) =>
                            _handleProfileAction(action, selectedProfile),
                        itemBuilder: (context) => [
                          const PopupMenuItem(
                            value: _ProfileMenuAction.create,
                            child: Row(
                              children: [
                                Icon(Icons.add),
                                SizedBox(width: 12),
                                Text('新建方案'),
                              ],
                            ),
                          ),
                          PopupMenuItem(
                            value: _ProfileMenuAction.rename,
                            enabled: selectedProfile != null,
                            child: const Row(
                              children: [
                                Icon(Icons.edit_outlined),
                                SizedBox(width: 12),
                                Text('重命名'),
                              ],
                            ),
                          ),
                          PopupMenuItem(
                            value: _ProfileMenuAction.delete,
                            enabled:
                                selectedProfile != null && profiles.length > 1,
                            child: const Row(
                              children: [
                                Icon(Icons.delete_outline),
                                SizedBox(width: 12),
                                Text('删除方案'),
                              ],
                            ),
                          ),
                          const PopupMenuDivider(),
                          const PopupMenuItem(
                            value: _ProfileMenuAction.export,
                            child: Row(
                              children: [
                                Icon(Icons.content_copy_outlined),
                                SizedBox(width: 12),
                                Text('导出并复制方案码'),
                              ],
                            ),
                          ),
                          const PopupMenuItem(
                            value: _ProfileMenuAction.import,
                            child: Row(
                              children: [
                                Icon(Icons.content_paste_go_outlined),
                                SizedBox(width: 12),
                                Text('从剪贴板导入方案码'),
                              ],
                            ),
                          ),
                        ],
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

  @override
  bool shouldRepaint(FanCurvePainter oldDelegate) =>
      oldDelegate.points != points ||
      oldDelegate.currentTemperature != currentTemperature ||
      oldDelegate.curveColor != curveColor ||
      oldDelegate.markerColor != markerColor ||
      oldDelegate.gridColor != gridColor ||
      oldDelegate.labelColor != labelColor;
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
    final theme = Theme.of(context);
    final colors = theme.colorScheme;
    final dark = theme.brightness == Brightness.dark;
    final cpuColor = dark ? const Color(0xffffb74d) : const Color(0xffe65100);
    final gpuColor = dark ? const Color(0xff64b5f6) : const Color(0xff1565c0);
    final fanColor = dark ? const Color(0xff81c784) : const Color(0xff2e7d32);
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
    return Card(
      key: const ValueKey('temperature-history-card'),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                const Icon(Icons.history),
                const SizedBox(width: 8),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        '温度历史',
                        style: Theme.of(context).textTheme.titleMedium,
                      ),
                      Text(
                        snapshot.enabled
                            ? '${snapshot.points.length} 个采样 · 后台保留 ${snapshot.retentionHours} 小时'
                            : '后台记录已关闭',
                        style: Theme.of(context).textTheme.bodySmall,
                      ),
                    ],
                  ),
                ),
                if (widget.busy) ...[
                  const SizedBox(
                    width: 18,
                    height: 18,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  ),
                  const SizedBox(width: 8),
                ],
                const Text('后台记录'),
                Switch(
                  key: const ValueKey('temperature-history-enabled'),
                  value: snapshot.enabled,
                  onChanged: controlsEnabled ? widget.onEnabledChanged : null,
                ),
              ],
            ),
            const SizedBox(height: 12),
            Wrap(
              spacing: 12,
              runSpacing: 8,
              children: [
                _historyLegend(cpuColor, 'CPU 温度'),
                _historyLegend(gpuColor, 'GPU 温度'),
                _historyLegend(fanColor, '散热器转速'),
                Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    const Text('保留'),
                    const SizedBox(width: 6),
                    DropdownButton<int>(
                      key: const ValueKey('temperature-history-retention'),
                      value: snapshot.retentionHours,
                      isDense: true,
                      items: [
                        for (final hours in retentionOptions)
                          DropdownMenuItem(
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
                  ],
                ),
              ],
            ),
            const SizedBox(height: 12),
            if (points.length < 2)
              const SizedBox(
                height: 160,
                child: Center(child: Text('等待 Core 记录更多温度采样')),
              )
            else
              Semantics(
                label:
                    '温度历史图，${snapshot.points.length} 个采样，包含 CPU、GPU 温度和散热器转速',
                child: RepaintBoundary(
                  child: SizedBox(
                    height: 260,
                    width: double.infinity,
                    child: _TemperatureHistoryChart(
                      key: const ValueKey('temperature-history-chart'),
                      points: points,
                      gapThreshold: gapThreshold,
                      cpuColor: cpuColor,
                      gpuColor: gpuColor,
                      fanColor: fanColor,
                      gridColor: colors.outlineVariant,
                      labelColor: colors.onSurfaceVariant,
                    ),
                  ),
                ),
              ),
          ],
        ),
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
    required this.gridColor,
    required this.labelColor,
    super.key,
  });

  final List<TemperatureHistoryPoint> points;
  final int gapThreshold;
  final Color cpuColor;
  final Color gpuColor;
  final Color fanColor;
  final Color gridColor;
  final Color labelColor;

  @override
  State<_TemperatureHistoryChart> createState() =>
      _TemperatureHistoryChartState();
}

class _TemperatureHistoryChartState extends State<_TemperatureHistoryChart> {
  int? selectedIndex;

  @override
  void didUpdateWidget(_TemperatureHistoryChart oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!identical(oldWidget.points, widget.points) ||
        (selectedIndex != null && selectedIndex! >= widget.points.length)) {
      selectedIndex = null;
    }
  }

  void _selectAt(Offset position, Size size) {
    final chart = _temperatureHistoryChartRect(size);
    int? next;
    if (position.dx >= chart.left && position.dx <= chart.right) {
      final first = widget.points.first.timestamp;
      final span = math.max(1, widget.points.last.timestamp - first);
      final target = first + span * (position.dx - chart.left) / chart.width;
      var low = 0;
      var high = widget.points.length - 1;
      while (low < high) {
        final middle = (low + high) ~/ 2;
        if (widget.points[middle].timestamp < target) {
          low = middle + 1;
        } else {
          high = middle;
        }
      }
      next = low;
      if (low > 0 &&
          (widget.points[low - 1].timestamp - target).abs() <
              (widget.points[low].timestamp - target).abs()) {
        next = low - 1;
      }
    }
    if (next != selectedIndex) setState(() => selectedIndex = next);
  }

  void _clearSelection() {
    if (selectedIndex != null) setState(() => selectedIndex = null);
  }

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        final size = Size(constraints.maxWidth, constraints.maxHeight);
        final index = selectedIndex;
        final selected = index == null ? null : widget.points[index];
        final chart = _temperatureHistoryChartRect(size);
        final span = math.max(
          1,
          widget.points.last.timestamp - widget.points.first.timestamp,
        );
        final selectedX = selected == null
            ? 0.0
            : chart.left +
                  chart.width *
                      (selected.timestamp - widget.points.first.timestamp) /
                      span;
        const tooltipWidth = 208.0;
        var tooltipLeft = selectedX + 12;
        if (tooltipLeft + tooltipWidth > size.width) {
          tooltipLeft = selectedX - tooltipWidth - 12;
        }
        tooltipLeft = tooltipLeft
            .clamp(4.0, math.max(4.0, size.width - tooltipWidth - 4))
            .toDouble();
        return MouseRegion(
          cursor: SystemMouseCursors.precise,
          onHover: (event) => _selectAt(event.localPosition, size),
          onExit: (_) => _clearSelection(),
          child: GestureDetector(
            behavior: HitTestBehavior.opaque,
            onTapDown: (details) => _selectAt(details.localPosition, size),
            child: Stack(
              children: [
                CustomPaint(
                  size: size,
                  painter: _TemperatureHistoryPainter(
                    points: widget.points,
                    gapThreshold: widget.gapThreshold,
                    cpuColor: widget.cpuColor,
                    gpuColor: widget.gpuColor,
                    fanColor: widget.fanColor,
                    gridColor: widget.gridColor,
                    labelColor: widget.labelColor,
                    selectedIndex: selectedIndex,
                  ),
                ),
                if (selected != null)
                  Positioned(
                    left: tooltipLeft,
                    top: chart.top + 8,
                    width: tooltipWidth,
                    child: IgnorePointer(
                      child: _historyTooltip(context, selected),
                    ),
                  ),
              ],
            ),
          ),
        );
      },
    );
  }

  Widget _historyTooltip(BuildContext context, TemperatureHistoryPoint point) {
    final colors = Theme.of(context).colorScheme;
    return Material(
      elevation: 6,
      color: colors.surfaceContainerHighest,
      borderRadius: BorderRadius.circular(10),
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              _historyDateTime(point.timestamp),
              style: const TextStyle(fontWeight: FontWeight.w600),
            ),
            if (point.cpuTemp > 0)
              _tooltipRow(widget.cpuColor, 'CPU', '${point.cpuTemp} °C'),
            if (point.gpuTemp > 0)
              _tooltipRow(widget.gpuColor, 'GPU', '${point.gpuTemp} °C'),
            if (point.fanRpm > 0)
              _tooltipRow(widget.fanColor, '散热器', '${point.fanRpm} RPM'),
          ],
        ),
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
        Text(value, style: const TextStyle(fontWeight: FontWeight.w600)),
      ],
    ),
  );
}

class _TemperatureHistoryPainter extends CustomPainter {
  const _TemperatureHistoryPainter({
    required this.points,
    required this.gapThreshold,
    required this.cpuColor,
    required this.gpuColor,
    required this.fanColor,
    required this.gridColor,
    required this.labelColor,
    required this.selectedIndex,
  });

  final List<TemperatureHistoryPoint> points;
  final int gapThreshold;
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
    final temperatures = [
      for (final point in points)
        if (point.cpuTemp > 0) point.cpuTemp,
      for (final point in points)
        if (point.gpuTemp > 0) point.gpuTemp,
    ];
    if (temperatures.isEmpty) return;
    final tempMin = math.max(0, temperatures.reduce(math.min) - 5).toDouble();
    var tempMax = (temperatures.reduce(math.max) + 5).toDouble();
    if (tempMax - tempMin < 10) tempMax = tempMin + 10;
    final fanPeak = points.fold<int>(
      0,
      (peak, point) => math.max(peak, point.fanRpm),
    );
    final fanMax = math.max(1000, ((fanPeak + 999) ~/ 1000) * 1000);
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
        '${(tempMax - (tempMax - tempMin) * fraction).round()}°',
        Offset(chart.left - 8, y),
        labelColor,
        alignRight: true,
        centerVertically: true,
      );
      _paintChartLabel(
        canvas,
        '${(fanMax * (1 - fraction)).round()}',
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

    Offset position(TemperatureHistoryPoint point, int value, bool rpm) =>
        Offset(
          chart.left + chart.width * (point.timestamp - firstTimestamp) / span,
          chart.bottom -
              chart.height *
                  (rpm
                      ? value / fanMax
                      : (value - tempMin) / (tempMax - tempMin)),
        );

    void drawSeries(
      int Function(TemperatureHistoryPoint point) valueOf,
      Color color, {
      bool rpm = false,
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
          final offset = position(point, value, rpm);
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

    drawSeries((point) => point.cpuTemp, cpuColor);
    drawSeries((point) => point.gpuTemp, gpuColor);
    drawSeries((point) => point.fanRpm, fanColor, rpm: true);

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
      void marker(int value, Color color, {bool rpm = false}) {
        if (value > 0) {
          canvas.drawCircle(
            position(point, value, rpm),
            4,
            Paint()..color = color,
          );
        }
      }

      marker(point.cpuTemp, cpuColor);
      marker(point.gpuTemp, gpuColor);
      marker(point.fanRpm, fanColor, rpm: true);
    }
  }

  @override
  bool shouldRepaint(_TemperatureHistoryPainter oldDelegate) =>
      oldDelegate.points != points ||
      oldDelegate.gapThreshold != gapThreshold ||
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
