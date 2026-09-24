import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:http/http.dart' as http;
import 'package:shared_preferences/shared_preferences.dart';
import 'package:window_manager/window_manager.dart';

import '../config/language.dart';
import '../config/theme.dart';
import '../services/bridge_client.dart';
import '../services/receive_popup_service.dart';
import '../services/transfer_presentation.dart';
import 'desktop_drop_panel.dart' show anchoredRect, resolveCornerAnchor;

// Small always-on-top card shown in the bottom-right corner while the main
// window is minimized or hidden. It runs in its own process, launched by
// ReceivePopupService, and exits as soon as it is answered or dismissed.
// Like the desktop drop panel, the window surface is transparent and the
// card floats inside it, inset so its shadow is not clipped.
const receivePopupSize = Size(376, 176);
const _cardWidth = 360.0;
const _completedAutoClose = Duration(seconds: 10);

Future<void> runReceivePopup() async {
  final request = ReceivePopupRequest.fromEnvironment();
  if (request == null) exit(0);

  final prefs = await SharedPreferences.getInstance();
  final languageIndex = prefs.getInt('language') ?? AppLanguage.english.index;
  final language =
      AppLanguage.values[languageIndex.clamp(0, AppLanguage.values.length - 1)];
  final themeIndex = prefs.getInt('theme_mode') ?? ThemeMode.dark.index;
  final themeMode = ThemeMode.values[themeIndex.clamp(0, 2)];
  final accentName = prefs.getString('accent_color');
  AppColors.applyAccent(
    AppColors.accentPresets.firstWhere(
      (preset) => preset.name == accentName,
      orElse: () => AppColors.accentPresets.first,
    ),
  );

  await windowManager.ensureInitialized();
  Rect? bounds;
  try {
    bounds = anchoredRect(await resolveCornerAnchor(), receivePopupSize);
  } catch (_) {}
  windowManager.waitUntilReadyToShow(
    const WindowOptions(
      size: receivePopupSize,
      title: 'OsharePC Receive',
      titleBarStyle: TitleBarStyle.hidden,
      backgroundColor: Colors.transparent,
      alwaysOnTop: true,
      skipTaskbar: true,
    ),
    () async {
      await windowManager.setAsFrameless();
      await windowManager.setResizable(false);
      if (bounds != null) await windowManager.setBounds(bounds);
      // setBounds drops topmost on Windows; re-assert it last.
      await windowManager.show(inactive: true);
      // Re-assert once visible so the popup lands above other topmost
      // windows in the same corner, such as the drop target's hot-zone.
      await windowManager.setAlwaysOnTop(true);
    },
  );

  runApp(
    MaterialApp(
      title: 'OsharePC Receive',
      debugShowCheckedModeBanner: false,
      theme: AppTheme.lightTheme,
      darkTheme: AppTheme.darkTheme,
      themeMode: themeMode,
      locale: language.locale,
      supportedLocales: AppLanguage.values.map((l) => l.locale),
      home: ReceivePopupPage(request: request, language: language),
    ),
  );
}

class ReceivePopupPage extends StatefulWidget {
  final ReceivePopupRequest request;
  final AppLanguage language;

  const ReceivePopupPage({
    super.key,
    required this.request,
    required this.language,
  });

  @override
  State<ReceivePopupPage> createState() => _ReceivePopupPageState();
}

