import 'dart:async';
import 'dart:io';
import 'package:flutter/material.dart';
import 'package:path/path.dart' as p;
import 'package:shared_preferences/shared_preferences.dart';
import 'package:window_manager/window_manager.dart';
import 'config/theme.dart';
import 'config/language.dart';
import 'models/models.dart';
import 'pages/desktop_drop_panel.dart';
import 'pages/home_page.dart';
import 'pages/receive_popup.dart';
import 'services/bridge_client.dart';
import 'services/desktop_drop_panel_service.dart';
import 'services/receive_popup_service.dart';
import 'services/tray_service.dart';
import 'widgets/native_drop_zone.dart';

void main() async {
  WidgetsFlutterBinding.ensureInitialized();

  if (isDesktopDropPanelProcess(
    arguments: Platform.executableArguments,
    environmentValue: Platform.environment[desktopDropPanelEnvironment],
  )) {
    await _runDesktopDropPanel();
    return;
  }

  if (isReceivePopupProcess(
    arguments: Platform.executableArguments,
    environmentValue: Platform.environment[receivePopupEnvironment],
  )) {
    await runReceivePopup();
    return;
  }

  final startup = Platform.executableArguments.contains('--startup');
  final prefs = await SharedPreferences.getInstance();
  final startMinimized = prefs.getBool('start_minimized') ?? false;
  final quickSaveIndex = prefs.getInt('quick_save_mode') ?? 1;
  final initiallyVisible = !(startup || startMinimized);
  final bridgeClient = BridgeClient(
    initialQuickSaveMode: QuickSaveMode.values[quickSaveIndex.clamp(0, 2)],
    initialWindowVisible: initiallyVisible,
  );
  final desktopDropPanelService = DesktopDropPanelService(
    bridgeClient: bridgeClient,
  );
  final receivePopupService = ReceivePopupService(bridgeClient: bridgeClient);
  final trayService = TrayService(
    bridgeClient: bridgeClient,
    onExitCleanup: () async {
      await receivePopupService.dispose();
      await desktopDropPanelService.disable();
    },
  );
  bridgeClient.onNotification = trayService.showNotification;
  await trayService.init();

  AppLanguage initialLanguage = AppLanguage.english;
  final hasSavedLanguage = prefs.containsKey('language');

  try {
    final exeDir = p.dirname(Platform.resolvedExecutable);
    final marker = File(p.join(exeDir, 'installer-language.txt'));
    if (await marker.exists()) {
      final code = (await marker.readAsString()).trim();
      if (!hasSavedLanguage) {
        initialLanguage = switch (code) {
          'zh-CN' => AppLanguage.simplifiedChinese,
          'zh-TW' => AppLanguage.traditionalChinese,
          _ => AppLanguage.english,
        };
        await prefs.setInt('language', initialLanguage.index);
      }
      await marker.delete();
    }
  } catch (_) {}

  if (hasSavedLanguage) {
    final savedIndex = prefs.getInt('language') ?? AppLanguage.english.index;
    initialLanguage =
        AppLanguage.values[savedIndex.clamp(0, AppLanguage.values.length - 1)];
  }

  bridgeClient.setLanguage(initialLanguage);

  runApp(
    OShareApp(
      bridgeClient: bridgeClient,
      trayService: trayService,
      desktopDropPanelService: desktopDropPanelService,
      startHidden: startup || startMinimized,
      initialLanguage: initialLanguage,
    ),
  );
}

Future<void> _runDesktopDropPanel() async {
  final prefs = await SharedPreferences.getInstance();
  final languageIndex = prefs.getInt('language') ?? AppLanguage.english.index;
  final initialLanguage =
      AppLanguage.values[languageIndex.clamp(0, AppLanguage.values.length - 1)];
  final themeIndex = prefs.getInt('theme_mode') ?? ThemeMode.dark.index;
  final initialThemeMode = ThemeMode.values[themeIndex.clamp(0, 2)];
  final accentName = prefs.getString('accent_color');
  final initialAccent = AppColors.accentPresets.firstWhere(
    (preset) => preset.name == accentName,
    orElse: () => AppColors.accentPresets.first,
  );

  final bridgeClient = BridgeClient(
    bridgeToken: Platform.environment['OSHAREPC_BRIDGE_TOKEN'],
    manageBackend: false,
    manageIncomingTransfers: false,
    initialWindowVisible: false,
  );
  bridgeClient.setLanguage(initialLanguage);

  await windowManager.ensureInitialized();
  final anchor = await resolveCornerAnchor();
  final idleRect = panelWindowRect(anchor);
  windowManager.waitUntilReadyToShow(
    WindowOptions(
      size: panelWindowSize,
      minimumSize: panelWindowSize,
      maximumSize: panelWindowSize,
      title: 'OsharePC Drop Target',
      titleBarStyle: TitleBarStyle.hidden,
      backgroundColor: Colors.transparent,
      alwaysOnTop: true,
      skipTaskbar: true,
    ),
    () async {
      await windowManager.setAsFrameless();
      await windowManager.setSkipTaskbar(true);
      await windowManager.setResizable(false);
      await windowManager.setMinimizable(false);
      await windowManager.setMaximizable(false);
      // window_manager's setBounds() does SetWindowPos(hwnd, HWND_TOP, ...)
      // on Windows — NOT HWND_TOPMOST — so it silently drops topmost status
      // even though the WS_EX_TOPMOST style bit stays set. setAlwaysOnTop
      // must be the *last* z-order-affecting call, and gets re-asserted
      // after every later setBounds too (see _DesktopDropPanelPageState).
      await windowManager.setBounds(idleRect);
      await windowManager.setAlwaysOnTop(true);
      // The native hot-zone remains hidden until this position is established.
      try {
        await DragDropService.instance.activateDesktopDropPanel();
      } catch (error) {
        debugPrint(
          '[DesktopDropPanel] Failed to activate native hot zone: $error',
        );
      }
    },
  );

  runApp(
    DesktopDropPanelApp(
      bridgeClient: bridgeClient,
      initialLanguage: initialLanguage,
      initialThemeMode: initialThemeMode,
      initialAccent: initialAccent,
    ),
  );
}

