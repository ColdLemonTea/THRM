import 'dart:math' as math;

typedef TemperatureHistoryPoint = ({
  int timestamp,
  int cpuTemp,
  int gpuTemp,
  double cpuPower,
  double gpuPower,
  int fanRpm,
  int cpuFanRpm,
  int gpuFanRpm,
});

typedef TimelineEvent = ({int timestamp, String type, String labelKey});

typedef TemperatureHistorySnapshot = ({
  bool enabled,
  int sampleIntervalSeconds,
  int retentionHours,
  List<TemperatureHistoryPoint> points,
  List<TimelineEvent> events,
});

const TemperatureHistorySnapshot emptyTemperatureHistorySnapshot = (
  enabled: false,
  sampleIntervalSeconds: 5,
  retentionHours: 1,
  points: <TemperatureHistoryPoint>[],
  events: <TimelineEvent>[],
);

TemperatureHistorySnapshot readTemperatureHistorySnapshot(Object? raw) {
  if (raw is! Map) return emptyTemperatureHistorySnapshot;
  final sampleInterval = _positiveInt(raw['sampleIntervalSeconds']);
  final retention = _positiveInt(raw['retentionHours']).clamp(1, 24);
  final points = <TemperatureHistoryPoint>[];
  final rawPoints = raw['points'];
  if (rawPoints is List) {
    for (final rawPoint in rawPoints) {
      final point = readTemperatureHistoryPoint(rawPoint);
      if (point != null) points.add(point);
    }
  }
  final events = <TimelineEvent>[];
  if (raw['events'] case final List rawEvents) {
    for (final rawEvent in rawEvents) {
      final event = readTimelineEvent(rawEvent);
      if (event != null) events.add(event);
    }
  }
  return (
    enabled: raw['enabled'] == true,
    sampleIntervalSeconds: sampleInterval == 0 ? 5 : sampleInterval,
    retentionHours: retention,
    points: _normalizeHistoryPoints(
      points,
      retentionHours: retention,
      sampleIntervalSeconds: sampleInterval == 0 ? 5 : sampleInterval,
    ),
    events: mergeTimelineEvents(events),
  );
}

TimelineEvent? readTimelineEvent(Object? raw) {
  if (raw is! Map) return null;
  var timestamp = _positiveInt(raw['timestamp']);
  if (timestamp > 0 && timestamp < 1000000000000) timestamp *= 1000;
  final labelKey = raw['labelKey']?.toString() ?? '';
  if (timestamp == 0 || labelKey.isEmpty) return null;
  final type = raw['type']?.toString() ?? 'mode';
  return (
    timestamp: timestamp,
    type: const {'mode', 'disconnect', 'resume', 'profile'}.contains(type)
        ? type
        : 'mode',
    labelKey: labelKey,
  );
}

List<TimelineEvent> mergeTimelineEvents(
  List<TimelineEvent> current, [
  TimelineEvent? incoming,
]) {
  final byId = <String, TimelineEvent>{
    for (final event in current)
      '${event.timestamp}|${event.type}|${event.labelKey}': event,
  };
  if (incoming case final event?) {
    byId['${event.timestamp}|${event.type}|${event.labelKey}'] = event;
  }
  final events = byId.values.toList()
    ..sort((left, right) => left.timestamp.compareTo(right.timestamp));
  return List.unmodifiable(
    events.length > 240 ? events.sublist(events.length - 240) : events,
  );
}

TemperatureHistoryPoint? readTemperatureHistoryPoint(Object? raw) {
  if (raw is! Map) return null;
  var timestamp = _positiveInt(raw['timestamp']);
  if (timestamp > 0 && timestamp < 1000000000000) timestamp *= 1000;
  final cpuTemp = _positiveInt(raw['cpuTemp']);
  final gpuTemp = _positiveInt(raw['gpuTemp']);
  final fanRpm = _positiveInt(raw['fanRpm']);
  if (timestamp == 0 || (cpuTemp == 0 && gpuTemp == 0 && fanRpm == 0)) {
    return null;
  }
  return (
    timestamp: timestamp,
    cpuTemp: cpuTemp,
    gpuTemp: gpuTemp,
    cpuPower: _positiveDouble(raw['cpuPower']),
    gpuPower: _positiveDouble(raw['gpuPower']),
    fanRpm: fanRpm,
    cpuFanRpm: _positiveInt(raw['cpuFanRpm']),
    gpuFanRpm: _positiveInt(raw['gpuFanRpm']),
  );
}

