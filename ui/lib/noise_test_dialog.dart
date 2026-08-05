import 'dart:async';

import 'package:fluent_ui/fluent_ui.dart' as fluent;
import 'package:flutter/material.dart';
import 'package:record/record.dart';

import 'app_controller.dart';
import 'noise_test.dart';

class NoiseTestDialog extends StatefulWidget {
  const NoiseTestDialog({required this.controller, super.key});

  final AppController controller;

  @override
  State<NoiseTestDialog> createState() => _NoiseTestDialogState();
}

class _NoiseTestDialogState extends State<NoiseTestDialog> {
  final meter = NoiseMeter();
  List<InputDevice> devices = const [];
  InputDevice? selectedDevice;
  List<NoiseSample> samples = const [];
  NoiseAnalysis? analysis;
  String status = '正在检查麦克风…';
  String? error;
  bool loading = true;
  bool running = false;
  bool cancelled = false;
  int step = 0;
  static const steps = <int>[
    1000,
    1250,
    1500,
    1750,
    2000,
    2250,
    2500,
    2750,
    3000,
    3250,
    3500,
    3750,
    4000,
  ];

  @override
  void initState() {
    super.initState();
    _loadDevices();
  }

  Future<void> _loadDevices() async {
    try {
      final found = await meter.devices();
      if (!mounted) return;
      setState(() {
        devices = found;
        selectedDevice = found.firstOrNull;
        status = found.isEmpty ? '没有检测到麦克风' : '请保持环境安静，然后开始扫频';
      });
    } catch (caught) {
      if (mounted) setState(() => error = '麦克风不可用：$caught');
    } finally {
      if (mounted) setState(() => loading = false);
    }
  }

  Future<void> _delay(Duration duration) async {
    final deadline = DateTime.now().add(duration);
    while (DateTime.now().isBefore(deadline)) {
      if (cancelled) throw const _NoiseTestCancelled();
      await Future<void>.delayed(const Duration(milliseconds: 100));
    }
  }

  Future<int> _waitForRpm(int target) async {
    final tolerance = (target * 0.06).round().clamp(120, 1000);
    final deadline = DateTime.now().add(const Duration(seconds: 20));
    var last = -1;
    var hit = 0;
    var stalled = 0;
    while (DateTime.now().isBefore(deadline)) {
      await _delay(const Duration(milliseconds: 500));
      final value = widget.controller.fanData?['currentRpm'];
      final current = value is num ? value.round() : -1;
      if (current <= 0) continue;
      hit = (current - target).abs() <= tolerance ? hit + 1 : 0;
      stalled = last > 0 && (current - last).abs() < 30 ? stalled + 1 : 0;
      last = current;
      if (hit >= 2 || stalled >= 4) return current;
    }
    return last > 0 ? last : target;
  }

  Future<void> _setSpeed(int rpm) async {
    final result = await widget.controller.client.request(
      'SetCustomSpeed',
      data: {'enabled': true, 'rpm': rpm},
    );
    if (result == false) throw StateError('Core 未设置测试转速');
  }

  Future<void> _restore(Map<String, dynamic> config) async {
    if (config['customSpeedEnabled'] == true) {
      await widget.controller.client.request(
        'SetCustomSpeed',
        data: {
          'enabled': true,
          'rpm': (config['customSpeedRPM'] as num?)?.round() ?? 2000,
        },
      );
      return;
    }
    await widget.controller.client.request(
      'SetCustomSpeed',
      data: {'enabled': false, 'rpm': 0},
    );
    if (config['autoControl'] == true) {
      await widget.controller.client.request(
        'SetAutoControl',
        data: {'enabled': true},
      );
    } else if (config['manualGear'] case final String gear
        when gear.isNotEmpty) {
      await widget.controller.client.request(
        'SetManualGear',
        data: {'gear': gear, 'level': config['manualLevel'] ?? '中'},
      );
    }
  }

  Future<void> _run() async {
    final original = Map<String, dynamic>.from(
      widget.controller.config ?? const {},
    );
    setState(() {
      running = true;
      cancelled = false;
      error = null;
      analysis = null;
      samples = const [];
    });
    try {
      await meter.open(selectedDevice);
      final measured = <NoiseSample>[];
      for (var index = 0; index < steps.length; index++) {
        final rpm = steps[index];
        setState(() {
          step = index;
          status = '等待风扇稳定在 $rpm RPM';
        });
        await _setSpeed(rpm);
        final actual = await _waitForRpm(rpm);
        await _delay(const Duration(milliseconds: 1500));
        setState(() => status = '正在测量 $rpm RPM 的噪音');
        var level = await meter.measure(const Duration(seconds: 3));
        if (level.rangeDb > 6) {
          final retry = await meter.measure(const Duration(seconds: 3));
          if (retry.rangeDb < level.rangeDb) level = retry;
        }
        measured.add((rpm: rpm, actualRpm: actual, db: level.db));
        if (mounted) setState(() => samples = List.unmodifiable(measured));
      }
      if (mounted) {
        setState(() {
          analysis = analyzeNoiseSamples(measured);
          status = '测量完成';
        });
      }
    } on _NoiseTestCancelled {
      if (mounted) setState(() => status = '测试已取消，正在恢复风扇设置');
    } catch (caught) {
      if (mounted) setState(() => error = '噪音测试失败：$caught');
    } finally {
      try {
        await _restore(original);
      } catch (caught) {
        if (mounted) setState(() => error = '恢复风扇设置失败：$caught');
      }
      await meter.close();
      if (mounted) setState(() => running = false);
    }
  }