class _ReceivePopupPageState extends State<ReceivePopupPage>
    with SingleTickerProviderStateMixin {
  Timer? _autoClose;
  bool _busy = false;
  late final AnimationController _appear;

  bool get _isOffer => widget.request.mode == ReceivePopupMode.offer;

  @override
  void initState() {
    super.initState();
    _appear = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 220),
    )..forward();
    if (!_isOffer) _autoClose = Timer(_completedAutoClose, _close);
  }

  @override
  void dispose() {
    _autoClose?.cancel();
    _appear.dispose();
    super.dispose();
  }

  Future<void> _close() async {
    try {
      await _appear.reverse();
      await windowManager.hide();
    } catch (_) {}
    exit(0);
  }

  Future<void> _answer(bool accept) async {
    if (_busy) return;
    setState(() => _busy = true);
    try {
      await http
          .post(
            Uri.parse('${BridgeClient.baseUrl}/api/confirm-receive'),
            headers: {
              BridgeClient.tokenHeader:
                  Platform.environment['OSHAREPC_BRIDGE_TOKEN'] ?? '',
              'Content-Type': 'application/json',
            },
            body: jsonEncode({'id': widget.request.id, 'accept': accept}),
          )
          .timeout(const Duration(seconds: 3));
    } catch (error) {
      debugPrint('[ReceivePopup] confirm-receive failed: $error');
    }
    await _close();
  }

  void _openFolder() {
    final dir = widget.request.saveDirectory;
    if (dir.isNotEmpty && Directory(dir).existsSync()) {
      Process.start('explorer.exe', [dir]);
    }
    _close();
  }

  @override
  Widget build(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final curve = CurvedAnimation(parent: _appear, curve: Curves.easeOutCubic);

    return Scaffold(
      backgroundColor: Colors.transparent,
      body: Align(
        alignment: Alignment.bottomRight,
        child: FadeTransition(
          opacity: curve,
          child: SlideTransition(
            position: Tween(
              begin: const Offset(0, 0.12),
              end: Offset.zero,
            ).animate(curve),
            child: SafeArea(
              minimum: const EdgeInsets.all(8),
              child: SizedBox(
                width: _cardWidth,
                child: Material(
                  color: isDark
                      ? AppColors.darkSurface
                      : AppColors.lightSurface,
                  borderRadius: BorderRadius.circular(18),
                  elevation: 10,
                  clipBehavior: Clip.antiAlias,
                  child: Padding(
                    padding: const EdgeInsets.fromLTRB(14, 10, 14, 12),
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        _buildHeader(isDark),
                        const SizedBox(height: 10),
                        _buildSummary(isDark),
                        const SizedBox(height: 10),
                        _isOffer
                            ? _buildOfferActions(isDark)
                            : _buildCompletedActions(isDark),
                      ],
                    ),
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildHeader(bool isDark) {
    final title = _isOffer
        ? appText(widget.language, 'incomingTransfer')
        : appText(widget.language, 'receiveComplete');
    return SizedBox(
      height: 32,
      child: Row(
        children: [
          Container(
            width: 28,
            height: 28,
            decoration: BoxDecoration(
              color: isDark
                  ? AppColors.darkAccentSoft
                  : AppColors.lightAccentSoft,
              borderRadius: BorderRadius.circular(9),
            ),
            child: Icon(
              _isOffer ? Icons.file_download_rounded : Icons.check_rounded,
              size: 17,
              color: isDark ? AppColors.darkAccent : AppColors.lightAccent,
            ),
          ),
          const SizedBox(width: 9),
          Expanded(
            child: Text(
              title,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: 14,
                fontWeight: FontWeight.w700,
                color: isDark ? AppColors.darkText : AppColors.lightText,
              ),
            ),
          ),
          if (!_isOffer)
            IconButton(
              onPressed: _close,
              icon: const Icon(Icons.close_rounded, size: 18),
              visualDensity: VisualDensity.compact,
            ),
        ],
      ),
    );
  }

  Widget _buildSummary(bool isDark) {
    final request = widget.request;
    final lang = widget.language;
    final muted = isDark ? AppColors.darkTextMuted : AppColors.lightTextMuted;
    final details = [
      '${request.fileCount} ${appText(lang, 'files')}',
      if (request.totalBytes > 0) formatByteSize(request.totalBytes),
    ].join(' · ');
    return Container(
      padding: const EdgeInsets.fromLTRB(10, 8, 10, 8),
      decoration: BoxDecoration(
        color: isDark ? AppColors.darkCard : AppColors.lightCard,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(
          color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
        ),
      ),
      child: Row(
        children: [
          Icon(Icons.phone_android_rounded, size: 18, color: muted),
          const SizedBox(width: 8),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  _isOffer
                      ? '${request.senderName} ${appText(lang, 'wantsToSend')}'
                      : request.senderName,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontSize: 11,
                    fontWeight: FontWeight.w700,
                    color: isDark ? AppColors.darkText : AppColors.lightText,
                  ),
                ),
                const SizedBox(height: 2),
                Text(details, style: TextStyle(fontSize: 10, color: muted)),
              ],
            ),
          ),
        ],
      ),
    );
  }

  ButtonStyle _primaryStyle(bool isDark) => ElevatedButton.styleFrom(
    backgroundColor: isDark ? AppColors.darkAccent : AppColors.lightAccent,
    foregroundColor: AppColors.onAccent(isDark),
    padding: const EdgeInsets.symmetric(vertical: 8),
    shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
    elevation: 0,
  );

  Widget _buildOfferActions(bool isDark) {
    final lang = widget.language;
    return Row(
      children: [
        Expanded(
          child: OutlinedButton(
            style: OutlinedButton.styleFrom(
              padding: const EdgeInsets.symmetric(vertical: 8),
              shape: RoundedRectangleBorder(
                borderRadius: BorderRadius.circular(10),
              ),
              side: BorderSide(
                color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
              ),
            ),
            onPressed: _busy ? null : () => _answer(false),
            child: Text(
              appText(lang, 'decline'),
              style: TextStyle(
                color: isDark
                    ? AppColors.darkTextMuted
                    : AppColors.lightTextMuted,
                fontWeight: FontWeight.w600,
              ),
            ),
          ),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: ElevatedButton(
            style: _primaryStyle(isDark),
            onPressed: _busy ? null : () => _answer(true),
            child: Text(
              appText(lang, 'accept'),
              style: const TextStyle(fontWeight: FontWeight.w700),
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildCompletedActions(bool isDark) {
    return SizedBox(
      width: double.infinity,
      child: ElevatedButton.icon(
        style: _primaryStyle(isDark),
        onPressed: widget.request.saveDirectory.isEmpty ? null : _openFolder,
        icon: const Icon(Icons.folder_open_rounded, size: 18),
        label: Text(
          appText(widget.language, 'openFolder'),
          style: const TextStyle(fontWeight: FontWeight.w700),
        ),
      ),
    );
  }
}
