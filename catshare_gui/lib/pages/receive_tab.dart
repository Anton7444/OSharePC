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
      if (!const ['completed', 'failed', 'cancelled'].contains(transfer.phase)) {
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
                                    ? const Color(0xFF1E3F35)
                                    : const Color(0xFFD4EDE5))
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
                        color: isDark ? AppColors.darkText : AppColors.lightText,
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
                      selected: widget.client.quickSaveMode == QuickSaveMode.favorites
                          ? QuickSaveMode.off
                          : widget.client.quickSaveMode,
                      onSelected: (mode) => widget.client.setQuickSaveMode(mode),
                    ),
                  ],
                ),
              ),
            ),
          ),

          // 6. Incoming Transfer Dialog Modal Overlay
          if (widget.client.pendingIncomingOffer != null)
            _buildIncomingModal(context, widget.client.pendingIncomingOffer!),
          if (widget.client.transferState.active &&
              !widget.client.transferState.isSending)
            _buildTransferModal(context, widget.client.transferState),

          // 7. Drag and Drop Overlay
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
                          ? const Color(0xFF1B382F)
                          : const Color(0xFFE2F3EC),
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

  String _formatSize(int bytes) {
    if (bytes < 1024) return '$bytes B';
    if (bytes < 1024 * 1024) return '${(bytes / 1024).toStringAsFixed(1)} KB';
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
    final determinate =
        transfer.phase == 'receiving' && transfer.totalBytes > 0;
    final title = transfer.statusText.isEmpty
        ? appText(widget.language, 'receiving')
        : transfer.statusText;
    return Container(
      color: Colors.black54,
      child: Center(
        child: Container(
          width: 460,
          margin: const EdgeInsets.all(24),
          padding: const EdgeInsets.all(28),
          decoration: BoxDecoration(
            color: isDark ? AppColors.darkSurface : AppColors.lightSurface,
            borderRadius: BorderRadius.circular(20),
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
                    size: 30,
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Text(
                      title,
                      style: TextStyle(
                        fontSize: 20,
                        fontWeight: FontWeight.w700,
                        color: isDark
                            ? AppColors.darkText
                            : AppColors.lightText,
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
                        style: const TextStyle(fontWeight: FontWeight.w700),
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
          padding: const EdgeInsets.all(28),
          decoration: BoxDecoration(
            color: isDark ? AppColors.darkSurface : AppColors.lightSurface,
            borderRadius: BorderRadius.circular(20),
            border: Border.all(
              color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
              width: 1.5,
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
                  Container(
                    width: 46,
                    height: 46,
                    decoration: BoxDecoration(
                      color: isDark
                          ? const Color(0xFF1E3F35)
                          : const Color(0xFFD4EDE5),
                      shape: BoxShape.circle,
                    ),
                    child: Icon(
                      Icons.phone_android_rounded,
                      color: isDark
                          ? AppColors.darkAccent
                          : AppColors.lightAccent,
                      size: 26,
                    ),
                  ),
                  const SizedBox(width: 14),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          appText(widget.language, 'incomingTransfer'),
                          style: TextStyle(
                            fontSize: 18,
                            fontWeight: FontWeight.w700,
                            color: isDark
                                ? AppColors.darkText
                                : AppColors.lightText,
                          ),
                        ),
                        const SizedBox(height: 2),
                        Text(
                          '${offer.name} ${appText(widget.language, 'wantsToSend')}',
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
              Container(
                width: double.infinity,
                padding: const EdgeInsets.all(16),
                decoration: BoxDecoration(
                  color: isDark
                      ? const Color(0xFF182923)
                      : const Color(0xFFEFF5F2),
                  borderRadius: BorderRadius.circular(12),
                  border: Border.all(
                    color: isDark
                        ? AppColors.darkBorder
                        : AppColors.lightBorder,
                  ),
                ),
                child: Row(
                  children: [
                    Icon(
                      fileCount > 1
                          ? Icons.folder_copy_rounded
                          : Icons.insert_drive_file_outlined,
                      color: isDark
                          ? AppColors.darkAccent
                          : AppColors.lightAccent,
                      size: 24,
                    ),
                    const SizedBox(width: 14),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            '$fileCount ${appText(widget.language, 'files')}',
                            style: TextStyle(
                              fontSize: 14,
                              fontWeight: FontWeight.w600,
                              color: isDark
                                  ? AppColors.darkText
                                  : AppColors.lightText,
                            ),
                          ),
                          if (offer.totalBytes > 0) ...[
                            const SizedBox(height: 2),
                            Text(
                              _formatSize(offer.totalBytes),
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
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 24),
              Row(
                children: [
                  Expanded(
                    child: OutlinedButton(
                      style: OutlinedButton.styleFrom(
                        padding: const EdgeInsets.symmetric(vertical: 12),
                        shape: RoundedRectangleBorder(
                          borderRadius: BorderRadius.circular(12),
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
                          borderRadius: BorderRadius.circular(12),
                        ),
                        elevation: 0,
                      ),
                      onPressed: () => widget.client.confirmReceive(offer.id, true),
                      child: Text(
                        appText(widget.language, 'accept'),
                        style: const TextStyle(fontWeight: FontWeight.w700),
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