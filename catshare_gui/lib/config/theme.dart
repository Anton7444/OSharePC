import 'package:flutter/material.dart';

class AccentPreset {
  final String name;
  final Color dark;
  final Color light;

  const AccentPreset({required this.name, required this.dark, required this.light});
}

class AppColors {
  // The entire neutral palette below (backgrounds, cards, borders, even the
  // muted text tones) was originally hand-picked at a single hue (~160°,
  // teal) with varying lightness — a "seed-tinted" neutral ladder, not truly
  // gray. So swapping only the accent left the whole app shell looking green
  // no matter what accent was picked. AppColors.applyAccent rotates this
  // ladder's hue to match the chosen accent (keeping saturation/lightness
  // fixed, Material-You style) so the *whole* app recolors, not just buttons.
  static const _baseDarkBackground = Color(0xFF101916);
  static const _baseDarkSurface = Color(0xFF15221E);
  static const _baseDarkCard = Color(0xFF1B2A25);
  static const _baseDarkCardHover = Color(0xFF22352F);
  static const _baseDarkBorder = Color(0xFF283F38);
  static const _baseDarkText = Color(0xFFF2F6F4);
  static const _baseDarkTextMuted = Color(0xFF9FB0A9);
  static const _baseDarkTextSubtle = Color(0xFF6D7D76);

  static const _baseLightBackground = Color(0xFFF5F8F6);
  static const _baseLightSurface = Color(0xFFE9F0EC);
  static const _baseLightBorder = Color(0xFFD3DFDA);
  static const _baseLightText = Color(0xFF131D1A);
  static const _baseLightTextMuted = Color(0xFF5B6B65);

  static Color darkBackground = _baseDarkBackground;
  static Color darkSurface = _baseDarkSurface;
  static Color darkCard = _baseDarkCard;
  static Color darkCardHover = _baseDarkCardHover;
  static Color darkBorder = _baseDarkBorder;
  static Color darkText = _baseDarkText;
  static Color darkTextMuted = _baseDarkTextMuted;
  static Color darkTextSubtle = _baseDarkTextSubtle;

  // Accent colors are mutable so the user can pick a different app color at
  // runtime (see AppColors.applyAccent); defaults match the original teal.
  // The *Subtle/*Soft/*Strong tiers are all blends of the background with the
  // current accent — every "selected"/"highlighted" surface in the app reads
  // one of these instead of a one-off hardcoded hex, so picking a new accent
  // recolors those surfaces too instead of leaving them stuck on teal.
  static Color darkAccent = const Color(0xFF5CE0BE);
  static Color darkAccentSubtle = const Color(0xFF162923);
  static Color darkAccentSoft = const Color(0xFF1E3F35);
  static Color darkAccentStrong = const Color(0xFF28443B);
  static Color darkAccentContainer = const Color(0xFF1E3F35);
  static Color darkOnAccentContainer = const Color(0xFF86F0D4);

  // Light Theme
  static Color lightBackground = _baseLightBackground;
  static Color lightSurface = _baseLightSurface;
  static const lightCard = Color(0xFFFFFFFF);
  static Color lightBorder = _baseLightBorder;
  static Color lightText = _baseLightText;
  static Color lightTextMuted = _baseLightTextMuted;
  static Color lightAccent = const Color(0xFF0D9488);
  static Color lightAccentSubtle = const Color(0xFFE8F0EC);
  static Color lightAccentSoft = const Color(0xFFD4EDE5);
  static Color lightAccentStrong = const Color(0xFFD0EAE2);
  static Color lightAccentContainer = const Color(0xFFD4EDE5);
  static Color lightOnAccentContainer = const Color(0xFF063D35);

  static const List<AccentPreset> accentPresets = [
    AccentPreset(name: 'Teal', dark: Color(0xFF5CE0BE), light: Color(0xFF0D9488)),
    AccentPreset(name: 'Blue', dark: Color(0xFF5B9DF9), light: Color(0xFF2563EB)),
    AccentPreset(name: 'Purple', dark: Color(0xFFB18CF8), light: Color(0xFF7C3AED)),
    AccentPreset(name: 'Pink', dark: Color(0xFFF783C7), light: Color(0xFFDB2777)),
    AccentPreset(name: 'Orange', dark: Color(0xFFFFA65C), light: Color(0xFFEA580C)),
    AccentPreset(name: 'Red', dark: Color(0xFFFF7A7A), light: Color(0xFFDC2626)),
    AccentPreset(name: 'Green', dark: Color(0xFF8AE06B), light: Color(0xFF16A34A)),
    AccentPreset(name: 'Amber', dark: Color(0xFFFFD75C), light: Color(0xFFCA8A04)),
  ];

