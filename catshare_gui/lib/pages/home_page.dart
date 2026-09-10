import 'package:flutter/material.dart';
import '../config/theme.dart';
import '../config/language.dart';
import '../services/bridge_client.dart';
import '../services/outgoing_staging_controller.dart';
import 'receive_tab.dart';
import 'send_tab.dart';
import 'settings_tab.dart';

class HomePage extends StatefulWidget {
  final BridgeClient client;
  final ThemeMode currentThemeMode;
  final ValueChanged<ThemeMode> onThemeChanged;
  final AppLanguage currentLanguage;
  final ValueChanged<AppLanguage> onLanguageChanged;

  const HomePage({
    super.key,
    required this.client,
    required this.currentThemeMode,
    required this.onThemeChanged,
    required this.currentLanguage,
    required this.onLanguageChanged,
  });

  @override
  State<HomePage> createState() => _HomePageState();
}

class _HomePageState extends State<HomePage> {
  static const double _sidebarWidth = 188;

  int _currentIndex = 0;
  late final OutgoingStagingController _stagingController;

  @override
  void initState() {
    super.initState();
    _stagingController = OutgoingStagingController(bridgeClient: widget.client);
  }

  @override
  void dispose() {
    _stagingController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final isDark = theme.brightness == Brightness.dark;
    final railBg = isDark ? AppColors.darkSurface : AppColors.lightSurface;
    final status = widget.client.status;

    return Scaffold(
      body: Row(
        children: [
          Container(
            width: _sidebarWidth,
            decoration: BoxDecoration(
              color: railBg,
              border: Border(
                right: BorderSide(
                  color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
                  width: 1,
                ),
              ),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const SizedBox(height: 24),
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 18),
                  child: Text(
                    'OsharePC',
                    style: TextStyle(
                      fontSize: 15,
                      fontWeight: FontWeight.w600,
                      letterSpacing: -0.2,
                      color: isDark
                          ? AppColors.darkTextMuted
                          : AppColors.lightTextMuted,
                    ),
                  ),
                ),
                const SizedBox(height: 24),
                _buildNavItem(
                  index: 0,
                  icon: Icons.wifi_tethering_rounded,
                  label: appText(widget.currentLanguage, 'receive'),
                  isDark: isDark,
                ),
                const SizedBox(height: 4),
                _buildNavItem(
                  index: 1,
                  icon: Icons.send_rounded,
                  label: appText(widget.currentLanguage, 'send'),
                  isDark: isDark,
                ),
                const SizedBox(height: 4),
                _buildNavItem(
                  index: 2,
                  icon: Icons.settings_rounded,
                  label: appText(widget.currentLanguage, 'settings'),
                  isDark: isDark,
                ),
                const Spacer(),
                Padding(
                  padding: const EdgeInsets.fromLTRB(16, 12, 16, 18),
                  child: Row(
                    children: [
                      Container(
                        width: 7,
                        height: 7,
                        decoration: BoxDecoration(
                          shape: BoxShape.circle,
                          color: widget.client.isConnecting
                              ? Colors.amber
                              : (isDark
                                    ? AppColors.darkAccent
                                    : AppColors.lightAccent),
                        ),
                      ),
                      const SizedBox(width: 9),
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            Text(
                              widget.client.isConnecting
                                  ? 'Connecting...'
                                  : 'Online',
                              style: TextStyle(
                                fontSize: 12,
                                fontWeight: FontWeight.w600,
                                color: isDark
                                    ? AppColors.darkText
                                    : AppColors.lightText,
                              ),
                            ),
                            const SizedBox(height: 2),
                            Text(
                              status.lanIp.isEmpty
                                  ? 'Mutual Transmission'
                                  : status.lanIp,
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                              style: TextStyle(
                                fontSize: 11,
                                color: isDark
                                    ? AppColors.darkTextMuted
                                    : AppColors.lightTextMuted,
                              ),
                            ),
                          ],
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
          Expanded(
            child: IndexedStack(
              index: _currentIndex,
              children: [
                ReceiveTab(
                  client: widget.client,
                  language: widget.currentLanguage,
                  stagingController: _stagingController,
                  isCurrentTab: _currentIndex == 0,
                  onNavigateToSend: () => setState(() => _currentIndex = 1),
                ),
                SendTab(
                  client: widget.client,
                  language: widget.currentLanguage,
                  stagingController: _stagingController,
                  isCurrentTab: _currentIndex == 1,
                ),
                SettingsTab(
                  client: widget.client,
                  currentThemeMode: widget.currentThemeMode,
                  onThemeChanged: widget.onThemeChanged,
                  currentLanguage: widget.currentLanguage,
                  onLanguageChanged: widget.onLanguageChanged,
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildNavItem({
    required int index,
    required IconData icon,
    required String label,
    required bool isDark,
  }) {
    final isSelected = _currentIndex == index;
    final activeBg = isDark ? const Color(0xFF1B352D) : const Color(0xFFDCEBE5);
    final activeFg = isDark ? AppColors.darkAccent : AppColors.lightAccent;
    final inactiveFg = isDark
        ? AppColors.darkTextMuted
        : AppColors.lightTextMuted;

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 10),
      child: InkWell(
        onTap: () => setState(() => _currentIndex = index),
        borderRadius: BorderRadius.circular(10),
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 160),
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          decoration: BoxDecoration(
            color: isSelected ? activeBg : Colors.transparent,
            borderRadius: BorderRadius.circular(10),
          ),
          child: Row(
            children: [
              Icon(icon, size: 19, color: isSelected ? activeFg : inactiveFg),
              const SizedBox(width: 11),
              Text(
                label,
                style: TextStyle(
                  fontSize: 13,
                  fontWeight: isSelected ? FontWeight.w600 : FontWeight.w500,
                  color: isSelected ? activeFg : inactiveFg,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
