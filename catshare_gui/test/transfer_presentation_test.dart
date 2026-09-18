import 'package:flutter_test/flutter_test.dart';
import 'package:catshare_gui/services/transfer_presentation.dart';

void main() {
  test('ETA is unavailable without a positive speed', () {
    expect(
      estimateRemaining(sentBytes: 50, totalBytes: 100, speedBytesPerSec: 0),
      isNull,
    );
    expect(
      estimateRemaining(sentBytes: 50, totalBytes: 0, speedBytesPerSec: 10),
      isNull,
    );
    expect(
      estimateRemaining(
        sentBytes: 50,
        totalBytes: 100,
        speedBytesPerSec: 0.833333,
      ),
      const Duration(seconds: 60),
    );
    expect(formatTransferDuration(const Duration(seconds: 84)), '1m 24s');
  });

  test('ETA clamps completed transfers and formats hours', () {
    expect(
      estimateRemaining(sentBytes: 120, totalBytes: 100, speedBytesPerSec: 10),
      Duration.zero,
    );
    expect(formatTransferDuration(const Duration(seconds: 7)), '7s');
    expect(formatTransferDuration(const Duration(seconds: 3661)), '1h 1m');
  });

  test('byte sizes and paths are human-readable', () {
    expect(formatByteSize(0), '0 B');
    expect(formatByteSize(1024), '1.0 KB');
    expect(formatByteSize(1536), '1.5 KB');
    expect(formatByteSize(1024 * 1024), '1.0 MB');
    expect(fileNameForPath(r'C:\\Users\\Anton\\report.txt'), 'report.txt');
    expect(fileNameForPath('/tmp/report.txt'), 'report.txt');
    expect(fileNameForPath('report.txt'), 'report.txt');
  });
}
