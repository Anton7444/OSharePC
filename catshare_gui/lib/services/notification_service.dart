import 'dart:ffi';
import 'dart:io';
import 'package:ffi/ffi.dart';
import 'package:win32/win32.dart';

class NotificationService {
  static Future<void> show(String title, String message) async {
    if (!Platform.isWindows) return;
    final data = calloc<NOTIFYICONDATA>();
    try {
      data.ref
        ..cbSize = sizeOf<NOTIFYICONDATA>()
        ..hWnd = GetForegroundWindow()
        ..uID = 1
        ..uFlags = NIF_INFO
        ..szInfoTitle = title
        ..szInfo = message
        ..dwInfoFlags = NIIF_INFO;
      Shell_NotifyIcon(NIM_ADD, data);
      Shell_NotifyIcon(NIM_MODIFY, data);
      await Future<void>.delayed(const Duration(seconds: 8));
      Shell_NotifyIcon(NIM_DELETE, data);
    } finally {
      calloc.free(data);
    }
  }
}
