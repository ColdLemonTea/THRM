import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';

import 'ipc_probe.dart';

enum CoreConnection { connecting, connected, waiting }

const _temperatureMetadataKeys = [
  'cpuSensors',
  'gpuSensors',
  'cpuPowerSensors',
  'gpuPowerSensors',
  'gpuDevices',
  'cpuModel',
  'gpuModel',
  'selectedGpuDevice',
];

Map<String, dynamic> mergeTemperatureMetadata(
  Map<String, dynamic>? previous,
  Map<String, dynamic> incoming,
) {
  final merged = {...incoming};
  if (previous == null) return merged;
  for (final key in _temperatureMetadataKeys) {
    final value = incoming[key];
    final missing =
        !incoming.containsKey(key) ||
        value == null ||
        (value is String && value.isEmpty);
    if (missing && previous.containsKey(key)) {
      merged[key] = previous[key];
    }
  }
  return merged;
}

Map<String, dynamic> patchConfig(
  Map<String, dynamic>? current,
  Map<String, dynamic> patch,
) => {...?current, ...patch};

class AppController extends ChangeNotifier {
  AppController({IpcClient? client})
    : client = client ?? IpcClient(timeout: const Duration(seconds: 6));

  final IpcClient client;

  CoreConnection connection = CoreConnection.connecting;
  String? error;
  String? lastEvent;
  Map<String, dynamic>? config;
  Map<String, dynamic>? deviceStatus;
  Map<String, dynamic>? fanData;
  Map<String, dynamic>? temperature;
  bool deviceConnected = false;
  bool updatingAutoControl = false;

  StreamSubscription<Map<String, dynamic>>? _eventSubscription;
  bool _started = false;
  bool _disposed = false;
  bool _launchAttempted = false;

  void start() {
    if (_started) return;
    _started = true;
    _eventSubscription = client.events.listen(_handleEvent);
    unawaited(_connectionLoop());
  }

  Future<void> _connectionLoop() async {
    while (!_disposed) {
      connection = CoreConnection.connecting;
      _notify();
      var transportConnected = false;
      try {
        await client.connect();
        transportConnected = true;
        await _syncSnapshot();
        if (_disposed) return;
        connection = CoreConnection.connected;
        error = null;
        _launchAttempted = false;
        _notify();
        await client.disconnected;
        if (_disposed) return;
        connection = CoreConnection.waiting;
        error = 'Core 连接已断开，正在自动重连';
        _notify();
      } catch (caught) {
        await client.close();
        if (!transportConnected && !_launchAttempted) {
          _launchAttempted = true;
          await _launchCore();
        }
        if (_disposed) return;
        connection = CoreConnection.waiting;
        error = 'Core 暂不可用：$caught';
        _notify();
      }
      await Future<void>.delayed(const Duration(seconds: 2));
    }
  }

  Future<void> refresh() async {
    if (!client.isConnected) return;
    try {
      await _syncSnapshot();
      error = null;
    } catch (caught) {
      error = '刷新失败：$caught';
    }
    _notify();
  }

  Future<void> setAutoControl(bool enabled) async {
    if (!client.isConnected || updatingAutoControl) return;
    updatingAutoControl = true;
    error = null;
    _notify();
    try {
      await client.request('SetAutoControl', data: {'enabled': enabled});
      config = patchConfig(config, {'autoControl': enabled});
    } catch (caught) {
      error = '设置智能控温失败：$caught';
    } finally {
      updatingAutoControl = false;
      _notify();
    }
  }

  Future<void> _syncSnapshot() async {
    final responses = await Future.wait([
      client.request('GetConfig'),
      client.request('GetDeviceStatus'),
    ]);
    config = _jsonMap(responses[0], 'GetConfig');
    deviceStatus = _jsonMap(responses[1], 'GetDeviceStatus');
    deviceConnected = deviceStatus?['connected'] == true;
    fanData = _optionalMap(deviceStatus?['currentData']);
    final snapshotTemperature = _optionalMap(deviceStatus?['temperature']);
    if (snapshotTemperature != null) {
      temperature = mergeTemperatureMetadata(temperature, snapshotTemperature);
    }
  }

  void _handleEvent(Map<String, dynamic> event) {
    final type = event['type']?.toString();
    if (type == null) return;
    lastEvent = type;
    final data = event['data'];
    switch (type) {
      case 'fan-data-update':
        fanData = _optionalMap(data);
      case 'temperature-update':
        final update = _optionalMap(data);
        if (update != null) {
          temperature = mergeTemperatureMetadata(temperature, update);
        }
      case 'device-connected':
        deviceConnected = true;
        final info = _optionalMap(data);
        if (info != null) {
          deviceStatus = {...?deviceStatus, ...info, 'connected': true};
        }
      case 'device-disconnected':
        deviceConnected = false;
        deviceStatus = {...?deviceStatus, 'connected': false};
      case 'config-update':
        config = _optionalMap(data) ?? config;
    }
    _notify();
  }

  Map<String, dynamic> _jsonMap(Object? value, String request) {
    final map = _optionalMap(value);
    if (map == null) throw FormatException('$request 返回的不是 JSON object');
    return map;
  }

  Map<String, dynamic>? _optionalMap(Object? value) =>
      value is Map<String, dynamic> ? Map<String, dynamic>.from(value) : null;

  Future<bool> _launchCore() async {
    final names = Platform.isWindows
        ? const ['THRM Core.exe', 'BS2PRO-Core.exe']
        : const ['thrm-core', 'bs2pro-core'];
    final executableDir = File(Platform.resolvedExecutable).parent.path;
    for (final name in names) {
      final bundled = '$executableDir${Platform.pathSeparator}$name';
      if (await File(bundled).exists() && await _startDetached(bundled)) {
        return true;
      }
    }
    for (final name in names) {
      if (await _startDetached(name)) return true;
    }
    return false;
  }

  Future<bool> _startDetached(String executable) async {
    try {
      await Process.start(
        executable,
        const [],
        mode: ProcessStartMode.detached,
      );
      return true;
    } catch (_) {
      return false;
    }
  }

  void _notify() {
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    unawaited(_eventSubscription?.cancel());
    unawaited(client.dispose());
    super.dispose();
  }
}
