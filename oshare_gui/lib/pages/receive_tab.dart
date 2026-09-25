import 'package:flutter/material.dart';
import '../config/theme.dart';
import '../config/language.dart';
import '../models/models.dart';
import '../services/bridge_client.dart';
import '../services/outgoing_staging_controller.dart';
import '../widgets/custom_segmented_button.dart';
import '../widgets/native_drop_zone.dart';
import '../widgets/radar_logo.dart';

class ReceiveTab extends StatefulWidget {
  final BridgeClient client;
  final AppLanguage language;
  final OutgoingStagingController stagingController;
  final bool isCurrentTab;
  final VoidCallback onNavigateToSend;

  const ReceiveTab({
    super.key,
    required this.client,
    required this.language,
    required this.stagingController,
    this.isCurrentTab = true,
    required this.onNavigateToSend,
  });

  @override
  State<ReceiveTab> createState() => _ReceiveTabState();
}

class _ReceiveTabState extends State<ReceiveTab> {
  bool _isDragging = false;

  bool get _isDropAllowed {
    if (widget.client.pendingIncomingOffer != null) return false;
    final transfer = widget.client.transferState;
    if (transfer.active && !transfer.isSending) {
      if (!const [
        'completed',
        'failed',
        'cancelled',
      ].contains(transfer.phase)) {
        return false;
      }
    }
    return true;
  }

  List<String> _formatIpAsHashes(String ip) {
    if (ip.isEmpty) return ['#--'];
    return ip.split('.').map((part) => '#$part').toList();
  }

