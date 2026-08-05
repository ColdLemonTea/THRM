import 'package:flutter_test/flutter_test.dart';
import 'package:thrm_ui/noise_test.dart';

void main() {
  test('noise analysis finds a prominent resonance and normalizes profile', () {
    final result = analyzeNoiseSamples(const [
      (rpm: 1000, actualRpm: 1000, db: -50),
      (rpm: 1500, actualRpm: 1500, db: -48),
      (rpm: 2000, actualRpm: 2000, db: -42),
      (rpm: 2500, actualRpm: 2500, db: -47),
      (rpm: 3000, actualRpm: 3000, db: -44),
    ]);

    expect(result.profile.first.db, 0);
    expect(result.resonance?.peakRpm, 2000);
    expect(result.resonance?.startRpm, 1500);
    expect(result.resonance?.endRpm, 2500);
    expect(result.lowConfidence, isFalse);
  });
}
