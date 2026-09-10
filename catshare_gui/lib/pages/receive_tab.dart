import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import '../config/theme.dart';
import '../config/language.dart';
import '../models/models.dart';
import '../services/bridge_client.dart';
import '../services/outgoing_staging_controller.dart';
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
      if (!const ['completed', 'failed', 'cancelled'].contains(transfer.phase)) {
        return false;
      }
    }
    return true;
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

  Future<void> _copyIp(String ip) async {
    if (ip.isEmpty) return;
    await Clipboard.setData(ClipboardData(text: ip));
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(appText(widget.language, 'ipCopied')),
        behavior: SnackBarBehavior.floating,
        duration: const Duration(seconds: 1),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final status = widget.client.status;
    final theme = Theme.of(context);
    final isDark = theme.brightness == Brightness.dark;
    final isEnabled = status.receiveEnabled;
    final ip = status.lanIp;
    final displayIp = ip.isEmpty ? '--' : ip;
    final quickSaveEnabled = widget.client.quickSaveMode == QuickSaveMode.on;
    final saveDirectory = status.saveDirectory.isEmpty
        ? 'Downloads\\CatShare'
        : status.saveDirectory;

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
              padding: const EdgeInsets.symmetric(horizontal: 32, vertical: 32),
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 720),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.center,
                  children: [
                    RadarLogo(size: 144, active: isEnabled),
                    const SizedBox(height: 24),
                    _buildReceiveStatus(isEnabled: isEnabled, isDark: isDark),
                    const SizedBox(height: 14),
                    Text(
                      status.deviceName.isEmpty ? 'OsharePC' : status.deviceName,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      textAlign: TextAlign.center,
                      style: TextStyle(
                        fontSize: 28,
                        fontWeight: FontWeight.w600,
                        letterSpacing: -0.35,
                        color: isDark ? AppColors.darkText : AppColors.lightText,
                      ),
                    ),
                    const SizedBox(height: 12),
                    Text(
                      appText(widget.language, 'receiveHint'),
                      textAlign: TextAlign.center,
                      style: TextStyle(
                        fontSize: 13,
                        height: 1.45,
                        color: isDark
                            ? AppColors.darkTextMuted
                            : AppColors.lightTextMuted,
                      ),
                    ),
                    const SizedBox(height: 24),
                    _buildIpRow(displayIp: displayIp, rawIp: ip, isDark: isDark),
                    const SizedBox(height: 28),
                    Divider(
                      height: 1,
                      color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
                    ),
                    const SizedBox(height: 20),
                    _buildQuickSaveRow(
                      isDark: isDark,
                      enabled: quickSaveEnabled,
                      saveDirectory: saveDirectory,
                    ),
                  ],
                ),
              ),
            ),
          ),
          if (widget.client.pendingIncomingOffer != null)
            _buildIncomingModal(context, widget.client.pendingIncomingOffer!),
          if (widget.client.transferState.active &&
              !widget.client.transferState.isSending)
            _buildTransferModal(context, widget.client.transferState),
          if (_isDragging && _isDropAllowed)
            Positioned.fill(child: _buildDragOverlay(isDark)),
        ],
      ),
    );
  }

  Widget _buildReceiveStatus({
    required bool isEnabled,
    required bool isDark,
  }) {
    final foreground = isEnabled
        ? (isDark ? AppColors.darkAccent : AppColors.lightAccent)
        : (isDark ? AppColors.darkTextMuted : AppColors.lightTextMuted);

    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: () => widget.client.setReceiveEnabled(!isEnabled),
        borderRadius: BorderRadius.circular(8),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Container(
                width: 7,
                height: 7,
                decoration: BoxDecoration(
                  shape: BoxShape.circle,
                  color: isEnabled
                      ? foreground
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
                  fontSize: 13,
                  fontWeight: FontWeight.w600,
                  color: foreground,
                ),
              ),
              const SizedBox(width: 6),
              Icon(
                isEnabled ? Icons.pause_rounded : Icons.play_arrow_rounded,
                size: 15,
                color: foreground,
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildIpRow({
    required String displayIp,
    required String rawIp,
    required bool isDark,
  }) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          appText(widget.language, 'localIp'),
          style: TextStyle(
            fontSize: 12,
            color: isDark
                ? AppColors.darkTextMuted
                : AppColors.lightTextMuted,
          ),
        ),
        const SizedBox(width: 12),
        SelectableText(
          displayIp,
          style: TextStyle(
            fontFamily: 'Consolas',
            fontSize: 15,
            fontWeight: FontWeight.w500,
            letterSpacing: 0.15,
            color: isDark ? AppColors.darkText : AppColors.lightText,
          ),
        ),
        const SizedBox(width: 4),
        IconButton(
          tooltip: appText(widget.language, 'copy'),
          onPressed: rawIp.isEmpty ? null : () => _copyIp(rawIp),
          visualDensity: VisualDensity.compact,
          iconSize: 17,
          color: isDark ? AppColors.darkTextMuted : AppColors.lightTextMuted,
          icon: const Icon(Icons.copy_rounded),
        ),
      ],
    );
  }

  Widget _buildQuickSaveRow({
    required bool isDark,
    required bool enabled,
    required String saveDirectory,
  }) {
    return SizedBox(
      width: double.infinity,
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.center,
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  appText(widget.language, 'quickSave'),
                  style: TextStyle(
                    fontSize: 14,
                    fontWeight: FontWeight.w600,
                    color: isDark ? AppColors.darkText : AppColors.lightText,
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  '${appText(widget.language, 'quickSaveDestinationHint')} $saveDirectory',
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
            ),
          ),
          const SizedBox(width: 20),
          Switch(
            value: enabled,
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

  Widget _buildDragOverlay(bool isDark) {
    return Container(
      color: isDark
          ? Colors.black.withValues(alpha: 0.75)
          : Colors.white.withValues(alpha: 0.85),
      child: Center(
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 30, vertical: 22),
          decoration: BoxDecoration(
            color: isDark ? const Color(0xFF1B382F) : const Color(0xFFE2F3EC),
            borderRadius: BorderRadius.circular(16),
            border: Border.all(
              color: isDark ? AppColors.darkAccent : AppColors.lightAccent,
              width: 1.5,
            ),
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(
                Icons.file_upload_rounded,
                size: 42,
                color: isDark ? AppColors.darkAccent : AppColors.lightAccent,
              ),
              const SizedBox(height: 12),
              Text(
                appText(widget.language, 'dropToSendFiles'),
                textAlign: TextAlign.center,
                style: TextStyle(
                  fontSize: 16,
                  fontWeight: FontWeight.w600,
                  color: isDark ? AppColors.darkText : AppColors.lightText,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  String _formatSize(int bytes) {
    if (bytes < 1024) return '$bytes B';
    if (bytes < 1024 * 1024) {
      return '${(bytes / 1024).toStringAsFixed(1)} KB';
    }
    if (bytes < 1024 * 1024 * 1024) {
      return '${(bytes / (1024 * 1024)).toStringAsFixed(1)} MB';
    }
    return '${(bytes / (1024 * 1024 * 1024)).toStringAsFixed(2)} GB';
  }

  Widget _buildTransferModal(
    BuildContext context,
    TransferStateModel transfer,
  ) {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final canCancel = !const [
      'completed',
      'failed',
      'cancelled',
    ].contains(transfer.phase);
    final determinate = transfer.phase == 'receiving' && transfer.totalBytes > 0;
    final title = transfer.statusText.isEmpty
        ? appText(widget.language, 'receiving')
        : transfer.statusText;

    return Container(
      color: Colors.black54,
      child: Center(
        child: Container(
          width: 460,
          margin: const EdgeInsets.all(24),
          padding: const EdgeInsets.all(26),
          decoration: BoxDecoration(
            color: isDark ? AppColors.darkSurface : AppColors.lightSurface,
            borderRadius: BorderRadius.circular(18),
            border: Border.all(
              color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
            ),
            boxShadow: const [
              BoxShadow(
                color: Colors.black45,
                blurRadius: 24,
                offset: Offset(0, 10),
              ),
            ],
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Icon(
                    transfer.phase == 'failed'
                        ? Icons.error_outline_rounded
                        : Icons.download_rounded,
                    color: transfer.phase == 'failed'
                        ? Colors.redAccent
                        : (isDark
                              ? AppColors.darkAccent
                              : AppColors.lightAccent),
                    size: 28,
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Text(
                      title,
                      style: TextStyle(
                        fontSize: 19,
                        fontWeight: FontWeight.w600,
                        color: isDark ? AppColors.darkText : AppColors.lightText,
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 18),
              Text(
                '${appText(widget.language, 'receivingFrom')} ${transfer.targetDevice.isEmpty ? appText(widget.language, 'nearbyPhone') : transfer.targetDevice}',
                style: TextStyle(
                  color: isDark
                      ? AppColors.darkTextMuted
                      : AppColors.lightTextMuted,
                ),
              ),
              if (transfer.fileName.isNotEmpty) ...[
                const SizedBox(height: 8),
                Text(
                  transfer.fileName,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontWeight: FontWeight.w600,
                    color: isDark ? AppColors.darkText : AppColors.lightText,
                  ),
                ),
              ],
              if (transfer.fileCount > 1) ...[
                const SizedBox(height: 4),
                Text(
                  '${transfer.fileCount} ${appText(widget.language, 'files')}',
                  style: TextStyle(
                    color: isDark
                        ? AppColors.darkTextMuted
                        : AppColors.lightTextMuted,
                  ),
                ),
              ],
              const SizedBox(height: 22),
              ClipRRect(
                borderRadius: BorderRadius.circular(8),
                child: LinearProgressIndicator(
                  value: determinate
                      ? transfer.progress
                      : (transfer.phase == 'completed' ? 1 : null),
                  minHeight: 8,
                  backgroundColor: isDark
                      ? const Color(0xFF243932)
                      : const Color(0xFFE2EBE6),
                  color: transfer.phase == 'failed'
                      ? Colors.redAccent
                      : (isDark ? AppColors.darkAccent : AppColors.lightAccent),
                ),
              ),
              const SizedBox(height: 12),
              if (determinate || transfer.phase == 'completed')
                Row(
                  mainAxisAlignment: MainAxisAlignment.spaceBetween,
                  children: [
                    Text(
                      '${(transfer.progress * 100).toStringAsFixed(0)}%',
                      style: const TextStyle(fontWeight: FontWeight.w600),
                    ),
                    Text(
                      '${_formatSize(transfer.sentBytes)} / ${_formatSize(transfer.totalBytes)}',
                      style: TextStyle(
                        color: isDark
                            ? AppColors.darkTextMuted
                            : AppColors.lightTextMuted,
                      ),
                    ),
                  ],
                ),
              if (transfer.speedBytesPerSec > 0 &&
                  transfer.phase == 'receiving') ...[
                const SizedBox(height: 8),
                Text(
                  '${_formatSize(transfer.speedBytesPerSec.toInt())}/s',
                  style: TextStyle(
                    color: isDark
                        ? AppColors.darkAccent
                        : AppColors.lightAccent,
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ],
              if (transfer.errorText.isNotEmpty) ...[
                const SizedBox(height: 12),
                Container(
                  width: double.infinity,
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(
                    color: Colors.redAccent.withValues(alpha: 0.15),
                    borderRadius: BorderRadius.circular(10),
                    border: Border.all(
                      color: Colors.redAccent.withValues(alpha: 0.3),
                    ),
                  ),
                  child: Text(
                    transfer.errorText,
                    style: const TextStyle(
                      color: Colors.redAccent,
                      fontSize: 13,
                    ),
                  ),
                ),
              ],
              const SizedBox(height: 24),
              Row(
                mainAxisAlignment: MainAxisAlignment.end,
                children: [
                  if (canCancel)
                    OutlinedButton(
                      style: OutlinedButton.styleFrom(
                        foregroundColor: isDark
                            ? AppColors.darkTextMuted
                            : AppColors.lightTextMuted,
                        side: BorderSide(
                          color: isDark
                              ? AppColors.darkBorder
                              : AppColors.lightBorder,
                        ),
                        shape: RoundedRectangleBorder(
                          borderRadius: BorderRadius.circular(10),
                        ),
                        padding: const EdgeInsets.symmetric(
                          horizontal: 18,
                          vertical: 10,
                        ),
                      ),
                      onPressed: () => widget.client.cancelTransfer(),
                      child: Text(appText(widget.language, 'cancelTransfer')),
                    )
                  else
                    ElevatedButton(
                      style: ElevatedButton.styleFrom(
                        backgroundColor: isDark
                            ? AppColors.darkAccent
                            : AppColors.lightAccent,
                        foregroundColor: isDark
                            ? const Color(0xFF0F1E19)
                            : Colors.white,
                        shape: RoundedRectangleBorder(
                          borderRadius: BorderRadius.circular(10),
                        ),
                        padding: const EdgeInsets.symmetric(
                          horizontal: 22,
                          vertical: 10,
                        ),
                        elevation: 0,
                      ),
                      onPressed: () => widget.client.dismissTransferModal(),
                      child: Text(
                        appText(widget.language, 'close'),
                        style: const TextStyle(fontWeight: FontWeight.w600),
                      ),
                    ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildIncomingModal(
    BuildContext context,
    IncomingTransferOffer offer,
  ) {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final fileCount = int.tryParse(offer.count) ?? 1;

    return Container(
      color: Colors.black54,
      child: Center(
        child: Container(
          width: 440,
          margin: const EdgeInsets.all(24),
          padding: const EdgeInsets.all(26),
          decoration: BoxDecoration(
            color: isDark ? AppColors.darkSurface : AppColors.lightSurface,
            borderRadius: BorderRadius.circular(18),
            border: Border.all(
              color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
            ),
            boxShadow: const [
              BoxShadow(
                color: Colors.black45,
                blurRadius: 24,
                offset: Offset(0, 10),
              ),
            ],
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Icon(
                    Icons.phone_android_rounded,
                    color: isDark ? AppColors.darkAccent : AppColors.lightAccent,
                    size: 28,
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          appText(widget.language, 'incomingTransfer'),
                          style: TextStyle(
                            fontSize: 18,
                            fontWeight: FontWeight.w600,
                            color: isDark
                                ? AppColors.darkText
                                : AppColors.lightText,
                          ),
                        ),
                        const SizedBox(height: 2),
                        Text(
                          '${offer.name} ${appText(widget.language, 'wantsToSend')}',
                          maxLines: 2,
                          overflow: TextOverflow.ellipsis,
                          style: TextStyle(
                            fontSize: 13,
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
              const SizedBox(height: 22),
              Row(
                children: [
                  Icon(
                    fileCount > 1
                        ? Icons.folder_copy_rounded
                        : Icons.insert_drive_file_outlined,
                    color: isDark ? AppColors.darkAccent : AppColors.lightAccent,
                    size: 22,
                  ),
                  const SizedBox(width: 12),
                  Text(
                    '$fileCount ${appText(widget.language, 'files')}',
                    style: TextStyle(
                      fontSize: 14,
                      fontWeight: FontWeight.w600,
                      color: isDark ? AppColors.darkText : AppColors.lightText,
                    ),
                  ),
                  if (offer.totalBytes > 0) ...[
                    const SizedBox(width: 8),
                    Text(
                      '• ${_formatSize(offer.totalBytes)}',
                      style: TextStyle(
                        fontSize: 12,
                        color: isDark
                            ? AppColors.darkTextMuted
                            : AppColors.lightTextMuted,
                      ),
                    ),
                  ],
                ],
              ),
              const SizedBox(height: 24),
              Row(
                children: [
                  Expanded(
                    child: OutlinedButton(
                      style: OutlinedButton.styleFrom(
                        padding: const EdgeInsets.symmetric(vertical: 12),
                        shape: RoundedRectangleBorder(
                          borderRadius: BorderRadius.circular(10),
                        ),
                        side: BorderSide(
                          color: isDark
                              ? AppColors.darkBorder
                              : AppColors.lightBorder,
                        ),
                      ),
                      onPressed: () => widget.client.confirmReceive(offer.id, false),
                      child: Text(
                        appText(widget.language, 'decline'),
                        style: TextStyle(
                          color: isDark
                              ? AppColors.darkTextMuted
                              : AppColors.lightTextMuted,
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: ElevatedButton(
                      style: ElevatedButton.styleFrom(
                        backgroundColor: isDark
                            ? AppColors.darkAccent
                            : AppColors.lightAccent,
                        foregroundColor: isDark
                            ? const Color(0xFF0F1E19)
                            : Colors.white,
                        padding: const EdgeInsets.symmetric(vertical: 12),
                        shape: RoundedRectangleBorder(
                          borderRadius: BorderRadius.circular(10),
                        ),
                        elevation: 0,
                      ),
                      onPressed: () => widget.client.confirmReceive(offer.id, true),
                      child: Text(
                        appText(widget.language, 'accept'),
                        style: const TextStyle(fontWeight: FontWeight.w600),
                      ),
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}
