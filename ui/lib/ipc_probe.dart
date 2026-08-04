import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:dart_ipc/dart_ipc.dart' as ipc;

const _protocolVersion = '3.0';

class IpcProbe {
  IpcProbe({String? endpoint, this.timeout = const Duration(seconds: 3)})
    : endpoint = endpoint ?? endpointForPipe('THRM-IPC');

  final String endpoint;
  final Duration timeout;
  final _pending = <String, Completer<Map<String, dynamic>>>{};
  final _events = StreamController<Map<String, dynamic>>.broadcast();
  Socket? _socket;
  StreamSubscription<String>? _subscription;
  Completer<void>? _disconnectSignal;
  Future<void> _writes = Future.value();
  int _requestSerial = 0;

  static String endpointForPipe(String pipeName) {
    if (Platform.isWindows) return r'\\.\pipe\' + pipeName;
    if (Platform.isLinux) {
      return '${Directory.systemTemp.path}${Platform.pathSeparator}$pipeName.sock';
    }
    throw UnsupportedError('THRM Flutter UI 仅支持 Windows 和 Linux');
  }

  Stream<Map<String, dynamic>> get events => _events.stream;
  Future<void> get disconnected => _disconnectSignal?.future ?? Future.value();

  Future<void> connect() async {
    if (_socket != null) return;
    final socket = await ipc.connect(endpoint);
    _socket = socket;
    _disconnectSignal = Completer<void>();
    _subscription = socket
        .cast<List<int>>()
        .transform(utf8.decoder)
        .transform(const LineSplitter())
        .listen(
          (line) {
            try {
              _handleLine(line);
            } catch (error, stackTrace) {
              _disconnect(socket, error, stackTrace);
            }
          },
          onError: (Object error, StackTrace stackTrace) =>
              _disconnect(socket, error, stackTrace),
          onDone: () => _disconnect(socket, StateError('IPC 连接已关闭')),
          cancelOnError: true,
        );
  }

  Future<Object?> request(String type, {Object? data}) async {
    final socket = _socket;
    if (socket == null) throw StateError('IPC 尚未连接');

    final id =
        'flutter-${DateTime.now().microsecondsSinceEpoch}-${_requestSerial++}';
    final completer = Completer<Map<String, dynamic>>();
    _pending[id] = completer;
    final message = <String, Object?>{
      'protocolVersion': _protocolVersion,
      'requestId': id,
      'timestamp': DateTime.now().millisecondsSinceEpoch,
      'type': type,
      'data': data,
    };

    try {
      await _write(socket, utf8.encode('${jsonEncode(message)}\n'));
    } catch (error, stackTrace) {
      _pending.remove(id);
      _disconnect(socket, error, stackTrace);
      rethrow;
    }

    try {
      final response = await completer.future.timeout(timeout);
      if (response['success'] != true) {
        throw StateError(response['error']?.toString() ?? 'IPC 请求失败');
      }
      return response['data'];
    } finally {
      _pending.remove(id);
    }
  }

  Future<void> _write(Socket socket, List<int> bytes) async {
    final previous = _writes;
    final turn = Completer<void>();
    _writes = turn.future;
    try {
      await previous;
      if (!identical(_socket, socket)) throw StateError('IPC 连接已关闭');
      socket.add(bytes);
      await socket.flush();
    } finally {
      turn.complete();
    }
  }

  Future<void> reconnect({
    int attempts = 40,
    Duration delay = const Duration(milliseconds: 50),
  }) async {
    await close();
    Object? lastError;
    for (var attempt = 0; attempt < attempts; attempt++) {
      try {
        await connect();
        return;
      } catch (error) {
        lastError = error;
        if (attempt + 1 < attempts) await Future<void>.delayed(delay);
      }
    }
    throw StateError('IPC 重连失败: $lastError');
  }

  void _handleLine(String line) {
    final decoded = jsonDecode(line);
    if (decoded is! Map<String, dynamic>) {
      throw const FormatException('IPC 帧不是 JSON object');
    }
    if (decoded['isResponse'] == true) {
      final id = decoded['requestId'];
      if (id is! String || id.isEmpty) {
        throw const FormatException('IPC 响应缺少 requestId');
      }
      final completer = _pending.remove(id);
      if (completer == null) return;
      if (decoded['protocolVersion'] != _protocolVersion) {
        completer.completeError(
          FormatException('IPC 协议版本不匹配: ${decoded['protocolVersion']}'),
        );
      } else {
        completer.complete(decoded);
      }
      return;
    }
    if (decoded['isEvent'] == true) {
      _events.add(decoded);
      return;
    }
    throw const FormatException('未知 IPC 帧类型');
  }

  void _disconnect(Socket source, Object error, [StackTrace? stackTrace]) {
    if (!identical(_socket, source)) return;
    _socket = null;
    _subscription = null;
    final writes = _writes;
    unawaited(() async {
      try {
        await writes;
        await source.close();
      } catch (_) {}
    }());
    for (final completer in _pending.values) {
      if (!completer.isCompleted) {
        completer.completeError(error, stackTrace);
      }
    }
    _pending.clear();
    final signal = _disconnectSignal;
    if (signal != null && !signal.isCompleted) signal.complete();
  }

  Future<void> close() async {
    final socket = _socket;
    final subscription = _subscription;
    _socket = null;
    _subscription = null;
    await subscription?.cancel();
    await _writes;
    try {
      await socket?.close();
    } catch (_) {}
    final error = StateError('IPC 连接已关闭');
    for (final completer in _pending.values) {
      if (!completer.isCompleted) completer.completeError(error);
    }
    _pending.clear();
    final signal = _disconnectSignal;
    if (signal != null && !signal.isCompleted) signal.complete();
  }

  Future<void> dispose() async {
    await close();
    await _events.close();
  }
}

Future<({int configBytes, String eventType})> runIpcProbe({
  String? endpoint,
}) async {
  final probe = IpcProbe(endpoint: endpoint);
  try {
    await probe.connect();
    final event = probe.events.first.timeout(
      const Duration(seconds: 3),
      onTimeout: () => const <String, dynamic>{'type': '3 秒内无事件'},
    );
    final responses = await Future.wait([
      probe.request('Ping'),
      probe.request('GetConfig'),
    ]);
    if (responses.first != 'pong') {
      throw StateError('Ping 返回 ${responses.first}');
    }
    final firstEvent = await event;
    return (
      configBytes: utf8.encode(jsonEncode(responses.last)).length,
      eventType: firstEvent['type']?.toString() ?? '无类型事件',
    );
  } finally {
    await probe.dispose();
  }
}
