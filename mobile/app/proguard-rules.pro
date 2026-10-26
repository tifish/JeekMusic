# TagLib 的 JNI 层会按名字反射创建这些类，不能被 R8 改名或删除。
-keep class com.kyant.taglib.** { *; }

# ML Kit discovers these manifest registrars via Class.getDeclaredConstructor().
# AGP 9 / R8 full mode removes their implicit no-arg constructors without this rule.
-keep class com.google.mlkit.** implements com.google.firebase.components.ComponentRegistrar {
    public <init>();
}
