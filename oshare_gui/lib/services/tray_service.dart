import 'dart:io';
import 'package:flutter/material.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:tray_manager/tray_manager.dart';
import 'package:window_manager/window_manager.dart';
import 'bridge_client.dart';
import 'notification_service.dart';

class TrayService with TrayListener, WindowListener {
  final BridgeClient bridgeClient;
  final Future<void> Function()? onExitCleanup;

  TrayService({required this.bridgeClient, this.onExitCleanup});

  Future<void> setStartHidden(bool hidden) async {
    if (!hidden) return;
    bridgeClient.setMainWindowVisible(false);
    await windowManager.hide();
  }

  Future<void> showNotification(String title, String message) async {
    final minimized =
        !(await windowManager.isVisible()) || await windowManager.isMinimized();
    if (minimized) await NotificationService.show(title, message);
  }

  Future<void> init() async {
    trayManager.addListener(this);
    windowManager.addListener(this);

    await windowManager.ensureInitialized();
    await windowManager.setMinimumSize(const Size(900, 620));
    await windowManager.setSize(const Size(1080, 720));
    await windowManager.center();
    await windowManager.setTitle('OsharePC');
    await windowManager.setPreventClose(true);

    try {
      await trayManager.setIcon(
        Platform.isWindows ? 'assets/app.ico' : 'assets/app.ico',
      );
      await trayManager.setToolTip('OsharePC - Mutual Transmission');
      _updateMenu();
    } catch (e) {
      debugPrint('Error setting tray icon: $e');
    }
  }

  Future<void> _updateMenu() async {
    final receiveActive = bridgeClient.status.receiveEnabled;
    final menu = Menu(
      items: [
        MenuItem(key: 'show', label: 'Open OsharePC'),
        MenuItem(
          key: 'toggle_receive',
          label: receiveActive ? 'Pause Receiving' : 'Resume Receiving',
        ),
        MenuItem.separator(),
        MenuItem(key: 'exit', label: 'Quit'),
      ],
    );
    await trayManager.setContextMenu(menu);
  }

  @override
  void onTrayIconMouseDown() {
    windowManager.show();
    windowManager.focus();
    bridgeClient.setMainWindowVisible(true);
  }

  @override
  void onTrayIconRightMouseDown() {
    _updateMenu();
    trayManager.popUpContextMenu();
  }

  @override
  void onTrayMenuItemClick(MenuItem menuItem) {
    if (menuItem.key == 'show') {
      windowManager.show();
      windowManager.focus();
      bridgeClient.setMainWindowVisible(true);
    } else if (menuItem.key == 'toggle_receive') {
      final current = bridgeClient.status.receiveEnabled;
      bridgeClient.setReceiveEnabled(!current);
    } else if (menuItem.key == 'exit') {
      _exitApplication();
    }
  }

  Future<void> _exitApplication() async {
    // Disappear immediately; the remaining cleanup runs out of sight.
    try {
      await windowManager.hide();
      await trayManager.destroy();
    } catch (_) {}
    // Give user-supplied cleanup (e.g. persisting settings) its own generous
    // timeout so a slow disk write isn't silently truncated by the backend
    // shutdown's much shorter internal timeout.
    try {
      if (onExitCleanup != null) {
        await onExitCleanup!().timeout(const Duration(seconds: 5));
      }
    } catch (e) {
      debugPrint('Error during exit cleanup: $e');
    }
    try {
      await bridgeClient.shutdownBackend();
    } catch (_) {}
    await windowManager.destroy();
    exit(0);
  }

  @override
  void onWindowClose() async {
    final prefs = await SharedPreferences.getInstance();
    final closeToTray = prefs.getBool('close_to_tray') ?? true;
    if (closeToTray) {
      await windowManager.hide();
      bridgeClient.setMainWindowVisible(false);
    } else {
      await _exitApplication();
    }
  }

  @override
  void onWindowMinimize() async {
    bridgeClient.setMainWindowVisible(false);
    final prefs = await SharedPreferences.getInstance();
    final minimizeToTray = prefs.getBool('minimize_to_tray') ?? true;
    if (minimizeToTray) {
      await windowManager.hide();
    }
  }

  @override
  void onWindowRestore() {
    bridgeClient.setMainWindowVisible(true);
  }

  void dispose() {
    trayManager.removeListener(this);
    windowManager.removeListener(this);
  }
}
