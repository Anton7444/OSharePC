import 'dart:io';
import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:shared_preferences/shared_preferences.dart';
import '../config/theme.dart';
import '../config/language.dart';
import '../models/models.dart';
import '../services/bridge_client.dart';
import '../services/receive_popup_service.dart';
import '../services/startup_service.dart';
import '../widgets/custom_segmented_button.dart';

class SettingsTab extends StatefulWidget {
  final BridgeClient client;
  final ThemeMode currentThemeMode;
  final ValueChanged<ThemeMode> onThemeChanged;
  final AccentPreset currentAccent;
  final ValueChanged<AccentPreset> onAccentChanged;
  final AppLanguage currentLanguage;
  final ValueChanged<AppLanguage> onLanguageChanged;
  final bool desktopDropTargetEnabled;
  final ValueChanged<bool> onDesktopDropTargetChanged;

  const SettingsTab({
    super.key,
    required this.client,
    required this.currentThemeMode,
    required this.onThemeChanged,
    required this.currentAccent,
    required this.onAccentChanged,
    required this.currentLanguage,
    required this.onLanguageChanged,
    required this.desktopDropTargetEnabled,
    required this.onDesktopDropTargetChanged,
  });

  @override
  State<SettingsTab> createState() => _SettingsTabState();
}

class _SettingsTabState extends State<SettingsTab> {
  static const _guiVersion = '1.0';
  bool _minimizeToTray = true;
  bool _closeToTray = true;
  bool _launchAtStartup = false;
  bool _startMinimized = false;
  bool _receivePopupEnabled = true;
  final _oppoSsoidController = TextEditingController();

  @override
  void initState() {
    super.initState();
    _loadTrayPrefs();
    _loadStartupPrefs();
  }

