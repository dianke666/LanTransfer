# LanTransfer Release R8/ProGuard 规则（任务 25.1）
-keepattributes *Annotation*
-keep class com.lantransfer.app.storage.** { *; }
-dontwarn org.bouncycastle.**
