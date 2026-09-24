import 'dart:io';
import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import '../config/theme.dart';
import '../config/language.dart';
import '../models/models.dart';
import '../services/bridge_client.dart';
import '../services/outgoing_staging_controller.dart';
import '../services/transfer_presentation.dart';
import '../widgets/native_drop_zone.dart';

class SendTab extends StatefulWidget {
  final BridgeClient client;
  final AppLanguage language;
  final OutgoingStagingController stagingController;
  final bool isCurrentTab;

  const SendTab({
    super.key,
    required this.client,
    required this.language,
    required this.stagingController,
    this.isCurrentTab = true,
  });

  @override
  State<SendTab> createState() => _SendTabState();
}

class _SendTabState extends State<SendTab> {
  bool _isDragging = false;

  bool get _isTransferActive {
    final transfer = widget.client.transferState;
    return transfer.active &&
        !const ['completed', 'failed', 'cancelled'].contains(transfer.phase);
  }

  String _formatSize(int bytes) {
    if (bytes < 1024) return '$bytes B';
    if (bytes < 1024 * 1024) return '${(bytes / 1024).toStringAsFixed(1)} KB';
    if (bytes < 1024 * 1024 * 1024) {
      return '${(bytes / (1024 * 1024)).toStringAsFixed(1)} MB';
    }
    return '${(bytes / (1024 * 1024 * 1024)).toStringAsFixed(2)} GB';
  }

  Future<void> _handleDroppedPaths(List<String> paths) async {
    setState(() => _isDragging = false);
    await widget.stagingController.addPaths(paths, language: widget.language);
    _checkStagingError();
  }

  Future<void> _pickFiles() async {
    final files = await FilePicker.pickFiles();
    if (files.isNotEmpty) {
      final paths = files.map((file) => file.path).whereType<String>().toList();
      await widget.stagingController.addPaths(paths, language: widget.language);
      _checkStagingError();
    }
  }

  Future<void> _pickFolder() async {
    final dir = await FilePicker.getDirectoryPath();
    if (dir != null) {
      await widget.stagingController.addPaths([dir], language: widget.language);
      _checkStagingError();
    }
  }

