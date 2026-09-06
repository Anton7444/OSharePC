import 'package:flutter/material.dart';
import '../config/theme.dart';
import '../config/language.dart';
import '../services/bridge_client.dart';
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
  int _currentIndex = 0;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final isDark = theme.brightness == Brightness.dark;
    final railBg = isDark ? AppColors.darkSurface : AppColors.lightSurface;
    final status = widget.client.status;

    return Scaffold(
      body: Row(
        children: [
          // 1. Left Sidebar Rail (LocalSend aesthetic)
          Container(
            width: 220,
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
                const SizedBox(height: 32),
                // App Brand
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 24),
                  child: Row(
                    children: [
                      Container(
                        width: 36,
                        height: 36,
                        decoration: BoxDecoration(
                          color: isDark
                              ? AppColors.darkAccent
                              : AppColors.lightAccent,
                          borderRadius: BorderRadius.circular(10),
                        ),
                        child: const Icon(
                          Icons.pets_rounded,
                          size: 20,
                          color: Color(0xFF072920),
                        ),
                      ),
                      const SizedBox(width: 12),
                      Text(
                        'OsharePC',
                        style: TextStyle(
                          fontSize: 22,
                          fontWeight: FontWeight.w800,
                          letterSpacing: -0.5,
                          color: isDark
                              ? AppColors.darkText
                              : AppColors.lightText,
                        ),
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: 36),

                // Navigation Items
                _buildNavItem(
                  index: 0,
                  icon: Icons.wifi_tethering_rounded,
                  label: appText(widget.currentLanguage, 'receive'),
                  isDark: isDark,
                ),
                const SizedBox(height: 6),
                _buildNavItem(
                  index: 1,
                  icon: Icons.send_rounded,
                  label: appText(widget.currentLanguage, 'send'),
                  isDark: isDark,
                ),
                const SizedBox(height: 6),
                _buildNavItem(
                  index: 2,
                  icon: Icons.settings_rounded,
                  label: appText(widget.currentLanguage, 'settings'),
                  isDark: isDark,
                ),

                const Spacer(),

                // Bottom Connection Status Indicator
                Padding(
                  padding: const EdgeInsets.all(20),
                  child: Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 14,
                      vertical: 10,
                    ),
                    decoration: BoxDecoration(
                      color: isDark ? AppColors.darkCard : AppColors.lightCard,
                      borderRadius: BorderRadius.circular(12),
                      border: Border.all(
                        color: isDark
                            ? AppColors.darkBorder
                            : AppColors.lightBorder,
                        width: 1,
                      ),
                    ),
                    child: Row(
                      children: [
                        Container(
                          width: 8,
                          height: 8,
                          decoration: BoxDecoration(
                            shape: BoxShape.circle,
                            color: widget.client.isConnecting
                                ? Colors.amber
                                : (isDark
                                      ? AppColors.darkAccent
                                      : AppColors.lightAccent),
                          ),
                        ),
                        const SizedBox(width: 10),
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
                                  fontWeight: FontWeight.w700,
                                  color: isDark
                                      ? AppColors.darkText
                                      : AppColors.lightText,
                                ),
                              ),
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
                ),
              ],
            ),
          ),

          // 2. Right Main Content Panel
          Expanded(
            child: IndexedStack(
              index: _currentIndex,
              children: [
                ReceiveTab(
                  client: widget.client,
                  language: widget.currentLanguage,
                ),
                SendTab(
                  client: widget.client,
                  language: widget.currentLanguage,
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
    final activeBg = isDark ? const Color(0xFF1E3F35) : const Color(0xFFD4EDE5);
    final activeFg = isDark ? AppColors.darkAccent : AppColors.lightAccent;
    final inactiveFg = isDark
        ? AppColors.darkTextMuted
        : AppColors.lightTextMuted;

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 14),
      child: InkWell(
        onTap: () => setState(() => _currentIndex = index),
        borderRadius: BorderRadius.circular(14),
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 180),
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
          decoration: BoxDecoration(
            color: isSelected ? activeBg : Colors.transparent,
            borderRadius: BorderRadius.circular(14),
          ),
          child: Row(
            children: [
              Icon(icon, size: 20, color: isSelected ? activeFg : inactiveFg),
              const SizedBox(width: 14),
              Text(
                label,
                style: TextStyle(
                  fontSize: 14,
                  fontWeight: isSelected ? FontWeight.w700 : FontWeight.w500,
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
