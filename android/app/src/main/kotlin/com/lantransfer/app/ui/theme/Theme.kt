package com.lantransfer.app.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import com.lantransfer.app.platform.ThemeMode
import com.lantransfer.protocol.DeviceType

/** 与 shared/design-tokens.json 及 Windows 端完全一致的语义色板。 */
@Immutable
data class LanSemanticColors(
    val canvas: Color,
    val surface: Color,
    val surfaceSubtle: Color,
    val border: Color,
    val accent: Color,
    val accentSoft: Color,
    val success: Color,
    val successSoft: Color,
    val warning: Color,
    val warningSoft: Color,
    val danger: Color,
    val dangerSoft: Color,
    val textPrimary: Color,
    val textSecondary: Color,
    val textMuted: Color,
    val bubbleOut: Color,
    val bubbleOutText: Color,
    val bubbleIn: Color,
    val bubbleInText: Color,
    val androidBadge: Color,
    val windowsBadge: Color,
    val iosBadge: Color,
) {
    fun deviceAccent(type: DeviceType): Color = when (type) {
        DeviceType.ANDROID -> androidBadge
        DeviceType.WINDOWS -> windowsBadge
        DeviceType.IOS -> iosBadge
    }
}

private val LightSemantic = LanSemanticColors(
    canvas = Color(0xFFF6F8FC),
    surface = Color(0xFFFFFFFF),
    surfaceSubtle = Color(0xFFF1F5F9),
    border = Color(0xFFE2E8F0),
    accent = Color(0xFF2563EB),
    accentSoft = Color(0xFFEFF6FF),
    success = Color(0xFF10B981),
    successSoft = Color(0xFFECFDF5),
    warning = Color(0xFFF59E0B),
    warningSoft = Color(0xFFFFFBEB),
    danger = Color(0xFFEF4444),
    dangerSoft = Color(0xFFFEF2F2),
    textPrimary = Color(0xFF0F172A),
    textSecondary = Color(0xFF475569),
    textMuted = Color(0xFF64748B),
    bubbleOut = Color(0xFF2563EB),
    bubbleOutText = Color(0xFFFFFFFF),
    bubbleIn = Color(0xFFFFFFFF),
    bubbleInText = Color(0xFF0F172A),
    androidBadge = Color(0xFF10B981),
    windowsBadge = Color(0xFF2563EB),
    iosBadge = Color(0xFF6366F1),
)

private val DarkSemantic = LanSemanticColors(
    canvas = Color(0xFF0B1120),
    surface = Color(0xFF131C31),
    surfaceSubtle = Color(0xFF1B2742),
    border = Color(0xFF24324E),
    accent = Color(0xFF60A5FA),
    accentSoft = Color(0xFF172554),
    success = Color(0xFF34D399),
    successSoft = Color(0xFF064E3B),
    warning = Color(0xFFFBBF24),
    warningSoft = Color(0xFF451A03),
    danger = Color(0xFFF87171),
    dangerSoft = Color(0xFF450A0A),
    textPrimary = Color(0xFFF1F5F9),
    textSecondary = Color(0xFFCBD5E1),
    textMuted = Color(0xFF94A3B8),
    bubbleOut = Color(0xFF2563EB),
    bubbleOutText = Color(0xFFFFFFFF),
    bubbleIn = Color(0xFF1E293B),
    bubbleInText = Color(0xFFF1F5F9),
    androidBadge = Color(0xFF34D399),
    windowsBadge = Color(0xFF60A5FA),
    iosBadge = Color(0xFF818CF8),
)

