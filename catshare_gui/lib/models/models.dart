enum QuickSaveMode { off, favorites, on }

class DeviceModel {
  final String address;
  final String name;
  final String kind;
  final int rssi;
  final bool catShare;
  final String addressText;

  DeviceModel({
    required this.address,
    required this.name,
    required this.kind,
    required this.rssi,
    required this.catShare,
    required this.addressText,
  });

  factory DeviceModel.fromJson(Map<String, dynamic> json) {
    return DeviceModel(
      address: json['address']?.toString() ?? '',
      name: json['name']?.toString() ?? 'Nearby device',
      kind: json['kind']?.toString() ?? 'Mutual Transmission',
      rssi: json['rssi'] is num ? (json['rssi'] as num).toInt() : -70,
      catShare: json['catShare'] == true,
      addressText: json['addressText']?.toString() ?? '',
    );
  }
}

class EngineStatus {
  final int seq;
  final bool connected;
  final bool receiveEnabled;
  final String senderId;
  final String deviceName;
  final String saveDirectory;
  final String lanIp;
  final String mac;
  final int transferPort;
  final String state;
  final IncomingTransferOffer? pendingTransfer;

  EngineStatus({
    this.seq = 0,
    required this.connected,
    required this.receiveEnabled,
    required this.senderId,
    required this.deviceName,
    required this.saveDirectory,
    required this.lanIp,
    required this.mac,
    required this.transferPort,
    required this.state,
    this.pendingTransfer,
  });

  factory EngineStatus.initial() {
    return EngineStatus(
      seq: 0,
      connected: false,
      receiveEnabled: true,
      senderId: '',
      deviceName: 'OsharePC',
      saveDirectory: '',
      lanIp: '',
      mac: '',
      transferPort: 8959,
      state: 'Connecting to bridge...',
      pendingTransfer: null,
    );
  }

  factory EngineStatus.fromJson(Map<String, dynamic> json) {
    return EngineStatus(
      seq: json['seq'] is num ? (json['seq'] as num).toInt() : 0,
      connected: json['connected'] == true,
      receiveEnabled: json['receiveEnabled'] == true,
      senderId: json['senderId']?.toString() ?? '',
      deviceName: json['deviceName']?.toString() ?? 'OsharePC',
      saveDirectory: json['saveDirectory']?.toString() ?? '',
      lanIp: json['lanIp']?.toString() ?? '',
      mac: json['mac']?.toString() ?? '',
      transferPort: json['transferPort'] is num
          ? (json['transferPort'] as num).toInt()
          : 8959,
      state: json['state']?.toString() ?? '',
      pendingTransfer: json['pendingTransfer'] is Map
          ? IncomingTransferOffer.fromJson(
              Map<String, dynamic>.from(json['pendingTransfer']),
            )
          : null,
    );
  }
}

class IncomingTransferOffer {
  final String id;
  final String name;
  final String mimeType;
  final String count;
  final int totalBytes;

  IncomingTransferOffer({
    required this.id,
    required this.name,
    required this.mimeType,
    required this.count,
    this.totalBytes = 0,
  });

  factory IncomingTransferOffer.fromJson(Map<String, dynamic> json) {
    return IncomingTransferOffer(
      id: json['id']?.toString() ?? '',
      name: json['name']?.toString() ?? 'Nearby Phone',
      mimeType: json['mimeType']?.toString() ?? '',
      count: json['count']?.toString() ?? '1',
      totalBytes: json['totalSize'] is num
          ? (json['totalSize'] as num).toInt()
          : 0,
    );
  }
}

class TransferStateModel {
  final bool active;
  final bool isSending;
  final String targetDevice;
  final int sentBytes;
  final int totalBytes;
  final double speedBytesPerSec;
  final String statusText;
  final String phase;
  final String fileName;
  final String saveDirectory;
  final String errorText;
  final int fileCount;

  TransferStateModel({
    this.active = false,
    this.isSending = true,
    this.targetDevice = '',
    this.sentBytes = 0,
    this.totalBytes = 0,
    this.speedBytesPerSec = 0,
    this.statusText = '',
    this.phase = 'idle',
    this.fileName = '',
    this.saveDirectory = '',
    this.errorText = '',
    this.fileCount = 1,
  });

  double get progress {
    if (totalBytes <= 0) return 0.0;
    final p = sentBytes / totalBytes;
    return p.clamp(0.0, 1.0);
  }
}
