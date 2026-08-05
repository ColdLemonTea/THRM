import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:ui';

import 'package:flutter/widgets.dart';
import 'package:flutter_acrylic/flutter_acrylic.dart' as acrylic;
import 'package:window_manager/window_manager.dart';

import 'ipc_probe.dart';

const _defaultSize = Size(1024, 768);
const _minimumSize = Size(800, 600);

int? windowsBuildNumber(String version) {
  final match = RegExp(
    r'(?:10\.0\.|\bbuild\s+)(\d+)',
    caseSensitive: false,
  ).firstMatch(version);
  return int.tryParse(match?.group(1) ?? '');
}

class DesktopWindowState {
  const DesktopWindowState({
    this.width = 1024,
    this.height = 768,
    this.x = -1,
    this.y = -1,
    this.maximised = false,
  });

  factory DesktopWindowState.fromJson(Object? value) {
    if (value is! Map) return const DesktopWindowState();
    final width = value['width'];
    final height = value['height'];
    return DesktopWindowState(
      width: width is num && width >= _minimumSize.width
          ? width.toDouble()
          : _defaultSize.width,
      height: height is num && height >= _minimumSize.height
          ? height.toDouble()
          : _defaultSize.height,
      x: value['x'] is num ? (value['x'] as num).toDouble() : -1,
      y: value['y'] is num ? (value['y'] as num).toDouble() : -1,
      maximised: value['maximised'] == true,
    );
  }

  final double width;
  final double height;
  final double x;
  final double y;
  final bool maximised;

  Map<String, Object> toJson() => {
    'width': width.round(),
    'height': height.round(),
    'x': x.round(),
    'y': y.round(),
    'maximised': maximised,
  };
}

class DesktopLifecycle with WindowListener {
  DesktopLifecycle._(this._lockFile, this._stateFile, this._state);

  RandomAccessFile? _lockFile;
  final File _stateFile;
  DesktopWindowState _state;
  Timer? _saveTimer;
  bool _closing = false;
  Future<void> Function()? onClose;

  static Future<DesktopLifecycle?> initialize() async {
    final directory = Directory(_configDirectory());
    await directory.create(recursive: true);
    final lockFile = await File(
      '${directory.path}${Platform.pathSeparator}flutter-ui.lock',
    ).open(mode: FileMode.append);
    try {
      lockFile.lockSync(FileLock.exclusive);
    } on FileSystemException {
      await lockFile.close();
      return null;
    }

    final stateFile = File(
      '${directory.path}${Platform.pathSeparator}window.json',
    );
    var state = const DesktopWindowState();
    try {
      state = DesktopWindowState.fromJson(
        jsonDecode(await stateFile.readAsString()),
      );
    } catch (_) {}

    await windowManager.ensureInitialized();
    await acrylic.Window.initialize();
    final lifecycle = DesktopLifecycle._(lockFile, stateFile, state);
    windowManager.addListener(lifecycle);
    await windowManager.setPreventClose(true);
    return lifecycle;
  }

  static Future<void> showExistingInstance() async {
    final client = IpcClient(timeout: const Duration(seconds: 2));
    try {
      await client.connect();
      await client.request('ShowWindow');
    } catch (_) {
      // The existing GUI may still be starting; the lock is the source of truth.
    } finally {
      await client.dispose();
    }
  }

  Future<void> showWhenReady() async {
    await windowManager.waitUntilReadyToShow(
      WindowOptions(
        size: Size(_state.width, _state.height),
        minimumSize: _minimumSize,
        center: _state.x < 0 || _state.y < 0,
        title: 'THRM',
      ),
    );
    if (_state.x >= 0 && _state.y >= 0) {
      await windowManager.setPosition(Offset(_state.x, _state.y));
    }
    if (_state.maximised) await windowManager.maximize();
    await show();
  }

  Future<void> show() async {
    if (await windowManager.isMinimized()) await windowManager.restore();
    await windowManager.show();
    await windowManager.focus();
  }

  Future<void> applyMaterial(String mode, {required bool dark}) async {
    if (!Platform.isWindows) return;
    final build = windowsBuildNumber(Platform.operatingSystemVersion);
    final effect = switch (mode) {
      'acrylic' => acrylic.WindowEffect.acrylic,
      'mica' || 'on' => acrylic.WindowEffect.mica,
      'tabbed' => acrylic.WindowEffect.tabbed,
      'off' => acrylic.WindowEffect.solid,
      _ =>
        build != null && build >= 22000
            ? acrylic.WindowEffect.mica
            : acrylic.WindowEffect.solid,
    };
    try {
      await acrylic.Window.setEffect(
        effect: effect,
        dark: dark,
        color: dark ? const Color(0xff202020) : const Color(0xfff3f3f3),
      );
    } catch (_) {
      await acrylic.Window.setEffect(
        effect: acrylic.WindowEffect.solid,
        dark: dark,
        color: dark ? const Color(0xff202020) : const Color(0xfff3f3f3),
      );
    }
  }

  Future<void> close() async {
    if (_closing) return;
    _closing = true;
    _saveTimer?.cancel();
    final closeHandler = onClose;
    if (closeHandler != null) unawaited(closeHandler());
    windowManager.removeListener(this);
    final lockFile = _lockFile;
    _lockFile = null;
    try {
      lockFile?.unlockSync();
      await lockFile?.close();
    } catch (_) {}
    await windowManager.setPreventClose(false);
    await windowManager.close();
  }

  void _scheduleSave() {
    _saveTimer?.cancel();
    _saveTimer = Timer(const Duration(milliseconds: 350), _persist);
  }

  Future<void> _persist() async {
    try {
      final maximised = await windowManager.isMaximized();
      if (maximised) {
        _state = DesktopWindowState(
          width: _state.width,
          height: _state.height,
          x: _state.x,
          y: _state.y,
          maximised: true,
        );
      } else {
        final size = await windowManager.getSize();
        final position = await windowManager.getPosition();
        _state = DesktopWindowState(
          width: size.width,
          height: size.height,
          x: position.dx,
          y: position.dy,
        );
      }
      final temporary = File('${_stateFile.path}.tmp');
      await temporary.writeAsString(
        const JsonEncoder.withIndent('  ').convert(_state.toJson()),
        flush: true,
      );
      await temporary.rename(_stateFile.path);
    } catch (_) {}
  }

  @override
  void onWindowClose() => unawaited(close());

  @override
  void onWindowMaximize() => _scheduleSave();

  @override
  void onWindowUnmaximize() => _scheduleSave();

  @override
  void onWindowMove() => _scheduleSave();

  @override
  void onWindowResize() => _scheduleSave();
}

String _configDirectory() {
  final home =
      Platform.environment[Platform.isWindows ? 'USERPROFILE' : 'HOME'];
  if (home == null || home.isEmpty) return Directory.systemTemp.path;
  if (Platform.isWindows) return '$home${Platform.pathSeparator}.thrm';
  final xdg = Platform.environment['XDG_CONFIG_HOME'];
  final base = xdg != null && Directory(xdg).isAbsolute
      ? xdg
      : '$home${Platform.pathSeparator}.config';
  return '$base${Platform.pathSeparator}thrm';
}