  void _checkStagingError() {
    final error = widget.stagingController.stagingError;
    if (error != null && mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(error), behavior: SnackBarBehavior.floating),
      );
    }
  }

  void _sendTo(DeviceModel device) {
    if (widget.stagingController.selectedFiles.isEmpty) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(appText(widget.language, 'selectFiles')),
          behavior: SnackBarBehavior.floating,
        ),
      );
      return;
    }
    if (widget.stagingController.isStaging ||
        !widget.stagingController.hasValidStagedSelection) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(
            widget.stagingController.stagingError ??
                appText(widget.language, 'stagingFailed'),
          ),
          behavior: SnackBarBehavior.floating,
        ),
      );
      return;
    }
    if (_isTransferActive) {
      return;
    }
    widget.client.sendToDevice(device);
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final isDark = theme.brightness == Brightness.dark;
    final devices = widget.client.devices;
    final transfer = widget.client.transferState;

    return ListenableBuilder(
      listenable: widget.stagingController,
      builder: (context, _) {
        final selectedFiles = widget.stagingController.selectedFiles;
        return NativeDropZone(
          enabled: widget.isCurrentTab && !_isTransferActive,
          onDragStateChanged: (isDragging) {
            setState(() => _isDragging = isDragging);
          },
          onDropped: _handleDroppedPaths,
          child: Stack(
            children: [
              ListView(
                padding: const EdgeInsets.symmetric(
                  horizontal: 28,
                  vertical: 24,
                ),
                children: [
                  // Section 1: Selection Header
                  Text(
                    appText(widget.language, 'selection'),
                    style: TextStyle(
                      fontSize: 18,
                      fontWeight: FontWeight.w700,
                      color: isDark ? AppColors.darkText : AppColors.lightText,
                    ),
                  ),
                  const SizedBox(height: 14),

                  if (selectedFiles.isEmpty)
                    _buildEmptySelection(isDark)
                  else
                    _buildStagedFilesCard(isDark, selectedFiles),

                  const SizedBox(height: 32),

                  // Section 2: Nearby Devices Header
                  Row(
                    children: [
                      Text(
                        appText(widget.language, 'nearby'),
                        style: TextStyle(
                          fontSize: 18,
                          fontWeight: FontWeight.w700,
                          color: isDark
                              ? AppColors.darkText
                              : AppColors.lightText,
                        ),
                      ),
                      const Spacer(),
                      Container(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 10,
                          vertical: 4,
                        ),
                        decoration: BoxDecoration(
                          color: isDark
                              ? AppColors.darkAccentSoft
                              : AppColors.lightAccentSoft,
                          borderRadius: BorderRadius.circular(12),
                        ),
                        child: Row(
                          children: [
                            Container(
                              width: 7,
                              height: 7,
                              decoration: BoxDecoration(
                                shape: BoxShape.circle,
                                color: isDark
                                    ? AppColors.darkAccent
                                    : AppColors.lightAccent,
                              ),
                            ),
                            const SizedBox(width: 6),
                            Text(
                              '${devices.length} ${appText(widget.language, 'found')}',
                              style: TextStyle(
                                fontSize: 12,
                                fontWeight: FontWeight.w600,
                                color: isDark
                                    ? AppColors.darkAccent
                                    : AppColors.lightAccent,
                              ),
                            ),
                          ],
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 16),

                  if (devices.isEmpty)
                    _buildNoDevicesCard(isDark)
                  else
                    _buildDeviceGrid(devices, isDark),
                ],
              ),

              // Section 3: Active Transfer Modal
              if (shouldShowSendTransferModal(
                isActive: transfer.active,
                isBackground: transfer.isBackground,
              ))
                _buildTransferModal(context, transfer, isDark),
            ],
          ),
        );
      },
    );
  }

  Widget _buildEmptySelection(bool isDark) {
    final borderColor = _isDragging
        ? (isDark ? AppColors.darkAccent : AppColors.lightAccent)
        : (isDark ? AppColors.darkBorder : AppColors.lightBorder);
    final bgColor = _isDragging
        ? (isDark ? AppColors.darkAccentSoft : AppColors.lightAccentSoft)
        : (isDark ? AppColors.darkCard : AppColors.lightCard);

    return InkWell(
      onTap: _isTransferActive ? null : _pickFiles,
      borderRadius: BorderRadius.circular(16),
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 150),
        width: double.infinity,
        height: 140,
        padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 16),
        decoration: BoxDecoration(
          color: bgColor,
          borderRadius: BorderRadius.circular(16),
          border: Border.all(color: borderColor, width: _isDragging ? 2 : 1),
        ),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(
              _isDragging
                  ? Icons.file_download_rounded
                  : Icons.drive_folder_upload_rounded,
              size: 36,
              color: isDark ? AppColors.darkAccent : AppColors.lightAccent,
            ),
            const SizedBox(height: 10),
            Text(
              _isDragging
                  ? appText(widget.language, 'releaseToAdd')
                  : appText(widget.language, 'dropFilesHere'),
              textAlign: TextAlign.center,
              style: TextStyle(
                fontSize: 14,
                fontWeight: FontWeight.w600,
                color: isDark ? AppColors.darkText : AppColors.lightText,
              ),
            ),
            const SizedBox(height: 12),
            Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                OutlinedButton.icon(
                  onPressed: _isTransferActive ? null : _pickFiles,
                  icon: const Icon(Icons.description_rounded, size: 16),
                  label: Text(appText(widget.language, 'file')),
                  style: OutlinedButton.styleFrom(
                    foregroundColor: isDark
                        ? AppColors.darkAccent
                        : AppColors.lightAccent,
                    side: BorderSide(
                      color: isDark
                          ? AppColors.darkBorder
                          : AppColors.lightBorder,
                    ),
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(8),
                    ),
                    padding: const EdgeInsets.symmetric(
                      horizontal: 12,
                      vertical: 6,
                    ),
                  ),
                ),
                const SizedBox(width: 12),
                OutlinedButton.icon(
                  onPressed: _isTransferActive ? null : _pickFolder,
                  icon: const Icon(Icons.folder_rounded, size: 16),
                  label: Text(appText(widget.language, 'folder')),
                  style: OutlinedButton.styleFrom(
                    foregroundColor: isDark
                        ? AppColors.darkAccent
                        : AppColors.lightAccent,
                    side: BorderSide(
                      color: isDark
                          ? AppColors.darkBorder
                          : AppColors.lightBorder,
                    ),
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(8),
                    ),
                    padding: const EdgeInsets.symmetric(
                      horizontal: 12,
                      vertical: 6,
                    ),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildStagedFilesCard(bool isDark, List<String> selectedFiles) {
    final borderColor = _isDragging
        ? (isDark ? AppColors.darkAccent : AppColors.lightAccent)
        : (isDark ? AppColors.darkBorder : AppColors.lightBorder);
    final bgColor = _isDragging
        ? (isDark ? AppColors.darkAccentSoft : AppColors.lightAccentSoft)
        : (isDark ? AppColors.darkCard : AppColors.lightCard);

    return AnimatedContainer(
      duration: const Duration(milliseconds: 150),
      padding: const EdgeInsets.all(18),
      decoration: BoxDecoration(
        color: bgColor,
        borderRadius: BorderRadius.circular(16),
        border: Border.all(color: borderColor, width: _isDragging ? 2 : 1),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(
                Icons.folder_copy_rounded,
                size: 20,
                color: isDark ? AppColors.darkAccent : AppColors.lightAccent,
              ),
              const SizedBox(width: 10),
              Text(
                '${widget.stagingController.totalCount} ${appText(widget.language, 'files')}',
                style: TextStyle(
                  fontSize: 15,
                  fontWeight: FontWeight.w700,
                  color: isDark ? AppColors.darkText : AppColors.lightText,
                ),
              ),
              const SizedBox(width: 8),
              Text(
                '•  ${_formatSize(widget.stagingController.totalBytes)}',
                style: TextStyle(
                  fontSize: 14,
                  fontWeight: FontWeight.w500,
                  color: isDark
                      ? AppColors.darkTextMuted
                      : AppColors.lightTextMuted,
                ),
              ),
              const Spacer(),
              TextButton.icon(
                onPressed: _isTransferActive ? null : _pickFiles,
                icon: const Icon(Icons.add, size: 16),
                label: Text(appText(widget.language, 'add')),
                style: TextButton.styleFrom(
                  foregroundColor: isDark
                      ? AppColors.darkAccent
                      : AppColors.lightAccent,
                ),
              ),
              const SizedBox(width: 4),
              IconButton(
                onPressed: _isTransferActive
                    ? null
                    : () => widget.stagingController.clear(),
                icon: const Icon(Icons.close, size: 18),
                tooltip: appText(widget.language, 'clear'),
                color: isDark
                    ? AppColors.darkTextMuted
                    : AppColors.lightTextMuted,
              ),
            ],
          ),
          if (_isDragging) ...[
            const SizedBox(height: 8),
            Container(
              width: double.infinity,
              padding: const EdgeInsets.symmetric(vertical: 6),
              decoration: BoxDecoration(
                color: isDark
                    ? AppColors.darkAccentSoft
                    : AppColors.lightAccentSoft,
                borderRadius: BorderRadius.circular(8),
              ),
              child: Text(
                appText(widget.language, 'releaseToAdd'),
                textAlign: TextAlign.center,
                style: TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.w600,
                  color: isDark ? AppColors.darkAccent : AppColors.lightAccent,
                ),
              ),
            ),
          ],
          const SizedBox(height: 12),
          ConstrainedBox(
            constraints: const BoxConstraints(maxHeight: 140),
            child: ListView.separated(
              shrinkWrap: true,
              itemCount: selectedFiles.length,
              separatorBuilder: (context, index) => const SizedBox(height: 6),
              itemBuilder: (context, index) {
                final filePath = selectedFiles[index];
                final fileName = filePath.split(Platform.pathSeparator).last;
                return Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 12,
                    vertical: 8,
                  ),
                  decoration: BoxDecoration(
                    color: isDark
                        ? AppColors.darkAccentSubtle
                        : AppColors.lightAccentSubtle,
                    borderRadius: BorderRadius.circular(10),
                  ),
                  child: Row(
                    children: [
                      Icon(
                        Icons.insert_drive_file_outlined,
                        size: 16,
                        color: isDark
                            ? AppColors.darkTextMuted
                            : AppColors.lightTextMuted,
                      ),
                      const SizedBox(width: 10),
                      Expanded(
                        child: Text(
                          fileName,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: TextStyle(
                            fontSize: 13,
                            color: isDark
                                ? AppColors.darkText
                                : AppColors.lightText,
                          ),
                        ),
                      ),
                      IconButton(
                        icon: const Icon(Icons.close, size: 14),
                        padding: EdgeInsets.zero,
                        constraints: const BoxConstraints(),
                        color: isDark
                            ? AppColors.darkTextSubtle
                            : AppColors.lightTextMuted,
                        onPressed: _isTransferActive
                            ? null
                            : () => widget.stagingController.removePath(
                                filePath,
                                language: widget.language,
                              ),
                      ),
                    ],
                  ),
                );
              },
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildNoDevicesCard(bool isDark) {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.symmetric(vertical: 48, horizontal: 24),
      decoration: BoxDecoration(
        color: isDark ? AppColors.darkCard : AppColors.lightCard,
        borderRadius: BorderRadius.circular(16),
        border: Border.all(
          color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
          width: 1,
        ),
      ),
      child: Column(
        children: [
          Icon(
            Icons.wifi_tethering_rounded,
            size: 40,
            color: isDark ? AppColors.darkTextSubtle : AppColors.lightTextMuted,
          ),
          const SizedBox(height: 14),
          Text(
            appText(widget.language, 'searching'),
            style: TextStyle(
              fontSize: 15,
              fontWeight: FontWeight.w600,
              color: isDark ? AppColors.darkText : AppColors.lightText,
            ),
          ),
          const SizedBox(height: 6),
          Text(
            appText(widget.language, 'openPhone'),
            textAlign: TextAlign.center,
            style: TextStyle(
              fontSize: 13,
              color: isDark
                  ? AppColors.darkTextMuted
                  : AppColors.lightTextMuted,
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildDeviceGrid(List<DeviceModel> devices, bool isDark) {
    return GridView.builder(
      shrinkWrap: true,
      physics: const NeverScrollableScrollPhysics(),
      gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(
        maxCrossAxisExtent: 320,
        mainAxisExtent: 110,
        crossAxisSpacing: 14,
        mainAxisSpacing: 14,
      ),
      itemCount: devices.length,
      itemBuilder: (context, index) {
        final dev = devices[index];
        return _buildDeviceCard(dev, isDark);
      },
    );
  }

  Widget _buildDeviceCard(DeviceModel device, bool isDark) {
    return InkWell(
      onTap: () => _sendTo(device),
      borderRadius: BorderRadius.circular(16),
      child: Container(
        padding: const EdgeInsets.all(14),
        decoration: BoxDecoration(
          color: isDark ? AppColors.darkCard : AppColors.lightCard,
          borderRadius: BorderRadius.circular(16),
          border: Border.all(
            color: isDark ? AppColors.darkBorder : AppColors.lightBorder,
            width: 1,
          ),
        ),
        child: Row(
          children: [
            Container(
              width: 48,
              height: 48,
              decoration: BoxDecoration(
                color: isDark
                    ? AppColors.darkAccentSoft
                    : AppColors.lightAccentSoft,
                borderRadius: BorderRadius.circular(12),
              ),
              child: Icon(
                Icons.smartphone_rounded,
                size: 26,
                color: isDark ? AppColors.darkAccent : AppColors.lightAccent,
              ),
            ),
            const SizedBox(width: 14),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  Text(
                    device.name,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontSize: 15,
                      fontWeight: FontWeight.w700,
                      color: isDark ? AppColors.darkText : AppColors.lightText,
                    ),
                  ),
                  const SizedBox(height: 5),
                  Row(
                    children: [
                      Container(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 6,
                          vertical: 2,
                        ),
                        decoration: BoxDecoration(
                          color: isDark
                              ? AppColors.darkAccentSoft
                              : AppColors.lightAccentSoft,
                          borderRadius: BorderRadius.circular(6),
                        ),
                        child: Text(
                          device.kind,
                          style: TextStyle(
                            fontSize: 11,
                            fontWeight: FontWeight.w600,
                            color: isDark
                                ? AppColors.darkAccent
                                : AppColors.lightAccent,
                          ),
                        ),
                      ),
                      const SizedBox(width: 8),
                      Icon(
                        Icons.network_wifi_rounded,
                        size: 14,
                        color: isDark
                            ? AppColors.darkTextMuted
                            : AppColors.lightTextMuted,
                      ),
                      const SizedBox(width: 4),
                      Text(
                        '${device.rssi} dBm',
                        style: TextStyle(
                          fontSize: 11,
                          color: isDark
                              ? AppColors.darkTextMuted
                              : AppColors.lightTextMuted,
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
    );
  }

  Widget _buildTransferModal(
    BuildContext context,
    TransferStateModel transfer,
    bool isDark,
  ) {
    final canCancel = !const [
      'completed',
      'failed',
      'cancelled',
    ].contains(transfer.phase);
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
              width: 1.5,
            ),
            boxShadow: const [
              BoxShadow(
                color: Colors.black54,
                blurRadius: 24,
                offset: Offset(0, 10),
              ),
            ],
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Container(
                width: 60,
                height: 60,
                decoration: BoxDecoration(
                  color: isDark
                      ? AppColors.darkAccentSoft
                      : AppColors.lightAccentSoft,
                  shape: BoxShape.circle,
                ),
                child: Icon(
                  transfer.isSending
                      ? Icons.upload_rounded
                      : Icons.download_rounded,
                  size: 32,
                  color: isDark ? AppColors.darkAccent : AppColors.lightAccent,
                ),
              ),
              const SizedBox(height: 18),
              Text(
                transfer.statusText.isEmpty
                    ? appText(widget.language, 'transferring')
                    : transfer.statusText,
                textAlign: TextAlign.center,
                style: TextStyle(
                  fontSize: 18,
                  fontWeight: FontWeight.w700,
                  color: isDark ? AppColors.darkText : AppColors.lightText,
                ),
              ),
              const SizedBox(height: 20),
              ClipRRect(
                borderRadius: BorderRadius.circular(8),
                child: LinearProgressIndicator(
                  value: transfer.progress > 0 ? transfer.progress : null,
                  minHeight: 8,
                  backgroundColor: isDark
                      ? AppColors.darkAccentSoft
                      : AppColors.lightAccentSoft,
                  color: isDark ? AppColors.darkAccent : AppColors.lightAccent,
                ),
              ),
              const SizedBox(height: 14),
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  Text(
                    '${(transfer.progress * 100).toStringAsFixed(0)}%',
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w600,
                      color: isDark ? AppColors.darkText : AppColors.lightText,
                    ),
                  ),
                  if (transfer.speedBytesPerSec > 0)
                    Text(
                      '${_formatSize(transfer.speedBytesPerSec.toInt())}/s',
                      style: TextStyle(
                        fontSize: 13,
                        color: isDark
                            ? AppColors.darkAccent
                            : AppColors.lightAccent,
                      ),
                    ),
                  Text(
                    '${_formatSize(transfer.sentBytes)} / ${_formatSize(transfer.totalBytes)}',
                    style: TextStyle(
                      fontSize: 13,
                      color: isDark
                          ? AppColors.darkTextMuted
                          : AppColors.lightTextMuted,
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 24),
              SizedBox(
                width: double.infinity,
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
                  onPressed: canCancel
                      ? widget.client.cancelTransfer
                      : widget.client.dismissTransferModal,
                  child: Text(
                    appText(
                      widget.language,
                      canCancel ? 'cancelTransfer' : 'dismiss',
                    ),
                    style: TextStyle(
                      color: isDark
                          ? AppColors.darkTextMuted
                          : AppColors.lightTextMuted,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
