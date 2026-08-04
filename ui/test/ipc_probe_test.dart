import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:thrm_ui/app_controller.dart';
import 'package:thrm_ui/ipc_probe.dart';
import 'package:thrm_ui/main.dart' as app;
import 'package:thrm_ui/smooth_scroll.dart';
import 'package:thrm_ui/temperature_history.dart';

void main() {
  test('temperature deltas preserve sensor metadata', () {
    final merged = mergeTemperatureMetadata(
      {
        'cpuTemp': 50,
        'cpuModel': 'CPU',
        'gpuModel': 'GPU',
        'cpuSensors': ['package'],
      },
      {'cpuTemp': 60, 'gpuModel': ''},
    );
    expect(merged, {
      'cpuTemp': 60,
      'cpuModel': 'CPU',
      'gpuModel': 'GPU',
      'cpuSensors': ['package'],
    });
  });

  test('config patch preserves fields unknown to Flutter', () {
    final original = {
      'autoControl': true,
      'unknown': {'preserved': true},
    };
    expect(patchConfig(original, {'autoControl': false}), {
      'autoControl': false,
      'unknown': {'preserved': true},
    });
    expect(original['autoControl'], isTrue);
  });

  test('fan curve parser enforces the Core ordering contract', () {
    expect(
      app.readFanCurve([
        {'temperature': 30, 'rpm': 1000},
        {'temperature': 60, 'rpm': 2200},
        {'temperature': 90, 'rpm': 3600},
      ]),
      const [
        (temperature: 30, rpm: 1000),
        (temperature: 60, rpm: 2200),
        (temperature: 90, rpm: 3600),
      ],
    );
    expect(
      app.readFanCurve([
        {'temperature': 30, 'rpm': 2200},
        {'temperature': 60, 'rpm': 1000},
      ]),
      isEmpty,
    );
  });

  test('fan curve profile options ignore malformed entries', () {
    expect(
      app.readFanCurveProfileOptions([
        {'id': 'balanced', 'name': '均衡'},
        {'id': '', 'name': '无效'},
        {'id': 'quiet', 'name': ''},
      ]),
      const [(id: 'balanced', name: '均衡'), (id: 'quiet', name: 'quiet')],
    );
  });

  test('fan curve RPM editing rounds, clamps, and preserves order', () {
    const curve = [
      (temperature: 30, rpm: 1500),
      (temperature: 60, rpm: 2000),
      (temperature: 90, rpm: 3000),
    ];
    expect(app.syncFanCurveRpmAtIndex(curve, 1, 1274), const [
      (temperature: 30, rpm: 1250),
      (temperature: 60, rpm: 1250),
      (temperature: 90, rpm: 3000),
    ]);
    expect(app.syncFanCurveRpmAtIndex(curve, 1, 3749), const [
      (temperature: 30, rpm: 1500),
      (temperature: 60, rpm: 3750),
      (temperature: 90, rpm: 3750),
    ]);
    expect(app.syncFanCurveRpmAtIndex(curve, 2, 9999).last.rpm, 4000);
  });

  test('temperature history normalizes, replaces, and downsamples points', () {
    final snapshot = readTemperatureHistorySnapshot({
      'enabled': true,
      'sampleIntervalSeconds': 5,
      'retentionHours': 1,
      'points': [
        {'timestamp': 1700000010, 'cpuTemp': 54, 'gpuTemp': 58, 'fanRpm': 1800},
        {'timestamp': 1700000000, 'cpuTemp': 50, 'gpuTemp': 55, 'fanRpm': 1500},
        {'timestamp': 0, 'cpuTemp': 99},
      ],
    });
    expect(snapshot.points.map((point) => point.timestamp), [
      1700000000000,
      1700000010000,
    ]);
    final replaced = appendTemperatureHistoryPoint(snapshot, {
      'timestamp': 1700000010000,
      'cpuTemp': 56,
      'gpuTemp': 60,
      'fanRpm': 1900,
    });
    expect(replaced.points, hasLength(2));
    expect(replaced.points.last.cpuTemp, 56);
    final appended = appendTemperatureHistoryPoint(replaced, {
      'timestamp': 1700000020000,
      'cpuTemp': 58,
      'gpuTemp': 62,
      'fanRpm': 2100,
    });
    expect(downsampleTemperatureHistory(appended.points, 2), [
      appended.points.first,
      appended.points.last,
    ]);
    expect(temperatureHistoryGapThreshold(appended.points, 5), 30000);
  });

  testWidgets('desktop shell fits the minimum window', (tester) async {
    await tester.binding.setSurfaceSize(const Size(800, 600));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final controller = AppController();
    addTearDown(controller.dispose);

    await tester.pumpWidget(
      MaterialApp(home: app.ThrmShell(controller: controller)),
    );
    expect(find.text('THRM · 状态'), findsOneWidget);
    expect(tester.takeException(), isNull);

    await tester.tap(find.text('关于'));
    await tester.pump();
    expect(find.text('Flutter 3 渲染实验'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('app supplies zh-CN locale for consistent CJK fallback', (
    tester,
  ) async {
    await tester.pumpWidget(const app.ThrmApp());
    await tester.pump();
    expect(
      Localizations.localeOf(tester.element(find.byType(Scaffold))),
      const Locale('zh', 'CN'),
    );
    await tester.pumpWidget(const SizedBox.shrink());
    await tester.pump(const Duration(seconds: 6));
  });

  testWidgets('status page shows power, bridge warning, and auto control', (
    tester,
  ) async {
    final controller = AppController()
      ..connection = CoreConnection.connected
      ..deviceConnected = true
      ..config = {'autoControl': true}
      ..temperature = {
        'cpuPower': 42.5,
        'gpuPower': 80,
        'bridgeOk': false,
        'bridgeMessage': 'PawnIO 读取失败',
      };
    final scrollController = ScrollController();
    addTearDown(controller.dispose);
    addTearDown(scrollController.dispose);

    await tester.pumpWidget(
      MaterialApp(
        home: app.StatusPage(
          controller: controller,
          scrollController: scrollController,
        ),
      ),
    );

    expect(find.text('42.5W'), findsOneWidget);
    expect(find.text('80W'), findsOneWidget);
    expect(find.text('PawnIO 读取失败'), findsOneWidget);
    expect(tester.widget<Switch>(find.byType(Switch)).value, isTrue);
  });

  testWidgets('curve page renders the Core curve', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1000, 700));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final controller = AppController()
      ..config = {
        'fanCurve': [
          {'temperature': 30, 'rpm': 1000},
          {'temperature': 60, 'rpm': 2200},
          {'temperature': 90, 'rpm': 3600},
        ],
      }
      ..temperature = {'controlTemp': 65}
      ..temperatureHistory = readTemperatureHistorySnapshot({
        'enabled': true,
        'sampleIntervalSeconds': 5,
        'retentionHours': 1,
        'points': [
          {
            'timestamp': 1700000000000,
            'cpuTemp': 50,
            'gpuTemp': 55,
            'fanRpm': 1500,
          },
          {
            'timestamp': 1700000005000,
            'cpuTemp': 54,
            'gpuTemp': 58,
            'fanRpm': 1800,
          },
          {
            'timestamp': 1700000010000,
            'cpuTemp': 57,
            'gpuTemp': 61,
            'fanRpm': 2100,
          },
        ],
      })
      ..fanData = {'targetRpm': 2400};
    addTearDown(controller.dispose);

    await tester.pumpWidget(
      MaterialApp(home: app.ThrmShell(controller: controller)),
    );
    await tester.tap(find.text('曲线').first);
    await tester.pump();

    expect(find.text('风扇曲线'), findsOneWidget);
    expect(find.text('3 个控制点 · 当前显示 Core 生效曲线'), findsOneWidget);
    expect(find.text('控温 65°C'), findsOneWidget);
    expect(find.text('目标 2400 RPM'), findsOneWidget);
    expect(find.text('温度历史'), findsOneWidget);
    expect(find.text('3 个采样 · 后台保留 1 小时'), findsOneWidget);
    final historyChart = find.byKey(
      const ValueKey('temperature-history-chart'),
    );
    expect(historyChart, findsOneWidget);
    await tester.ensureVisible(historyChart);
    await tester.pumpAndSettle();
    final pointer = TestPointer(7, PointerDeviceKind.mouse);
    await tester.sendEventToBinding(
      pointer.hover(tester.getCenter(historyChart)),
    );
    await tester.pump();
    expect(find.text('54 °C'), findsOneWidget);
    expect(find.text('58 °C'), findsOneWidget);
    expect(find.text('1800 RPM'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('temperature history controls refresh Core state', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1000, 800));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final responses = <String, Object?>{
      'SetTemperatureHistoryEnabled': true,
      'SetTemperatureHistoryRetentionHours': true,
      'GetTemperatureHistory': {
        'enabled': false,
        'sampleIntervalSeconds': 5,
        'retentionHours': 1,
        'points': <Object?>[],
      },
    };
    final client = _RecordingIpcClient(responses);
    final controller = AppController(client: client)
      ..connection = CoreConnection.connected
      ..config = {
        'fanCurve': [
          {'temperature': 30, 'rpm': 1000},
          {'temperature': 60, 'rpm': 2200},
          {'temperature': 90, 'rpm': 3600},
        ],
      }
      ..temperatureHistory = readTemperatureHistorySnapshot({
        'enabled': true,
        'sampleIntervalSeconds': 5,
        'retentionHours': 1,
        'points': [
          {'timestamp': 1700000000000, 'cpuTemp': 50, 'fanRpm': 1500},
          {'timestamp': 1700000005000, 'cpuTemp': 52, 'fanRpm': 1700},
        ],
      });
    final scrollController = ScrollController();
    addTearDown(controller.dispose);
    addTearDown(scrollController.dispose);

    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: app.FanCurvePage(
            controller: controller,
            scrollController: scrollController,
          ),
        ),
      ),
    );
    final enabledSwitch = find.byKey(
      const ValueKey('temperature-history-enabled'),
    );
    await tester.ensureVisible(enabledSwitch);
    await tester.pumpAndSettle();
    expect(tester.widget<Switch>(enabledSwitch).value, isTrue);

    await tester.tap(enabledSwitch);
    await tester.pumpAndSettle();
    expect(find.text('关闭后台温度记录？'), findsOneWidget);
    expect(client.requests, isEmpty);
    await tester.tap(find.text('取消'));
    await tester.pumpAndSettle();
    expect(client.requests, isEmpty);

    await tester.tap(enabledSwitch);
    await tester.pumpAndSettle();
    await tester.tap(find.text('关闭并清空'));
    await tester.pumpAndSettle();
    expect(client.requests.map((request) => request.type), [
      'SetTemperatureHistoryEnabled',
      'GetTemperatureHistory',
    ]);
    expect(client.requests.first.data, {'enabled': false});
    expect(controller.temperatureHistory.enabled, isFalse);
    expect(controller.temperatureHistory.points, isEmpty);

    client.requests.clear();
    responses['GetTemperatureHistory'] = {
      'enabled': false,
      'sampleIntervalSeconds': 5,
      'retentionHours': 3,
      'points': <Object?>[],
    };
    await tester.tap(
      find.byKey(const ValueKey('temperature-history-retention')),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('3 小时').last);
    await tester.pumpAndSettle();
    expect(client.requests.map((request) => request.type), [
      'SetTemperatureHistoryRetentionHours',
      'GetTemperatureHistory',
    ]);
    expect(client.requests.first.data, {'value': 3});
    expect(controller.temperatureHistory.retentionHours, 3);
  });

  testWidgets('curve edits can be discarded and low RPM saves are confirmed', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1000, 700));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final client = _RecordingIpcClient();
    final controller = AppController(client: client)
      ..connection = CoreConnection.connected
      ..config = {
        'fanCurve': [
          {'temperature': 30, 'rpm': 1000},
          {'temperature': 60, 'rpm': 2200},
          {'temperature': 90, 'rpm': 3600},
        ],
        'unknown': {'preserved': true},
      };
    final scrollController = ScrollController();
    addTearDown(controller.dispose);
    addTearDown(scrollController.dispose);

    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: app.FanCurvePage(
            controller: controller,
            scrollController: scrollController,
          ),
        ),
      ),
    );

    Future<void> dragFirstPointLow() async {
      await tester.drag(
        find.byKey(const ValueKey('fan-curve-point-0')),
        const Offset(0, 60),
      );
      await tester.pumpAndSettle();
      expect(find.textContaining('有未保存修改'), findsOneWidget);
    }

    await dragFirstPointLow();
    await tester.tap(find.text('保存'));
    await tester.pumpAndSettle();
    expect(find.text('低转速风险'), findsOneWidget);
    await tester.tap(find.text('取消'));
    await tester.pumpAndSettle();
    expect(client.requests, isEmpty);

    await tester.tap(find.text('放弃修改'));
    await tester.pumpAndSettle();
    expect(find.textContaining('有未保存修改'), findsNothing);

    await dragFirstPointLow();
    await tester.tap(find.text('保存'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('仍然保存'));
    await tester.pumpAndSettle();

    expect(client.requests.single.type, 'SetFanCurve');
    final sentCurve = client.requests.single.data as List<dynamic>;
    expect((sentCurve.first as Map<String, dynamic>)['rpm'], lessThan(1000));
    expect(controller.config?['fanCurve'], sentCurve);
    expect(controller.config?['unknown'], {'preserved': true});
    expect(find.textContaining('有未保存修改'), findsNothing);
  });

  testWidgets('profile switching protects an unsaved curve draft', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1000, 700));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    const balancedCurve = [
      {'temperature': 30, 'rpm': 1500},
      {'temperature': 60, 'rpm': 2200},
      {'temperature': 90, 'rpm': 3600},
    ];
    const quietCurve = [
      {'temperature': 30, 'rpm': 800},
      {'temperature': 60, 'rpm': 1800},
      {'temperature': 90, 'rpm': 3200},
    ];
    final client = _RecordingIpcClient({
      'SetActiveFanCurveProfile': {
        'id': 'quiet',
        'name': '静音',
        'curve': quietCurve,
      },
    });
    final controller = AppController(client: client)
      ..connection = CoreConnection.connected
      ..config = {
        'fanCurve': balancedCurve,
        'fanCurveProfiles': [
          {'id': 'balanced', 'name': '均衡', 'curve': balancedCurve},
          {'id': 'quiet', 'name': '静音', 'curve': quietCurve},
        ],
        'activeFanCurveProfileId': 'balanced',
        'unknown': {'preserved': true},
      };
    final scrollController = ScrollController();
    addTearDown(controller.dispose);
    addTearDown(scrollController.dispose);

    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: app.FanCurvePage(
            controller: controller,
            scrollController: scrollController,
          ),
        ),
      ),
    );
    final selectorRect = tester.getRect(
      find.byKey(const ValueKey('fan-curve-profile-selector')),
    );
    final saveRect = tester.getRect(
      find.byKey(const ValueKey('fan-curve-save')),
    );
    expect(selectorRect.center.dy, closeTo(saveRect.center.dy, 0.1));

    await tester.drag(
      find.byKey(const ValueKey('fan-curve-point-0')),
      const Offset(0, 60),
    );
    await tester.pumpAndSettle();
    expect(find.textContaining('有未保存修改'), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('fan-curve-profile-selector')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('静音').last);
    await tester.pumpAndSettle();
    expect(find.text('曲线尚未保存'), findsOneWidget);
    expect(client.requests, isEmpty);

    await tester.tap(find.text('放弃并切换'));
    await tester.pumpAndSettle();
    expect(client.requests.single.type, 'SetActiveFanCurveProfile');
    expect(client.requests.single.data, {'id': 'quiet'});
    expect(controller.config?['activeFanCurveProfileId'], 'quiet');
    expect(controller.config?['fanCurve'], quietCurve);
    expect(controller.config?['unknown'], {'preserved': true});
    expect(find.textContaining('有未保存修改'), findsNothing);
  });

  testWidgets('profile menu creates, renames, and deletes a profile', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1000, 700));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    const balancedCurve = [
      {'temperature': 30, 'rpm': 1500},
      {'temperature': 60, 'rpm': 2200},
      {'temperature': 90, 'rpm': 3600},
    ];
    const quietCurve = [
      {'temperature': 30, 'rpm': 1000},
      {'temperature': 60, 'rpm': 1800},
      {'temperature': 90, 'rpm': 3200},
    ];
    final responses = <String, Object?>{
      'SaveFanCurveProfile': {
        'id': 'gaming',
        'name': '游戏',
        'curve': balancedCurve,
      },
      'DeleteFanCurveProfile': true,
    };
    final client = _RecordingIpcClient(responses);
    final controller = AppController(client: client)
      ..connection = CoreConnection.connected
      ..config = {
        'fanCurve': balancedCurve,
        'fanCurveProfiles': [
          {
            'id': 'balanced',
            'name': '均衡',
            'curve': balancedCurve,
            'future': 'preserved',
          },
          {'id': 'quiet', 'name': '静音', 'curve': quietCurve},
        ],
        'activeFanCurveProfileId': 'balanced',
        'unknown': {'preserved': true},
      };
    final scrollController = ScrollController();
    addTearDown(controller.dispose);
    addTearDown(scrollController.dispose);

    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: app.FanCurvePage(
            controller: controller,
            scrollController: scrollController,
          ),
        ),
      ),
    );

    await tester.tap(find.byKey(const ValueKey('fan-curve-profile-menu')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('新建方案'));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const ValueKey('fan-curve-profile-name-input')),
      '游戏',
    );
    await tester.tap(find.text('确定'));
    await tester.pumpAndSettle();
    expect(client.requests.single.type, 'SaveFanCurveProfile');
    expect(client.requests.single.data, {
      'id': '',
      'name': '游戏',
      'curve': balancedCurve,
      'setActive': true,
    });
    expect(controller.config?['activeFanCurveProfileId'], 'gaming');

    client.requests.clear();
    responses['SaveFanCurveProfile'] = {
      'id': 'gaming',
      'name': '性能',
      'curve': balancedCurve,
    };
    await tester.tap(find.byKey(const ValueKey('fan-curve-profile-menu')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('重命名'));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const ValueKey('fan-curve-profile-name-input')),
      '性能',
    );
    await tester.tap(find.text('确定'));
    await tester.pumpAndSettle();
    expect(client.requests.single.type, 'SaveFanCurveProfile');
    expect((client.requests.single.data as Map)['id'], 'gaming');
    expect((client.requests.single.data as Map)['setActive'], isFalse);
    expect(
      ((controller.config?['fanCurveProfiles'] as List).first as Map)['future'],
      'preserved',
    );
    expect(find.text('性能'), findsOneWidget);

    client.requests.clear();
    await tester.tap(find.byKey(const ValueKey('fan-curve-profile-menu')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('删除方案'));
    await tester.pumpAndSettle();
    expect(find.text('删除曲线方案？'), findsOneWidget);
    await tester.tap(find.text('删除').last);
    await tester.pumpAndSettle();
    expect(client.requests.single.type, 'DeleteFanCurveProfile');
    expect(client.requests.single.data, {'id': 'gaming'});
    expect(controller.config?['activeFanCurveProfileId'], 'quiet');
    expect(controller.config?['fanCurve'], quietCurve);
    expect(controller.config?['unknown'], {'preserved': true});
  });

  testWidgets('profile transfer saves drafts and syncs imported config', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1000, 700));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    var clipboardText = '';
    tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
      SystemChannels.platform,
      (call) async {
        if (call.method == 'Clipboard.setData') {
          clipboardText = (call.arguments as Map)['text'] as String;
        } else if (call.method == 'Clipboard.getData') {
          return {'text': clipboardText};
        }
        return null;
      },
    );
    addTearDown(
      () => tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
        SystemChannels.platform,
        null,
      ),
    );
    const balancedCurve = [
      {'temperature': 30, 'rpm': 1500},
      {'temperature': 60, 'rpm': 2200},
      {'temperature': 90, 'rpm': 3600},
    ];
    const importedCurve = [
      {'temperature': 30, 'rpm': 1000},
      {'temperature': 60, 'rpm': 1800},
      {'temperature': 90, 'rpm': 3200},
    ];
    final responses = <String, Object?>{
      'SetFanCurve': true,
      'ExportFanCurveProfiles': 'B2C1.exported',
      'ImportFanCurveProfiles': true,
      'GetConfig': {
        'fanCurve': importedCurve,
        'fanCurveProfiles': [
          {'id': 'balanced', 'name': '均衡', 'curve': balancedCurve},
          {'id': 'imported', 'name': '导入项', 'curve': importedCurve},
        ],
        'activeFanCurveProfileId': 'imported',
        'unknown': {'preserved': true},
      },
    };
    final client = _RecordingIpcClient(responses);
    final controller = AppController(client: client)
      ..connection = CoreConnection.connected
      ..config = {
        'fanCurve': balancedCurve,
        'fanCurveProfiles': [
          {'id': 'balanced', 'name': '均衡', 'curve': balancedCurve},
        ],
        'activeFanCurveProfileId': 'balanced',
        'unknown': {'preserved': true},
      };
    final scrollController = ScrollController();
    addTearDown(controller.dispose);
    addTearDown(scrollController.dispose);

    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: app.FanCurvePage(
            controller: controller,
            scrollController: scrollController,
          ),
        ),
      ),
    );
    await tester.drag(
      find.byKey(const ValueKey('fan-curve-point-0')),
      const Offset(0, -30),
    );
    await tester.pumpAndSettle();
    expect(find.textContaining('有未保存修改'), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('fan-curve-profile-menu')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('导出并复制方案码'));
    await tester.pumpAndSettle();
    expect(client.requests.map((request) => request.type), [
      'SetFanCurve',
      'ExportFanCurveProfiles',
    ]);
    expect(clipboardText, 'B2C1.exported');
    expect(find.textContaining('有未保存修改'), findsNothing);

    client.requests.clear();
    clipboardText = '  B2C1.imported  ';
    await tester.tap(find.byKey(const ValueKey('fan-curve-profile-menu')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('从剪贴板导入方案码'));
    await tester.pumpAndSettle();
    expect(client.requests.map((request) => request.type), [
      'ImportFanCurveProfiles',
      'GetConfig',
    ]);
    expect(client.requests.first.data, {'code': 'B2C1.imported'});
    expect(controller.config?['activeFanCurveProfileId'], 'imported');
    expect(controller.config?['fanCurve'], importedCurve);
    expect(controller.config?['unknown'], {'preserved': true});
    expect(find.text('导入项'), findsOneWidget);
  });

  testWidgets('mouse wheel scrolling animates and accumulates ticks', (
    tester,
  ) async {
    final controller = SmoothScrollController();
    addTearDown(controller.dispose);
    await tester.pumpWidget(
      MaterialApp(
        home: ListView.builder(
          controller: controller,
          itemExtent: 100,
          itemCount: 20,
          itemBuilder: (_, index) => Text('$index'),
        ),
      ),
    );

    final pointer = TestPointer(1, PointerDeviceKind.mouse);
    final center = tester.getCenter(find.byType(ListView));
    await tester.sendEventToBinding(pointer.hover(center));
    await tester.sendEventToBinding(pointer.scroll(const Offset(0, 120)));
    expect(controller.offset, 0);
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 40));
    expect(controller.offset, inExclusiveRange(0, 120));

    await tester.sendEventToBinding(pointer.scroll(const Offset(0, 120)));
    await tester.pumpAndSettle();
    expect(controller.offset, closeTo(240, 0.1));
  });

  test(
    'Go IPC handles Dart framing, concurrency, events, and reconnect',
    () async {
      final pipeName =
          'THRM-Flutter-Test-$pid-${DateTime.now().microsecondsSinceEpoch}';
      final fixture = await Process.start(
        'go',
        ['run', 'tool/ipc_fixture.go'],
        workingDirectory: Directory.current.path,
        environment: {...Platform.environment, 'THRM_IPC_PIPE_NAME': pipeName},
      );
      final stderr = StringBuffer();
      final ready = Completer<void>();
      final restarted = Completer<void>();
      var exited = false;
      final exitCode = fixture.exitCode.then((code) {
        exited = true;
        return code;
      });
      final stdoutSubscription = fixture.stdout
          .transform(utf8.decoder)
          .transform(const LineSplitter())
          .listen((line) {
            if (line == 'READY' && !ready.isCompleted) ready.complete();
            if (line == 'RESTARTED' && !restarted.isCompleted) {
              restarted.complete();
            }
          });
      final stderrSubscription = fixture.stderr
          .transform(utf8.decoder)
          .listen(stderr.write);
      final probe = IpcClient(
        endpoint: IpcClient.endpointForPipe(pipeName),
        timeout: const Duration(seconds: 5),
      );

      try {
        await ready.future.timeout(const Duration(seconds: 15));
        await probe.connect();
        final eventFuture = probe.events
            .firstWhere((event) => event['type'] == 'fixture-event')
            .timeout(const Duration(seconds: 5));
        final configFuture = probe.request('GetConfig');
        final pingResults = await Future.wait(
          List.generate(
            16,
            (sequence) => probe.request('Ping', data: {'sequence': sequence}),
          ),
        );
        expect(pingResults, List.generate(16, (index) => index));

        final config = await configFuture as Map<String, dynamic>;
        expect((config['blob'] as String).length, 16 * 1024);
        expect(config['unknown'], {'preserved': true});
        final status =
            await probe.request('GetDeviceStatus') as Map<String, dynamic>;
        expect(status['connected'], isTrue);
        expect(
          (status['currentData'] as Map<String, dynamic>)['currentRpm'],
          2345,
        );
        final event = await eventFuture;
        expect(
          ((event['data'] as Map<String, dynamic>)['blob'] as String).length,
          8 * 1024,
        );

        final controller = AppController(
          client: IpcClient(
            endpoint: IpcClient.endpointForPipe(pipeName),
            timeout: const Duration(seconds: 5),
          ),
        )..start();
        try {
          for (var attempt = 0; attempt < 100; attempt++) {
            if (controller.connection == CoreConnection.connected) break;
            await Future<void>.delayed(const Duration(milliseconds: 20));
          }
          expect(controller.connection, CoreConnection.connected);
          expect(controller.deviceConnected, isTrue);
          expect(controller.deviceStatus?['model'], 'THRM fixture');
          expect(controller.config?['unknown'], {'preserved': true});
          expect(controller.temperatureHistory.enabled, isTrue);
          expect(controller.temperatureHistory.points, hasLength(6));
          await controller.setAutoControl(false);
          expect(controller.config?['autoControl'], isFalse);
          expect(controller.config?['unknown'], {'preserved': true});
          const curve = <Map<String, int>>[
            {'temperature': 30, 'rpm': 900},
            {'temperature': 60, 'rpm': 2300},
            {'temperature': 90, 'rpm': 3700},
          ];
          expect(await controller.setFanCurve(curve), isTrue);
          expect(controller.config?['fanCurve'], curve);
          expect(controller.config?['unknown'], {'preserved': true});
          final fixtureConfig =
              await probe.request('GetConfig') as Map<String, dynamic>;
          expect(fixtureConfig['fanCurve'], curve);
          expect(await controller.setActiveFanCurveProfile('quiet'), isTrue);
          expect(controller.config?['activeFanCurveProfileId'], 'quiet');
          expect(
            ((controller.config?['fanCurve'] as List).first as Map)['rpm'],
            800,
          );
          expect(controller.config?['unknown'], {'preserved': true});
          final switchedConfig =
              await probe.request('GetConfig') as Map<String, dynamic>;
          expect(switchedConfig['activeFanCurveProfileId'], 'quiet');
          expect(
            await controller.saveFanCurveProfile(
              id: '',
              name: '游戏',
              curve: curve,
              setActive: true,
            ),
            isTrue,
          );
          final createdId =
              controller.config?['activeFanCurveProfileId'] as String;
          expect(createdId, startsWith('fixture-'));
          expect(
            await controller.saveFanCurveProfile(
              id: createdId,
              name: '性能',
              curve: curve,
              setActive: false,
            ),
            isTrue,
          );
          final profiles = controller.config?['fanCurveProfiles'] as List;
          expect(
            (profiles.cast<Map>().singleWhere(
              (profile) => profile['id'] == createdId,
            ))['name'],
            '性能',
          );
          expect(await controller.deleteFanCurveProfile(createdId), isTrue);
          expect(controller.config?['activeFanCurveProfileId'], 'quiet');
          expect(controller.config?['unknown'], {'preserved': true});
          final managedConfig =
              await probe.request('GetConfig') as Map<String, dynamic>;
          expect(managedConfig['activeFanCurveProfileId'], 'quiet');
          final exportedCode = await controller.exportFanCurveProfiles();
          expect(exportedCode, startsWith('B2C1.'));
          expect(
            await controller.importFanCurveProfiles(exportedCode!),
            isTrue,
          );
          final importedProfiles =
              controller.config?['fanCurveProfiles'] as List;
          expect(importedProfiles, hasLength(4));
          expect(controller.config?['activeFanCurveProfileId'], isNot('quiet'));
          expect(
            ((controller.config?['fanCurve'] as List).first as Map)['rpm'],
            800,
          );
          expect(controller.config?['unknown'], {'preserved': true});
          final importedConfig =
              await probe.request('GetConfig') as Map<String, dynamic>;
          expect(importedConfig['fanCurveProfiles'], hasLength(4));
          expect(
            importedConfig['activeFanCurveProfileId'],
            controller.config?['activeFanCurveProfileId'],
          );
          expect(
            await controller.setTemperatureHistoryRetentionHours(3),
            isTrue,
          );
          expect(controller.temperatureHistory.retentionHours, 3);
          expect(await controller.setTemperatureHistoryEnabled(false), isTrue);
          expect(controller.temperatureHistory.enabled, isFalse);
          expect(controller.temperatureHistory.points, isEmpty);
          expect(await controller.setTemperatureHistoryEnabled(true), isTrue);
          expect(controller.temperatureHistory.enabled, isTrue);
        } finally {
          controller.dispose();
        }

        final disconnected = probe.disconnected;
        expect(await probe.request('RestartProbe'), 'restarting');
        await disconnected.timeout(const Duration(seconds: 5));
        await restarted.future.timeout(const Duration(seconds: 5));
        await probe.reconnect();
        expect(await probe.request('Ping'), 'pong');

        expect(await probe.request('QuitFixture'), 'bye');
        expect(
          await exitCode.timeout(const Duration(seconds: 5)),
          0,
          reason: stderr.toString(),
        );
      } finally {
        await probe.dispose();
        if (!exited) fixture.kill();
        await stdoutSubscription.cancel();
        await stderrSubscription.cancel();
      }
    },
    timeout: const Timeout(Duration(seconds: 35)),
  );
}

class _RecordingIpcClient extends IpcClient {
  _RecordingIpcClient([this.responses = const {}]) : super(endpoint: 'test');

  final Map<String, Object?> responses;
  final requests = <({String type, Object? data})>[];

  @override
  bool get isConnected => true;

  @override
  Future<Object?> request(String type, {Object? data}) async {
    requests.add((type: type, data: data));
    return responses[type] ?? true;
  }
}
