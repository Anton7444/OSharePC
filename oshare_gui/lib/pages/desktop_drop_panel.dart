import 'dart:async';

import 'package:flutter/material.dart';
import 'package:screen_retriever/screen_retriever.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:window_manager/window_manager.dart';

import '../config/language.dart';
import '../config/theme.dart';
import '../models/models.dart';
import '../services/bridge_client.dart';
import '../services/outgoing_staging_controller.dart';
import '../services/transfer_presentation.dart';
import '../widgets/native_drop_zone.dart';

// The native OLE hot-zone owns desktop hit-testing. The fixed Flutter window
// stays hidden at idle, appears for a file drag, and displays the picker after
// a successful drop.
const panelDropTargetSize = Size(360, 150);
const panelExpandedMinWidth = 364.0; // two phone columns
// Upper bound only: the staged panel hugs its content vertically.
const panelExpandedHeight = 280.0;
const _deviceChipWidth = 156.0;
const _deviceChipGap = 8.0;
const _deviceChipHeight = 58.0;
// SafeArea inset (2 x 8) plus the card's horizontal padding (2 x 14).
const _panelHorizontalChrome = 44.0;
const panelExpandedSize = Size(panelExpandedMinWidth, panelExpandedHeight);
const panelWindowSize = Size(720, 360);
const desktopDropInstructionMaxLines = 3;
// Kept as aliases while the state-transition animation is migrated.
const panelIdleSize = panelDropTargetSize;
const panelPreviewSize = panelDropTargetSize;
const _cornerMargin = 0.0;

enum DesktopDropPanelStage { idle, dragging, staged }

// Manual cancellation is independent from post-transfer cleanup. Keeping this
// policy explicit prevents a failed clear from being retried by terminal-state
// handling during the next build.
const manualCancelArmsTransferCleanup = false;

// Runtime transition used by the manual-cancel path. It deliberately clears
// an already-armed flag even when the bridge clear fails.
bool manualCancelCleanupState({
  required bool wasArmed,
  required bool cleared,
}) => false;

String fileNameForPath(String path) {
  final normalized = path.replaceAll('\\', '/');
  final parts = normalized.split('/');
  return parts.isEmpty || parts.last.isEmpty ? path : parts.last;
}

String _formatBytes(int bytes) {
  if (bytes < 1024) return '$bytes B';
  if (bytes < 1024 * 1024) return '${(bytes / 1024).toStringAsFixed(1)} KB';
  if (bytes < 1024 * 1024 * 1024) {
    return '${(bytes / (1024 * 1024)).toStringAsFixed(1)} MB';
  }
  return '${(bytes / (1024 * 1024 * 1024)).toStringAsFixed(1)} GB';
}

Size panelSizeForStage(DesktopDropPanelStage stage, {int deviceCount = 0}) {
  switch (stage) {
    case DesktopDropPanelStage.idle:
    case DesktopDropPanelStage.dragging:
      return panelDropTargetSize;
    case DesktopDropPanelStage.staged:
      final width = panelWidthForDeviceCount(deviceCount);
      return Size(width, panelExpandedHeight);
  }
}

bool panelShouldExpand({
  required bool isDragging,
  required bool hasStagedFiles,
}) => hasStagedFiles;

DesktopDropPanelStage panelStageForState({
  required bool isDragging,
  required bool hasStagedFiles,
  bool terminal = false,
}) {
  if (terminal || (!isDragging && !hasStagedFiles)) {
    return DesktopDropPanelStage.idle;
  }
  if (hasStagedFiles) return DesktopDropPanelStage.staged;
  return DesktopDropPanelStage.dragging;
}

/// Phones are laid out in a grid: two columns for up to two phones, three
/// columns beyond that. Extra rows scroll instead of widening the panel.
int panelDeviceColumns(int count) => count <= 2 ? 2 : 3;

double panelWidthForDeviceCount(int count) {
  final columns = panelDeviceColumns(count);
  return _panelHorizontalChrome +
      columns * _deviceChipWidth +
      (columns - 1) * _deviceChipGap;
}

DeviceModel? resolveSelectedDevice(
  List<DeviceModel> devices,
  String? selectedAddress,
) {
  if (selectedAddress == null) return null;
  for (final device in devices) {
    if (device.address == selectedAddress) return device;
  }
  return null;
}

/// The bottom-right corner of the primary display's work area (i.e.
/// excluding the taskbar), in the same logical-pixel space `window_manager`
/// uses for window bounds.
Future<Offset> resolveCornerAnchor({double margin = _cornerMargin}) async {
  try {
    final display = await screenRetriever.getPrimaryDisplay();
    final position = display.visiblePosition;
    final size = display.visibleSize;
    if (position == null || size == null) {
      throw StateError('Primary display work area is unavailable.');
    }
    return Offset(
      position.dx + size.width - margin,
      position.dy + size.height - margin,
    );
  } catch (error) {
    debugPrint('[DesktopDropPanel] Failed to resolve corner anchor: $error');
    rethrow;
  }
}