  Future<void> _saveOppoSsoid() async {
    final ssoid = _oppoSsoidController.text.trim();
    final ok = await widget.client.updateSettings(oppoSsoid: ssoid);
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(
          ok
              ? appText(widget.currentLanguage, 'oppoSsoidSaved')
              : appText(widget.currentLanguage, 'oppoSsoidSaveFailed'),
        ),
      ),
    );
  }

  Future<void> _loadStartupPrefs() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      final enabled = await StartupService.isEnabled();
      if (!mounted) return;
      setState(() {
        _launchAtStartup = enabled;
        _startMinimized = prefs.getBool('start_minimized') ?? false;
      });
    } catch (_) {}
  }

  Future<void> _saveLaunchAtStartup(bool val) async {
    final saved = await StartupService.setEnabled(val);
    if (!mounted) return;
    setState(() => _launchAtStartup = saved ? val : false);
  }

  Future<void> _saveStartMinimized(bool val) async {
    setState(() => _startMinimized = val);
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool('start_minimized', val);
  }

  Future<void> _loadTrayPrefs() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      if (!mounted) return;
      setState(() {
        _minimizeToTray = prefs.getBool('minimize_to_tray') ?? true;
        _closeToTray = prefs.getBool('close_to_tray') ?? true;
        _receivePopupEnabled =
            prefs.getBool(receivePopupEnabledPref) ?? true;
      });
    } catch (_) {}
  }

  Future<void> _saveReceivePopupEnabled(bool val) async {
    setState(() => _receivePopupEnabled = val);
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool(receivePopupEnabledPref, val);
  }

  Future<void> _saveMinimizeToTray(bool val) async {
    setState(() => _minimizeToTray = val);
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool('minimize_to_tray', val);
    await widget.client.updateSettings(minimizeToTray: val);
  }

  Future<void> _saveCloseToTray(bool val) async {
    setState(() => _closeToTray = val);
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool('close_to_tray', val);
    await widget.client.updateSettings(closeToTray: val);
  }

  Future<void> _pickFolder() async {
    final dir = await FilePicker.getDirectoryPath();
    if (dir != null) {
      widget.client.updateSettings(saveDirectory: dir);
    }
  }

  void _openFolder() {
    final dir = widget.client.status.saveDirectory;
    if (dir.isNotEmpty && Directory(dir).existsSync()) {
      Process.start('explorer.exe', [dir]);
    }
  }

  Future<void> _openLogFolder() async {
    final localAppData = Platform.environment['LOCALAPPDATA'];
    if (localAppData == null || localAppData.isEmpty) return;

    final dir = Directory('$localAppData\\OSharePC');
    if (!dir.existsSync()) {
      dir.createSync(recursive: true);
    }
    await Process.start('explorer.exe', [dir.path]);
  }

  @override
  void dispose() {
    _oppoSsoidController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final isDark = theme.brightness == Brightness.dark;
    final status = widget.client.status;

    return ListView(
      padding: const EdgeInsets.symmetric(horizontal: 28, vertical: 24),
      children: [
        // Section: General
        _buildSectionHeader(appText(widget.currentLanguage, 'general'), isDark),
        const SizedBox(height: 12),
        _buildCard(
          isDark,
          children: [
            // Destination Folder
            Padding(
              padding: const EdgeInsets.all(16),
              child: Row(
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          appText(widget.currentLanguage, 'destination'),
                          style: TextStyle(
                            fontSize: 14,
                            fontWeight: FontWeight.w600,
                            color: isDark
                                ? AppColors.darkText
                                : AppColors.lightText,
                          ),
                        ),
                        const SizedBox(height: 4),
                        Text(
                          status.saveDirectory.isEmpty
                              ? 'Downloads\\OShare'
                              : status.saveDirectory,
                          style: TextStyle(
                            fontSize: 12,
                            color: isDark
                                ? AppColors.darkTextMuted
                                : AppColors.lightTextMuted,
                          ),
                        ),
                      ],
                    ),
                  ),
                  OutlinedButton.icon(
                    onPressed: _pickFolder,
                    icon: const Icon(Icons.folder_open, size: 16),
                    label: Text(appText(widget.currentLanguage, 'browse')),
                    style: OutlinedButton.styleFrom(
                      foregroundColor: isDark
                          ? AppColors.darkAccent
                          : AppColors.lightAccent,
                      side: BorderSide(
                        color: isDark
                            ? AppColors.darkBorder
                            : AppColors.lightBorder,
                      ),
                      shape: RoundedRectangleBorder(
                        borderRadius: BorderRadius.circular(10),
                      ),
                    ),
                  ),
                  const SizedBox(width: 8),
                  IconButton(
                    tooltip: appText(widget.currentLanguage, 'openFolder'),
                    onPressed: _openFolder,
                    icon: const Icon(Icons.open_in_new, size: 18),
                    color: isDark
                        ? AppColors.darkTextMuted
                        : AppColors.lightTextMuted,
                  ),
                ],
              ),
            ),
            Divider(
              height: 1,
              color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
            ),
            // Quick Save
            Padding(
              padding: const EdgeInsets.all(16),
              child: Row(
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          appText(widget.currentLanguage, 'quickSave'),
                          style: TextStyle(
                            fontSize: 14,
                            fontWeight: FontWeight.w600,
                            color: isDark
                                ? AppColors.darkText
                                : AppColors.lightText,
                          ),
                        ),
                        const SizedBox(height: 4),
                        Text(
                          appText(widget.currentLanguage, 'quickSaveHint'),
                          style: TextStyle(
                            fontSize: 12,
                            color: isDark
                                ? AppColors.darkTextMuted
                                : AppColors.lightTextMuted,
                          ),
                        ),
                      ],
                    ),
                  ),
                  CustomSegmentedButton<QuickSaveMode>(
                    values: const [QuickSaveMode.off, QuickSaveMode.on],
                    labels: [
                      appText(widget.currentLanguage, 'off'),
                      appText(widget.currentLanguage, 'on'),
                    ],
                    selected:
                        widget.client.quickSaveMode == QuickSaveMode.favorites
                        ? QuickSaveMode.off
                        : widget.client.quickSaveMode,
                    onSelected: (mode) => widget.client.setQuickSaveMode(mode),
                  ),
                ],
              ),
            ),
            Divider(
              height: 1,
              color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
            ),
            Padding(
              padding: const EdgeInsets.all(16),
              child: Row(
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          appText(widget.currentLanguage, 'language'),
                          style: TextStyle(
                            fontSize: 14,
                            fontWeight: FontWeight.w600,
                            color: isDark
                                ? AppColors.darkText
                                : AppColors.lightText,
                          ),
                        ),
                        const SizedBox(height: 4),
                        Text(
                          appText(widget.currentLanguage, 'chooseLanguage'),
                          style: TextStyle(
                            fontSize: 12,
                            color: isDark
                                ? AppColors.darkTextMuted
                                : AppColors.lightTextMuted,
                          ),
                        ),
                      ],
                    ),
                  ),
                  CustomSegmentedButton<AppLanguage>(
                    values: AppLanguage.values,
                    labels: AppLanguage.values
                        .map((language) => language.label)
                        .toList(),
                    selected: widget.currentLanguage,
                    onSelected: widget.onLanguageChanged,
                  ),
                ],
              ),
            ),
            Divider(
              height: 1,
              color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
            ),
            SwitchListTile(
              title: Text(
                appText(widget.currentLanguage, 'launchAtStartup'),
                style: TextStyle(
                  fontSize: 14,
                  fontWeight: FontWeight.w600,
                  color: isDark ? AppColors.darkText : AppColors.lightText,
                ),
              ),
              value: _launchAtStartup,
              activeThumbColor: isDark
                  ? AppColors.darkAccent
                  : AppColors.lightAccent,
              onChanged: StartupService.isInstalledBuild
                  ? _saveLaunchAtStartup
                  : null,
            ),
            Divider(
              height: 1,
              color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
            ),
            SwitchListTile(
              title: Text(
                appText(widget.currentLanguage, 'startMinimized'),
                style: TextStyle(
                  fontSize: 14,
                  fontWeight: FontWeight.w600,
                  color: isDark ? AppColors.darkText : AppColors.lightText,
                ),
              ),
              value: _startMinimized,
              activeThumbColor: isDark
                  ? AppColors.darkAccent
                  : AppColors.lightAccent,
              onChanged: _saveStartMinimized,
            ),
          ],
        ),

        const SizedBox(height: 28),

        // Section: System Tray
        _buildSectionHeader(
          appText(widget.currentLanguage, 'systemTray'),
          isDark,
        ),
        const SizedBox(height: 12),
        _buildCard(
          isDark,
          children: [
            SwitchListTile(
              title: Text(
                appText(widget.currentLanguage, 'closeTray'),
                style: TextStyle(
                  fontSize: 14,
                  fontWeight: FontWeight.w600,
                  color: isDark ? AppColors.darkText : AppColors.lightText,
                ),
              ),
              subtitle: Text(
                appText(widget.currentLanguage, 'closeTrayHint'),
                style: TextStyle(
                  fontSize: 12,
                  color: isDark
                      ? AppColors.darkTextMuted
                      : AppColors.lightTextMuted,
                ),
              ),
              value: _closeToTray,
              activeThumbColor: isDark
                  ? AppColors.darkAccent
                  : AppColors.lightAccent,
              onChanged: _saveCloseToTray,
            ),
            Divider(
              height: 1,
              color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
            ),
            SwitchListTile(
              title: Text(
                appText(widget.currentLanguage, 'minimizeTray'),
                style: TextStyle(
                  fontSize: 14,
                  fontWeight: FontWeight.w600,
                  color: isDark ? AppColors.darkText : AppColors.lightText,
                ),
              ),
              subtitle: Text(
                appText(widget.currentLanguage, 'minimizeTrayHint'),
                style: TextStyle(
                  fontSize: 12,
                  color: isDark
                      ? AppColors.darkTextMuted
                      : AppColors.lightTextMuted,
                ),
              ),
              value: _minimizeToTray,
              activeThumbColor: isDark
                  ? AppColors.darkAccent
                  : AppColors.lightAccent,
              onChanged: _saveMinimizeToTray,
            ),
            Divider(
              height: 1,
              color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
            ),
            SwitchListTile(
              title: Text(
                appText(widget.currentLanguage, 'receivePopup'),
                style: TextStyle(
                  fontSize: 14,
                  fontWeight: FontWeight.w600,
                  color: isDark ? AppColors.darkText : AppColors.lightText,
                ),
              ),
              subtitle: Text(
                appText(widget.currentLanguage, 'receivePopupHint'),
                style: TextStyle(
                  fontSize: 12,
                  color: isDark
                      ? AppColors.darkTextMuted
                      : AppColors.lightTextMuted,
                ),
              ),
              value: _receivePopupEnabled,
              activeThumbColor: isDark
                  ? AppColors.darkAccent
                  : AppColors.lightAccent,
              onChanged: _saveReceivePopupEnabled,
            ),
            Divider(
              height: 1,
              color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
            ),
            SwitchListTile(
              title: Text(
                appText(widget.currentLanguage, 'desktopDropTarget'),
                style: TextStyle(
                  fontSize: 14,
                  fontWeight: FontWeight.w600,
                  color: isDark ? AppColors.darkText : AppColors.lightText,
                ),
              ),
              subtitle: Text(
                appText(widget.currentLanguage, 'desktopDropTargetHint'),
                style: TextStyle(
                  fontSize: 12,
                  color: isDark
                      ? AppColors.darkTextMuted
                      : AppColors.lightTextMuted,
                ),
              ),
              value: widget.desktopDropTargetEnabled,
              activeThumbColor: isDark
                  ? AppColors.darkAccent
                  : AppColors.lightAccent,
              onChanged: widget.onDesktopDropTargetChanged,
            ),
            Divider(
              height: 1,
              color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
            ),
            SwitchListTile(
              title: Text(
                appText(widget.currentLanguage, 'receiveSuccessNotification'),
                style: TextStyle(
                  fontSize: 14,
                  fontWeight: FontWeight.w600,
                  color: isDark ? AppColors.darkText : AppColors.lightText,
                ),
              ),
              subtitle: Text(
                appText(
                  widget.currentLanguage,
                  'receiveSuccessNotificationHint',
                ),
                style: TextStyle(
                  fontSize: 12,
                  color: isDark
                      ? AppColors.darkTextMuted
                      : AppColors.lightTextMuted,
                ),
              ),
              value: widget.client.receiveSuccessNotifications,
              activeThumbColor: isDark
                  ? AppColors.darkAccent
                  : AppColors.lightAccent,
              onChanged: widget.client.setReceiveSuccessNotifications,
            ),
          ],
        ),

        const SizedBox(height: 28),

        // Section: Appearance
        _buildSectionHeader(
          appText(widget.currentLanguage, 'appearance'),
          isDark,
        ),
        const SizedBox(height: 12),
        _buildCard(
          isDark,
          children: [
            Padding(
              padding: const EdgeInsets.all(16),
              child: Row(
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          appText(widget.currentLanguage, 'theme'),
                          style: TextStyle(
                            fontSize: 14,
                            fontWeight: FontWeight.w600,
                            color: isDark
                                ? AppColors.darkText
                                : AppColors.lightText,
                          ),
                        ),
                        const SizedBox(height: 4),
                        Text(
                          appText(widget.currentLanguage, 'themeHint'),
                          style: TextStyle(
                            fontSize: 12,
                            color: isDark
                                ? AppColors.darkTextMuted
                                : AppColors.lightTextMuted,
                          ),
                        ),
                      ],
                    ),
                  ),
                  CustomSegmentedButton<ThemeMode>(
                    values: const [
                      ThemeMode.dark,
                      ThemeMode.light,
                      ThemeMode.system,
                    ],
                    labels: [
                      appText(widget.currentLanguage, 'dark'),
                      appText(widget.currentLanguage, 'light'),
                      appText(widget.currentLanguage, 'system'),
                    ],
                    selected: widget.currentThemeMode,
                    onSelected: widget.onThemeChanged,
                  ),
                ],
              ),
            ),
            Divider(
              height: 1,
              color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
            ),
            Padding(
              padding: const EdgeInsets.all(16),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          appText(widget.currentLanguage, 'accentColor'),
                          style: TextStyle(
                            fontSize: 14,
                            fontWeight: FontWeight.w600,
                            color: isDark
                                ? AppColors.darkText
                                : AppColors.lightText,
                          ),
                        ),
                        const SizedBox(height: 4),
                        Text(
                          appText(widget.currentLanguage, 'accentColorHint'),
                          style: TextStyle(
                            fontSize: 12,
                            color: isDark
                                ? AppColors.darkTextMuted
                                : AppColors.lightTextMuted,
                          ),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(width: 16),
                  Wrap(
                    spacing: 10,
                    runSpacing: 10,
                    children: [
                      for (final preset in AppColors.accentPresets)
                        _buildAccentSwatch(preset, isDark),
                    ],
                  ),
                ],
              ),
            ),
          ],
        ),

        const SizedBox(height: 28),

        // Section: OPPO Account (experimental)
        _buildSectionHeader(appText(widget.currentLanguage, 'oppoAccount'), isDark),
        const SizedBox(height: 12),
        _buildCard(
          isDark,
          children: [
            Padding(
              padding: const EdgeInsets.all(16),
              child: Row(
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          appText(widget.currentLanguage, 'oppoAccount'),
                          style: TextStyle(
                            fontSize: 14,
                            fontWeight: FontWeight.w600,
                            color: isDark
                                ? AppColors.darkText
                                : AppColors.lightText,
                          ),
                        ),
                        const SizedBox(height: 4),
                        Text(
                          appText(widget.currentLanguage, 'oppoAccountHint'),
                          style: TextStyle(
                            fontSize: 12,
                            color: isDark
                                ? AppColors.darkTextMuted
                                : AppColors.lightTextMuted,
                          ),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(width: 12),
                  OutlinedButton.icon(
                    onPressed: () => _showOppoAccountLoginDialog(context, isDark),
                    icon: const Icon(Icons.qr_code, size: 16),
                    label: Text(
                      appText(widget.currentLanguage, 'oppoAccountLoginBtn'),
                    ),
                    style: OutlinedButton.styleFrom(
                      foregroundColor: isDark
                          ? AppColors.darkAccent
                          : AppColors.lightAccent,
                      side: BorderSide(
                        color: isDark
                            ? AppColors.darkBorder
                            : AppColors.lightBorder,
                      ),
                      shape: RoundedRectangleBorder(
                        borderRadius: BorderRadius.circular(10),
                      ),
                    ),
                  ),
                ],
              ),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.center,
                children: [
                  Expanded(
                    child: TextField(
                      controller: _oppoSsoidController,
                      decoration: InputDecoration(
                        isDense: true,
                        labelText: appText(widget.currentLanguage, 'oppoSsoidLabel'),
                        hintText: appText(widget.currentLanguage, 'oppoSsoidHint'),
                      ),
                    ),
                  ),
                  const SizedBox(width: 12),
                  OutlinedButton(
                    onPressed: _saveOppoSsoid,
                    child: Text(appText(widget.currentLanguage, 'save')),
                  ),
                ],
              ),
            ),
          ],
        ),

        const SizedBox(height: 28),

        // Section: Network & Info
        _buildSectionHeader(appText(widget.currentLanguage, 'network'), isDark),
        const SizedBox(height: 12),
        _buildCard(
          isDark,
          children: [
            _buildInfoRow(
              appText(widget.currentLanguage, 'localIp'),
              status.lanIp.isEmpty ? '127.0.0.1' : status.lanIp,
              isDark,
            ),
            Divider(
              height: 1,
              color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
            ),
            _buildInfoRow(
              appText(widget.currentLanguage, 'transferPort'),
              '${status.transferPort} (Stock 互传)',
              isDark,
            ),
            Divider(
              height: 1,
              color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
            ),
            _buildInfoRow(
              appText(widget.currentLanguage, 'bridgeServer'),
              'http://127.0.0.1:8960',
              isDark,
            ),
            Divider(
              height: 1,
              color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
            ),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
              child: Row(
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          appText(widget.currentLanguage, 'logFolder'),
                          style: TextStyle(
                            fontSize: 13,
                            fontWeight: FontWeight.w500,
                            color: isDark
                                ? AppColors.darkTextMuted
                                : AppColors.lightTextMuted,
                          ),
                        ),
                        const SizedBox(height: 4),
                        Text(
                          '%LOCALAPPDATA%\\OSharePC',
                          style: TextStyle(
                            fontSize: 12,
                            color: isDark
                                ? AppColors.darkTextMuted
                                : AppColors.lightTextMuted,
                          ),
                        ),
                      ],
                    ),
                  ),
                  OutlinedButton.icon(
                    onPressed: _openLogFolder,
                    icon: const Icon(Icons.folder_open, size: 16),
                    label: Text(
                      appText(widget.currentLanguage, 'openLogFolder'),
                    ),
                    style: OutlinedButton.styleFrom(
                      foregroundColor: isDark
                          ? AppColors.darkAccent
                          : AppColors.lightAccent,
                      side: BorderSide(
                        color: isDark
                            ? AppColors.darkBorder
                            : AppColors.lightBorder,
                      ),
                      shape: RoundedRectangleBorder(
                        borderRadius: BorderRadius.circular(10),
                      ),
                    ),
                  ),
                ],
              ),
            ),
            Divider(
              height: 1,
              color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
            ),
            _buildInfoRow(
              appText(widget.currentLanguage, 'version'),
              'OsharePC GUI $_guiVersion',
              isDark,
            ),
          ],
        ),
      ],
    );
  }

  Future<void> _showOppoAccountLoginDialog(BuildContext context, bool isDark) async {
    widget.client.startOppoAccountLogin();
    await showDialog<void>(
      context: context,
      barrierDismissible: false,
      builder: (dialogContext) {
        return AnimatedBuilder(
          animation: widget.client,
          builder: (context, _) {
            final client = widget.client;
            final methods = client.oppoAccountMethods;
            final error = client.oppoAccountError;
            final status = client.oppoAccountStatus;
            final qrUrl = client.oppoAccountQrUrl;

            Widget body;
            if (error != null) {
              body = Text(
                error,
                style: TextStyle(
                  color: isDark ? AppColors.darkText : AppColors.lightText,
                ),
              );
            } else if (methods != null) {
              body = Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  if (client.oppoAccountName != null) ...[
                    Text(
                      client.oppoAccountName!,
                      style: TextStyle(
                        fontWeight: FontWeight.w600,
                        color: isDark
                            ? AppColors.darkText
                            : AppColors.lightText,
                      ),
                    ),
                    const SizedBox(height: 8),
                  ],
                  Text(
                    appText(widget.currentLanguage, 'oppoAccountMethodsFound'),
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w600,
                      color: isDark
                          ? AppColors.darkTextMuted
                          : AppColors.lightTextMuted,
                    ),
                  ),
                  const SizedBox(height: 4),
                  Text(
                    methods.isEmpty ? '—' : methods.join(', '),
                    style: TextStyle(
                      color: isDark ? AppColors.darkText : AppColors.lightText,
                    ),
                  ),
                  const SizedBox(height: 12),
                  Text(
                    appText(widget.currentLanguage, 'oppoAccountNoMoreYet'),
                    style: TextStyle(
                      fontSize: 12,
                      color: isDark
                          ? AppColors.darkTextMuted
                          : AppColors.lightTextMuted,
                    ),
                  ),
                ],
              );
            } else if (qrUrl != null) {
              String statusText;
              switch (status) {
                case 'SCANNED':
                  statusText = appText(widget.currentLanguage, 'oppoAccountScanned');
                  break;
                case 'CONFIRMED':
                  statusText = appText(widget.currentLanguage, 'oppoAccountConfirmed');
                  break;
                case 'EXPIRED':
                case 'CANCELLED':
                  statusText = appText(widget.currentLanguage, 'oppoAccountExpired');
                  break;
                default:
                  statusText = appText(widget.currentLanguage, 'oppoAccountWaitingScan');
              }
              body = Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(appText(widget.currentLanguage, 'oppoAccountScanHint')),
                  const SizedBox(height: 16),
                  ClipRRect(
                    borderRadius: BorderRadius.circular(8),
                    child: Image.network(qrUrl, width: 220, height: 220),
                  ),
                  const SizedBox(height: 16),
                  Text(
                    statusText,
                    style: TextStyle(
                      color: isDark
                          ? AppColors.darkTextMuted
                          : AppColors.lightTextMuted,
                    ),
                  ),
                ],
              );
            } else {
              body = const Padding(
                padding: EdgeInsets.all(24),
                child: SizedBox(
                  width: 24,
                  height: 24,
                  child: CircularProgressIndicator(strokeWidth: 2.5),
                ),
              );
            }

            return AlertDialog(
              backgroundColor: isDark ? AppColors.darkCard : AppColors.lightCard,
              title: Text(
                appText(widget.currentLanguage, 'oppoAccountDialogTitle'),
                style: TextStyle(
                  color: isDark ? AppColors.darkText : AppColors.lightText,
                ),
              ),
              content: body,
              actions: [
                TextButton(
                  onPressed: () {
                    if (client.oppoAccountActive) {
                      client.cancelOppoAccountLogin();
                    } else {
                      client.clearOppoAccountState();
                    }
                    Navigator.of(dialogContext).pop();
                  },
                  child: Text(appText(widget.currentLanguage, 'close')),
                ),
              ],
            );
          },
        );
      },
    );
  }

  Widget _buildSectionHeader(String title, bool isDark) {
    return Text(
      title,
      style: TextStyle(
        fontSize: 18,
        fontWeight: FontWeight.w700,
        color: isDark ? AppColors.darkText : AppColors.lightText,
      ),
    );
  }

  Widget _buildCard(bool isDark, {required List<Widget> children}) {
    return Container(
      decoration: BoxDecoration(
        color: isDark ? AppColors.darkCard : AppColors.lightCard,
        borderRadius: BorderRadius.circular(16),
        border: Border.all(
          color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
          width: 1,
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: children,
      ),
    );
  }

  Widget _buildAccentSwatch(AccentPreset preset, bool isDark) {
    final color = isDark ? preset.dark : preset.light;
    final isSelected = preset.name == widget.currentAccent.name;

    return Tooltip(
      message: appText(widget.currentLanguage, 'accent${preset.name}'),
      child: GestureDetector(
        onTap: () => widget.onAccentChanged(preset),
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          width: 28,
          height: 28,
          decoration: BoxDecoration(
            color: color,
            shape: BoxShape.circle,
            border: Border.all(
              color: isSelected
                  ? (isDark ? AppColors.darkText : AppColors.lightText)
                  : Colors.transparent,
              width: 2,
            ),
          ),
          alignment: Alignment.center,
          child: isSelected
              ? Icon(
                  Icons.check,
                  size: 15,
                  color:
                      ThemeData.estimateBrightnessForColor(color) ==
                          Brightness.dark
                      ? Colors.white
                      : Colors.black87,
                )
              : null,
        ),
      ),
    );
  }

  Widget _buildInfoRow(String label, String value, bool isDark) {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      child: Row(
        children: [
          Text(
            label,
            style: TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w500,
              color: isDark
                  ? AppColors.darkTextMuted
                  : AppColors.lightTextMuted,
            ),
          ),
          const Spacer(),
          Text(
            value,
            style: TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w600,
              color: isDark ? AppColors.darkText : AppColors.lightText,
            ),
          ),
        ],
      ),
    );
  }
}
