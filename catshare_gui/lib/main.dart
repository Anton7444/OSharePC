import 'dart:async';
import 'dart:io';
import 'package:flutter/material.dart';
import 'package:path/path.dart' as p;
import 'package:shared_preferences/shared_preferences.dart';
import 'package:window_manager/window_manager.dart';
import 'config/theme.dart';
import 'config/language.dart';
import 'pages/desktop_drop_panel.dart';
import 'pages/home_page.dart';
import 'services/bridge_client.dart';
import 'services/desktop_drop_panel_service.dart';
import 'services/tray_service.dart';

void main() async {
  WidgetsFlutterBinding.ensureInitialized();

  if (isDesktopDropPanelProcess(
    arguments: Platform.executableArguments,
    environmentValue: Platform.environment[desktopDropPanelEnvironment],
  )) {
    await _runDesktopDropPanel();
    return;
  }

  final bridgeClient = BridgeClient();
  final desktopDropPanelService = DesktopDropPanelService(
    bridgeClient: bridgeClient,
  );
  final trayService = TrayService(
    bridgeClient: bridgeClient,
    onExitCleanup: desktopDropPanelService.disable,
  );
  bridgeClient.onNotification = trayService.showNotification;
  await trayService.init();

  final startup = Platform.executableArguments.contains('--startup');
  final prefs = await SharedPreferences.getInstance();
  final startMinimized = prefs.getBool('start_minimized') ?? false;

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
    CatShareApp(
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
      // Shown (mapped) but essentially transparent: Windows silently skips
      // hit-testing a layered window at *exactly* opacity 0 (verified
      // empirically — WindowFromPoint falls through to whatever is behind
      // it), so this uses a hair above zero instead, which stays fully
      // hit-testable and still reads as invisible to the eye. It must
      // never take focus — that would steal keyboard focus from whatever
      // app the user is dragging out of.
      await windowManager.show();
      await windowManager.setOpacity(panelInvisibleOpacity);
      await windowManager.setAlwaysOnTop(true);
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

class CatShareApp extends StatefulWidget {
  final BridgeClient bridgeClient;
  final TrayService trayService;
  final DesktopDropPanelService desktopDropPanelService;
  final bool startHidden;
  final AppLanguage initialLanguage;

  const CatShareApp({
    super.key,
    required this.bridgeClient,
    required this.trayService,
    required this.desktopDropPanelService,
    required this.startHidden,
    required this.initialLanguage,
  });

  @override
  State<CatShareApp> createState() => _CatShareAppState();
}

class _CatShareAppState extends State<CatShareApp> {
  ThemeMode _themeMode = ThemeMode.dark;
  AccentPreset _accent = AppColors.accentPresets.first;
  late AppLanguage _language;
  bool _desktopDropTargetEnabled = false;

  @override
  void initState() {
    super.initState();
    _language = widget.initialLanguage;
    _loadThemeMode();
    _loadAccent();
    _loadDesktopDropTarget();
    if (widget.startHidden) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        widget.trayService.setStartHidden(true);
      });
    }
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