TemperatureHistorySnapshot appendTemperatureHistoryPoint(
  TemperatureHistorySnapshot snapshot,
  Object? raw,
) {
  final point = readTemperatureHistoryPoint(raw);
  if (point == null) return snapshot;
  final current = snapshot.points;
  final last = current.isEmpty ? null : current.last;
  final next = last != null && point.timestamp == last.timestamp
      ? [...current.sublist(0, current.length - 1), point]
      : [...current, point];
  return (
    enabled: snapshot.enabled,
    sampleIntervalSeconds: snapshot.sampleIntervalSeconds,
    retentionHours: snapshot.retentionHours,
    points: last != null && point.timestamp < last.timestamp
        ? _normalizeHistoryPoints(
            next,
            retentionHours: snapshot.retentionHours,
            sampleIntervalSeconds: snapshot.sampleIntervalSeconds,
          )
        : _trimSortedHistoryPoints(
            next,
            retentionHours: snapshot.retentionHours,
            sampleIntervalSeconds: snapshot.sampleIntervalSeconds,
          ),
    events: snapshot.events,
  );
}

List<TemperatureHistoryPoint> downsampleTemperatureHistory(
  List<TemperatureHistoryPoint> points,
  int maxPoints,
  int sampleIntervalSeconds,
) {
  if (maxPoints <= 0 || points.length <= maxPoints) return points;
  if (maxPoints == 1) return [points.last];
  final stride = (points.length / maxPoints).ceil();
  final bucketWidth =
      _temperatureHistoryCadence(points, sampleIntervalSeconds) * stride;
  final result = <TemperatureHistoryPoint>[];
  // 时间桶固定在时间轴上，滑动窗口删掉首点时不会让其余采样全部换位。
  var previousBucket = -1;
  for (final point in points) {
    final bucket = point.timestamp ~/ bucketWidth;
    if (bucket != previousBucket) {
      result.add(point);
      previousBucket = bucket;
    }
  }
  if (result.last != points.last) result.add(points.last);
  return List.unmodifiable(result);
}

int temperatureHistoryGapThreshold(
  List<TemperatureHistoryPoint> points,
  int sampleIntervalSeconds,
) {
  final cadence = _temperatureHistoryCadence(points, sampleIntervalSeconds);
  return math.max(30000, cadence * 3);
}

int _temperatureHistoryCadence(
  List<TemperatureHistoryPoint> points,
  int sampleIntervalSeconds,
) {
  final deltas = <int>[];
  for (var index = 1; index < points.length; index++) {
    final delta = points[index].timestamp - points[index - 1].timestamp;
    if (delta > 0) deltas.add(delta);
  }
  deltas.sort();
  final median = deltas.isEmpty
      ? 0
      : deltas.length.isOdd
      ? deltas[deltas.length ~/ 2]
      : (deltas[deltas.length ~/ 2 - 1] + deltas[deltas.length ~/ 2]) ~/ 2;
  return math.max(median, math.max(1, sampleIntervalSeconds) * 1000);
}

List<TemperatureHistoryPoint> _normalizeHistoryPoints(
  List<TemperatureHistoryPoint> points, {
  required int retentionHours,
  required int sampleIntervalSeconds,
}) {
  if (points.isEmpty) return const [];
  points.sort((left, right) => left.timestamp.compareTo(right.timestamp));
  final deduplicated = <TemperatureHistoryPoint>[];
  for (final point in points) {
    if (deduplicated.isNotEmpty &&
        deduplicated.last.timestamp == point.timestamp) {
      deduplicated[deduplicated.length - 1] = point;
    } else {
      deduplicated.add(point);
    }
  }
  return _trimSortedHistoryPoints(
    deduplicated,
    retentionHours: retentionHours,
    sampleIntervalSeconds: sampleIntervalSeconds,
  );
}

List<TemperatureHistoryPoint> _trimSortedHistoryPoints(
  List<TemperatureHistoryPoint> points, {
  required int retentionHours,
  required int sampleIntervalSeconds,
}) {
  if (points.isEmpty) return const [];
  final cutoff = points.last.timestamp - retentionHours * 3600000;
  final limit = retentionHours * 3600 ~/ math.max(1, sampleIntervalSeconds) + 1;
  var start = 0;
  while (start < points.length && points[start].timestamp < cutoff) {
    start++;
  }
  if (points.length - start > limit) {
    start = points.length - limit;
  }
  return List.unmodifiable(points.sublist(start));
}

int _positiveInt(Object? raw) =>
    raw is num && raw.isFinite && raw > 0 ? raw.round() : 0;

double _positiveDouble(Object? raw) =>
    raw is num && raw.isFinite && raw > 0 ? raw.toDouble() : 0;
