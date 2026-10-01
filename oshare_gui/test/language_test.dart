import 'package:flutter_test/flutter_test.dart';
import 'package:oshare_gui/config/language.dart';

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

  test('device kind labels are translated but brand names are kept', () {
    expect(
      deviceKindText(AppLanguage.traditionalChinese, 'Alliance (OnePlus)'),
      '互傳聯盟 (OnePlus)',
    );
    expect(
      deviceKindText(AppLanguage.simplifiedChinese, 'Alliance (OPPO/realme)'),
      '互传联盟 (OPPO/realme)',
    );
    expect(deviceKindText(AppLanguage.traditionalChinese, 'Contacts'), '聯絡人');
    expect(deviceKindText(AppLanguage.simplifiedChinese, 'LAN'), '局域网');
    expect(deviceKindText(AppLanguage.traditionalChinese, 'Legacy OEM'), '舊版其他品牌');
    expect(deviceKindText(AppLanguage.traditionalChinese, 'OShare'), 'OShare');
    expect(deviceKindText(AppLanguage.english, 'Contacts'), 'Contacts');
  });
}
