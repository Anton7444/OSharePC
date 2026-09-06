import 'package:flutter/material.dart';

class AppColors {
  // Dark Theme (LocalSend Slate-Teal aesthetic)
  static const darkBackground = Color(0xFF101916);
  static const darkSurface = Color(0xFF15221E);
  static const darkCard = Color(0xFF1B2A25);
  static const darkCardHover = Color(0xFF22352F);
  static const darkBorder = Color(0xFF283F38);

  static const darkAccent = Color(0xFF5CE0BE);
  static const darkAccentContainer = Color(0xFF1D3E35);
  static const darkOnAccentContainer = Color(0xFF86F0D4);

  static const darkText = Color(0xFFF2F6F4);
  static const darkTextMuted = Color(0xFF9FB0A9);
  static const darkTextSubtle = Color(0xFF6D7D76);

  // Light Theme
  static const lightBackground = Color(0xFFF5F8F6);
  static const lightSurface = Color(0xFFE9F0EC);
  static const lightCard = Color(0xFFFFFFFF);
  static const lightBorder = Color(0xFFD3DFDA);
  static const lightAccent = Color(0xFF0D9488);
  static const lightText = Color(0xFF131D1A);
  static const lightTextMuted = Color(0xFF5B6B65);
}

class AppTheme {
  static ThemeData get darkTheme {
    return ThemeData(
      useMaterial3: true,
      brightness: Brightness.dark,
      scaffoldBackgroundColor: AppColors.darkBackground,
      fontFamily: 'Segoe UI',
      colorScheme: const ColorScheme.dark(
        primary: AppColors.darkAccent,
        onPrimary: Color(0xFF052A20),
        primaryContainer: AppColors.darkAccentContainer,
        onPrimaryContainer: AppColors.darkOnAccentContainer,
        surface: AppColors.darkSurface,
        onSurface: AppColors.darkText,
        surfaceContainer: AppColors.darkCard,
        surfaceContainerHigh: AppColors.darkCardHover,
        outline: AppColors.darkBorder,
      ),
      cardTheme: CardThemeData(
        color: AppColors.darkCard,
        elevation: 0,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: const BorderSide(color: AppColors.darkBorder, width: 1),
        ),
      ),
      dialogTheme: DialogThemeData(
        backgroundColor: AppColors.darkSurface,
        elevation: 8,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(20),
          side: const BorderSide(color: AppColors.darkBorder, width: 1),
        ),
      ),
      tooltipTheme: TooltipThemeData(
        decoration: BoxDecoration(
          color: AppColors.darkCardHover,
          borderRadius: BorderRadius.circular(8),
          border: Border.all(color: AppColors.darkBorder),
        ),
        textStyle: const TextStyle(color: AppColors.darkText, fontSize: 12),
      ),
    );
  }

  static ThemeData get lightTheme {
    return ThemeData(
      useMaterial3: true,
      brightness: Brightness.light,
      scaffoldBackgroundColor: AppColors.lightBackground,
      fontFamily: 'Segoe UI',
      colorScheme: const ColorScheme.light(
        primary: AppColors.lightAccent,
        onPrimary: Colors.white,
        surface: AppColors.lightSurface,
        onSurface: AppColors.lightText,
        surfaceContainer: AppColors.lightCard,
        outline: AppColors.lightBorder,
      ),
      cardTheme: CardThemeData(
        color: AppColors.lightCard,
        elevation: 0,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: const BorderSide(color: AppColors.lightBorder, width: 1),
        ),
      ),
      dialogTheme: DialogThemeData(
        backgroundColor: AppColors.lightSurface,
        elevation: 8,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(20),
        ),
      ),
    );
  }
}
