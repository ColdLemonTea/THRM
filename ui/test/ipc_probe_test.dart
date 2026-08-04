import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:thrm_ui/ipc_probe.dart';

void main() {
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
      final probe = IpcProbe(
        endpoint: IpcProbe.endpointForPipe(pipeName),
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
        final event = await eventFuture;
        expect(
          ((event['data'] as Map<String, dynamic>)['blob'] as String).length,
          8 * 1024,
        );

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
