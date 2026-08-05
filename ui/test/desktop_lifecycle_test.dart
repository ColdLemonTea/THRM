import 'package:flutter_test/flutter_test.dart';
import 'package:thrm_ui/desktop_lifecycle.dart';

void main() {
  test('reads Windows build numbers used by Dart and Windows', () {
    expect(windowsBuildNumber('Windows 11 Pro 10.0 (Build 26200)'), 26200);
    expect(windowsBuildNumber('Microsoft Windows NT 10.0.19045.0'), 19045);
  });

  test('window state keeps the Wails window.json format and safe minimums', () {
    final state = DesktopWindowState.fromJson({
      'width': 1200,
      'height': 500,
      'x': 42,
      'y': 84,
      'maximised': true,
    });

    expect(state.width, 1200);
    expect(state.height, 768);
    expect(state.toJson(), {
      'width': 1200,
      'height': 768,
      'x': 42,
      'y': 84,
      'maximised': true,
    });
  });
}
