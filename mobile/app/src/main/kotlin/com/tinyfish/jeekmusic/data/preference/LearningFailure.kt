package com.tinyfish.jeekmusic.data.preference

import com.tinyfish.jeekmusic.R
import org.json.JSONObject

/** A closed diagnostic vocabulary. Remote exception text and arbitrary fields are never displayed/copied. */
object LearningFailure {
    val resources = mapOf(
        "dependency_missing" to (R.string.learning_failure_dependency_missing_message to R.string.learning_failure_dependency_missing_next),
        "download_failed" to (R.string.learning_failure_download_failed_message to R.string.learning_failure_download_failed_next),
        "download_integrity" to (R.string.learning_failure_download_integrity_message to R.string.learning_failure_download_integrity_next),
        "recording_missing" to (R.string.learning_failure_recording_missing_message to R.string.learning_failure_recording_missing_next),
        "decode_unsupported" to (R.string.learning_failure_decode_unsupported_message to R.string.learning_failure_decode_unsupported_next),
        "decode_length" to (R.string.learning_failure_decode_length_message to R.string.learning_failure_decode_length_next),
        "decode_nonfinite" to (R.string.learning_failure_decode_nonfinite_message to R.string.learning_failure_decode_nonfinite_next),
        "silent_audio" to (R.string.learning_failure_silent_audio_message to R.string.learning_failure_silent_audio_next),
        "recording_changed" to (R.string.learning_failure_recording_changed_message to R.string.learning_failure_recording_changed_next),
        "memory_budget" to (R.string.learning_failure_memory_budget_message to R.string.learning_failure_memory_budget_next),
        "time_budget" to (R.string.learning_failure_time_budget_message to R.string.learning_failure_time_budget_next),
        "worker_exit" to (R.string.learning_failure_worker_exit_message to R.string.learning_failure_worker_exit_next),
        "cancelled" to (R.string.learning_failure_cancelled_message to R.string.learning_failure_cancelled_next),
        "interrupted" to (R.string.learning_failure_interrupted_message to R.string.learning_failure_interrupted_next),
        "insufficient_material" to (R.string.learning_failure_insufficient_material_message to R.string.learning_failure_insufficient_material_next),
        "validation_failed" to (R.string.learning_failure_validation_failed_message to R.string.learning_failure_validation_failed_next),
        "check_material_required" to (R.string.learning_failure_check_material_required_message to R.string.learning_failure_check_material_required_next),
        "disk_space" to (R.string.learning_failure_disk_space_message to R.string.learning_failure_disk_space_next),
        "work_budget" to (R.string.learning_failure_work_budget_message to R.string.learning_failure_work_budget_next),
        "invalidated" to (R.string.learning_failure_invalidated_message to R.string.learning_failure_invalidated_next),
        "local_io" to (R.string.learning_failure_local_io_message to R.string.learning_failure_local_io_next),
        "history_unavailable" to (R.string.learning_failure_history_unavailable_message to R.string.learning_failure_history_unavailable_next),
        "unexpected_failure" to (R.string.learning_failure_unexpected_failure_message to R.string.learning_failure_unexpected_failure_next)
    )
    private val stages=setOf("prepare","extract","train","evaluate","update_library","check_material","orchestrate")
    private val dimensions=setOf("MOOD","VOICE","GENRE","CALMNESS")
    private val types=setOf("ValueError","TimeoutError","MemoryError","FileNotFoundError","CalledProcessError","ModuleNotFoundError","ImportError","OSError","Win32Exception","InvalidOperationException","IOException","JsonException")
    fun sanitize(raw: JSONObject?): JSONObject? {
        if(raw==null) return null
        val code=raw.optString("code").takeIf { it in resources } ?: "unexpected_failure"
        val result=JSONObject().put("version",1).put("code",code).put("stage",raw.optString("stage").takeIf { it in stages } ?: "orchestrate")
            .put("workerPhase",raw.optString("workerPhase").takeIf { it in setOf("startup","download","install","model_load","recording_check","decode","embedding","prepare","extract","train","evaluate") } ?: JSONObject.NULL)
            .put("dimension",raw.optString("dimension").takeIf { it in dimensions } ?: JSONObject.NULL)
            .put("workerErrorType",raw.optString("workerErrorType").takeIf { it in types } ?: JSONObject.NULL).put("historical",raw.optBoolean("historical"))
        for(key in listOf("exitCode","expectedSamples","actualSamples","occurredAt")) {
            val value=raw.opt(key) as? Number
            val n=value?.toLong()
            if(n!=null && when(key) { "exitCode"->n in Int.MIN_VALUE.toLong()..Int.MAX_VALUE.toLong(); "occurredAt"->n in 1..4102444799999L; else->n in 0..100000000 }) result.put(key,n)
        }
        return result
    }
    fun diagnostics(raw: JSONObject): String {
        val safe=sanitize(raw)!!
        return listOf("version","code","stage","workerPhase","dimension","exitCode","workerErrorType","historical","expectedSamples","actualSamples","occurredAt").joinToString("\n") { "$it=${safe.opt(it)?.takeUnless { v -> v==JSONObject.NULL } ?: "unknown"}" }
    }
}
