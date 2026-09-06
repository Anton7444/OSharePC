import 'dart:io';

class StartupService {
  static const _runKey = r'HKCU\Software\Microsoft\Windows\CurrentVersion\Run';
  static const _valueName = 'OsharePC';

  static bool get isInstalledBuild =>
      Platform.isWindows &&
      Platform.resolvedExecutable.toLowerCase().contains(r'\osharepc\');

  static Future<bool> isEnabled() async {
    if (!isInstalledBuild) return false;
    final result = await Process.run('reg.exe', ['query', _runKey, '/v', _valueName]);
    return result.exitCode == 0;
  }

  static Future<bool> setEnabled(bool enabled) async {
    if (!isInstalledBuild) return false;
    if (enabled) {
      final result = await Process.run('reg.exe', [
        'add', _runKey, '/v', _valueName, '/t', 'REG_SZ',
        '/d', '"${Platform.resolvedExecutable}" --startup', '/f',
      ]);
      return result.exitCode == 0;
    }
    final result = await Process.run(
      'reg.exe', ['delete', _runKey, '/v', _valueName, '/f'],
    );
    return result.exitCode == 0 || result.exitCode == 1;
  }
}
