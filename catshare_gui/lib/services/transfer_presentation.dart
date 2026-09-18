import 'dart:math' as math;

Duration? estimateRemaining({
  required int sentBytes,
  required int totalBytes,
  required double speedBytesPerSec,
}) {
  if (totalBytes <= 0 || speedBytesPerSec <= 0) return null;
  final remainingBytes = math.max(0, totalBytes - sentBytes);
  final seconds = (remainingBytes / speedBytesPerSec).round();
  return Duration(seconds: seconds);
}

String formatTransferDuration(Duration duration) {
  final seconds = math.max(0, duration.inSeconds);
  if (seconds < 60) return '${seconds}s';
  final minutes = seconds ~/ 60;
  if (minutes < 60) return '${minutes}m ${seconds % 60}s';
  return '${minutes ~/ 60}h ${minutes % 60}m';
}

String formatTransferEta(Duration? duration) {
  if (duration == null) return '—';
  return formatTransferDuration(duration);
}

String formatByteSize(int bytes) {
  if (bytes < 1024) return '${math.max(0, bytes)} B';
  const units = ['KB', 'MB', 'GB', 'TB'];
  var value = bytes / 1024;
  var unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return '${value.toStringAsFixed(1)} ${units[unit]}';
}

String fileNameForPath(String path) {
  final trimmed = path.replaceFirst(RegExp(r'[\\/]+$'), '');
  final separator = math.max(
    trimmed.lastIndexOf('/'),
    trimmed.lastIndexOf('\\'),
  );
  return separator < 0 ? trimmed : trimmed.substring(separator + 1);
}
