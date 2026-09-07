import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

class DragDropService {
  static final DragDropService instance = DragDropService._();
  DragDropService._();

  static const MethodChannel _channel = MethodChannel('catshare/drag_drop');

  final List<void Function(bool isDragging)> _dragStateListeners = [];
  final List<void Function(List<String> paths)> _dropListeners = [];
  bool _initialized = false;
  bool _isDragging = false;

  bool get isDragging => _isDragging;

  void init() {
    if (_initialized) return;
    _initialized = true;
    _channel.setMethodCallHandler((call) async {
      switch (call.method) {
        case 'entered':
        case 'updated':
          _isDragging = true;
          for (final l in List.of(_dragStateListeners)) {
            l(true);
          }
          break;
        case 'exited':
          _isDragging = false;
          for (final l in List.of(_dragStateListeners)) {
            l(false);
          }
          break;
        case 'dropped':
          _isDragging = false;
          for (final l in List.of(_dragStateListeners)) {
            l(false);
          }
          final args = call.arguments;
          List<String> paths = [];
          if (args is Map && args['paths'] is List) {
            paths = (args['paths'] as List).map((e) => e.toString()).toList();
          }
          debugPrint('[DragDropService] Dropped ${paths.length} items');
          for (final l in List.of(_dropListeners)) {
            l(paths);
          }
          break;
      }
    });
  }

  void addDragStateListener(void Function(bool) listener) =>
      _dragStateListeners.add(listener);
  void removeDragStateListener(void Function(bool) listener) =>
      _dragStateListeners.remove(listener);

  void addDropListener(void Function(List<String>) listener) =>
      _dropListeners.add(listener);
  void removeDropListener(void Function(List<String>) listener) =>
      _dropListeners.remove(listener);
}

class NativeDropZone extends StatefulWidget {
  final Widget child;
  final bool enabled;
  final ValueChanged<bool>? onDragStateChanged;
  final ValueChanged<List<String>>? onDropped;

  const NativeDropZone({
    super.key,
    required this.child,
    this.enabled = true,
    this.onDragStateChanged,
    this.onDropped,
  });

  @override
  State<NativeDropZone> createState() => _NativeDropZoneState();
}

class _NativeDropZoneState extends State<NativeDropZone> {
  @override
  void initState() {
    super.initState();
    DragDropService.instance.init();
    if (widget.enabled) {
      _attach();
    }
  }

  @override
  void didUpdateWidget(NativeDropZone oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.enabled && !oldWidget.enabled) {
      _attach();
    } else if (!widget.enabled && oldWidget.enabled) {
      _detach();
      widget.onDragStateChanged?.call(false);
    }
  }

  void _attach() {
    DragDropService.instance.addDragStateListener(_onDragState);
    DragDropService.instance.addDropListener(_onDrop);
  }

  void _detach() {
    DragDropService.instance.removeDragStateListener(_onDragState);
    DragDropService.instance.removeDropListener(_onDrop);
  }

  void _onDragState(bool isDragging) {
    if (!mounted || !widget.enabled) return;
    widget.onDragStateChanged?.call(isDragging);
  }

  void _onDrop(List<String> paths) {
    if (!mounted || !widget.enabled) return;
    widget.onDropped?.call(paths);
  }

  @override
  void dispose() {
    if (widget.enabled) {
      _detach();
    }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return widget.child;
  }
}