  Future<void> _handleDroppedPaths(List<String> paths) async {
    setState(() => _isDragging = false);
    if (!_isDropAllowed) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(appText(widget.language, 'dragDropUnavailable')),
            behavior: SnackBarBehavior.floating,
          ),
        );
      }
      return;
    }

    final success = await widget.stagingController.addPaths(
      paths,
      language: widget.language,
    );
    if (success && mounted) {
      widget.client.dismissTransferModal();
      widget.onNavigateToSend();
    } else if (widget.stagingController.stagingError != null && mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(widget.stagingController.stagingError!),
          behavior: SnackBarBehavior.floating,
        ),
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    final status = widget.client.status;
    final theme = Theme.of(context);
    final isDark = theme.brightness == Brightness.dark;
    final isEnabled = status.receiveEnabled;
    final ipParts = _formatIpAsHashes(status.lanIp);

    return NativeDropZone(
      enabled: widget.isCurrentTab && _isDropAllowed,
      onDragStateChanged: (isDragging) {
        setState(() => _isDragging = isDragging);
      },
      onDropped: _handleDroppedPaths,
      child: Stack(
        children: [
          Center(
            child: SingleChildScrollView(
              child: Padding(
                padding: const EdgeInsets.symmetric(
                  vertical: 40,
                  horizontal: 24,
                ),
                child: Column(
                  mainAxisAlignment: MainAxisAlignment.center,
                  crossAxisAlignment: CrossAxisAlignment.center,
                  children: [
                    // 1. Center Radar Logo
                    RadarLogo(size: 160, active: isEnabled),
                    const SizedBox(height: 28),

                    // 2. Receive Feature Toggle Switch Button
                    InkWell(
                      onTap: () => widget.client.setReceiveEnabled(!isEnabled),
                      borderRadius: BorderRadius.circular(20),
                      child: AnimatedContainer(
                        duration: const Duration(milliseconds: 250),
                        padding: const EdgeInsets.symmetric(
                          horizontal: 16,
                          vertical: 7,
                        ),
                        decoration: BoxDecoration(
                          color: isEnabled
                              ? (isDark
                                    ? AppColors.darkAccentSoft
                                    : AppColors.lightAccentSoft)
                              : (isDark
                                    ? const Color(0xFF26332E)
                                    : const Color(0xFFE2EBE6)),
                          borderRadius: BorderRadius.circular(20),
                          border: Border.all(
                            color: isEnabled
                                ? (isDark
                                      ? AppColors.darkAccent.withValues(
                                          alpha: 0.5,
                                        )
                                      : AppColors.lightAccent.withValues(
                                          alpha: 0.5,
                                        ))
                                : (isDark
                                      ? AppColors.darkBorder
                                      : AppColors.lightBorder),
                            width: 1,
                          ),
                        ),
                        child: Row(
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            Container(
                              width: 8,
                              height: 8,
                              decoration: BoxDecoration(
                                shape: BoxShape.circle,
                                color: isEnabled
                                    ? (isDark
                                          ? AppColors.darkAccent
                                          : AppColors.lightAccent)
                                    : (isDark
                                          ? AppColors.darkTextSubtle
                                          : AppColors.lightTextMuted),
                              ),
                            ),
                            const SizedBox(width: 8),
                            Text(
                              appText(
                                widget.language,
                                isEnabled ? 'receiveActive' : 'receivePaused',
                              ),
                              style: TextStyle(
                                fontSize: 12,
                                fontWeight: FontWeight.w700,
                                letterSpacing: 0.8,
                                color: isEnabled
                                    ? (isDark
                                          ? AppColors.darkAccent
                                          : AppColors.lightAccent)
                                    : (isDark
                                          ? AppColors.darkTextMuted
                                          : AppColors.lightTextMuted),
                              ),
                            ),
                            const SizedBox(width: 6),
                            Icon(
                              isEnabled
                                  ? Icons.pause_circle_outline_rounded
                                  : Icons.play_circle_outline_rounded,
                              size: 16,
                              color: isEnabled
                                  ? (isDark
                                        ? AppColors.darkAccent
                                        : AppColors.lightAccent)
                                  : (isDark
                                        ? AppColors.darkTextMuted
                                        : AppColors.lightTextMuted),
                            ),
                          ],
                        ),
                      ),
                    ),
                    const SizedBox(height: 24),

                    // 3. Device Name (Big & friendly font)
                    Text(
                      status.deviceName.isEmpty
                          ? 'OsharePC'
                          : status.deviceName,
                      textAlign: TextAlign.center,
                      style: TextStyle(
                        fontSize: 34,
                        fontWeight: FontWeight.w700,
                        color: isDark
                            ? AppColors.darkText
                            : AppColors.lightText,
                        letterSpacing: -0.5,
                      ),
                    ),
                    const SizedBox(height: 10),

                    // 4. IP / Hash segments
                    Wrap(
                      spacing: 8,
                      runSpacing: 4,
                      alignment: WrapAlignment.center,
                      children: ipParts.map((part) {
                        return Container(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 10,
                            vertical: 4,
                          ),
                          decoration: BoxDecoration(
                            color: isDark
                                ? AppColors.darkCard
                                : AppColors.lightCard,
                            borderRadius: BorderRadius.circular(8),
                            border: Border.all(
                              color: isDark
                                  ? AppColors.darkBorder
                                  : AppColors.lightBorder,
                            ),
                          ),
                          child: Text(
                            part,
                            style: TextStyle(
                              fontSize: 12,
                              fontWeight: FontWeight.w600,
                              color: isDark
                                  ? AppColors.darkTextMuted
                                  : AppColors.lightTextMuted,
                            ),
                          ),
                        );
                      }).toList(),
                    ),
                    const SizedBox(height: 48),

                    // 5. Quick Save Controls
                    Text(
                      appText(widget.language, 'quickSave'),
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w500,
                        color: isDark
                            ? AppColors.darkTextMuted
                            : AppColors.lightTextMuted,
                      ),
                    ),
                    const SizedBox(height: 10),
                    CustomSegmentedButton<QuickSaveMode>(
                      values: const [QuickSaveMode.off, QuickSaveMode.on],
                      labels: [
                        appText(widget.language, 'off'),
                        appText(widget.language, 'on'),
                      ],
                      selected:
                          widget.client.quickSaveMode == QuickSaveMode.favorites
                          ? QuickSaveMode.off
                          : widget.client.quickSaveMode,
                      onSelected: (mode) =>
                          widget.client.setQuickSaveMode(mode),
                    ),
                  ],
                ),
              ),
            ),
          ),

          // 6. Drag and Drop Overlay
          if (_isDragging && _isDropAllowed)
            Positioned.fill(
              child: Container(
                color: isDark
                    ? Colors.black.withValues(alpha: 0.75)
                    : Colors.white.withValues(alpha: 0.85),
                child: Center(
                  child: Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 32,
                      vertical: 24,
                    ),
                    decoration: BoxDecoration(
                      color: isDark
                          ? AppColors.darkAccentSoft
                          : AppColors.lightAccentSoft,
                      borderRadius: BorderRadius.circular(20),
                      border: Border.all(
                        color: isDark
                            ? AppColors.darkAccent
                            : AppColors.lightAccent,
                        width: 2,
                      ),
                      boxShadow: const [
                        BoxShadow(
                          color: Colors.black26,
                          blurRadius: 16,
                          offset: Offset(0, 6),
                        ),
                      ],
                    ),
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Icon(
                          Icons.file_upload_rounded,
                          size: 48,
                          color: isDark
                              ? AppColors.darkAccent
                              : AppColors.lightAccent,
                        ),
                        const SizedBox(height: 14),
                        Text(
                          appText(widget.language, 'dropToSendFiles'),
                          textAlign: TextAlign.center,
                          style: TextStyle(
                            fontSize: 18,
                            fontWeight: FontWeight.w700,
                            color: isDark
                                ? AppColors.darkText
                                : AppColors.lightText,
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}