Rect panelWindowRect(Offset bottomRight) =>
    anchoredRect(bottomRight, panelWindowSize);

Rect anchoredRect(Offset bottomRight, Size size) => Rect.fromLTWH(
  bottomRight.dx - size.width,
  bottomRight.dy - size.height,
  size.width,
  size.height,
);

class DesktopDropPanelApp extends StatefulWidget {
  final BridgeClient bridgeClient;
  final AppLanguage initialLanguage;
  final ThemeMode initialThemeMode;
  final AccentPreset initialAccent;

  const DesktopDropPanelApp({
    super.key,
    required this.bridgeClient,
    required this.initialLanguage,
    required this.initialThemeMode,
    required this.initialAccent,
  });

  @override
  State<DesktopDropPanelApp> createState() => _DesktopDropPanelAppState();
}

class _DesktopDropPanelAppState extends State<DesktopDropPanelApp> {
  late AppLanguage _language;
  late ThemeMode _themeMode;
  late AccentPreset _accent;
  Timer? _preferenceTimer;
  SharedPreferences? _preferences;

  @override
  void initState() {
    super.initState();
    _language = widget.initialLanguage;
    _themeMode = widget.initialThemeMode;
    _accent = widget.initialAccent;
    AppColors.applyAccent(_accent);
    widget.bridgeClient.setLanguage(_language);
    _preferenceTimer = Timer.periodic(
      const Duration(seconds: 1),
      (_) => _reloadPreferences(),
    );
    _reloadPreferences();
  }

  Future<void> _reloadPreferences() async {
    try {
      final preferences = _preferences ??=
          await SharedPreferences.getInstance();
      await preferences.reload();

      final languageIndex = preferences.getInt('language') ?? _language.index;
      final nextLanguage = AppLanguage
          .values[languageIndex.clamp(0, AppLanguage.values.length - 1)];
      final themeIndex = preferences.getInt('theme_mode') ?? _themeMode.index;
      final nextTheme = ThemeMode.values[themeIndex.clamp(0, 2)];
      final accentName = preferences.getString('accent_color');
      final nextAccent = AppColors.accentPresets.firstWhere(
        (preset) => preset.name == accentName,
        orElse: () => _accent,
      );

      if (!mounted) return;
      final changed =
          nextLanguage != _language ||
          nextTheme != _themeMode ||
          nextAccent.name != _accent.name;
      if (!changed) return;

      setState(() {
        _language = nextLanguage;
        _themeMode = nextTheme;
        _accent = nextAccent;
        AppColors.applyAccent(nextAccent);
      });
      widget.bridgeClient.setLanguage(nextLanguage);
    } catch (error) {
      debugPrint('[DesktopDropPanelApp] Preference reload failed: $error');
    }
  }

  @override
  void dispose() {
    _preferenceTimer?.cancel();
    widget.bridgeClient.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'OsharePC Drop Target',
      debugShowCheckedModeBanner: false,
      theme: AppTheme.lightTheme,
      darkTheme: AppTheme.darkTheme,
      themeMode: _themeMode,
      locale: _language.locale,
      supportedLocales: AppLanguage.values.map((language) => language.locale),
      home: DesktopDropPanelPage(
        client: widget.bridgeClient,
        language: _language,
      ),
    );
  }
}

class DesktopDropPanelPage extends StatefulWidget {
  final BridgeClient client;
  final AppLanguage language;
  final OutgoingStagingController? stagingController;
  final Future<bool> Function(DeviceModel)? sendToDeviceOverride;
  final bool manageNativeDropPanel;

  const DesktopDropPanelPage({
    super.key,
    required this.client,
    required this.language,
    this.stagingController,
    this.sendToDeviceOverride,
    this.manageNativeDropPanel = true,
  });

  @override
  State<DesktopDropPanelPage> createState() => _DesktopDropPanelPageState();
}

