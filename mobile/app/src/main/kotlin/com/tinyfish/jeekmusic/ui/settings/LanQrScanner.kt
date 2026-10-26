package com.tinyfish.jeekmusic.ui.settings

import android.annotation.SuppressLint
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.BarcodeScannerOptions
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.common.InputImage
import com.tinyfish.jeekmusic.R
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/** Only camera frames enter this production scanner. Never log barcode values or save frames. */
@SuppressLint("UnsafeOptInUsageError")
@Composable
internal fun LanQrScanner(onResult: (String) -> Unit, onCancel: () -> Unit, onFailure: () -> Unit) {
    val context = LocalContext.current
    val owner = LocalLifecycleOwner.current
    val preview = remember { PreviewView(context).apply { implementationMode = PreviewView.ImplementationMode.COMPATIBLE; scaleType = PreviewView.ScaleType.FIT_CENTER } }
    val resultCallback by rememberUpdatedState(onResult)
    val failureCallback by rememberUpdatedState(onFailure)
    DisposableEffect(owner) {
        val active = AtomicBoolean(true)
        val delivered = AtomicBoolean(false)
        val main = ContextCompat.getMainExecutor(context)
        val scanner = try { BarcodeScanning.getClient(BarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).build()) }
        catch (_: Exception) {
            failureCallback()
            return@DisposableEffect onDispose { active.set(false) }
        }
        val executor = Executors.newSingleThreadExecutor()
        val analysis = ImageAnalysis.Builder().setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST).build()
        val cameraPreview = Preview.Builder().build().also { it.setSurfaceProvider(preview.surfaceProvider) }
        val future = ProcessCameraProvider.getInstance(context)
        var provider: ProcessCameraProvider? = null
        fun fail() { if (active.get() && delivered.compareAndSet(false, true)) failureCallback() }
        analysis.setAnalyzer(executor) { proxy ->
            val image = proxy.image
            if (!active.get() || delivered.get() || image == null) { proxy.close(); return@setAnalyzer }
            try {
                scanner.process(InputImage.fromMediaImage(image, proxy.imageInfo.rotationDegrees))
                    .addOnSuccessListener(main) { codes ->
                        val raw = codes.firstOrNull { it.rawValue != null }?.rawValue
                        if (active.get() && raw != null && delivered.compareAndSet(false, true)) resultCallback(raw)
                    }
                    .addOnFailureListener(main) { fail() }
                    .addOnCompleteListener { proxy.close() }
            } catch (_: Exception) { proxy.close(); main.execute { fail() } }
        }
        future.addListener({
            if (active.get()) try {
                provider = future.get()
                provider!!.bindToLifecycle(owner, CameraSelector.DEFAULT_BACK_CAMERA, cameraPreview, analysis)
            } catch (_: Exception) { fail() }
        }, main)
        onDispose {
            active.set(false)
            analysis.clearAnalyzer()
            provider?.unbind(cameraPreview, analysis)
            executor.shutdown()
            scanner.close()
        }
    }
    AlertDialog(onDismissRequest = onCancel, title = { Text(stringResource(R.string.lan_scan)) },
        text = { Column {
            Text(stringResource(R.string.lan_scan_hint))
            Spacer(Modifier.height(8.dp))
            AndroidView(factory = { preview }, modifier = Modifier.fillMaxWidth().height(320.dp))
        } }, confirmButton = {}, dismissButton = { TextButton(onClick = onCancel) { Text(stringResource(R.string.cancel)) } })
}