  static Color _rehue(Color base, double hue) =>
      HSLColor.fromColor(base).withHue(hue).toColor();

  static void applyAccent(AccentPreset preset) {
    final darkHue = HSLColor.fromColor(preset.dark).hue;
    final lightHue = HSLColor.fromColor(preset.light).hue;

    darkBackground = _rehue(_baseDarkBackground, darkHue);
    darkSurface = _rehue(_baseDarkSurface, darkHue);
    darkCard = _rehue(_baseDarkCard, darkHue);
    darkCardHover = _rehue(_baseDarkCardHover, darkHue);
    darkBorder = _rehue(_baseDarkBorder, darkHue);
    darkText = _rehue(_baseDarkText, darkHue);
    darkTextMuted = _rehue(_baseDarkTextMuted, darkHue);
    darkTextSubtle = _rehue(_baseDarkTextSubtle, darkHue);

    darkAccent = preset.dark;
    darkAccentSubtle = Color.lerp(darkBackground, preset.dark, 0.08)!;
    darkAccentSoft = Color.lerp(darkBackground, preset.dark, 0.18)!;
    darkAccentStrong = Color.lerp(darkBackground, preset.dark, 0.25)!;
    darkAccentContainer = darkAccentSoft;
    darkOnAccentContainer = Color.lerp(preset.dark, Colors.white, 0.35)!;

    lightBackground = _rehue(_baseLightBackground, lightHue);
    lightSurface = _rehue(_baseLightSurface, lightHue);
    lightBorder = _rehue(_baseLightBorder, lightHue);
    lightText = _rehue(_baseLightText, lightHue);
    lightTextMuted = _rehue(_baseLightTextMuted, lightHue);

    lightAccent = preset.light;
    lightAccentSubtle = Color.lerp(lightBackground, preset.light, 0.06)!;
    lightAccentSoft = Color.lerp(lightBackground, preset.light, 0.125)!;
    lightAccentStrong = Color.lerp(lightBackground, preset.light, 0.16)!;
    lightAccentContainer = lightAccentSoft;
    lightOnAccentContainer = Color.lerp(preset.light, Colors.black, 0.45)!;
  }

  /// Contrasting foreground for text/icons drawn directly on a solid
  /// [darkAccent]/[lightAccent] background (e.g. a filled button).
  static Color onAccent(bool isDark) {
    final accent = isDark ? darkAccent : lightAccent;
    return ThemeData.estimateBrightnessForColor(accent) == Brightness.dark
        ? Colors.white
        : const Color(0xFF052A20);
  }
}

class AppTheme {
  static ThemeData get darkTheme {
    return ThemeData(
      useMaterial3: true,
      brightness: Brightness.dark,
      scaffoldBackgroundColor: AppColors.darkBackground,
      fontFamily: 'Segoe UI',
      colorScheme: ColorScheme.dark(
        primary: AppColors.darkAccent,
        onPrimary: AppColors.onAccent(true),
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
          side: BorderSide(color: AppColors.darkBorder, width: 1),
        ),
      ),
      dialogTheme: DialogThemeData(
        backgroundColor: AppColors.darkSurface,
        elevation: 8,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(20),
          side: BorderSide(color: AppColors.darkBorder, width: 1),
        ),
      ),
      tooltipTheme: TooltipThemeData(
        decoration: BoxDecoration(
          color: AppColors.darkCardHover,
          borderRadius: BorderRadius.circular(8),
          border: Border.all(color: AppColors.darkBorder),
        ),
        textStyle: TextStyle(color: AppColors.darkText, fontSize: 12),
      ),
    );
  }

  static ThemeData get lightTheme {
    return ThemeData(
      useMaterial3: true,
      brightness: Brightness.light,
      scaffoldBackgroundColor: AppColors.lightBackground,
      fontFamily: 'Segoe UI',
      colorScheme: ColorScheme.light(
        primary: AppColors.lightAccent,
        onPrimary: AppColors.onAccent(false),
        primaryContainer: AppColors.lightAccentContainer,
        onPrimaryContainer: AppColors.lightOnAccentContainer,
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
          side: BorderSide(color: AppColors.lightBorder, width: 1),
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
