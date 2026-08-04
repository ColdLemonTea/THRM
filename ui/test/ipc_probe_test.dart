import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:thrm_ui/app_controller.dart';
import 'package:thrm_ui/ipc_probe.dart';
import 'package:thrm_ui/main.dart' as app;
import 'package:thrm_ui/smooth_scroll.dart';

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
    expect(tester.takeException(), isNull);
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
          await controller.setAutoControl(false);
          expect(controller.config?['autoControl'], isFalse);
          expect(controller.config?['unknown'], {'preserved': true});
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
