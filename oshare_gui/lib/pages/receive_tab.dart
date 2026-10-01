import 'package:flutter/material.dart';
import '../config/theme.dart';
import '../config/language.dart';
import '../models/models.dart';
import '../services/bridge_client.dart';
import '../services/outgoing_staging_controller.dart';
import '../widgets/custom_segmented_button.dart';
import '../widgets/native_drop_zone.dart';

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

  Widget _statusPill(bool isEnabled, bool isDark) {
    final accent = isDark ? AppColors.darkAccent : AppColors.lightAccent;
    final muted = isDark ? AppColors.darkTextMuted : AppColors.lightTextMuted;
    final fg = isEnabled ? accent : muted;
    return InkWell(
      onTap: () => widget.client.setReceiveEnabled(!isEnabled),
      borderRadius: BorderRadius.circular(20),
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 250),
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 7),
        decoration: BoxDecoration(
          color: isEnabled
              ? (isDark ? AppColors.darkAccentSoft : AppColors.lightAccentSoft)
              : Colors.transparent,
          borderRadius: BorderRadius.circular(20),
          border: Border.all(
            color: isEnabled
                ? accent.withValues(alpha: 0.5)
                : (isDark ? AppColors.darkBorder : AppColors.lightBorder),
          ),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              width: 8,
              height: 8,
              decoration: BoxDecoration(shape: BoxShape.circle, color: fg),
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
                letterSpacing: 0.5,
                color: fg,
              ),
            ),
            const SizedBox(width: 6),
            Icon(
              isEnabled
                  ? Icons.pause_circle_outline_rounded
                  : Icons.play_circle_outline_rounded,
              size: 16,
              color: fg,
            ),
          ],
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final status = widget.client.status;
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final isEnabled = status.receiveEnabled;
    final ipParts = _formatIpAsHashes(status.lanIp);
    final accent = isDark ? AppColors.darkAccent : AppColors.lightAccent;
    final textColor = isDark ? AppColors.darkText : AppColors.lightText;
    final muted = isDark ? AppColors.darkTextMuted : AppColors.lightTextMuted;
    final cardColor = isDark ? AppColors.darkCard : AppColors.lightCard;
    final borderColor = isDark ? AppColors.darkBorder : AppColors.lightBorder;

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
              padding: const EdgeInsets.symmetric(vertical: 32, horizontal: 24),
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 480),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    // Device card: name + status, IP chips, quick save
                    Container(
                      padding: const EdgeInsets.all(22),
                      decoration: BoxDecoration(
                        color: cardColor,
                        borderRadius: BorderRadius.circular(18),
                        border: Border.all(color: borderColor),
                      ),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Row(
                            children: [
                              Container(
                                width: 44,
                                height: 44,
                                decoration: BoxDecoration(
                                  color: accent.withValues(alpha: 0.14),
                                  borderRadius: BorderRadius.circular(12),
                                ),
                                child: Icon(
                                  Icons.devices_rounded,
                                  color: accent,
                                  size: 22,
                                ),
                              ),
                              const SizedBox(width: 14),
                              Expanded(
                                child: Text(
                                  status.deviceName.isEmpty
                                      ? 'OSharePC'
                                      : status.deviceName,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: TextStyle(
                                    fontSize: 24,
                                    fontWeight: FontWeight.w700,
                                    color: textColor,
                                    letterSpacing: -0.3,
                                  ),
                                ),
                              ),
                              const SizedBox(width: 10),
                              _statusPill(isEnabled, isDark),
                            ],
                          ),
                          const SizedBox(height: 14),
                          Wrap(
                            spacing: 8,
                            runSpacing: 6,
                            children: ipParts.map((part) {
                              return Container(
                                padding: const EdgeInsets.symmetric(
                                  horizontal: 10,
                                  vertical: 4,
                                ),
                                decoration: BoxDecoration(
                                  color: accent.withValues(alpha: 0.08),
                                  borderRadius: BorderRadius.circular(8),
                                ),
                                child: Text(
                                  part,
                                  style: TextStyle(
                                    fontSize: 12,
                                    fontWeight: FontWeight.w600,
                                    color: muted,
                                  ),
                                ),
                              );
                            }).toList(),
                          ),
                          Padding(
                            padding: const EdgeInsets.symmetric(vertical: 18),
                            child: Divider(height: 1, color: borderColor),
                          ),
                          Row(
                            children: [
                              Expanded(
                                child: Text(
                                  appText(widget.language, 'quickSave'),
                                  style: TextStyle(
                                    fontSize: 14,
                                    fontWeight: FontWeight.w600,
                                    color: textColor,
                                  ),
                                ),
                              ),
                              CustomSegmentedButton<QuickSaveMode>(
                                values: const [
                                  QuickSaveMode.off,
                                  QuickSaveMode.on,
                                ],
                                labels: [
                                  appText(widget.language, 'off'),
                                  appText(widget.language, 'on'),
                                ],
                                selected:
                                    widget.client.quickSaveMode ==
                                        QuickSaveMode.favorites
                                    ? QuickSaveMode.off
                                    : widget.client.quickSaveMode,
                                onSelected: (mode) =>
                                    widget.client.setQuickSaveMode(mode),
                              ),
                            ],
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),

          // Drag and Drop Overlay
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
                      border: Border.all(color: accent, width: 2),
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
                        Icon(Icons.file_upload_rounded, size: 48, color: accent),
                        const SizedBox(height: 14),
                        Text(
                          appText(widget.language, 'dropToSendFiles'),
                          textAlign: TextAlign.center,
                          style: TextStyle(
                            fontSize: 18,
                            fontWeight: FontWeight.w700,
                            color: textColor,
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