class _DesktopDropPanelPageState extends State<DesktopDropPanelPage>
    with TickerProviderStateMixin {
  late final OutgoingStagingController _stagingController;

  // The native window keeps a stable 720x360 surface. This controller animates
  // only the Flutter visuals: 0 is idle, 0.5 is the compact drag panel, and 1
  // is the expanded device picker.
  late final AnimationController _visual;

  // Fades and slides the whole panel out before its window hides, matching
  // the receive popup. 1 is fully shown.
  late final AnimationController _exit;

  int _collapseGeneration = 0;
  Timer? _collapseTimer;
  Timer? _autoCollapseTimer;

  String? _selectedAddress;
  bool _isDragging = false;
  bool _hasStagedDrop = false;
  bool _clearAfterTransfer = false;
  bool _clearInFlight = false;
  bool _terminalFailureShown = false;
  bool _terminalStatusShown = false;
  bool _terminalCleanupAttempted = false;
  bool _selectionSyncPending = false;

  @override
  void initState() {
    super.initState();
    _stagingController =
        widget.stagingController ??
        OutgoingStagingController(bridgeClient: widget.client);
    _visual = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 220),
    );
    _exit = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 220),
      value: 1,
    );
  }

  // Only outgoing transfers belong to this panel. Receives must not toggle
  // the native hot-zone: re-showing it raises it above the receive popup in
  // the same corner and swallows the popup's clicks.
  bool get _isTransferActive {
    final transfer = widget.client.transferState;
    return transfer.active &&
        transfer.isSending &&
        !const ['completed', 'failed', 'cancelled'].contains(transfer.phase);
  }

  DeviceModel? _selectedDevice(List<DeviceModel> devices) {
    final selected = resolveSelectedDevice(devices, _selectedAddress);
    final resolvedAddress = selected?.address;
    if (resolvedAddress != _selectedAddress && !_selectionSyncPending) {
      _selectionSyncPending = true;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        _selectionSyncPending = false;
        if (!mounted || _selectedAddress == resolvedAddress) return;
        setState(() => _selectedAddress = resolvedAddress);
      });
    }
    return selected;
  }

  void _handleDragStateChanged(bool isDragging) {
    if (!mounted) return;
    // DragDropService can emit repeated `updated=true` events while the same
    // drag remains over the target. Native opacity/geometry work belongs only
    // to the actual state transition.
    if (isDragging == _isDragging) return;
    setState(() => _isDragging = isDragging);
    if (isDragging) {
      _onDragEntered();
    } else {
      _onDragExited();
    }
  }

  void _onDragEntered() {
    // A second drag can enter while the device picker is staged. Preserve the
    // expanded stage until that staged selection is explicitly cleared.
    if (_hasStagedDrop) return;
    _collapseTimer?.cancel();
    _autoCollapseTimer?.cancel();
    _collapseGeneration++;
    // A new drag can arrive while the exit animation is still running.
    _exit.forward();
    if (widget.manageNativeDropPanel) {
      unawaited(_showDesktopPanelForDragPreview());
    }
    _visual.animateTo(0.5, curve: Curves.easeOutCubic);
  }

  Future<void> _showDesktopPanelForDragPreview() async {
    try {
      // Never activate/focus the Flutter window while Explorer is inside an
      // OLE DoDragDrop loop. The native hot-zone remains the real drop target;
      // this window is only the visual preview until Drop/DragLeave.
      debugPrint(
        '[DesktopDropPanel] windowManager.show(inactive: true) for drag preview',
      );
      await windowManager.show(inactive: true);
    } catch (error) {
      debugPrint('[DesktopDropPanel] Failed to show drag preview: $error');
    }
  }

  Future<void> _hideDesktopPanelForIdle() async {
    try {
      debugPrint('[DesktopDropPanel] windowManager.hide() for idle');
      await windowManager.hide();
      await DragDropService.instance.setDesktopDropPanelHitTestTransparent(
        false,
      );
      await DragDropService.instance.restoreDesktopDropPanel();
      debugPrint(
        '[DesktopDropPanel] Flutter panel hidden; idle hot-zone restored',
      );
    } catch (error) {
      debugPrint('[DesktopDropPanel] Failed to hide panel for idle: $error');
      rethrow;
    }
  }

  void _onDragExited() {
    if (_hasStagedDrop) {
      if (widget.manageNativeDropPanel) {
        unawaited(
          DragDropService.instance.setDesktopDropPanelHitTestTransparent(false),
        );
      }
      return;
    }
    _collapseTimer?.cancel();
    _collapseTimer = Timer(const Duration(milliseconds: 200), () {
      if (!mounted || _isDragging) return;
      _collapse();
    });
  }

  Future<void> _collapse() async {
    final collapseGeneration = ++_collapseGeneration;
    _collapseTimer?.cancel();
    _autoCollapseTimer?.cancel();
    if (!mounted || collapseGeneration != _collapseGeneration || _isDragging) {
      return;
    }
    _hasStagedDrop = false;
    if (widget.manageNativeDropPanel) {
      await _exit.reverse();
      if (!mounted ||
          collapseGeneration != _collapseGeneration ||
          _isDragging) {
        _exit.forward();
        return;
      }
      try {
        // Dart owns Flutter visibility; native only restores its hot-zone.
        await _hideDesktopPanelForIdle();
      } catch (error) {
        // Leave the picker visible if native visibility could not be restored.
        debugPrint(
          '[DesktopDropPanel] Failed to restore the idle hot zone: $error',
        );
        _exit.forward();
        return;
      }
    }
    if (!mounted || collapseGeneration != _collapseGeneration || _isDragging) {
      return;
    }
    await _visual.animateTo(0, curve: Curves.easeOutCubic);
    if (!mounted || collapseGeneration != _collapseGeneration) return;
    // The window is hidden now; reset so the next drag appears fully.
    _exit.value = 1;
  }

  void _scheduleAutoCollapse(Duration delay) {
    _autoCollapseTimer?.cancel();
    _autoCollapseTimer = Timer(delay, () {
      if (!mounted) return;
      _collapse();
    });
  }

  Future<void> _handleDroppedPaths(List<String> paths) async {
    if (_isTransferActive) return;
    _collapseTimer?.cancel();
    setState(() {
      _isDragging = false;
    });
    final staged = await _stagingController.addPaths(
      paths,
      language: widget.language,
    );
    if (!mounted) return;
    if (!staged || !_stagingController.hasValidStagedSelection) {
      _showMessage(
        _stagingController.stagingError ??
            appText(widget.language, 'desktopDropStagingFailed'),
      );
      _scheduleAutoCollapse(const Duration(milliseconds: 2200));
      return;
    }

    _terminalFailureShown = false;
    _terminalStatusShown = false;
    _terminalCleanupAttempted = false;
    if (!mounted) return;
    _collapseGeneration++;
    setState(() => _hasStagedDrop = true);
    _visual.animateTo(1, curve: Curves.easeOutCubic);

    // Dropping only stages the files. Sending is intentionally started by the
    // user's explicit phone selection in _buildDeviceStrip.
  }

  Future<void> _sendStagedFilesTo(DeviceModel device) async {
    if (!_hasStagedDrop ||
        !_stagingController.hasValidStagedSelection ||
        _isTransferActive) {
      return;
    }

    setState(() => _selectedAddress = device.address);
    final sent =
        await (widget.sendToDeviceOverride?.call(device) ??
            widget.client.sendToDevice(device, isBackground: true));
    if (!mounted) return;

    setState(() => _clearAfterTransfer = true);
    if (!sent) {
      final error = widget.client.transferState.errorText;
      _showMessage(
        error.isNotEmpty
            ? error
            : appText(widget.language, 'desktopDropSendFailed'),
      );
      await _clearStagedFiles();
      _scheduleAutoCollapse(const Duration(milliseconds: 2200));
    }
  }

  Future<void> _cancelStagedDrop() async {
    if (_isTransferActive || !_hasStagedDrop || _clearInFlight) return;
    final cleared = await _clearStagedFiles(
      armTransferCleanup: manualCancelArmsTransferCleanup,
    );
    if (!mounted || !cleared) return;
    setState(() => _selectedAddress = null);
    await _collapse();
  }

  Future<bool> _clearStagedFiles({
    bool reportFailure = true,
    bool armTransferCleanup = true,
  }) async {
    if (_clearInFlight) return false;
    _clearInFlight = true;
    var cleared = false;
    try {
      cleared = await _stagingController.clear(language: widget.language);
      if (mounted) {
        setState(() {
          _clearAfterTransfer = armTransferCleanup
              ? !cleared
              : manualCancelCleanupState(
                  wasArmed: _clearAfterTransfer,
                  cleared: cleared,
                );
          if (cleared) {
            _terminalFailureShown = false;
          }
        });
        if (!cleared && reportFailure) {
          _showMessage(
            _stagingController.stagingError ??
                appText(widget.language, 'desktopDropStagingFailed'),
          );
        }
      }
    } finally {
      _clearInFlight = false;
    }
    return cleared;
  }

  void _maybeClearAfterTransfer(TransferStateModel transfer) {
    if (!_clearAfterTransfer || _clearInFlight || _terminalCleanupAttempted) {
      return;
    }
    final terminal = const [
      'completed',
      'failed',
      'cancelled',
    ].contains(transfer.phase);
    if (!terminal) return;
    _terminalCleanupAttempted = true;

    if (transfer.phase == 'failed' && !_terminalFailureShown) {
      _terminalFailureShown = true;
      final error = transfer.errorText;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (!mounted) return;
        _showMessage(
          error.isNotEmpty
              ? error
              : appText(widget.language, 'desktopDropSendFailed'),
        );
      });
    }
    if (transfer.phase != 'failed' && !_terminalStatusShown) {
      _terminalStatusShown = true;
      final message = transfer.phase == 'completed'
          ? appText(widget.language, 'desktopDropTransferCompleted')
          : appText(widget.language, 'desktopDropTransferCancelled');
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _showMessage(message);
      });
    }

    unawaited(
      _clearStagedFiles().then((cleared) {
        if (cleared && mounted) unawaited(_collapse());
      }),
    );
  }

  void _showMessage(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(
        SnackBar(
          content: Text(message),
          behavior: SnackBarBehavior.floating,
          duration: const Duration(seconds: 3),
        ),
      );
  }

  @override
  void dispose() {
    _collapseTimer?.cancel();
    _autoCollapseTimer?.cancel();
    _visual.dispose();
    _exit.dispose();
    _stagingController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final isDark = theme.brightness == Brightness.dark;

    return ListenableBuilder(
      listenable: widget.client,
      builder: (context, _) {
        final devices = widget.client.devices;
        final selected = _selectedDevice(devices);
        final transfer = widget.client.transferState;
        _maybeClearAfterTransfer(transfer);

        return Scaffold(
          backgroundColor: Colors.transparent,
          body: NativeDropZone(
            enabled: !_isTransferActive,
            onDragStateChanged: _handleDragStateChanged,
            onDropped: _handleDroppedPaths,
            child: _buildExitTransition(
              AnimatedBuilder(
                animation: _visual,
                builder: (context, _) {
                  final previewT = (_visual.value * 2).clamp(0.0, 1.0);
                  final expansionT = ((_visual.value - 0.5) * 2).clamp(
                    0.0,
                    1.0,
                  );
                  return Stack(
                    fit: StackFit.expand,
                    children: [
                      _fadeLayer(
                        visible: previewT < 0.86 && expansionT < 0.01,
                        opacity: (1 - previewT * 1.3).clamp(0.0, 1.0),
                        interactive: false,
                        child: _buildSquareTarget(isDark, previewT),
                      ),
                      _fadeLayer(
                        visible:
                            _isDragging && previewT > 0.04 && expansionT < 0.99,
                        opacity:
                            ((previewT - 0.04) / 0.7).clamp(0.0, 1.0) *
                            (1 - expansionT),
                        interactive: false,
                        child: _buildCompactPanel(isDark, transfer),
                      ),
                      _fadeLayer(
                        visible: expansionT > 0.001,
                        opacity: ((expansionT - 0.05) / 0.7).clamp(0.0, 1.0),
                        interactive: expansionT > 0.9,
                        child: _buildExpandedPanel(
                          selected,
                          devices,
                          isDark,
                          transfer,
                        ),
                      ),
                    ],
                  );
                },
              ),
            ),
          ),
        );
      },
    );
  }

  Widget _buildExitTransition(Widget child) {
    final curve = CurvedAnimation(parent: _exit, curve: Curves.easeOutCubic);
    return FadeTransition(
      opacity: curve,
      child: SlideTransition(
        position: Tween(
          begin: const Offset(0, 0.04),
          end: Offset.zero,
        ).animate(curve),
        child: child,
      ),
    );
  }

  Widget _fadeLayer({
    required bool visible,
    required double opacity,
    required bool interactive,
    required Widget child,
  }) {
    if (!visible) return const SizedBox.shrink();
    return IgnorePointer(
      ignoring: !interactive,
      child: Opacity(opacity: opacity, child: child),
    );
  }

  Widget _buildSquareTarget(bool isDark, [double previewT = 0.0]) {
    final accent = isDark ? AppColors.darkAccent : AppColors.lightAccent;
    const baseSquareSize = 134.0;
    final scale = 1.0 + (0.08 * previewT);
    return Center(
      child: Transform.scale(
        scale: scale,
        child: Container(
          key: const ValueKey('desktop-drop-square-target'),
          width: baseSquareSize,
          height: baseSquareSize,
          decoration: BoxDecoration(
            color: isDark ? AppColors.darkSurface : AppColors.lightSurface,
            borderRadius: BorderRadius.circular(14),
            border: Border.all(color: accent, width: 2),
            boxShadow: const [
              BoxShadow(
                color: Colors.black38,
                blurRadius: 14,
                offset: Offset(0, 4),
              ),
            ],
          ),
          child: Icon(Icons.file_upload_rounded, color: accent, size: 36),
        ),
      ),
    );
  }

  Widget _buildCompactPanel(bool isDark, TransferStateModel transfer) {
    return Align(
      alignment: Alignment.bottomRight,
      child: SizedBox(
        width: panelDropTargetSize.width,
        height: panelDropTargetSize.height,
        child: SafeArea(
          minimum: const EdgeInsets.all(8),
          child: ClipRect(
            child: Material(
              color: isDark ? AppColors.darkSurface : AppColors.lightSurface,
              borderRadius: BorderRadius.circular(12),
              elevation: 10,
              child: Padding(
                padding: const EdgeInsets.fromLTRB(12, 8, 12, 10),
                child: Column(
                  children: [
                    _buildHeader(null, isDark),
                    const SizedBox(height: 8),
                    Expanded(child: _buildDropArea(null, isDark, transfer)),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildExpandedPanel(
    DeviceModel? selected,
    List<DeviceModel> devices,
    bool isDark,
    TransferStateModel transfer,
  ) {
    final width = panelWidthForDeviceCount(devices.length);
    return Align(
      alignment: Alignment.bottomRight,
      child: SizedBox(
        width: width,
        child: SafeArea(
          minimum: const EdgeInsets.all(8),
          child: ClipRect(
            child: Material(
              color: isDark ? AppColors.darkSurface : AppColors.lightSurface,
              borderRadius: BorderRadius.circular(18),
              elevation: 10,
              child: Padding(
                padding: const EdgeInsets.fromLTRB(14, 10, 14, 12),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    _buildHeader(selected, isDark),
                    const SizedBox(height: 10),
                    if (_isTransferActive) ...[
                      _buildTransferDetails(isDark, transfer),
                      const SizedBox(height: 10),
                    ] else ...[
                      SizedBox(
                        height: 52,
                        child: _buildDropArea(
                          selected,
                          isDark,
                          transfer,
                          compact: true,
                        ),
                      ),
                      const SizedBox(height: 10),
                    ],
                    if (_hasStagedDrop && !_isTransferActive) ...[
                      _buildStagedSummary(isDark),
                      const SizedBox(height: 10),
                    ],
                    _buildDeviceStrip(devices, selected, isDark),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildTransferDetails(bool isDark, TransferStateModel transfer) {
    final accent = isDark ? AppColors.darkAccent : AppColors.lightAccent;
    final muted = isDark ? AppColors.darkTextMuted : AppColors.lightTextMuted;
    final textColor = isDark ? AppColors.darkText : AppColors.lightText;
    final determinate = transfer.totalBytes > 0;
    final eta = estimateRemaining(
      sentBytes: transfer.sentBytes,
      totalBytes: transfer.totalBytes,
      speedBytesPerSec: transfer.speedBytesPerSec,
    );
    final percent = (transfer.progress * 100).round();
    final speed = transfer.speedBytesPerSec > 0
        ? '${_formatBytes(transfer.speedBytesPerSec.round())}/s'
        : '—';
    return Container(
      key: const ValueKey('desktop-drop-transfer-details'),
      padding: const EdgeInsets.fromLTRB(10, 8, 6, 8),
      decoration: BoxDecoration(
        color: isDark ? AppColors.darkCard : AppColors.lightCard,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(
          color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Icon(Icons.inventory_2_outlined, size: 16, color: muted),
              const SizedBox(width: 6),
              Expanded(
                child: Text(
                  '${appText(widget.language, 'desktopDropStagedSummary')} · ${_stagingController.totalCount} ${appText(widget.language, 'desktopDropFiles')} · ${_formatBytes(_stagingController.totalBytes)}',
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(fontSize: 10, color: muted),
                ),
              ),
            ],
          ),
          const SizedBox(height: 5),
          Row(
            children: [
              Expanded(
                child: Text(
                  transfer.fileName.isNotEmpty
                      ? transfer.fileName
                      : appText(widget.language, 'desktopDropTransfer'),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontSize: 11,
                    fontWeight: FontWeight.w700,
                    color: textColor,
                  ),
                ),
              ),
              Text('$percent%', style: TextStyle(fontSize: 11, color: muted)),
              IconButton(
                key: const ValueKey('desktop-drop-cancel-transfer'),
                tooltip: appText(widget.language, 'cancelTransfer'),
                onPressed: () async {
                  final cancelled = await widget.client.cancelTransfer();
                  if (!cancelled && mounted) {
                    _showMessage(
                      appText(widget.language, 'desktopDropCancelFailed'),
                    );
                  }
                },
                icon: const Icon(Icons.close_rounded, size: 18),
                visualDensity: VisualDensity.compact,
              ),
            ],
          ),
          const SizedBox(height: 6),
          LinearProgressIndicator(
            value: determinate ? transfer.progress : null,
            minHeight: 6,
            color: accent,
            backgroundColor: accent.withAlpha(35),
          ),
          const SizedBox(height: 6),
          Row(
            children: [
              Expanded(
                child: Text(
                  determinate
                      ? '${_formatBytes(transfer.sentBytes)} / ${_formatBytes(transfer.totalBytes)}'
                      : _formatBytes(transfer.sentBytes),
                  style: TextStyle(fontSize: 10, color: muted),
                ),
              ),
              Text(
                '${appText(widget.language, 'transferSpeed')}: $speed',
                style: TextStyle(fontSize: 10, color: muted),
              ),
              const SizedBox(width: 8),
              Text(
                '${appText(widget.language, 'transferEta')}: ${formatTransferEta(eta)}',
                style: TextStyle(fontSize: 10, color: muted),
              ),
            ],
          ),
          if (transfer.statusText.isNotEmpty) ...[
            const SizedBox(height: 4),
            Text(
              transfer.statusText,
              style: TextStyle(fontSize: 10, color: muted),
            ),
          ],
        ],
      ),
    );
  }

  Widget _buildStagedSummary(bool isDark) {
    final files = _stagingController.selectedFiles;
    final firstName = files.isEmpty ? '' : fileNameForPath(files.first);
    final count = _stagingController.totalCount;
    final bytes = _stagingController.totalBytes;
    final muted = isDark ? AppColors.darkTextMuted : AppColors.lightTextMuted;
    final textColor = isDark ? AppColors.darkText : AppColors.lightText;
    return Container(
      padding: const EdgeInsets.fromLTRB(10, 8, 6, 8),
      decoration: BoxDecoration(
        color: isDark ? AppColors.darkCard : AppColors.lightCard,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(
          color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
        ),
      ),
      child: Row(
        children: [
          Icon(Icons.inventory_2_outlined, size: 18, color: muted),
          const SizedBox(width: 8),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  appText(widget.language, 'desktopDropStagedSummary'),
                  style: TextStyle(
                    fontSize: 11,
                    fontWeight: FontWeight.w700,
                    color: textColor,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  '$firstName · $count ${appText(widget.language, 'desktopDropFiles')} · ${_formatBytes(bytes)}',
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(fontSize: 10, color: muted),
                ),
              ],
            ),
          ),
          const SizedBox(width: 4),
          IconButton(
            key: const ValueKey('desktop-drop-cancel-staged'),
            tooltip: appText(widget.language, 'desktopDropCancelStaged'),
            onPressed: _isTransferActive ? null : _cancelStagedDrop,
            icon: const Icon(Icons.close_rounded, size: 18),
            visualDensity: VisualDensity.compact,
          ),
        ],
      ),
    );
  }

  Widget _buildHeader(DeviceModel? selected, bool isDark) {
    final title = appText(widget.language, 'desktopDropTitle');
    return DragToMoveArea(
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
              Icons.file_upload_rounded,
              size: 17,
              color: isDark ? AppColors.darkAccent : AppColors.lightAccent,
            ),
          ),
          const SizedBox(width: 9),
          Expanded(
            child: FittedBox(
              fit: BoxFit.scaleDown,
              alignment: Alignment.centerLeft,
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
          ),
        ],
      ),
    );
  }

  Widget _buildDropArea(
    DeviceModel? selected,
    bool isDark,
    TransferStateModel transfer, {
    bool compact = false,
  }) {
    final accent = isDark ? AppColors.darkAccent : AppColors.lightAccent;
    final activeBackground = isDark
        ? AppColors.darkAccentSoft
        : AppColors.lightAccentSoft;
    final text = _isTransferActive
        ? appText(widget.language, 'desktopDropUnavailable')
        : (_isDragging
              ? appText(widget.language, 'desktopDropReleaseToSend')
              : (_hasStagedDrop && selected == null
                    ? appText(widget.language, 'desktopDropSelectDevice')
                    : appText(widget.language, 'dropFilesHere')));

    return AnimatedContainer(
      duration: const Duration(milliseconds: 150),
      decoration: BoxDecoration(
        color: _isDragging
            ? activeBackground.withAlpha(30)
            : Colors.transparent,
        borderRadius: BorderRadius.circular(12),
      ),
      child: CustomPaint(
        painter: _DashedBorderPainter(
          color: _isDragging
              ? accent
              : (isDark ? AppColors.darkBorder : AppColors.lightBorder),
          strokeWidth: _isDragging ? 2 : 1,
        ),
        child: compact
            ? _buildCompactDropContent(text, isDark, accent)
            : Center(
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(
                      _isTransferActive
                          ? Icons.sync_rounded
                          : Icons.cloud_upload_rounded,
                      size: 34,
                      color: _isDragging
                          ? accent
                          : (isDark
                                ? AppColors.darkTextMuted
                                : AppColors.lightTextMuted),
                    ),
                    const SizedBox(height: 8),
                    Padding(
                      padding: const EdgeInsets.symmetric(horizontal: 16),
                      child: LayoutBuilder(
                        builder: (context, constraints) => ConstrainedBox(
                          constraints: BoxConstraints(
                            maxWidth: constraints.maxWidth,
                          ),
                          child: Text(
                            text,
                            textAlign: TextAlign.center,
                            maxLines: desktopDropInstructionMaxLines,
                            softWrap: true,
                            overflow: TextOverflow.ellipsis,
                            style: TextStyle(
                              fontSize: 11,
                              fontWeight: FontWeight.w600,
                              color: isDark
                                  ? AppColors.darkText
                                  : AppColors.lightText,
                            ),
                          ),
                        ),
                      ),
                    ),
                    if (_isTransferActive &&
                        transfer.statusText.isNotEmpty) ...[
                      const SizedBox(height: 6),
                      Text(
                        transfer.statusText,
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
                  ],
                ),
              ),
      ),
    );
  }

  // Single-row drop hint for the staged panel, so the picker is not padded
  // out by the tall drag-state drop area.
  Widget _buildCompactDropContent(String text, bool isDark, Color accent) {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 14),
      child: Row(
        children: [
          Icon(
            Icons.cloud_upload_rounded,
            size: 22,
            color: _isDragging
                ? accent
                : (isDark ? AppColors.darkTextMuted : AppColors.lightTextMuted),
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              text,
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: 11,
                fontWeight: FontWeight.w600,
                color: isDark ? AppColors.darkText : AppColors.lightText,
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildDeviceStrip(
    List<DeviceModel> devices,
    DeviceModel? selected,
    bool isDark,
  ) {
    if (devices.isEmpty) {
      return SizedBox(
        height: 52,
        child: Align(
          alignment: Alignment.centerLeft,
          child: Text(
            appText(widget.language, 'desktopDropNoDevices'),
            style: TextStyle(
              fontSize: 11,
              color: isDark
                  ? AppColors.darkTextMuted
                  : AppColors.lightTextMuted,
            ),
          ),
        ),
      );
    }

    // At most two rows are visible; more phones scroll vertically.
    return ConstrainedBox(
      constraints: const BoxConstraints(
        maxHeight: _deviceChipHeight * 2 + _deviceChipGap,
      ),
      child: LayoutBuilder(
        builder: (context, constraints) {
          // Chips share the full row: one phone spans it, two split it.
          final columns = devices.length.clamp(
            1,
            panelDeviceColumns(devices.length),
          );
          final chipWidth =
              (constraints.maxWidth - (columns - 1) * _deviceChipGap) / columns;
          return SingleChildScrollView(
            child: Wrap(
              spacing: _deviceChipGap,
              runSpacing: _deviceChipGap,
              children: [
                for (final device in devices)
                  _buildDeviceChip(device, selected, isDark, chipWidth),
              ],
            ),
          );
        },
      ),
    );
  }

  Widget _buildDeviceChip(
    DeviceModel device,
    DeviceModel? selected,
    bool isDark,
    double width,
  ) {
    final isSelected = selected?.address == device.address;
    final accent = isDark ? AppColors.darkAccent : AppColors.lightAccent;
    return InkWell(
      onTap: () {
        if (_hasStagedDrop) {
          unawaited(_sendStagedFilesTo(device));
        } else {
          setState(() => _selectedAddress = device.address);
        }
      },
      borderRadius: BorderRadius.circular(12),
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 150),
        width: width,
        height: _deviceChipHeight,
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
        decoration: BoxDecoration(
          color: isSelected
              ? (isDark ? AppColors.darkAccentSoft : AppColors.lightAccentSoft)
              : (isDark ? AppColors.darkCard : AppColors.lightCard),
          borderRadius: BorderRadius.circular(12),
          border: Border.all(
            color: isSelected
                ? accent
                : (isDark ? AppColors.darkBorder : AppColors.lightBorder),
            width: isSelected ? 1.5 : 1,
          ),
        ),
        child: Row(
          children: [
            Icon(
              Icons.smartphone_rounded,
              size: 22,
              color: isSelected
                  ? accent
                  : (isDark
                        ? AppColors.darkTextMuted
                        : AppColors.lightTextMuted),
            ),
            const SizedBox(width: 7),
            Expanded(
              child: Column(
                mainAxisAlignment: MainAxisAlignment.center,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    device.name,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontSize: 12,
                      fontWeight: FontWeight.w700,
                      color: isDark ? AppColors.darkText : AppColors.lightText,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    '${device.kind} · ${device.rssi} dBm',
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontSize: 10,
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
    );
  }
}

class _DashedBorderPainter extends CustomPainter {
  final Color color;
  final double strokeWidth;

  const _DashedBorderPainter({required this.color, required this.strokeWidth});

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = strokeWidth;
    final rect = RRect.fromRectAndRadius(
      Offset.zero & size,
      const Radius.circular(12),
    );
    final path = Path()..addRRect(rect);
    for (final metric in path.computeMetrics()) {
      var distance = 0.0;
      while (distance < metric.length) {
        final end = (distance + 6).clamp(0, metric.length).toDouble();
        canvas.drawPath(metric.extractPath(distance, end), paint);
        distance += 11;
      }
    }
  }

  @override
  bool shouldRepaint(covariant _DashedBorderPainter oldDelegate) {
    return oldDelegate.color != color || oldDelegate.strokeWidth != strokeWidth;
  }
}
