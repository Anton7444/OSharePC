import 'package:flutter_test/flutter_test.dart';
import 'package:oshare_gui/models/models.dart';
import 'package:oshare_gui/services/transfer_presentation.dart';

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
    expect(formatTransferEta(Duration.zero), '0s');
    expect(formatTransferEta(null), '—');
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

  test(
    'receive modal shows its completed result only for visible transfers',
    () {
      expect(
        shouldShowReceiveTransferModal(
          isActive: true,
          phase: 'receiving',
          isWindowVisible: true,
          resultWasHidden: false,
        ),
        isTrue,
      );
      expect(
        shouldShowReceiveTransferModal(
          isActive: false,
          phase: 'completed',
          isWindowVisible: true,
          resultWasHidden: false,
        ),
        isFalse,
      );
      expect(
        shouldShowReceiveTransferModal(
          isActive: true,
          phase: 'completed',
          isWindowVisible: true,
          resultWasHidden: false,
        ),
        isTrue,
      );
      expect(
        shouldShowReceiveTransferModal(
          isActive: true,
          phase: 'failed',
          isWindowVisible: true,
          resultWasHidden: false,
        ),
        isTrue,
      );
      expect(
        shouldShowReceiveTransferModal(
          isActive: true,
          phase: 'completed',
          isWindowVisible: false,
          resultWasHidden: true,
        ),
        isFalse,
      );
      expect(
        shouldShowReceiveTransferModal(
          isActive: true,
          phase: 'completed',
          isWindowVisible: true,
          resultWasHidden: true,
        ),
        isFalse,
      );
    },
  );

  test('Quick Save accepts incoming transfers without prompting', () {
    expect(shouldAutoAcceptIncomingTransfer(QuickSaveMode.on), isTrue);
    expect(shouldAutoAcceptIncomingTransfer(QuickSaveMode.off), isFalse);
    expect(shouldAutoAcceptIncomingTransfer(QuickSaveMode.favorites), isFalse);
  });

  test('desktop drop sends do not show the main Send-tab modal', () {
    expect(
      shouldShowSendTransferModal(isActive: true, isBackground: true),
      isFalse,
    );
    expect(
      shouldShowSendTransferModal(isActive: true, isBackground: false),
      isTrue,
    );
    expect(
      shouldShowSendTransferModal(isActive: false, isBackground: false),
      isFalse,
    );
  });

  test('desktop drop results show only in a visible main window', () {
    expect(
      shouldShowSendResultDialog(
        isBackground: true,
        isWindowVisible: true,
        isDuplicate: false,
      ),
      isTrue,
    );
    expect(
      shouldShowSendResultDialog(
        isBackground: true,
        isWindowVisible: false,
        isDuplicate: false,
      ),
      isFalse,
    );
    expect(
      shouldShowSendResultDialog(
        isBackground: false,
        isWindowVisible: true,
        isDuplicate: false,
      ),
      isFalse,
    );
    expect(
      shouldShowSendResultDialog(
        isBackground: true,
        isWindowVisible: true,
        isDuplicate: true,
      ),
      isFalse,
    );
  });

  test('send result event IDs are handled only once', () {
    final tracker = SendResultEventTracker();

    expect(tracker.isFirst('send-1'), isTrue);
    expect(tracker.isFirst('send-1'), isFalse);
    expect(tracker.isFirst('send-2'), isTrue);
    expect(tracker.isFirst(null), isTrue);
  });
}
