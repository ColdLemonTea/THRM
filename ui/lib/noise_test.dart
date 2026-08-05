import 'dart:async';
import 'dart:math' as math;
import 'dart:typed_data';

import 'package:record/record.dart';

typedef NoiseSample = ({int rpm, int actualRpm, double db});
typedef NoiseProfilePoint = ({int rpm, double db});
typedef ResonanceBand = ({
  int startRpm,
  int endRpm,
  int peakRpm,
  double prominenceDb,
});
typedef NoiseAnalysis = ({
  List<NoiseProfilePoint> profile,
  double totalRiseDb,
  int? kneeRpm,
  ResonanceBand? resonance,
  bool lowConfidence,
});

class NoiseMeter {
  final recorder = AudioRecorder();
  final levels = StreamController<double>.broadcast();
  final samples = <double>[];
  StreamSubscription<Uint8List>? subscription;

  Future<List<InputDevice>> devices() async {
    if (!await recorder.hasPermission()) throw StateError('没有麦克风权限');
    return recorder.listInputDevices();
  }

  Future<void> open([InputDevice? device]) async {
    await close();
    final stream = await recorder.startStream(
      RecordConfig(
        encoder: AudioEncoder.pcm16bits,
        sampleRate: 44100,
        numChannels: 1,
        device: device,
        autoGain: false,
        echoCancel: false,
        noiseSuppress: false,
      ),
    );
    subscription = stream.listen(_consume);
  }

  void _consume(Uint8List bytes) {
    final data = ByteData.sublistView(bytes);
    for (var offset = 0; offset + 1 < data.lengthInBytes; offset += 2) {
      samples.add(data.getInt16(offset, Endian.little) / 32768);
    }
    while (samples.length >= 4096) {
      levels.add(_aWeightedLevel(samples.sublist(0, 4096), 44100));
      samples.removeRange(0, 4096);
    }
  }

  Future<({double db, double rangeDb})> measure(Duration duration) async {
    final frames = <double>[];
    final sub = levels.stream.listen(frames.add);
    await Future<void>.delayed(duration);
    await sub.cancel();
    if (frames.isEmpty) return (db: -120.0, rangeDb: 0.0);
    frames.sort();
    final middle = frames.length ~/ 2;
    final median = frames.length.isOdd
        ? frames[middle]
        : (frames[middle - 1] + frames[middle]) / 2;
    return (db: median, rangeDb: frames.last - frames.first);
  }

  Future<void> close() async {
    await subscription?.cancel();
    subscription = null;
    samples.clear();
    if (await recorder.isRecording()) await recorder.stop();
  }

  Future<void> dispose() async {
    await close();
    await levels.close();
    await recorder.dispose();
  }
}

double _aWeightedLevel(List<double> input, int sampleRate) {
  final real = List<double>.generate(
    input.length,
    (index) =>
        input[index] *
        (0.5 - 0.5 * math.cos(2 * math.pi * index / (input.length - 1))),
  );
  final imaginary = List<double>.filled(input.length, 0);
  _fft(real, imaginary);
  var energy = 0.0;
  for (var index = 1; index < input.length ~/ 2; index++) {
    final frequency = index * sampleRate / input.length;
    if (frequency < 40 || frequency > 16000) continue;
    final square = frequency * frequency;
    final ra =
        (math.pow(12194, 2) * square * square) /
        ((square + math.pow(20.6, 2)) *
            math.sqrt(
              (square + math.pow(107.7, 2)) * (square + math.pow(737.9, 2)),
            ) *
            (square + math.pow(12194, 2)));
    final weight = math.pow(10, (20 * math.log(ra) / math.ln10 + 2) / 10);
    energy +=
        (real[index] * real[index] + imaginary[index] * imaginary[index]) *
        weight;
  }
  return energy > 0 ? 10 * math.log(energy) / math.ln10 : -120;
}

