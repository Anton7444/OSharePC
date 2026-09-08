import 'dart:io';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
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

  bool _isSafeWebUrl(String raw) {
    final uri = Uri.tryParse(raw.trim());
    return uri != null &&
        uri.isAbsolute &&
        uri.host.isNotEmpty &&
        (uri.scheme == 'http' || uri.scheme == 'https');
  }

  Future<void> _openReceivedUrl(String url) async {
    if (!_isSafeWebUrl(url)) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(appText(widget.language, 'invalidLink'))),
        );
      }
      return;
    }
    try {
      await Process.start(
        'rundll32.exe',
        ['url.dll,FileProtocolHandler', url],
        mode: ProcessStartMode.detached,
      );
    } catch (_) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(appText(widget.language, 'invalidLink'))),
        );
      }
    }
  }

  Future<void> _copyReceivedUrl(String url) async {
    await Clipboard.setData(ClipboardData(text: url));
    if (mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(appText(widget.language, 'linkCopied')),
          behavior: SnackBarBehavior.floating,
        ),
      );
    }
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
    final receivedUrl = widget.client.lastReceivedUrl;

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
                    RadarLogo(size: 160, active: isEnabled),
                    const SizedBox(height: 28),
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
                                      ? AppColors.darkAccent.withValues(alpha: 0.5)
                                      : AppColors.lightAccent.withValues(alpha: 0.5))
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
                    Text(
                      status.deviceName.isEmpty ? 'OsharePC' : status.deviceName,
                      textAlign: TextAlign.center,
                      style: TextStyle(
                        fontSize: 34,
                        fontWeight: FontWeight.w700,
                        color: isDark ? AppColors.darkText : AppColors.lightText,
                        letterSpacing: -0.5,
                      ),
                    ),
                    const SizedBox(height: 10),
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
                    if (receivedUrl != null && receivedUrl.isNotEmpty) ...[
                      const SizedBox(height: 28),
                      _buildReceivedUrlCard(
                        isDark,
                        receivedUrl,
                        widget.client.lastReceivedUrlSender ?? '',
                      ),
                      const SizedBox(height: 30),
                    ] else
                      const SizedBox(height: 48),
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
          if (widget.client.pendingIncomingOffer != null)
            _buildIncomingModal(context, widget.client.pendingIncomingOffer!),
          if (widget.client.transferState.active &&
              !widget.client.transferState.isSending &&
              widget.client.lastReceivedUrl == null)
            _buildTransferModal(context, widget.client.transferState),
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

  Widget _buildReceivedUrlCard(bool isDark, String url, String sender) {
    return ConstrainedBox(
      constraints: const BoxConstraints(maxWidth: 620),
      child: Container(
        width: double.infinity,
        padding: const EdgeInsets.all(18),
        decoration: BoxDecoration(
          color: isDark ? AppColors.darkCard : AppColors.lightCard,
          borderRadius: BorderRadius.circular(16),
          border: Border.all(
            color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
          ),
          boxShadow: const [
            BoxShadow(
              color: Colors.black12,
              blurRadius: 12,
              offset: Offset(0, 4),
            ),
          ],
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Container(
                  width: 42,
                  height: 42,
                  decoration: BoxDecoration(
                    color: isDark
                        ? const Color(0xFF1E3F35)
                        : const Color(0xFFD4EDE5),
                    borderRadius: BorderRadius.circular(11),
                  ),
                  child: Icon(
                    Icons.link_rounded,
                    color: isDark ? AppColors.darkAccent : AppColors.lightAccent,
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        appText(widget.language, 'linkReceived'),
                        style: TextStyle(
                          fontSize: 16,
                          fontWeight: FontWeight.w700,
                          color: isDark ? AppColors.darkText : AppColors.lightText,
                        ),
                      ),
                      if (sender.isNotEmpty) ...[
                        const SizedBox(height: 2),
                        Text(
                          '${appText(widget.language, 'receivedFrom')} $sender',
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
                IconButton(
                  onPressed: widget.client.clearReceivedUrl,
                  icon: const Icon(Icons.close_rounded, size: 18),
                  tooltip: appText(widget.language, 'close'),
                ),
              ],
            ),
            const SizedBox(height: 12),
            Container(
              width: double.infinity,
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
              decoration: BoxDecoration(
                color: isDark
                    ? const Color(0xFF162520)
                    : const Color(0xFFEFF5F2),
                borderRadius: BorderRadius.circular(10),
              ),
              child: SelectableText(
                url,
                maxLines: 3,
                style: TextStyle(
                  fontSize: 13,
                  color: isDark ? AppColors.darkText : AppColors.lightText,
                ),
              ),
            ),
            const SizedBox(height: 14),
            Row(
              mainAxisAlignment: MainAxisAlignment.end,
              children: [
                OutlinedButton.icon(
                  onPressed: () => _copyReceivedUrl(url),
                  icon: const Icon(Icons.copy_rounded, size: 16),
                  label: Text(appText(widget.language, 'copyLink')),
                ),
                const SizedBox(width: 10),
                FilledButton.icon(
                  onPressed: () => _openReceivedUrl(url),
                  icon: const Icon(Icons.open_in_new_rounded, size: 16),
                  label: Text(appText(widget.language, 'openLink')),
                ),
              ],
            ),
          ],
        ),
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
    final determinate = transfer.phase == 'receiving' && transfer.totalBytes > 0;
    final isLink = transfer.statusText.toLowerCase().contains('link');
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
                        : (isLink ? Icons.link_rounded : Icons.download_rounded),
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
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontWeight: FontWeight.w600,
                    color: isDark ? AppColors.darkText : AppColors.lightText,
                  ),
                ),
              ],
              if (!isLink && transfer.fileCount > 1) ...[
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
              if (!isLink) ...[
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
                      onPressed: () => widget.client.cancelTransfer(),
                      child: Text(appText(widget.language, 'cancelTransfer')),
                    )
                  else
                    ElevatedButton(
                      onPressed: () => widget.client.dismissTransferModal(),
                      child: Text(appText(widget.language, 'close')),
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
    final isLink = offer.mimeType == 'http/*';

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
                      isLink ? Icons.link_rounded : Icons.phone_android_rounded,
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
                          appText(
                            widget.language,
                            isLink ? 'incomingLink' : 'incomingTransfer',
                          ),
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
                          '${offer.name} ${appText(widget.language, isLink ? 'wantsToShareLink' : 'wantsToSend')}',
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
                      isLink
                          ? Icons.language_rounded
                          : (fileCount > 1
                                ? Icons.folder_copy_rounded
                                : Icons.insert_drive_file_outlined),
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
                            isLink
                                ? appText(widget.language, 'shareLink')
                                : '$fileCount ${appText(widget.language, 'files')}',
                            style: TextStyle(
                              fontSize: 14,
                              fontWeight: FontWeight.w600,
                              color: isDark
                                  ? AppColors.darkText
                                  : AppColors.lightText,
                            ),
                          ),
                          if (!isLink && offer.totalBytes > 0) ...[
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
                      onPressed: () => widget.client.confirmReceive(offer.id, false),
                      child: Text(appText(widget.language, 'decline')),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: ElevatedButton(
                      onPressed: () => widget.client.confirmReceive(offer.id, true),
                      child: Text(appText(widget.language, 'accept')),
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
