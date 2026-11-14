#include <jni.h>
#include <whisper.h>
#include <atomic>
#include <cmath>
#include <memory>
#include <string>
#include <vector>
#include <time.h>

extern "C" int jeek_whisper_detect(whisper_context *, float *, ggml_abort_callback, void *);
struct Session { whisper_context *ctx; std::atomic<bool> cancelled{false}; int64_t deadline = 0; };
static int64_t now_ms() { timespec ts{}; clock_gettime(CLOCK_BOOTTIME, &ts); return ts.tv_sec * 1000LL + ts.tv_nsec / 1000000; }
static bool aborted(void *p) { auto *s = static_cast<Session *>(p); return s->cancelled.load() || now_ms() >= s->deadline; }
static void fail(JNIEnv *env, const char *message) { env->ThrowNew(env->FindClass("java/lang/IllegalStateException"), message); }
// Count Unicode letters using the same Character definition as the Kotlin/.NET gate.
static int letters(JNIEnv *env, const char *text) {
    auto cls = env->FindClass("java/lang/Character");
    auto method = env->GetStaticMethodID(cls, "isLetter", "(I)Z");
    int count = 0;
    const auto *p = reinterpret_cast<const unsigned char *>(text);
    while (*p) {
        unsigned int cp = *p++;
        int remaining = 0;
        if (cp >= 0xf0) { cp &= 7; remaining = 3; }
        else if (cp >= 0xe0) { cp &= 15; remaining = 2; }
        else if (cp >= 0xc0) { cp &= 31; remaining = 1; }
        while (remaining-- > 0 && (*p & 0xc0) == 0x80) cp = (cp << 6) | (*p++ & 63);
        if (env->CallStaticBooleanMethod(cls, method, static_cast<jint>(cp))) ++count;
    }
    env->DeleteLocalRef(cls);
    return count;
}
extern "C" JNIEXPORT jlong JNICALL Java_com_tinyfish_jeekmusic_analysis_NativeWhisper_open(JNIEnv *env, jobject, jstring path) {
    const char *name = env->GetStringUTFChars(path, nullptr);
    auto params = whisper_context_default_params(); params.use_gpu = false;
    auto *ctx = whisper_init_from_file_with_params(name, params);
    env->ReleaseStringUTFChars(path, name);
    if (!ctx) { fail(env, "Cannot load vocal-language model"); return 0; }
    return reinterpret_cast<jlong>(new Session{ctx});
}
extern "C" JNIEXPORT jstring JNICALL Java_com_tinyfish_jeekmusic_analysis_NativeWhisper_analyze(JNIEnv *env, jobject, jlong handle, jfloatArray input, jlong deadline) {
    auto *s = reinterpret_cast<Session *>(handle); s->deadline = deadline;
    const int size = env->GetArrayLength(input);
    if (size < 48000 || size > 320000) { fail(env, "Invalid language sample length"); return nullptr; }
    std::vector<float> pcm(size); env->GetFloatArrayRegion(input, 0, size, pcm.data());
    if (aborted(s)) { fail(env, "Language recognition cancelled or timed out"); return nullptr; }
    if (whisper_pcm_to_mel(s->ctx, pcm.data(), size, 2) != 0) { fail(env, "Cannot encode language sample"); return nullptr; }
    std::vector<float> probs(whisper_lang_max_id() + 1);
    const int lang = jeek_whisper_detect(s->ctx, probs.data(), aborted, s);
    if (lang < 0 || aborted(s)) { fail(env, "Language detection failed or cancelled"); return nullptr; }
    const char *language = whisper_lang_str(lang);
    const float probability = probs[lang];
    int supported = 0;
    if (std::isfinite(probability) && probability >= .6f) {
        auto params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
        params.n_threads = 2; params.language = language; params.no_context = true;
        params.print_realtime = params.print_progress = params.print_timestamps = params.print_special = false;
        params.temperature = params.temperature_inc = 0; params.no_speech_thold = .6f;
        params.abort_callback = aborted; params.abort_callback_user_data = s;
        if (whisper_full(s->ctx, params, pcm.data(), size) != 0 || aborted(s)) { fail(env, "Language transcription failed or cancelled"); return nullptr; }
        for (int i = 0; i < whisper_full_n_segments(s->ctx); ++i) {
            double log_prob = 0; int tokens = 0;
            for (int t = 0; t < whisper_full_n_tokens(s->ctx, i); ++t) {
                if (whisper_full_get_token_id(s->ctx, i, t) >= whisper_token_eot(s->ctx)) continue;
                float p = whisper_full_get_token_p(s->ctx, i, t);
                if (std::isfinite(p)) { log_prob += std::log(std::max(p, 1e-20f)); ++tokens; }
            }
            if (tokens && std::exp(log_prob / tokens) >= .25 && whisper_full_get_segment_no_speech_prob(s->ctx, i) < .6f)
                supported += letters(env, whisper_full_get_segment_text(s->ctx, i));
        }
    }
    const auto result = std::string(language) + "\t" + std::to_string(probability) + "\t" + std::to_string(supported);
    return env->NewStringUTF(result.c_str());
}
extern "C" JNIEXPORT void JNICALL Java_com_tinyfish_jeekmusic_analysis_NativeWhisper_cancelNative(JNIEnv *, jobject, jlong handle) { reinterpret_cast<Session *>(handle)->cancelled.store(true); }
extern "C" JNIEXPORT void JNICALL Java_com_tinyfish_jeekmusic_analysis_NativeWhisper_free(JNIEnv *, jobject, jlong handle) { auto *s = reinterpret_cast<Session *>(handle); whisper_free(s->ctx); delete s; }