private val LightColorScheme = lightColorScheme(
    primary = Color(0xFF2563EB),
    onPrimary = Color(0xFFFFFFFF),
    primaryContainer = Color(0xFFEFF6FF),
    onPrimaryContainer = Color(0xFF1E3A8A),
    secondary = Color(0xFF0EA5E9),
    onSecondary = Color(0xFFFFFFFF),
    secondaryContainer = Color(0xFFE0F2FE),
    onSecondaryContainer = Color(0xFF0C4A6E),
    background = Color(0xFFF6F8FC),
    onBackground = Color(0xFF0F172A),
    surface = Color(0xFFFFFFFF),
    onSurface = Color(0xFF0F172A),
    surfaceVariant = Color(0xFFF1F5F9),
    onSurfaceVariant = Color(0xFF475569),
    outline = Color(0xFFCBD5E1),
    outlineVariant = Color(0xFFE2E8F0),
    error = Color(0xFFEF4444),
    onError = Color(0xFFFFFFFF),
    errorContainer = Color(0xFFFEF2F2),
    onErrorContainer = Color(0xFF991B1B),
)

private val DarkColorScheme = darkColorScheme(
    primary = Color(0xFF60A5FA),
    onPrimary = Color(0xFF0B1120),
    primaryContainer = Color(0xFF172554),
    onPrimaryContainer = Color(0xFFDBEAFE),
    secondary = Color(0xFF38BDF8),
    onSecondary = Color(0xFF0B1120),
    secondaryContainer = Color(0xFF0C4A6E),
    onSecondaryContainer = Color(0xFFE0F2FE),
    background = Color(0xFF0B1120),
    onBackground = Color(0xFFF1F5F9),
    surface = Color(0xFF131C31),
    onSurface = Color(0xFFF1F5F9),
    surfaceVariant = Color(0xFF1B2742),
    onSurfaceVariant = Color(0xFFCBD5E1),
    outline = Color(0xFF334155),
    outlineVariant = Color(0xFF24324E),
    error = Color(0xFFF87171),
    onError = Color(0xFF450A0A),
    errorContainer = Color(0xFF450A0A),
    onErrorContainer = Color(0xFFFECACA),
)

private val LanTypography = Typography(
    headlineSmall = TextStyle(fontWeight = FontWeight.Bold, fontSize = 22.sp, lineHeight = 28.sp),
    titleLarge = TextStyle(fontWeight = FontWeight.Bold, fontSize = 19.sp, lineHeight = 25.sp),
    titleMedium = TextStyle(fontWeight = FontWeight.SemiBold, fontSize = 16.sp, lineHeight = 22.sp),
    titleSmall = TextStyle(fontWeight = FontWeight.SemiBold, fontSize = 14.sp, lineHeight = 20.sp),
    bodyLarge = TextStyle(fontWeight = FontWeight.Normal, fontSize = 15.sp, lineHeight = 22.sp),
    bodyMedium = TextStyle(fontWeight = FontWeight.Normal, fontSize = 14.sp, lineHeight = 20.sp),
    bodySmall = TextStyle(fontWeight = FontWeight.Normal, fontSize = 12.sp, lineHeight = 17.sp),
    labelLarge = TextStyle(fontWeight = FontWeight.SemiBold, fontSize = 14.sp, lineHeight = 18.sp),
    labelMedium = TextStyle(fontWeight = FontWeight.SemiBold, fontSize = 12.sp, lineHeight = 16.sp),
    labelSmall = TextStyle(fontWeight = FontWeight.Medium, fontSize = 11.sp, lineHeight = 14.sp),
)

val MonoTextStyle = TextStyle(
    fontFamily = FontFamily.Monospace,
    fontFeatureSettings = "tnum",
)

val LocalLanColors = staticCompositionLocalOf { LightSemantic }

@Composable
fun LanTransferTheme(
    themeMode: ThemeMode,
    content: @Composable () -> Unit,
) {
    val dark = when (themeMode) {
        ThemeMode.DARK -> true
        ThemeMode.LIGHT -> false
        ThemeMode.SYSTEM -> isSystemInDarkTheme()
    }
    val colors = if (dark) DarkSemantic else LightSemantic
    val scheme = if (dark) DarkColorScheme else LightColorScheme

    CompositionLocalProvider(LocalLanColors provides colors) {
        MaterialTheme(
            colorScheme = scheme,
            typography = LanTypography,
            content = content,
        )
    }
}
