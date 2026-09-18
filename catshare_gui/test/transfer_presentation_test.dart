import 'package:flutter_test/flutter_test.dart';
import 'package:catshare_gui/services/transfer_presentation.dart';

void main() {
  test('ETA is unavailable without a positive speed', () {
    expect(
      estimateRemaining(
        sentBytes: 50,
        totalBytes: 100,
        speedBytesPerSec: 0,
      ),
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
}
