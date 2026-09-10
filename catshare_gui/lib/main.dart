import 'dart:io';
import 'package:flutter/material.dart';
import 'package:path/path.dart' as p;
import 'package:shared_preferences/shared_preferences.dart';
import 'config/theme.dart';
import 'config/language.dart';
import 'pages/home_page.dart';
import 'services/bridge_client.dart';
import 'services/tray_service.dart';


void main() async {
  WidgetsFlutterBinding.ensureInitialized();


  final bridgeClient = BridgeClient();
  final trayService = TrayService(bridgeClient: bridgeClient);
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
    initialLanguage = AppLanguage.values[
        savedIndex.clamp(0, AppLanguage.values.length - 1)];
  }

  bridgeClient.setLanguage(initialLanguage);

  runApp(
    CatShareApp(
      bridgeClient: bridgeClient,
      trayService: trayService,
      startHidden: startup || startMinimized,
      initialLanguage: initialLanguage,
    ),
  );
}


class CatShareApp extends StatefulWidget {
  final BridgeClient bridgeClient;
  final TrayService trayService;
  final bool startHidden;
  final AppLanguage initialLanguage;


  const CatShareApp({
    super.key,
    required this.bridgeClient,
    required this.trayService,
    required this.startHidden,
    required this.initialLanguage,
  });


  @override
  State<CatShareApp> createState() => _CatShareAppState();
}


class _CatShareAppState extends State<CatShareApp> {
  ThemeMode _themeMode = ThemeMode.dark;
  late AppLanguage _language;


  @override
  void initState() {
    super.initState();
    _language = widget.initialLanguage;
    _loadThemeMode();
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

  @override
  void dispose() {
    widget.trayService.dispose();
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
            currentLanguage: _language,
            onLanguageChanged: _onLanguageChanged,
          ),
        );
      },
    );
  }
}
