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

  Future<void> _saveLaunchAtStartup(bool value) async {
    final saved = await StartupService.setEnabled(value);
    if (!mounted) return;
    setState(() => _launchAtStartup = saved ? value : false);
  }

  Future<void> _saveStartMinimized(bool value) async {
    setState(() => _startMinimized = value);
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool('start_minimized', value);
  }

  Future<void> _loadTrayPrefs() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      if (!mounted) return;
      setState(() {
        _minimizeToTray = prefs.getBool('minimize_to_tray') ?? true;
        _closeToTray = prefs.getBool('close_to_tray') ?? true;
      });
    } catch (_) {}
  }

  Future<void> _saveMinimizeToTray(bool value) async {
    setState(() => _minimizeToTray = value);
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool('minimize_to_tray', value);
    await widget.client.updateSettings(minimizeToTray: value);
  }

  Future<void> _saveCloseToTray(bool value) async {
    setState(() => _closeToTray = value);
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool('close_to_tray', value);
    await widget.client.updateSettings(closeToTray: value);
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
  Widget build(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final status = widget.client.status;

    return ListView(
      padding: const EdgeInsets.symmetric(horizontal: 28, vertical: 24),
      children: [
        _buildSectionHeader(appText(widget.currentLanguage, 'general'), isDark),
        const SizedBox(height: 12),
        _buildCard(
          isDark,
          children: [
            _buildDestinationRow(status.saveDirectory, isDark),
            _divider(isDark),
            _buildQuickSaveRow(isDark),
            _divider(isDark),
            _buildLanguageRow(isDark),
            _divider(isDark),
            _buildSwitchRow(
              title: appText(widget.currentLanguage, 'launchAtStartup'),
              value: _launchAtStartup,
              isDark: isDark,
              onChanged: StartupService.isInstalledBuild
                  ? _saveLaunchAtStartup
                  : null,
            ),
            _divider(isDark),
            _buildSwitchRow(
              title: appText(widget.currentLanguage, 'startMinimized'),
              value: _startMinimized,
              isDark: isDark,
              onChanged: _saveStartMinimized,
            ),
          ],
        ),
        const SizedBox(height: 28),
        _buildSectionHeader(
          appText(widget.currentLanguage, 'systemTray'),
          isDark,
        ),
        const SizedBox(height: 12),
        _buildCard(
          isDark,
          children: [
            _buildSwitchRow(
              title: appText(widget.currentLanguage, 'closeTray'),
              subtitle: appText(widget.currentLanguage, 'closeTrayHint'),
              value: _closeToTray,
              isDark: isDark,
              onChanged: _saveCloseToTray,
            ),
            _divider(isDark),
            _buildSwitchRow(
              title: appText(widget.currentLanguage, 'minimizeTray'),
              subtitle: appText(widget.currentLanguage, 'minimizeTrayHint'),
              value: _minimizeToTray,
              isDark: isDark,
              onChanged: _saveMinimizeToTray,
            ),
            _divider(isDark),
            _buildSwitchRow(
              title: appText(
                widget.currentLanguage,
                'receiveSuccessNotification',
              ),
              subtitle: appText(
                widget.currentLanguage,
                'receiveSuccessNotificationHint',
              ),
              value: widget.client.receiveSuccessNotifications,
              isDark: isDark,
              onChanged: widget.client.setReceiveSuccessNotifications,
            ),
          ],
        ),
        const SizedBox(height: 28),
        _buildSectionHeader(
          appText(widget.currentLanguage, 'appearance'),
          isDark,
        ),
        const SizedBox(height: 12),
        _buildCard(
          isDark,
          children: [
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
              child: Row(
                children: [
                  Expanded(
                    child: _buildLabel(
                      appText(widget.currentLanguage, 'theme'),
                      appText(widget.currentLanguage, 'themeHint'),
                      isDark,
                    ),
                  ),
                  const SizedBox(width: 16),
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
            _divider(isDark),
            _buildInfoRow(
              appText(widget.currentLanguage, 'transferPort'),
              '${status.transferPort} (Stock 互传)',
              isDark,
            ),
            _divider(isDark),
            _buildInfoRow(
              appText(widget.currentLanguage, 'bridgeServer'),
              'http://127.0.0.1:8960',
              isDark,
            ),
            _divider(isDark),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
              child: Row(
                children: [
                  Expanded(
                    child: _buildLabel(
                      appText(widget.currentLanguage, 'logFolder'),
                      '%LOCALAPPDATA%\\CatShareSender',
                      isDark,
                      compact: true,
                    ),
                  ),
                  const SizedBox(width: 16),
                  OutlinedButton.icon(
                    onPressed: _openLogFolder,
                    icon: const Icon(Icons.folder_open, size: 16),
                    label: Text(
                      appText(widget.currentLanguage, 'openLogFolder'),
                    ),
                    style: _outlinedButtonStyle(isDark),
                  ),
                ],
              ),
            ),
            _divider(isDark),
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

  Widget _buildDestinationRow(String saveDirectory, bool isDark) {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
      child: Row(
        children: [
          Expanded(
            child: _buildLabel(
              appText(widget.currentLanguage, 'destination'),
              saveDirectory.isEmpty ? 'Downloads\\CatShare' : saveDirectory,
              isDark,
            ),
          ),
          const SizedBox(width: 16),
          OutlinedButton.icon(
            onPressed: _pickFolder,
            icon: const Icon(Icons.folder_open, size: 16),
            label: Text(appText(widget.currentLanguage, 'browse')),
            style: _outlinedButtonStyle(isDark),
          ),
          const SizedBox(width: 4),
          IconButton(
            tooltip: appText(widget.currentLanguage, 'openFolder'),
            onPressed: _openFolder,
            visualDensity: VisualDensity.compact,
            icon: const Icon(Icons.open_in_new, size: 18),
            color: isDark
                ? AppColors.darkTextMuted
                : AppColors.lightTextMuted,
          ),
        ],
      ),
    );
  }

  Widget _buildQuickSaveRow(bool isDark) {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
      child: Row(
        children: [
          Expanded(
            child: _buildLabel(
              appText(widget.currentLanguage, 'quickSave'),
              appText(widget.currentLanguage, 'quickSaveHint'),
              isDark,
            ),
          ),
          const SizedBox(width: 16),
          Switch(
            value: widget.client.quickSaveMode == QuickSaveMode.on,
            activeThumbColor: isDark
                ? AppColors.darkAccent
                : AppColors.lightAccent,
            onChanged: (value) => widget.client.setQuickSaveMode(
              value ? QuickSaveMode.on : QuickSaveMode.off,
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildLanguageRow(bool isDark) {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
      child: Row(
        children: [
          Expanded(
            child: _buildLabel(
              appText(widget.currentLanguage, 'language'),
              appText(widget.currentLanguage, 'chooseLanguage'),
              isDark,
            ),
          ),
          const SizedBox(width: 16),
          CustomSegmentedButton<AppLanguage>(
            values: AppLanguage.values,
            labels: AppLanguage.values.map((language) => language.label).toList(),
            selected: widget.currentLanguage,
            onSelected: widget.onLanguageChanged,
          ),
        ],
      ),
    );
  }

  Widget _buildSwitchRow({
    required String title,
    String? subtitle,
    required bool value,
    required bool isDark,
    required ValueChanged<bool>? onChanged,
  }) {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
      child: Row(
        children: [
          Expanded(child: _buildLabel(title, subtitle, isDark)),
          const SizedBox(width: 16),
          Switch(
            value: value,
            activeThumbColor: isDark
                ? AppColors.darkAccent
                : AppColors.lightAccent,
            onChanged: onChanged,
          ),
        ],
      ),
    );
  }

  Widget _buildLabel(
    String title,
    String? subtitle,
    bool isDark, {
    bool compact = false,
  }) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          title,
          style: TextStyle(
            fontSize: compact ? 13 : 14,
            fontWeight: FontWeight.w600,
            color: compact
                ? (isDark
                      ? AppColors.darkTextMuted
                      : AppColors.lightTextMuted)
                : (isDark ? AppColors.darkText : AppColors.lightText),
          ),
        ),
        if (subtitle != null && subtitle.isNotEmpty) ...[
          const SizedBox(height: 4),
          Text(
            subtitle,
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
            style: TextStyle(
              fontSize: 12,
              height: 1.35,
              color: isDark
                  ? AppColors.darkTextMuted
                  : AppColors.lightTextMuted,
            ),
          ),
        ],
      ],
    );
  }

  Widget _buildSectionHeader(String title, bool isDark) {
    return Text(
      title,
      style: TextStyle(
        fontSize: 17,
        fontWeight: FontWeight.w600,
        color: isDark ? AppColors.darkText : AppColors.lightText,
      ),
    );
  }

  Widget _buildCard(bool isDark, {required List<Widget> children}) {
    return Container(
      decoration: BoxDecoration(
        color: isDark ? AppColors.darkCard : AppColors.lightCard,
        borderRadius: BorderRadius.circular(14),
        border: Border.all(
          color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: children,
      ),
    );
  }

  Widget _divider(bool isDark) {
    return Divider(
      height: 1,
      color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
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
          const SizedBox(width: 20),
          Expanded(
            child: Text(
              value,
              textAlign: TextAlign.end,
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: 13,
                fontWeight: FontWeight.w500,
                color: isDark ? AppColors.darkText : AppColors.lightText,
              ),
            ),
          ),
        ],
      ),
    );
  }

  ButtonStyle _outlinedButtonStyle(bool isDark) {
    return OutlinedButton.styleFrom(
      foregroundColor: isDark ? AppColors.darkAccent : AppColors.lightAccent,
      side: BorderSide(
        color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
      ),
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(9)),
    );
  }
}
