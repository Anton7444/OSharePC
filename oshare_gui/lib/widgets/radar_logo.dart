import 'dart:math' as math;
import 'package:flutter/material.dart';
import '../config/theme.dart';

class RadarLogo extends StatefulWidget {
  final double size;
  final bool active;

  const RadarLogo({
    super.key,
    this.size = 140,
    this.active = true,
  });

  @override
  State<RadarLogo> createState() => _RadarLogoState();
}

class _RadarLogoState extends State<RadarLogo> with SingleTickerProviderStateMixin {
  late final AnimationController _controller;

  @override
  void initState() {
    super.initState();
    _controller = AnimationController(
      vsync: this,
      duration: const Duration(seconds: 12),
    );
    if (widget.active) {
      _controller.repeat();
    }
  }

  @override
  void didUpdateWidget(RadarLogo oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.active && !_controller.isAnimating) {
      _controller.repeat();
    } else if (!widget.active && _controller.isAnimating) {
      _controller.stop();
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final isDark = theme.brightness == Brightness.dark;
    final primary = widget.active
        ? (isDark ? AppColors.darkAccent : AppColors.lightAccent)
        : (isDark ? AppColors.darkTextSubtle : AppColors.lightTextMuted);

    return AnimatedBuilder(
      animation: _controller,
      builder: (context, child) {
        return SizedBox(
          width: widget.size,
          height: widget.size,
          child: CustomPaint(
            painter: _RadarPainter(
              rotation: _controller.value * 2 * math.pi,
              color: primary,
              active: widget.active,
            ),
          ),
        );
      },
    );
  }
}

class _RadarPainter extends CustomPainter {
  final double rotation;
  final Color color;
  final bool active;

  _RadarPainter({
    required this.rotation,
    required this.color,
    required this.active,
  });

  @override
  void paint(Canvas canvas, Size size) {
    final center = Offset(size.width / 2, size.height / 2);
    final radius = size.width / 2;

    // 1. Center solid circle
    final centerPaint = Paint()
      ..color = color
      ..style = PaintingStyle.fill;
    canvas.drawCircle(center, radius * 0.42, centerPaint);

    // 2. Outer orbiting segmented arcs
    const segmentCount = 7;
    final outerRadius = radius * 0.82;
    final strokeWidth = radius * 0.16;

    final arcPaint = Paint()
      ..color = color.withValues(alpha: active ? 0.95 : 0.4)
      ..style = PaintingStyle.stroke
      ..strokeCap = StrokeCap.round
      ..strokeWidth = strokeWidth;

    final sweepAngle = (2 * math.pi / segmentCount) * 0.52;
    final stepAngle = 2 * math.pi / segmentCount;

    for (int i = 0; i < segmentCount; i++) {
      final startAngle = rotation + (i * stepAngle);
      canvas.drawArc(
        Rect.fromCircle(center: center, radius: outerRadius),
        startAngle,
        sweepAngle,
        false,
        arcPaint,
      );
    }
  }

  @override
  bool shouldRepaint(_RadarPainter oldDelegate) {
    return oldDelegate.rotation != rotation ||
        oldDelegate.color != color ||
        oldDelegate.active != active;
  }
}