void _fft(List<double> real, List<double> imaginary) {
  final length = real.length;
  for (var index = 1, reverse = 0; index < length; index++) {
    var bit = length >> 1;
    for (; reverse & bit != 0; bit >>= 1) {
      reverse ^= bit;
    }
    reverse ^= bit;
    if (index < reverse) {
      final realValue = real[index];
      real[index] = real[reverse];
      real[reverse] = realValue;
      final imaginaryValue = imaginary[index];
      imaginary[index] = imaginary[reverse];
      imaginary[reverse] = imaginaryValue;
    }
  }
  for (var size = 2; size <= length; size <<= 1) {
    final angle = -2 * math.pi / size;
    final baseReal = math.cos(angle);
    final baseImaginary = math.sin(angle);
    for (var start = 0; start < length; start += size) {
      var unitReal = 1.0;
      var unitImaginary = 0.0;
      for (var offset = 0; offset < size ~/ 2; offset++) {
        final even = start + offset;
        final odd = even + size ~/ 2;
        final oddReal = real[odd] * unitReal - imaginary[odd] * unitImaginary;
        final oddImaginary =
            real[odd] * unitImaginary + imaginary[odd] * unitReal;
        final evenReal = real[even];
        final evenImaginary = imaginary[even];
        real[even] = evenReal + oddReal;
        imaginary[even] = evenImaginary + oddImaginary;
        real[odd] = evenReal - oddReal;
        imaginary[odd] = evenImaginary - oddImaginary;
        final nextReal = unitReal * baseReal - unitImaginary * baseImaginary;
        unitImaginary = unitReal * baseImaginary + unitImaginary * baseReal;
        unitReal = nextReal;
      }
    }
  }
}

NoiseAnalysis analyzeNoiseSamples(List<NoiseSample> samples) {
  final resolved = [
    for (final sample in samples)
      (
        rpm:
            sample.actualRpm > 0 &&
                (sample.actualRpm - sample.rpm).abs() >
                    math.max(120, sample.rpm * 0.06)
            ? (sample.actualRpm / 10).round() * 10
            : sample.rpm,
        db: sample.db,
      ),
  ]..sort((left, right) => left.rpm.compareTo(right.rpm));
  final merged = <({int rpm, double db, int count})>[];
  for (final sample in resolved) {
    final last = merged.lastOrNull;
    if (last != null && sample.rpm - last.rpm < 60) {
      merged[merged.length - 1] = (
        rpm: last.rpm,
        db: (last.db * last.count + sample.db) / (last.count + 1),
        count: last.count + 1,
      );
    } else {
      merged.add((rpm: sample.rpm, db: sample.db, count: 1));
    }
  }
  if (merged.isEmpty) {
    return (
      profile: const [],
      totalRiseDb: 0,
      kneeRpm: null,
      resonance: null,
      lowConfidence: true,
    );
  }
  final minimum = merged.map((item) => item.db).reduce(math.min);
  final profile = [
    for (final item in merged)
      (rpm: item.rpm, db: ((item.db - minimum) * 10).round() / 10),
  ];
  final totalRise = profile.map((item) => item.db).reduce(math.max);
  ResonanceBand? resonance;
  for (var index = 1; index < profile.length - 1; index++) {
    final previous = profile[index - 1];
    final current = profile[index];
    final next = profile[index + 1];
    final expected =
        previous.db +
        (next.db - previous.db) *
            (current.rpm - previous.rpm) /
            (next.rpm - previous.rpm);
    final prominence = current.db - expected;
    if (prominence >= 2.5 &&
        (resonance == null || prominence > resonance.prominenceDb)) {
      resonance = (
        startRpm: previous.rpm,
        endRpm: next.rpm,
        peakRpm: current.rpm,
        prominenceDb: (prominence * 10).round() / 10,
      );
    }
  }
  int? knee;
  if (profile.length >= 3 && totalRise >= 3) {
    final average =
        totalRise / (profile.last.rpm - profile.first.rpm).clamp(1, 100000);
    for (var index = 0; index < profile.length - 1; index++) {
      final current = profile[index];
      final next = profile[index + 1];
      final slope = (next.db - current.db) / (next.rpm - current.rpm);
      if (slope > average * 1.5 && totalRise - current.db >= 2) {
        knee = current.rpm;
        break;
      }
    }
  }
  return (
    profile: List.unmodifiable(profile),
    totalRiseDb: (totalRise * 10).round() / 10,
    kneeRpm: knee,
    resonance: resonance,
    lowConfidence: totalRise < 3,
  );
}
