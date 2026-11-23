package com.lantransfer.app

import android.content.Context
import android.content.res.Configuration
import java.util.Locale

/** 强制简体中文区域配置（需求 1.5、设计 C6.5），不随系统语言切换。 */
object LocaleHelper {
    private val zhCn: Locale = Locale.SIMPLIFIED_CHINESE

    fun wrap(base: Context): Context {
        Locale.setDefault(zhCn)
        val config = Configuration(base.resources.configuration)
        config.setLocale(zhCn)
        return base.createConfigurationContext(config)
    }
}
