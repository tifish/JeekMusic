package com.tinyfish.jeekmusic

import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * 读取两端共用的测试用例（仓库根目录的 shared/testcases/，desktop/docs/design.md §3）。
 * 路径由 app/build.gradle.kts 的 testOptions 通过系统属性 jeek.testcases 传进来。
 */
object SharedCases {
    private val dir: File by lazy {
        System.getProperty("jeek.testcases")?.let(::File) ?: File("../../shared/testcases")
    }

    fun load(name: String): JSONObject = JSONObject(File(dir, name).readText(Charsets.UTF_8))
}

fun JSONObject.cases(key: String = "cases"): List<JSONObject> = getJSONArray(key).objects()

fun JSONArray.objects(): List<JSONObject> = (0 until length()).map { getJSONObject(it) }

fun JSONArray.strings(): List<String> = (0 until length()).map { getString(it) }

fun JSONObject.description(): String = optString("description")

/** 可以为 null 的字符串字段（JSON 里写 null 或没有这个字段）。 */
fun JSONObject.str(key: String): String? = if (isNull(key)) null else getString(key)

/** 可以为 null 的数字字段。 */
fun JSONObject.dbl(key: String): Double? = if (isNull(key)) null else getDouble(key)

fun JSONObject.flag(key: String): Boolean = optBoolean(key, false)

/** "lines" 按 \n 连接。 */
fun JSONObject.text(): String = getJSONArray("lines").strings().joinToString("\n")