  Future<void> _saveProfile() async {
    final result = analysis;
    if (result == null) return;
    final smartControl = {
      ...?widget.controller.config?['smartControl'] as Map?,
      'noiseProfile': [
        for (final point in result.profile) {'rpm': point.rpm, 'db': point.db},
      ],
      'noiseProfileUpdatedAt': DateTime.now().millisecondsSinceEpoch ~/ 1000,
    };
    final saved = await widget.controller.updateConfig({
      'smartControl': smartControl,
    }, action: '保存噪音档案');
    if (!mounted) return;
    if (saved) {
      Navigator.pop(context);
    } else {
      setState(() => error = widget.controller.error);
    }
  }

  Future<void> _applyResonance() async {
    final resonance = analysis?.resonance;
    if (resonance == null) return;
    final current = Map<String, dynamic>.from(
      widget.controller.config?['speedAvoidance'] as Map? ?? const {},
    );
    final saved = await widget.controller.updateConfig({
      'speedAvoidance': {
        ...current,
        'enabled': true,
        'minRpm': resonance.startRpm,
        'maxRpm': resonance.endRpm,
      },
    }, action: '应用共振避让区间');
    if (!saved && mounted) setState(() => error = widget.controller.error);
  }

  @override
  void dispose() {
    cancelled = true;
    unawaited(meter.dispose());
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final result = analysis;
    return fluent.ContentDialog(
      title: const Text('风扇噪音实测校准'),
      constraints: const BoxConstraints(maxWidth: 620),
      content: SizedBox(
        width: 560,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            const Text('测试会从 1000 到 4000 RPM 扫频。请把麦克风靠近散热器，并保持环境安静。'),
            const SizedBox(height: 12),
            if (devices.isNotEmpty)
              fluent.ComboBox<InputDevice>(
                value: selectedDevice,
                isExpanded: true,
                items: [
                  for (final device in devices)
                    fluent.ComboBoxItem(
                      value: device,
                      child: Text(
                        device.label.isEmpty ? '默认麦克风' : device.label,
                      ),
                    ),
                ],
                onChanged: running
                    ? null
                    : (value) => setState(() => selectedDevice = value),
              ),
            const SizedBox(height: 12),
            Text(status),
            if (running) ...[
              const SizedBox(height: 8),
              fluent.ProgressBar(value: (step + 1) * 100 / steps.length),
              const SizedBox(height: 4),
              Text('${step + 1} / ${steps.length} · 已采集 ${samples.length} 个点'),
            ],
            if (result != null) ...[
              const SizedBox(height: 12),
              fluent.InfoBar(
                title: Text(
                  '总噪音上升 +${result.totalRiseDb.toStringAsFixed(1)} dB',
                ),
                content: Text(
                  result.resonance == null
                      ? result.kneeRpm == null
                            ? '未检测到明显拐点或共振区间'
                            : '噪音从约 ${result.kneeRpm} RPM 开始明显上升'
                      : '检测到 ${result.resonance!.startRpm}–${result.resonance!.endRpm} RPM '
                            '共振区间（峰值 ${result.resonance!.peakRpm} RPM）',
                ),
                severity: result.lowConfidence
                    ? fluent.InfoBarSeverity.warning
                    : fluent.InfoBarSeverity.success,
              ),
            ],
            if (error != null) ...[
              const SizedBox(height: 12),
              fluent.InfoBar.error(
                title: const Text('测试失败'),
                content: Text(error!),
              ),
            ],
          ],
        ),
      ),
      actions: [
        if (running)
          fluent.Button(
            onPressed: () => setState(() => cancelled = true),
            child: const Text('取消测试'),
          )
        else if (result == null)
          fluent.FilledButton(
            onPressed: loading || devices.isEmpty ? null : _run,
            child: const Text('开始测试'),
          )
        else ...[
          if (result.resonance != null)
            fluent.Button(
              onPressed: _applyResonance,
              child: const Text('应用避让区间'),
            ),
          fluent.FilledButton(
            onPressed: result.lowConfidence ? null : _saveProfile,
            child: const Text('保存噪音档案'),
          ),
        ],
        fluent.Button(
          onPressed: running ? null : () => Navigator.pop(context),
          child: const Text('关闭'),
        ),
      ],
    );
  }
}

class _NoiseTestCancelled implements Exception {
  const _NoiseTestCancelled();
}
