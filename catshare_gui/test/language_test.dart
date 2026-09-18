import 'package:flutter_test/flutter_test.dart';
import 'package:catshare_gui/config/language.dart';

void main() {
  const requiredKeys = <String>[
    'desktopDropTarget',
    'desktopDropTargetHint',
    'desktopDropTitle',
    'desktopDropNoDevices',
    'desktopDropSelectDevice',
    'desktopDropReleaseToSend',
    'desktopDropStagedSummary',
    'desktopDropCancelStaged',
  ];

  test('desktop drop target has text in every supported language', () {
    for (final language in AppLanguage.values) {
      for (final key in requiredKeys) {
        final value = appText(language, key);
        expect(value, isNotEmpty, reason: '$key is missing for $language');
        expect(value, isNot(key), reason: '$key fell back to its key');
      }
    }
  });
}
