import 'dart:io';
import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:shared_preferences/shared_preferences.dart';
import '../config/theme.dart';
import '../config/language.dart';
import '../models/models.dart';
import '../services/bridge_client.dart';
import '../services/startup_service.dart';
import '../widgets/custom_segmented_button.dart';

class SettingsTab extends StatefulWidget {
  final BridgeClient client;
  final ThemeMode currentThemeMode;
  final ValueChanged<ThemeMode> onThemeChanged;
  final AppLanguage currentLanguage;
  final ValueChanged<AppLanguage> onLanguageChanged;

  const SettingsTab({
    super.key,
    required this.client,
    required this.currentThemeMode,
    required this.onThemeChanged,
    required this.currentLanguage,
    required this.onLanguageChanged,
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

  @override
  void initState() {
    super.initState();
    _loadTrayPrefs();
    _loadStartupPrefs();
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
      setState(() {
        _minimizeToTray = prefs.getBool('minimize_to_tray') ?? true;
        _closeToTray = prefs.getBool('close_to_tray') ?? true;
      });
    } catch (_) {}
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

    final dir = Directory('$localAppData\\CatShareSender');
    if (!dir.existsSync()) {
      dir.createSync(recursive: true);
    }
    await Process.start('explorer.exe', [dir.path]);
  }

  @override
  void dispose() {
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
                              ? 'Downloads\\CatShare'
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
                          '%LOCALAPPDATA%\\CatShareSender',
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