class OShareApp extends StatefulWidget {
  final BridgeClient bridgeClient;
  final TrayService trayService;
  final DesktopDropPanelService desktopDropPanelService;
  final bool startHidden;
  final AppLanguage initialLanguage;

  const OShareApp({
    super.key,
    required this.bridgeClient,
    required this.trayService,
    required this.desktopDropPanelService,
    required this.startHidden,
    required this.initialLanguage,
  });

  @override
  State<OShareApp> createState() => _OShareAppState();
}

class _OShareAppState extends State<OShareApp> {
  final GlobalKey<NavigatorState> _navigatorKey = GlobalKey<NavigatorState>();
  ThemeMode _themeMode = ThemeMode.dark;
  AccentPreset _accent = AppColors.accentPresets.first;
  late AppLanguage _language;
  bool _desktopDropTargetEnabled = false;

  @override
  void initState() {
    super.initState();
    _language = widget.initialLanguage;
    widget.bridgeClient.onSendResult = _showSendResult;
    _loadThemeMode();
    _loadAccent();
    _loadDesktopDropTarget();
    if (widget.startHidden) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        widget.trayService.setStartHidden(true);
      });
    }
  }

  void _showSendResult(bool success, String message) {
    _showResultDialog(
      appText(
        _language,
        success ? 'desktopDropTransferCompleted' : 'transferFailed',
      ),
      message: success ? null : message,
    );
  }

  void _showResultDialog(String title, {String? message}) {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || !widget.bridgeClient.mainWindowVisible) return;
      final context = _navigatorKey.currentContext;
      if (context == null) return;
      showDialog<void>(
        context: context,
        builder: (dialogContext) => AlertDialog(
          title: Text(title),
          content: message == null || message.isEmpty ? null : Text(message),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(),
              child: Text(
                MaterialLocalizations.of(dialogContext).okButtonLabel,
              ),
            ),
          ],
        ),
      );
    });
  }

  Future<void> _onLanguageChanged(AppLanguage language) async {
    setState(() => _language = language);
    widget.bridgeClient.setLanguage(language);
    final prefs = await SharedPreferences.getInstance();
    await prefs.setInt('language', language.index);
  }

  Future<void> _loadThemeMode() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      final modeIndex = prefs.getInt('theme_mode') ?? 0;
      setState(() {
        _themeMode = ThemeMode.values[modeIndex.clamp(0, 2)];
      });
    } catch (_) {}
  }

  Future<void> _onThemeChanged(ThemeMode mode) async {
    setState(() => _themeMode = mode);
    final prefs = await SharedPreferences.getInstance();
    await prefs.setInt('theme_mode', mode.index);
    await widget.bridgeClient.updateSettings(themeMode: mode.index);
  }

  Future<void> _loadAccent() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      final name = prefs.getString('accent_color');
      final preset = AppColors.accentPresets.firstWhere(
        (p) => p.name == name,
        orElse: () => AppColors.accentPresets.first,
      );
      setState(() {
        _accent = preset;
        AppColors.applyAccent(preset);
      });
    } catch (_) {}
  }

  Future<void> _onAccentChanged(AccentPreset preset) async {
    setState(() {
      _accent = preset;
      AppColors.applyAccent(preset);
    });
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString('accent_color', preset.name);
  }

  Future<void> _loadDesktopDropTarget() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      final enabled = prefs.getBool('desktop_drop_target_enabled') ?? false;
      if (!mounted) return;
      setState(() => _desktopDropTargetEnabled = enabled);
      if (enabled) {
        await widget.desktopDropPanelService.enable();
      }
    } catch (error) {
      debugPrint('Error loading desktop drop target preference: $error');
    }
  }

  Future<void> _onDesktopDropTargetChanged(bool enabled) async {
    setState(() => _desktopDropTargetEnabled = enabled);
    try {
      final prefs = await SharedPreferences.getInstance();
      await prefs.setBool('desktop_drop_target_enabled', enabled);
      if (enabled) {
        await widget.desktopDropPanelService.enable();
      } else {
        await widget.desktopDropPanelService.disable();
      }
    } catch (error) {
      debugPrint('Error updating desktop drop target preference: $error');
    }
  }

  @override
  void dispose() {
    widget.bridgeClient.onSendResult = null;
    widget.trayService.dispose();
    unawaited(widget.desktopDropPanelService.dispose());
    widget.bridgeClient.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: widget.bridgeClient,
      builder: (context, _) {
        return MaterialApp(
          navigatorKey: _navigatorKey,
          title: 'OsharePC',
          debugShowCheckedModeBanner: false,
          theme: AppTheme.lightTheme,
          darkTheme: AppTheme.darkTheme,
          themeMode: _themeMode,
          locale: _language.locale,
          supportedLocales: AppLanguage.values.map(
            (language) => language.locale,
          ),
          home: HomePage(
            client: widget.bridgeClient,
            currentThemeMode: _themeMode,
            onThemeChanged: _onThemeChanged,
            currentAccent: _accent,
            onAccentChanged: _onAccentChanged,
            currentLanguage: _language,
            onLanguageChanged: _onLanguageChanged,
            desktopDropTargetEnabled: _desktopDropTargetEnabled,
            onDesktopDropTargetChanged: _onDesktopDropTargetChanged,
          ),
        );
      },
    );
  }
}
