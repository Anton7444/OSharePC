import 'dart:ffi';
import 'dart:io';
import 'package:ffi/ffi.dart';
import 'package:win32/win32.dart';

class NotificationService {
  static int _nextId = 1;
  static HWND? _ownerWindow;

  /// Finds a stable HWND owned by this process to use as the Shell_NotifyIcon
  /// owner. showNotification() is only called while the app window is hidden
  /// or minimized, so GetForegroundWindow() at that point is almost always
  /// some OTHER process's window — using it as the icon's owner is semantically
  /// wrong and, combined with a fixed uID, lets overlapping notifications
  /// collide. This walks top-level windows with the Flutter runner's window
  /// class until it finds the one this process owns, and caches it (the main
  /// window persists for the app's lifetime even while hidden).
  static HWND? _findOwnerWindow() {
    if (_ownerWindow != null) return _ownerWindow;
    final currentPid = GetCurrentProcessId();
    final classNamePtr = 'FLUTTER_RUNNER_WIN32_WINDOW'.toNativeUtf16();
    final className = PCWSTR(classNamePtr);
    final pidPtr = calloc<Uint32>();
    try {
      var hwnd = FindWindowEx(null, null, className, null).value;
      while (hwnd.address != 0) {
        GetWindowThreadProcessId(hwnd, pidPtr);
        if (pidPtr.value == currentPid) {
          _ownerWindow = hwnd;
          return hwnd;
        }
        hwnd = FindWindowEx(null, hwnd, className, null).value;
      }
    } finally {
      calloc.free(pidPtr);
      calloc.free(classNamePtr);
    }
    return null;
  }

  static Future<void> show(String title, String message) async {
    if (!Platform.isWindows) return;
    final hWnd = _findOwnerWindow() ?? GetForegroundWindow();
    final id = _nextId++;
    final data = calloc<NOTIFYICONDATA>();
    try {
      data.ref
        ..cbSize = sizeOf<NOTIFYICONDATA>()
        ..hWnd = hWnd
        ..uID = id
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
