import 'dart:io';
import 'package:desktop_drop/desktop_drop.dart';
import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import '../config/theme.dart';
import '../config/language.dart';
import '../models/models.dart';
import '../services/bridge_client.dart';

class SendTab extends StatefulWidget {
  final BridgeClient client;
  final AppLanguage language;

  const SendTab({super.key, required this.client, required this.language});

  @override
  State<SendTab> createState() => _SendTabState();
}

class _SendTabState extends State<SendTab> {
  final List<String> _selectedFiles = [];
  bool _isDragging = false;

  String _formatSize(int bytes) {
    if (bytes < 1024) return '$bytes B';
    if (bytes < 1024 * 1024) return '${(bytes / 1024).toStringAsFixed(1)} KB';
    if (bytes < 1024 * 1024 * 1024) {
      return '${(bytes / (1024 * 1024)).toStringAsFixed(1)} MB';
    }
    return '${(bytes / (1024 * 1024 * 1024)).toStringAsFixed(2)} GB';
  }

  int get _totalSize {
    int sum = 0;
    for (final path in _selectedFiles) {
      try {
        final f = File(path);
        if (f.existsSync()) sum += f.lengthSync();
      } catch (_) {}
    }
    return sum;
  }

  Future<void> _addPaths(List<String> rawPaths) async {
    if (widget.client.transferState.active) {
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

    if (rawPaths.isEmpty) return;

    final newFiles = <String>[];
    bool hasError = false;

    for (final rawPath in rawPaths) {
      final path = rawPath.trim();
      if (path.isEmpty) continue;

      try {
        final type = FileSystemEntity.typeSync(path);
        if (type == FileSystemEntityType.file) {
          final f = File(path);
          if (f.existsSync()) {
            if (!_selectedFiles.contains(path) && !newFiles.contains(path)) {
              newFiles.add(path);
            }
          } else {
            hasError = true;
          }
        } else if (type == FileSystemEntityType.directory) {
          final dir = Directory(path);
          if (dir.existsSync()) {
            final entities = dir.listSync(recursive: true, followLinks: false);
            for (final entity in entities) {
              if (entity is File && entity.existsSync()) {
                if (!_selectedFiles.contains(entity.path) &&
                    !newFiles.contains(entity.path)) {
                  newFiles.add(entity.path);
                }
              }
            }
          } else {
            hasError = true;
          }
        } else {
          hasError = true;
        }
      } catch (e) {
        debugPrint('Error inspecting path "$path": $e');
        hasError = true;
      }
    }

    if (newFiles.isNotEmpty) {
      setState(() {
        _selectedFiles.addAll(newFiles);
      });
      _stage();
    }

    if (hasError && mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(appText(widget.language, 'unableToAddDropped')),
          behavior: SnackBarBehavior.floating,
        ),
      );
    }
  }

  Future<void> _pickFiles() async {
    final files = await FilePicker.pickFiles();
    if (files.isNotEmpty) {
      final paths = files
          .map((file) => file.path)
          .whereType<String>()
          .toList();
      await _addPaths(paths);
    }
  }

  Future<void> _pickFolder() async {
    final dir = await FilePicker.getDirectoryPath();
    if (dir != null) {
      await _addPaths([dir]);
    }
  }

  void _clearSelection() {
    setState(() => _selectedFiles.clear());
  }

  void _stage() {
    if (_selectedFiles.isNotEmpty) {
      widget.client.stageFiles(_selectedFiles);
    }
  }

  void _sendTo(DeviceModel device) {
    if (_selectedFiles.isEmpty) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(appText(widget.language, 'selectFiles')),
          behavior: SnackBarBehavior.floating,
        ),
      );
      return;
    }
    widget.client.stageFiles(_selectedFiles).then((staged) {
      if (!mounted) return;
      if (staged == null) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(
            content: Text('Could not stage the selected files.'),
            behavior: SnackBarBehavior.floating,
          ),
        );
        return;
      }
      widget.client.sendToDevice(device);
    });
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final isDark = theme.brightness == Brightness.dark;
    final devices = widget.client.devices;
    final transfer = widget.client.transferState;

    return Stack(
      children: [
        ListView(
          padding: const EdgeInsets.symmetric(horizontal: 28, vertical: 24),
          children: [
            // Section 1: Selection
            Text(
              appText(widget.language, 'selection'),
              style: TextStyle(
                fontSize: 18,
                fontWeight: FontWeight.w700,
                color: isDark ? AppColors.darkText : AppColors.lightText,
              ),
            ),
            const SizedBox(height: 14),

            if (_selectedFiles.isEmpty)
              _buildEmptySelection(isDark)
            else
              _buildStagedFilesCard(isDark),

            const SizedBox(height: 32),

            // Section 2: Nearby Devices
            Row(
              children: [
                Text(
                  appText(widget.language, 'nearby'),
                  style: TextStyle(
                    fontSize: 18,
                    fontWeight: FontWeight.w700,
                    color: isDark ? AppColors.darkText : AppColors.lightText,
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
                        ? const Color(0xFF1E3F35)
                        : const Color(0xFFD4EDE5),
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
        if (transfer.active) _buildTransferModal(context, transfer, isDark),
      ],
    );
  }

  Widget _buildEmptySelection(bool isDark) {
    final isTransferActive = widget.client.transferState.active;
    final borderColor = _isDragging
        ? (isDark ? AppColors.darkAccent : AppColors.lightAccent)
        : (isDark ? AppColors.darkBorder : AppColors.lightBorder);
    final bgColor = _isDragging
        ? (isDark
            ? const Color(0xFF1B382F)
            : const Color(0xFFE2F3EC))
        : (isDark ? AppColors.darkCard : AppColors.lightCard);

    return DropTarget(
      enable: !isTransferActive,
      onDragEntered: (_) => setState(() => _isDragging = true),
      onDragExited: (_) => setState(() => _isDragging = false),
      onDragDone: (detail) {
        setState(() => _isDragging = false);
        _addPaths(detail.files.map((f) => f.path).toList());
      },
      child: InkWell(
        onTap: isTransferActive ? null : _pickFiles,
        borderRadius: BorderRadius.circular(16),
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          width: double.infinity,
          height: 140,
          padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 16),
          decoration: BoxDecoration(
            color: bgColor,
            borderRadius: BorderRadius.circular(16),
            border: Border.all(
              color: borderColor,
              width: _isDragging ? 2 : 1,
            ),
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
                    onPressed: isTransferActive ? null : _pickFiles,
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
                    onPressed: isTransferActive ? null : _pickFolder,
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
      ),
    );
  }

  Widget _buildStagedFilesCard(bool isDark) {
    final isTransferActive = widget.client.transferState.active;
    final borderColor = _isDragging
        ? (isDark ? AppColors.darkAccent : AppColors.lightAccent)
        : (isDark ? AppColors.darkBorder : AppColors.lightBorder);
    final bgColor = _isDragging
        ? (isDark
            ? const Color(0xFF1B382F)
            : const Color(0xFFE2F3EC))
        : (isDark ? AppColors.darkCard : AppColors.lightCard);

    return DropTarget(
      enable: !isTransferActive,
      onDragEntered: (_) => setState(() => _isDragging = true),
      onDragExited: (_) => setState(() => _isDragging = false),
      onDragDone: (detail) {
        setState(() => _isDragging = false);
        _addPaths(detail.files.map((f) => f.path).toList());
      },
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 150),
        padding: const EdgeInsets.all(18),
        decoration: BoxDecoration(
          color: bgColor,
          borderRadius: BorderRadius.circular(16),
          border: Border.all(
            color: borderColor,
            width: _isDragging ? 2 : 1,
          ),
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
                  '${_selectedFiles.length} ${appText(widget.language, 'files')}',
                  style: TextStyle(
                    fontSize: 15,
                    fontWeight: FontWeight.w700,
                    color: isDark ? AppColors.darkText : AppColors.lightText,
                  ),
                ),
                const SizedBox(width: 8),
                Text(
                  '•  ${_formatSize(_totalSize)}',
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
                  onPressed: isTransferActive ? null : _pickFiles,
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
                  onPressed: isTransferActive ? null : _clearSelection,
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
                      ? const Color(0xFF1E3F35)
                      : const Color(0xFFD4EDE5),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Text(
                  appText(widget.language, 'releaseToAdd'),
                  textAlign: TextAlign.center,
                  style: TextStyle(
                    fontSize: 12,
                    fontWeight: FontWeight.w600,
                    color: isDark
                        ? AppColors.darkAccent
                        : AppColors.lightAccent,
                  ),
                ),
              ),
            ],
            const SizedBox(height: 12),
            ConstrainedBox(
              constraints: const BoxConstraints(maxHeight: 140),
              child: ListView.separated(
                shrinkWrap: true,
                itemCount: _selectedFiles.length,
                separatorBuilder: (context, index) => const SizedBox(height: 6),
                itemBuilder: (context, index) {
                  final filePath = _selectedFiles[index];
                  final fileName = filePath.split(Platform.pathSeparator).last;
                  return Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 12,
                      vertical: 8,
                    ),
                    decoration: BoxDecoration(
                      color: isDark
                          ? const Color(0xFF162520)
                          : const Color(0xFFEFF5F2),
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
                          onPressed: isTransferActive
                              ? null
                              : () {
                                  setState(() => _selectedFiles.removeAt(index));
                                  _stage();
                                },
                        ),
                      ],
                    ),
                  );
                },
              ),
            ),
          ],
        ),
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
                    ? const Color(0xFF1E3F35)
                    : const Color(0xFFD4EDE5),
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
                              ? const Color(0xFF243932)
                              : const Color(0xFFE2EBE6),
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
                      ? const Color(0xFF1E3F35)
                      : const Color(0xFFD4EDE5),
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
                      ? const Color(0xFF243932)
                      : const Color(0xFFE2EBE6),
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
