import 'package:flutter/material.dart';
import '../config/theme.dart';

class CustomSegmentedButton<T> extends StatelessWidget {
  final List<T> values;
  final List<String> labels;
  final T selected;
  final ValueChanged<T> onSelected;

  const CustomSegmentedButton({
    super.key,
    required this.values,
    required this.labels,
    required this.selected,
    required this.onSelected,
  }) : assert(values.length == labels.length);

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final isDark = theme.brightness == Brightness.dark;
    final borderColor = isDark ? AppColors.darkBorder : AppColors.lightBorder;
    final activeBg = isDark ? AppColors.darkAccentStrong : AppColors.lightAccentStrong;
    final activeFg = isDark ? AppColors.darkAccent : AppColors.lightAccent;
    final inactiveFg = isDark ? AppColors.darkTextMuted : AppColors.lightTextMuted;

    return Container(
      decoration: BoxDecoration(
        color: isDark ? AppColors.darkAccentSubtle : AppColors.lightAccentSubtle,
        borderRadius: BorderRadius.circular(24),
        border: Border.all(color: borderColor, width: 1),
      ),
      padding: const EdgeInsets.all(3),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: List.generate(values.length, (index) {
          final val = values[index];
          final isSelected = val == selected;
          final isFirst = index == 0;
          final isLast = index == values.length - 1;

          return GestureDetector(
            onTap: () => onSelected(val),
            child: AnimatedContainer(
              duration: const Duration(milliseconds: 200),
              curve: Curves.easeInOut,
              padding: const EdgeInsets.symmetric(horizontal: 22, vertical: 8),
              decoration: BoxDecoration(
                color: isSelected ? activeBg : Colors.transparent,
                borderRadius: BorderRadius.horizontal(
                  left: Radius.circular(isFirst ? 20 : (isSelected ? 16 : 0)),
                  right: Radius.circular(isLast ? 20 : (isSelected ? 16 : 0)),
                ),
              ),
              child: Text(
                labels[index],
                style: TextStyle(
                  color: isSelected ? activeFg : inactiveFg,
                  fontSize: 13,
                  fontWeight: isSelected ? FontWeight.w600 : FontWeight.w500,
                ),
              ),
            ),
          );
        }),
      ),
    );
  }
}
