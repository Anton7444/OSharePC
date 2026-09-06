import 'package:flutter/material.dart';
import '../config/theme.dart';
import '../config/language.dart';
import '../models/models.dart';
import '../services/bridge_client.dart';
import '../widgets/custom_segmented_button.dart';
import '../widgets/radar_logo.dart';

class ReceiveTab extends StatelessWidget {
  final BridgeClient client;
  final AppLanguage language;

  const ReceiveTab({super.key, required this.client, required this.language});

  List<String> _formatIpAsHashes(String ip) {
    if (ip.isEmpty) return ['#--'];
    return ip.split('.').map((part) => '#$part').toList();
  }

  @override
  Widget build(BuildContext context) {
    final status = client.status;
    final theme = Theme.of(context);
    final isDark = theme.brightness == Brightness.dark;
    final isEnabled = status.receiveEnabled;
    final ipParts = _formatIpAsHashes(status.lanIp);

    return Stack(
      children: [
        Center(
          child: SingleChildScrollView(
            child: Padding(
              padding: const EdgeInsets.symmetric(vertical: 40, horizontal: 24),
              child: Column(
                mainAxisAlignment: MainAxisAlignment.center,
                crossAxisAlignment: CrossAxisAlignment.center,
                children: [
                  // 1. Center Radar Logo
                  RadarLogo(size: 160, active: isEnabled),
                  const SizedBox(height: 28),

                  // 2. Receive Feature Toggle Switch Button
                  InkWell(
                    onTap: () => client.setReceiveEnabled(!isEnabled),
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
                              language,
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

                  // 4. IP / Hash segments
                  Wrap(
                    spacing: 8,
                    runSpacing: 4,
                    alignment: WrapAlignment.center,
                    children: ipParts.map((part) {
                      return Text(
                        part,
                        style: TextStyle(
                          fontSize: 18,
                          fontWeight: FontWeight.w500,
                          color: isDark
                              ? AppColors.darkTextMuted
                              : AppColors.lightTextMuted,
                          letterSpacing: 0.5,
                        ),
                      );
                    }).toList(),
                  ),
                  const SizedBox(height: 48),

                  // 5. Quick Save Controls
                  Text(
                    appText(language, 'quickSave'),
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
                    labels: [appText(language, 'off'), appText(language, 'on')],
                    selected: client.quickSaveMode == QuickSaveMode.favorites
                        ? QuickSaveMode.off
                        : client.quickSaveMode,
                    onSelected: (mode) => client.setQuickSaveMode(mode),
                  ),
                ],
              ),
            ),
          ),
        ),

        // 6. Incoming Transfer Dialog Modal Overlay
        if (client.pendingIncomingOffer != null)
          _buildIncomingModal(context, client.pendingIncomingOffer!),
        if (client.transferState.active && !client.transferState.isSending)
          _buildTransferModal(context, client.transferState),
      ],
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
        ? appText(language, 'receiving')
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
                '${appText(language, 'receivingFrom')} ${transfer.targetDevice.isEmpty ? appText(language, 'nearbyPhone') : transfer.targetDevice}',
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
                  '${transfer.fileCount} ${appText(language, 'files')}',
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
                  ),
                ),
              ],
              if (transfer.errorText.isNotEmpty) ...[
                const SizedBox(height: 12),
                Text(
                  transfer.errorText,
                  style: const TextStyle(color: Colors.redAccent),
                ),
              ],
              if (transfer.phase == 'completed') ...[
                const SizedBox(height: 12),
                Text(
                  '${appText(language, 'savedTo')} ${transfer.saveDirectory.isEmpty ? 'Downloads/OsharePC' : transfer.saveDirectory}',
                  style: TextStyle(
                    color: isDark
                        ? AppColors.darkTextMuted
                        : AppColors.lightTextMuted,
                  ),
                ),
              ],
              const SizedBox(height: 22),
              SizedBox(
                width: double.infinity,
                child: OutlinedButton(
                  onPressed: canCancel
                      ? client.cancelTransfer
                      : client.dismissTransferModal,
                  child: Text(
                    appText(language, canCancel ? 'cancelTransfer' : 'dismiss'),
                  ),
                ),
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
    final theme = Theme.of(context);
    final isDark = theme.brightness == Brightness.dark;

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
          child: Stack(
            children: [
              Positioned(
                top: 0,
                right: 0,
                child: IconButton(
                  icon: const Icon(Icons.close_rounded, size: 20),
                  tooltip: appText(language, 'close'),
                  color: isDark
                      ? AppColors.darkTextMuted
                      : AppColors.lightTextMuted,
                  onPressed: () => client.dismissIncomingOffer(),
                ),
              ),
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Container(
                      width: 60,
                      height: 60,
                      decoration: BoxDecoration(
                        color: isDark
                            ? const Color(0xFF1E3F35)
                            : const Color(0xFFD4EDE5),
                        shape: BoxShape.circle,
                      ),
                      child: Icon(
                        Icons.download_rounded,
                        size: 32,
                        color: isDark
                            ? AppColors.darkAccent
                            : AppColors.lightAccent,
                      ),
                    ),
                    const SizedBox(height: 18),
                    Text(
                      appText(language, 'incomingTransfer'),
                      style: TextStyle(
                        fontSize: 20,
                        fontWeight: FontWeight.w700,
                        color: isDark
                            ? AppColors.darkText
                            : AppColors.lightText,
                      ),
                    ),
                    const SizedBox(height: 10),
                    Text(
                      '${offer.name} ${appText(language, 'wantsToSend')} ${offer.count} ${appText(language, 'files')}.',
                      textAlign: TextAlign.center,
                      style: TextStyle(
                        fontSize: 14,
                        color: isDark
                            ? AppColors.darkTextMuted
                            : AppColors.lightTextMuted,
                      ),
                    ),
                    const SizedBox(height: 24),
                    Row(
                      children: [
                        Expanded(
                          child: OutlinedButton(
                            style: OutlinedButton.styleFrom(
                              padding: const EdgeInsets.symmetric(vertical: 14),
                              shape: RoundedRectangleBorder(
                                borderRadius: BorderRadius.circular(12),
                              ),
                              side: BorderSide(
                                color: isDark
                                    ? AppColors.darkBorder
                                    : AppColors.lightBorder,
                              ),
                            ),
                            onPressed: () =>
                                client.confirmReceive(offer.id, false),
                            child: Text(
                              appText(language, 'decline'),
                              style: TextStyle(
                                color: isDark
                                    ? AppColors.darkTextMuted
                                    : AppColors.lightTextMuted,
                                fontWeight: FontWeight.w600,
                              ),
                            ),
                          ),
                        ),
                        const SizedBox(width: 14),
                        Expanded(
                          child: ElevatedButton(
                            style: ElevatedButton.styleFrom(
                              padding: const EdgeInsets.symmetric(vertical: 14),
                              backgroundColor: isDark
                                  ? AppColors.darkAccent
                                  : AppColors.lightAccent,
                              foregroundColor: const Color(0xFF072920),
                              shape: RoundedRectangleBorder(
                                borderRadius: BorderRadius.circular(12),
                              ),
                              elevation: 0,
                            ),
                            onPressed: () =>
                                client.confirmReceive(offer.id, true),
                            child: Text(
                              appText(language, 'accept'),
                              style: TextStyle(fontWeight: FontWeight.w700),
                            ),
                          ),
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
    );
  }
}
