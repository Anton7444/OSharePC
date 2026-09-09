import 'dart:async';
import 'dart:io';
import 'package:flutter/foundation.dart';
import 'package:local_notifier/local_notifier.dart';

class NotificationService {
  static bool _initialized = false;
  static final Set<LocalNotification> _activeNotifications = {};

  static Future<void> init() async {
    if (!Platform.isWindows || _initialized) return;

    await localNotifier.setup(
      appName: 'OsharePC',
      shortcutPolicy: ShortcutPolicy.requireCreate,
    );
    _initialized = true;
  }

  static Future<void> show(
    String title,
    String message, {
    Future<void> Function()? onClick,
  }) async {
    if (!Platform.isWindows) return;

    try {
      await init();

      final notification = LocalNotification(
        title: title,
        body: message,
      );
      _activeNotifications.add(notification);

      notification.onClick = () {
        _activeNotifications.remove(notification);
        if (onClick != null) {
          unawaited(() async {
            try {
              await onClick();
            } catch (e) {
              debugPrint('Notification click handler failed: $e');
            }
          }());
        }
      };

      notification.onClose = (_) {
        _activeNotifications.remove(notification);
      };

      await notification.show();
    } catch (e) {
      debugPrint('Failed to show Windows notification: $e');
    }
  }
}
