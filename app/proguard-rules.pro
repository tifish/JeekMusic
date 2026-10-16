# TagLib 的 JNI 层会按名字反射创建这些类，不能被 R8 改名或删除。
-keep class com.kyant.taglib.** { *; }
