import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:fluent_ui/fluent_ui.dart' as fluent;
import 'package:flutter/material.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:thrm_ui/app_controller.dart';
import 'package:thrm_ui/ipc_probe.dart';
import 'package:thrm_ui/main.dart' as app;
import 'package:thrm_ui/smooth_scroll.dart';
import 'package:thrm_ui/temperature_history.dart';

Widget _fluentTestApp(Widget home) => fluent.FluentApp(
  locale: const Locale('zh', 'CN'),
  supportedLocales: const [Locale('zh', 'CN')],
  builder: (_, child) =>
      ScaffoldMessenger(child: child ?? const SizedBox.shrink()),
  home: home,
);

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

  test('control inputs are normalized before reaching Core', () {
    expect(
      app.normalizeLightStripConfig({
        'mode': 'invalid',
        'speed': 'fast',
        'brightness': 120,
        'colors': [
          {'r': -1, 'g': 128, 'b': 300},
        ],
      }),
      {
        'mode': 'smart_temp',
        'speed': 'fast',
        'brightness': 100,
        'colors': [
          {'r': 0, 'g': 128, 'b': 255},
          {'r': 0, 'g': 255, 'b': 0},
          {'r': 0, 'g': 128, 'b': 255},
        ],
      },
    );
    expect(app.normalizeHotkey('shift + CTRL + f12'), 'Ctrl+Shift+F12');
    expect(app.normalizeHotkey('F12'), isNull);
    expect(app.parseDeviceDebugCommand('5A A5 27 02 29'), 0x27);
    expect(app.parseDeviceDebugCommand('GG'), isNull);
    expect(app.isLatestVersion('v1.2.3', 'v1.2.2'), isTrue);
    expect(app.isLatestVersion('v1.2.3', 'v1.3.0'), isFalse);
  });

  test('control IPC updates preserve unknown config fields', () async {
    final client = _RecordingIpcClient();
    final controller = AppController(client: client)
      ..config = {
        'gearLight': false,
        'unknown': {'preserved': true},
      };
    addTearDown(controller.dispose);

    expect(
      await controller.runControlRequest(
        'SetGearLight',
        data: {'enabled': true},
        configPatch: {'gearLight': true},
      ),
      isTrue,
    );
    const light = {
      'mode': 'static_single',
      'speed': 'medium',
      'brightness': 80,
      'colors': [
        {'r': 12, 'g': 34, 'b': 56},
      ],
    };
    expect(
      await controller.runControlRequest(
        'SetLightStrip',
        data: {'config': light},
        configPatch: const {'lightStrip': light},
      ),
      isTrue,
    );
    expect(client.requests.map((request) => request.type), [
      'SetGearLight',
      'SetLightStrip',
    ]);
    expect(client.requests[0].data, {'enabled': true});
    expect(client.requests[1].data, {'config': light});
    expect(controller.config?['gearLight'], isTrue);
    expect(controller.config?['unknown'], {'preserved': true});
  });

  test('recovery controls call Core and refresh local state', () async {
    final client = _RecordingIpcClient({
      'Connect': true,
      'GetConfig': {'unknown': true},
      'GetDeviceStatus': {
        'connected': true,
        'temperature': {'controlTemp': 45, 'bridgeOk': true},
      },
      'GetTemperatureHistory': const <String, dynamic>{},
      'TestTemperatureReading': {
        'controlTemp': 51,
        'bridgeOk': true,
        'cpuModel': 'Test CPU',
      },
      'RestartPawnIO': {'success': true},
      'ReinstallPawnIO': {'success': true},
    });
    final controller = AppController(client: client);
    addTearDown(controller.dispose);

    expect(await controller.connectDevice(), isTrue);
    expect(controller.deviceConnected, isTrue);
    expect(controller.temperature?['controlTemp'], 45);
    expect(await controller.disconnectDevice(), isTrue);
    expect(controller.deviceConnected, isFalse);
    expect(await controller.testTemperatureReading(), isTrue);
    expect(controller.temperature?['controlTemp'], 51);
    expect(controller.temperature?['cpuModel'], 'Test CPU');
    expect(await controller.restartPawnIO(), {'success': true});
    expect(await controller.reinstallPawnIO(), {'success': true});
    expect(client.requests.map((request) => request.type), [
      'Connect',
      'GetConfig',
      'GetDeviceStatus',
      'GetTemperatureHistory',
      'Disconnect',
      'TestTemperatureReading',
      'RestartPawnIO',
      'ReinstallPawnIO',
    ]);
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

  test('learned offset summary matches the legacy four-point rule', () {
    const curve = [
      (temperature: 30, rpm: 1000),
      (temperature: 40, rpm: 1400),
      (temperature: 50, rpm: 1800),
      (temperature: 60, rpm: 2200),
      (temperature: 70, rpm: 2600),
      (temperature: 80, rpm: 3000),
    ];
    expect(
      app.summarizeLearnedOffsets(curve, [
        50,
        -500,
        400,
        0,
        -300,
        200,
      ], 'balanced'),
      const [
        (index: 1, temperature: 40, rpm: -500),
        (index: 2, temperature: 50, rpm: 400),
        (index: 4, temperature: 70, rpm: -300),
        (index: 5, temperature: 80, rpm: 200),
      ],
    );
    expect(
      app
          .summarizeLearnedOffsets(curve, [
            50,
            -500,
            400,
            0,
            -300,
            200,
          ], 'cooling')
          .map((item) => item.rpm),
      [400, 200, 50],
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

  test(
    'fan feature inputs use the same ordering and schedule rules as Core',
    () {
      final rpm = app.normalizeManualGearRpmMap({
        '静音': {'低': 700, '中': 1800, '高': 1600},
        '标准': {'低': 1200, '中': 5000, '高': 2600},
      });
      expect((rpm['静音'] as Map)['低'], 800);
      expect((rpm['静音'] as Map)['高'], 1800);
      expect((rpm['标准'] as Map)['低'], 1800);
      expect((rpm['标准'] as Map)['中'], 4500);
      expect((rpm['超频'] as Map)['高'], 4500);

      final overnight = <String, dynamic>{
        'enabled': true,
        'weekdays': [1],
        'startTime': '22:00',
        'endTime': '07:00',
      };
      expect(
        app.scheduleRuleMatchesAt(overnight, DateTime(2026, 8, 3, 23)),
        isTrue,
      );
      expect(
        app.scheduleRuleMatchesAt(overnight, DateTime(2026, 8, 4, 6, 59)),
        isTrue,
      );
      expect(
        app.scheduleRuleMatchesAt(overnight, DateTime(2026, 8, 4, 7)),
        isFalse,
      );
    },
  );

  test('fan feature config updates preserve unknown fields', () async {
    final client = _RecordingIpcClient();
    final controller = AppController(client: client)
      ..config = {
        'smartControl': {'learning': true, 'future': 42},
        'unknown': {'preserved': true},
      };
    addTearDown(controller.dispose);

    expect(
      await controller.updateConfig({
        'smartControl': {'learning': false, 'future': 42},
      }),
      isTrue,
    );
    expect(client.requests.single.type, 'UpdateConfig');
    expect((client.requests.single.data as Map)['unknown'], {
      'preserved': true,
    });
    expect((controller.config?['smartControl'] as Map)['future'], 42);

    client.requests.clear();
    expect(await controller.setManualGear('强劲', '高'), isTrue);
    expect(client.requests.single.data, {'gear': '强劲', 'level': '高'});
    expect(controller.config?['manualGear'], '强劲');
    expect((controller.config?['manualGearLevels'] as Map)['强劲'], '高');
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
    expect(downsampleTemperatureHistory(appended.points, 2, 5), [
      appended.points.first,
      appended.points.last,
    ]);
    expect(temperatureHistoryGapThreshold(appended.points, 5), 30000);

    final rolling = readTemperatureHistorySnapshot({
      'enabled': true,
      'sampleIntervalSeconds': 5,
      'retentionHours': 1,
      'points': [
        for (var index = 0; index < 10; index++)
          {'timestamp': 1700000000000 + index * 5000, 'cpuTemp': 40 + index},
      ],
    }).points;
    final before = downsampleTemperatureHistory(rolling.sublist(0, 9), 4, 5);
    final after = downsampleTemperatureHistory(rolling.sublist(1), 4, 5);
    expect(
      before.where(
        (point) =>
            point.timestamp >= rolling[2].timestamp &&
            point.timestamp <= rolling[8].timestamp,
      ),
      after.where(
        (point) =>
            point.timestamp >= rolling[2].timestamp &&
            point.timestamp <= rolling[8].timestamp,
      ),
    );
  });

  testWidgets('desktop shell fits the minimum window', (tester) async {
    await tester.binding.setSurfaceSize(const Size(800, 600));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final semantics = tester.ensureSemantics();
    final controller = AppController();
    var releaseChecks = 0;
    addTearDown(controller.dispose);

    await tester.pumpWidget(
      _fluentTestApp(
        app.ThrmShell(
          controller: controller,
          fetchLatestRelease: (_) async {
            releaseChecks++;
            return (
              tag: 'v0.1.0',
              url: 'https://github.com/TIANLI0/THRM/releases/latest',
              body: '',
              installerUrl: '',
              prerelease: false,
            );
          },
        ),
      ),
    );
    expect(find.text('THRM · 状态'), findsNothing);
    expect(find.text('Core 离线'), findsNothing);
    expect(find.text('正在自动连接后台服务'), findsOneWidget);
    expect(tester.takeException(), isNull);

    final paneToggle = find.byKey(const ValueKey('navigation-pane-toggle'));
    final pane = find.byKey(const ValueKey('navigation-pane'));
    final paneElement = tester.element(pane);
    final compactItemCenters = [
      for (var index = 0; index < 4; index++)
        tester.getCenter(find.byKey(ValueKey('navigation-item-$index'))).dy,
    ];
    expect(
      compactItemCenters.first -
          tester
              .getCenter(find.byIcon(fluent.WindowsIcons.global_nav_button))
              .dy,
      closeTo(compactItemCenters[1] - compactItemCenters.first, 0.1),
    );
    expect(tester.getSize(pane).width, 50);
    expect(
      tester.getSize(find.byKey(const ValueKey('navigation-about-gap'))).height,
      3,
    );
    expect(releaseChecks, 0);
    for (var index = 0; index < 7; index++) {
      await tester.tap(paneToggle);
      await tester.pump();
      expect(tester.element(pane), same(paneElement));
      await tester.pump(const Duration(milliseconds: 50));
      expect(tester.getSize(pane).width, greaterThan(50));
      expect(tester.getSize(pane).width, lessThan(180));
      for (var item = 0; item < compactItemCenters.length; item++) {
        expect(
          tester.getCenter(find.byKey(ValueKey('navigation-item-$item'))).dy,
          closeTo(compactItemCenters[item], 0.1),
        );
      }
      await tester.pumpAndSettle();
      expect(tester.element(pane), same(paneElement));
      expect(tester.getSize(pane).width, index.isEven ? 180 : 50);
    }
    expect(tester.takeException(), isNull);
    expect(tester.getSize(pane).width, 180);
    for (var index = 0; index < compactItemCenters.length; index++) {
      expect(
        tester.getCenter(find.byKey(ValueKey('navigation-item-$index'))).dy,
        closeTo(compactItemCenters[index], 0.1),
      );
    }
    expect(
      tester
          .widget<fluent.NavigationView>(find.byType(fluent.NavigationView))
          .pane
          ?.indicator,
      isA<fluent.StickyNavigationIndicator>(),
    );
    await tester.tap(find.byIcon(fluent.FluentIcons.info).last);
    await tester.pump(const Duration(milliseconds: 300));
    expect(find.byKey(const ValueKey('about-check-update')), findsOneWidget);
    expect(releaseChecks, 1);
    expect(tester.takeException(), isNull);
    semantics.dispose();
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
      _fluentTestApp(
        app.StatusPage(
          controller: controller,
          scrollController: scrollController,
        ),
      ),
    );

    expect(find.text('42.5W'), findsOneWidget);
    expect(find.text('80W'), findsOneWidget);
    expect(find.byIcon(fluent.FluentIcons.lightning_bolt), findsNWidgets(2));
    expect(find.text('PawnIO 读取失败'), findsOneWidget);
    scrollController.jumpTo(scrollController.position.maxScrollExtent);
    await tester.pump();
    expect(
      tester
          .widget<fluent.ToggleSwitch>(find.byType(fluent.ToggleSwitch))
          .checked,
      isTrue,
    );
  });

  testWidgets('control page selects sensors and records hotkeys', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1000, 700));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final client = _RecordingIpcClient({
      'TestTemperatureReading': {'controlTemp': 51, 'bridgeOk': true},
    });
    final controller = AppController(client: client)
      ..connection = CoreConnection.connected
      ..deviceConnected = true
      ..deviceStatus = {'model': 'BS2PRO'}
      ..config = {
        'lightStrip': {
          'mode': 'off',
          'speed': 'medium',
          'brightness': 100,
          'colors': [],
        },
        'customSpeedRPM': 2000,
        'legionFnQSupport': {'supported': false},
        'manualGearToggleHotkey': 'Ctrl+M',
        'themeMode': 'system',
        'unknown': {'preserved': true},
      }
      ..temperature = {
        'cpuModel': 'Test CPU',
        'cpuSensors': [
          {'key': 'average', 'name': 'Core Average', 'value': 41},
          {'key': 'core-0', 'name': 'P-Core #0', 'value': 36},
        ],
        'gpuDevices': [],
        'gpuSensors': [],
      };
    final scrollController = ScrollController();
    addTearDown(controller.dispose);
    addTearDown(scrollController.dispose);

    await tester.pumpWidget(
      _fluentTestApp(
        app.ControlPage(
          controller: controller,
          scrollController: scrollController,
        ),
      ),
    );
    expect(find.text('连接与恢复'), findsOneWidget);
    expect(
      find.byKey(const ValueKey('control-device-disconnect')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey('control-temperature-test')),
      findsOneWidget,
    );
    if (Platform.isWindows) {
      expect(
        find.byKey(const ValueKey('control-pawnio-restart')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('control-pawnio-reinstall')),
        findsOneWidget,
      );
    }
    final temperatureTest = find.byKey(
      const ValueKey('control-temperature-test'),
    );
    await tester.tap(temperatureTest);
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 250));
    expect(find.text('温度读取完成'), findsOneWidget);
    final notice = find.ancestor(
      of: find.text('温度读取完成'),
      matching: find.byType(fluent.InfoBar),
    );
    expect(tester.widget<fluent.InfoBar>(notice).onClose, isNull);
    expect(
      find.descendant(of: notice, matching: find.byType(fluent.IconButton)),
      findsNothing,
    );
    expect(
      find.descendant(
        of: notice,
        matching: find.byIcon(fluent.FluentIcons.completed_solid),
      ),
      findsOneWidget,
    );
    final noticeSize = tester.getSize(notice);
    expect(noticeSize, isNot(Size.zero));
    await tester.pump(const Duration(milliseconds: 125));
    expect(tester.getSize(notice), noticeSize);
    await tester.pump(const Duration(milliseconds: 125));
    expect(tester.getSize(notice), noticeSize);
    final fade = find.ancestor(
      of: find.text('温度读取完成'),
      matching: find.byType(FadeTransition),
    );
    final fadeOpacity = tester.widget<FadeTransition>(fade).opacity;
    await tester.pump(const Duration(milliseconds: 2700));
    expect(find.text('温度读取完成'), findsOneWidget);
    await tester.pump(const Duration(milliseconds: 50));
    expect(find.text('温度读取完成'), findsOneWidget);
    await tester.pump(const Duration(milliseconds: 125));
    expect(fadeOpacity.value, inExclusiveRange(0, 1));
    expect(find.text('温度读取完成'), findsOneWidget);
    await tester.pump(const Duration(milliseconds: 125));
    expect(find.text('温度读取完成'), findsNothing);
    client.requests.clear();
    Future<void> scrollTo(Finder finder) async {
      for (var index = 0; index < 20 && finder.evaluate().isEmpty; index++) {
        final position = scrollController.position;
        scrollController.jumpTo(
          (position.pixels + 420).clamp(0.0, position.maxScrollExtent),
        );
        await tester.pumpAndSettle();
      }
      expect(finder, findsOneWidget);
      await tester.ensureVisible(finder);
      await tester.pumpAndSettle();
    }

    final sensors = find.byKey(const ValueKey('control-cpu-sensors'));
    await scrollTo(sensors);
    final temperatureSource = find.byKey(
      const ValueKey('control-temperature-source'),
    );
    expect(
      tester.getSize(sensors).width,
      tester.getSize(temperatureSource).width,
    );
    expect(
      tester.getSize(sensors).height,
      tester.getSize(temperatureSource).height,
    );
    expect(tester.widget<fluent.DropDownButton>(sensors).disabled, isFalse);
    expect(
      tester.widget<fluent.ComboBox<String>>(temperatureSource).iconSize,
      12,
    );
    final sensorArrow = find.descendant(
      of: sensors,
      matching: find.byType(fluent.WindowsIcon),
    );
    expect(sensorArrow, findsOneWidget);
    final comboArrow = find.descendant(
      of: temperatureSource,
      matching: find.byType(fluent.WindowsIcon),
    );
    final sensorText = find.descendant(
      of: sensors,
      matching: find.text('自动选择'),
    );
    final comboText = find.descendant(
      of: temperatureSource,
      matching: find.text('CPU / GPU 最高温'),
    );
    expect(tester.getRect(sensorArrow).right, tester.getRect(comboArrow).right);
    expect(tester.getRect(sensorText).left, tester.getRect(comboText).left);
    expect(tester.widget<Text>(sensorText).textAlign, TextAlign.start);
    expect(tester.widget<fluent.WindowsIcon>(sensorArrow).size, 12);
    final gpuSensor = find.descendant(
      of: find.widgetWithText(fluent.InfoLabel, 'GPU 传感器'),
      matching: find.byWidgetPredicate((widget) => widget is fluent.ComboBox),
    );
    final temperatureCard = find.ancestor(
      of: temperatureSource,
      matching: find.byType(fluent.Card),
    );
    expect(gpuSensor, findsOneWidget);
    expect(temperatureCard, findsOneWidget);
    expect(
      tester.getRect(temperatureCard).bottom - tester.getRect(gpuSensor).bottom,
      greaterThanOrEqualTo(16),
    );
    await tester.tap(sensors);
    await tester.pumpAndSettle();
    expect(find.text('自动选择（推荐）'), findsOneWidget);
    expect(find.text('Core Average (41°C)'), findsOneWidget);
    await tester.tap(find.byKey(const ValueKey('control-cpu-sensor-average')));
    await tester.tap(find.byKey(const ValueKey('control-cpu-sensor-core-0')));
    await tester.tapAt(const Offset(10, 10));
    await tester.pumpAndSettle();
    expect(client.requests, hasLength(1));
    expect(client.requests.single.type, 'UpdateConfig');
    expect((client.requests.single.data as Map)['cpuSensors'], [
      'average',
      'core-0',
    ]);
    expect((client.requests.single.data as Map)['unknown'], {
      'preserved': true,
    });

    final recorder = find.byKey(const ValueKey('control-hotkey-manual'));
    await scrollTo(recorder);
    expect(find.text('点击录制'), findsNothing);
    final textBox = find.descendant(
      of: recorder,
      matching: find.byType(fluent.TextBox),
    );
    final clearHotkey = find.descendant(
      of: recorder,
      matching: find.byKey(const ValueKey('hotkey-clear')),
    );
    expect(clearHotkey, findsOneWidget);
    final textBoxRect = tester.getRect(textBox);
    final clearHotkeyRect = tester.getRect(clearHotkey);
    expect(clearHotkeyRect.size, const Size.square(24));
    expect(clearHotkeyRect.top, greaterThan(textBoxRect.top));
    expect(clearHotkeyRect.right, lessThan(textBoxRect.right));
    expect(clearHotkeyRect.bottom, lessThan(textBoxRect.bottom));
    await tester.tap(clearHotkey);
    await tester.pump();
    expect(tester.widget<fluent.TextBox>(textBox).controller?.text, isEmpty);
    await tester.tap(recorder);
    await tester.sendKeyDownEvent(LogicalKeyboardKey.controlLeft);
    await tester.sendKeyDownEvent(LogicalKeyboardKey.altLeft);
    await tester.sendKeyEvent(LogicalKeyboardKey.keyM);
    await tester.sendKeyUpEvent(LogicalKeyboardKey.altLeft);
    await tester.sendKeyUpEvent(LogicalKeyboardKey.controlLeft);
    await tester.pump();
    expect(
      tester.widget<fluent.TextBox>(textBox).controller?.text,
      'Ctrl+Alt+M',
    );

    final save = find.byKey(const ValueKey('control-hotkeys-save'));
    await scrollTo(save);
    await tester.tap(save);
    await tester.pumpAndSettle();
    final request = client.requests.last;
    expect(request.type, 'UpdateConfig');
    expect((request.data as Map)['manualGearToggleHotkey'], 'Ctrl+Alt+M');
    expect((request.data as Map)['unknown'], {'preserved': true});
    expect(tester.takeException(), isNull);
  });

  testWidgets('curve page renders the Core curve', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1000, 700));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final controller = AppController()
      ..config = {
        'autoControl': true,
        'fanCurve': [
          {'temperature': 30, 'rpm': 1000},
          {'temperature': 60, 'rpm': 2200},
          {'temperature': 90, 'rpm': 3600},
        ],
        'smartControl': {
          'learning': true,
          'learningBias': 'balanced',
          'learnedOffsets': [0, 200, 0],
        },
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
            'cpuPower': 35,
            'gpuPower': 70,
            'fanRpm': 1500,
          },
          {
            'timestamp': 1700000005000,
            'cpuTemp': 54,
            'gpuTemp': 58,
            'cpuPower': 42,
            'gpuPower': 78,
            'fanRpm': 1800,
          },
          {
            'timestamp': 1700000010000,
            'cpuTemp': 57,
            'gpuTemp': 61,
            'cpuPower': 50,
            'gpuPower': 85,
            'fanRpm': 2100,
          },
        ],
      })
      ..fanData = {'targetRpm': 2400};
    final scrollController = ScrollController();
    addTearDown(controller.dispose);
    addTearDown(scrollController.dispose);

    await tester.pumpWidget(
      _fluentTestApp(
        Scaffold(
          body: app.FanCurvePage(
            controller: controller,
            scrollController: scrollController,
          ),
        ),
      ),
    );

    expect(find.text('风扇曲线'), findsOneWidget);
    expect(find.text('3 个控制点 · 当前显示 Core 生效曲线'), findsOneWidget);
    expect(find.text('控温 65°C'), findsOneWidget);
    expect(find.text('目标 2400 RPM'), findsOneWidget);
    final pointer = TestPointer(7, PointerDeviceKind.mouse);
    final curveChart = find.byKey(const ValueKey('fan-curve-chart'));
    await tester.sendEventToBinding(
      pointer.hover(tester.getCenter(curveChart)),
    );
    await tester.pump();
    expect(find.text('温度：60 °C'), findsOneWidget);
    expect(find.text('基础曲线'), findsOneWidget);
    expect(find.text('2200 RPM'), findsOneWidget);
    expect(find.text('学习曲线'), findsOneWidget);
    expect(find.text('2400 RPM'), findsOneWidget);
    expect(
      tester
          .widget<AnimatedPositioned>(
            find.byKey(const ValueKey('fan-curve-tooltip-position')),
          )
          .duration,
      const Duration(milliseconds: 90),
    );
    expect(
      tester
          .widget<fluent.FlyoutContent>(find.byType(fluent.FlyoutContent))
          .useAcrylic,
      isTrue,
    );

    for (var index = 0; index < 4; index++) {
      scrollController.jumpTo(scrollController.position.maxScrollExtent);
      await tester.pumpAndSettle();
    }
    expect(find.text('硬件历史'), findsOneWidget);
    expect(find.text('3 个采样 · 后台保留 1 小时'), findsOneWidget);
    final historyChart = find.byKey(
      const ValueKey('temperature-history-chart'),
    );
    expect(historyChart, findsOneWidget);
    final temperatureChart = find.byKey(
      const ValueKey('temperature-history-temperature-chart'),
    );
    final powerChart = find.byKey(
      const ValueKey('temperature-history-power-chart'),
    );
    expect(temperatureChart, findsOneWidget);
    expect(powerChart, findsOneWidget);
    await tester.ensureVisible(temperatureChart);
    await tester.pumpAndSettle();
    await tester.sendEventToBinding(
      pointer.hover(tester.getCenter(temperatureChart)),
    );
    await tester.pump();
    expect(find.text('54 °C'), findsOneWidget);
    expect(find.text('58 °C'), findsOneWidget);
    expect(find.text('1800 RPM'), findsOneWidget);
    expect(find.text('42.0 W'), findsOneWidget);
    expect(find.text('78.0 W'), findsOneWidget);
    expect(
      find.byKey(const ValueKey('temperature-history-tooltip-position')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey('temperature-history-power-tooltip-position')),
      findsOneWidget,
    );
    expect(find.byType(fluent.FlyoutContent), findsNWidgets(2));
    expect(
      tester
          .widgetList<fluent.FlyoutContent>(find.byType(fluent.FlyoutContent))
          .every((flyout) => flyout.useAcrylic),
      isTrue,
    );

    await tester.ensureVisible(powerChart);
    await tester.pumpAndSettle();
    final powerRect = tester.getRect(powerChart);
    await tester.sendEventToBinding(
      pointer.hover(
        Offset(powerRect.left + powerRect.width * 0.8, powerRect.center.dy),
      ),
    );
    await tester.pump();
    expect(find.text('57 °C'), findsOneWidget);
    expect(find.text('61 °C'), findsOneWidget);
    expect(find.text('50.0 W'), findsOneWidget);
    expect(find.text('85.0 W'), findsOneWidget);

    await tester.ensureVisible(temperatureChart);
    await tester.pumpAndSettle();
    final chartRect = tester.getRect(temperatureChart);
    await tester.dragFrom(
      Offset(chartRect.left + chartRect.width * 0.5, chartRect.center.dy),
      Offset(chartRect.width * 0.35, 0),
    );
    await tester.pumpAndSettle();
    expect(find.text('重置缩放'), findsOneWidget);
    await tester.sendEventToBinding(
      pointer.hover(tester.getCenter(temperatureChart)),
    );
    await tester.pump();
    final historyPaint = find.byKey(
      const ValueKey('temperature-history-paint'),
    );
    final firstPainter = tester.widget<CustomPaint>(historyPaint).painter!;
    await tester.sendEventToBinding(
      pointer.hover(tester.getCenter(temperatureChart) + const Offset(1, 0)),
    );
    await tester.pump();
    final nextPainter = tester.widget<CustomPaint>(historyPaint).painter!;
    expect(nextPainter.shouldRepaint(firstPainter), isFalse);
    await tester.tap(find.text('重置缩放'));
    await tester.pumpAndSettle();
    expect(find.text('重置缩放'), findsNothing);

    await tester.dragFrom(
      Offset(chartRect.left + chartRect.width * 0.5, chartRect.center.dy),
      Offset(chartRect.width * 0.35, 0),
    );
    await tester.pumpAndSettle();
    await tester.tapAt(chartRect.center);
    await tester.pump(const Duration(milliseconds: 100));
    await tester.tapAt(chartRect.center);
    await tester.pumpAndSettle();
    expect(find.text('重置缩放'), findsNothing);
    expect(tester.takeException(), isNull);
  });

  testWidgets('remaining curve controls render and update Core config', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1100, 800));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    const curve = [
      {'temperature': 30, 'rpm': 1000},
      {'temperature': 60, 'rpm': 2200},
      {'temperature': 90, 'rpm': 3600},
    ];
    final client = _RecordingIpcClient();
    final controller = AppController(client: client)
      ..connection = CoreConnection.connected
      ..deviceConnected = true
      ..deviceStatus = {'model': 'BS2PRO'}
      ..config = {
        'autoControl': false,
        'fanCurve': curve,
        'fanCurveProfiles': [
          {'id': 'balanced', 'name': '均衡', 'curve': curve},
          {'id': 'quiet', 'name': '静音', 'curve': curve},
        ],
        'activeFanCurveProfileId': 'balanced',
        'manualGear': '标准',
        'manualLevel': '中',
        'smartControl': {
          'learning': true,
          'predictiveBoost': true,
          'learningBias': 'balanced',
          'filterTransientSpike': true,
          'targetTemp': 68,
          'learnedOffsets': [0, 100, 0],
          'future': 'preserved',
        },
        'speedAvoidance': {
          'enabled': true,
          'minRpm': 1900,
          'maxRpm': 2200,
          'marginRpm': 100,
          'emergencyBypassTemp': 80,
        },
        'timeCurveSchedule': {
          'enabled': true,
          'rules': [
            {
              'id': 'night',
              'name': '夜间',
              'enabled': true,
              'weekdays': [0, 1, 2, 3, 4, 5, 6],
              'startTime': '22:00',
              'endTime': '07:00',
              'curveProfileId': 'quiet',
            },
          ],
        },
        'unknown': {'preserved': true},
      };
    final scrollController = ScrollController();
    addTearDown(controller.dispose);
    addTearDown(scrollController.dispose);
    await tester.pumpWidget(
      _fluentTestApp(
        Scaffold(
          body: app.FanCurvePage(
            controller: controller,
            scrollController: scrollController,
          ),
        ),
      ),
    );

    Future<void> scrollTo(Finder finder) async {
      for (var index = 0; index < 16 && finder.evaluate().isEmpty; index++) {
        final position = scrollController.position;
        final next = (position.pixels + 420).clamp(
          0.0,
          position.maxScrollExtent,
        );
        scrollController.jumpTo(next);
        await tester.pumpAndSettle();
      }
      expect(finder, findsOneWidget);
      await tester.ensureVisible(finder);
      await tester.pumpAndSettle();
    }

    await scrollTo(find.text('手动挡位'));
    await tester.tap(find.text('强劲'));
    await tester.pumpAndSettle();
    expect(client.requests.single.type, 'SetManualGear');
    expect(controller.config?['manualGear'], '强劲');

    client.requests.clear();
    final learningSwitch = find.byKey(const ValueKey('fan-curve-learning'));
    await scrollTo(learningSwitch);
    final targetTemperature = find.byKey(
      const ValueKey('fan-curve-target-temperature'),
    );
    await scrollTo(targetTemperature);
    expect(
      tester.widget<fluent.NumberBox<int>>(targetTemperature).mode,
      fluent.SpinButtonPlacementMode.none,
    );
    await scrollTo(learningSwitch);
    await tester.tap(learningSwitch);
    await tester.pumpAndSettle();
    expect(client.requests.single.type, 'UpdateConfig');
    final sentConfig = client.requests.single.data as Map;
    expect((sentConfig['smartControl'] as Map)['learning'], isFalse);
    expect((sentConfig['smartControl'] as Map)['future'], 'preserved');
    expect(sentConfig['unknown'], {'preserved': true});

    client.requests.clear();
    await scrollTo(find.text('区间起点'));
    final addRule = find.byKey(const ValueKey('fan-curve-schedule-add'));
    await scrollTo(addRule);
    await tester.tap(addRule);
    await tester.pumpAndSettle();
    expect(client.requests.single.type, 'UpdateConfig');
    expect(
      ((client.requests.single.data as Map)['timeCurveSchedule']
          as Map)['rules'],
      hasLength(2),
    );
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
      _fluentTestApp(
        Scaffold(
          body: app.FanCurvePage(
            controller: controller,
            scrollController: scrollController,
          ),
        ),
      ),
    );
    for (var index = 0; index < 4; index++) {
      scrollController.jumpTo(scrollController.position.maxScrollExtent);
      await tester.pumpAndSettle();
    }
    final enabledSwitch = find.byKey(
      const ValueKey('temperature-history-enabled'),
    );
    await tester.ensureVisible(enabledSwitch);
    await tester.pumpAndSettle();
    expect(tester.widget<fluent.ToggleSwitch>(enabledSwitch).checked, isTrue);

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
    expect(find.byType(SnackBar), findsNothing);
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
      _fluentTestApp(
        Scaffold(
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
      _fluentTestApp(
        Scaffold(
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
    expect(selectorRect.height, closeTo(saveRect.height, 0.1));

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
      _fluentTestApp(
        Scaffold(
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
      _fluentTestApp(
        Scaffold(
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
