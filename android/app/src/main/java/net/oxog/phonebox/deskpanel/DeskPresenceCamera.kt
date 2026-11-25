package net.oxog.phonebox.deskpanel

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Bitmap
import android.graphics.ImageFormat
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.media.FaceDetector
import android.media.Image
import android.media.ImageReader
import android.os.Handler
import android.os.HandlerThread
import android.view.Surface

/** Single front-camera samples. All camera state and frame buffers belong to one worker thread. */
internal class DeskPresenceCamera(
    context: Context,
    private val rotation: () -> Int,
    private val report: (String) -> Unit,
) {
    private val manager = context.getSystemService(Context.CAMERA_SERVICE) as CameraManager?
    private val thread = HandlerThread("desk-presence").apply { start() }
    private val handler = Handler(thread.looper)
    private var enabled = false
    private var closing = false
    private var opening = false
    private val closingDevices = mutableSetOf<CameraDevice>()
    private var releaseId = 0
    private var generation = 0
    private var reader: ImageReader? = null
    private var device: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var request: CaptureRequest? = null
    private var sensorOrientation = 0
    private var sampleNumber = 0
    private val available = mutableSetOf<String>()
    private val availability = object : CameraManager.AvailabilityCallback() {
        override fun onCameraAvailable(cameraId: String) { if (enabled) available.add(cameraId) }
        override fun onCameraUnavailable(cameraId: String) { available.remove(cameraId) }
    }
    private var bitmap: Bitmap? = null
    private var pixels = IntArray(0)
    private var detector: FaceDetector? = null
    private val faces = arrayOfNulls<FaceDetector.Face>(3)

    private val reopen = Runnable { open() }
    private val timeout = Runnable { retry() }
    private val take = Runnable {
        val target = session
        val sample = request
        if (enabled && target != null && sample != null) {
            try {
                target.capture(sample, null, handler)
                handler.postDelayed(timeout, 5_000)
            } catch (_: Exception) { retry() }
        }
    }

    fun setEnabled(wanted: Boolean, confirmRelease: Int = 0) {
        handler.post {
            if (closing) return@post
            if (confirmRelease > 0) releaseId = confirmRelease
            if (wanted != enabled) {
                enabled = wanted
                if (enabled) {
                    releaseId = 0
                    manager?.registerAvailabilityCallback(availability, handler)
                    handler.postDelayed(reopen, 250)
                } else stop()
            }
            confirmReleased()
        }
    }

    fun close() {
        handler.post {
            closing = true
            enabled = false
            stop()
            // The open callback still owns any device that Android returns after release().
            if (opening) handler.postDelayed({ thread.quitSafely() }, 10_000) else thread.quitSafely()
        }
    }

    @SuppressLint("MissingPermission") // MainActivity checks camera permission before enabling the sampler.
    private fun open() {
        if (!enabled) return
        if (opening) { handler.postDelayed(reopen, 1_000); return }
        val cameraManager = manager ?: run { report("unknown"); return }
        try {
            val camera = cameraManager.cameraIdList.firstOrNull { id ->
                cameraManager.getCameraCharacteristics(id).get(CameraCharacteristics.LENS_FACING) == CameraCharacteristics.LENS_FACING_FRONT
            } ?: run { report("unknown"); return }
            if (camera !in available) { retry(); return }
            val characteristics = cameraManager.getCameraCharacteristics(camera)
            val size = characteristics.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
                ?.getOutputSizes(ImageFormat.YUV_420_888)
                ?.filter { it.width in 160..640 && it.height in 90..480 }
                ?.minByOrNull { it.width * it.height } ?: run { report("unknown"); return }
            sensorOrientation = characteristics.get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 0
            val current = generation
            val target = ImageReader.newInstance(size.width, size.height, ImageFormat.YUV_420_888, 2)
            reader = target
            target.setOnImageAvailableListener({ if (current == generation) onFrame(it) }, handler)
            handler.postDelayed(timeout, 8_000)
            opening = true
            cameraManager.openCamera(camera, object : CameraDevice.StateCallback() {
                private var delivered = false

                private fun openFinished() {
                    if (!delivered) opening = false
                    delivered = true
                }

                override fun onOpened(camera: CameraDevice) {
                    openFinished()
                    if (!enabled || current != generation) {
                        closeDevice(camera)
                        return
                    }
                    device = camera
                    configure(camera, target.surface, current)
                }

                override fun onDisconnected(camera: CameraDevice) = lost(camera)

                override fun onError(camera: CameraDevice, error: Int) = lost(camera)

                override fun onClosed(camera: CameraDevice) {
                    closingDevices.remove(camera)
                    confirmReleased()
                    if (closing && !opening && closingDevices.isEmpty()) thread.quitSafely()
                }

                private fun lost(camera: CameraDevice) {
                    openFinished()
                    closeDevice(camera)
                    if (current == generation) retry()
                }
            }, handler)
        } catch (_: Exception) { opening = false; retry(); confirmReleased() }
    }

    @Suppress("DEPRECATION") // The list-of-surfaces API is available on Android 5.
    private fun configure(camera: CameraDevice, surface: Surface, current: Int) {
        try {
            request = camera.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW).apply { addTarget(surface) }.build()
            camera.createCaptureSession(listOf(surface), object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(configured: CameraCaptureSession) {
                    if (!enabled || current != generation) { configured.close(); return }
                    session = configured
                    handler.removeCallbacks(timeout)
                    handler.post(take)
                }

                override fun onConfigureFailed(failed: CameraCaptureSession) {
                    failed.close()
                    if (current == generation) retry()
                }
            }, handler)
        } catch (_: Exception) { retry() }
    }

    private fun onFrame(source: ImageReader) {
        try {
            val image = source.acquireLatestImage() ?: return
            val result = image.use {
                if (++sampleNumber < 3) "unknown" else inspect(it)
            }
            report(result)
        } catch (_: Exception) { report("unknown") }
        handler.removeCallbacks(timeout)
        handler.postDelayed(take, 2_500)
    }

    private fun inspect(image: Image): String {
        val crop = image.cropRect
        val rawWidth = 160
        val rawHeight = (rawWidth * crop.height() / crop.width()).coerceIn(72, 240) and 1.inv()
        val degrees = (sensorOrientation - rotation() + 360) % 360
        val width = if (degrees == 90 || degrees == 270) rawHeight else rawWidth
        val height = if (degrees == 90 || degrees == 270) rawWidth else rawHeight
        var picture = bitmap
        if (picture == null || picture.width != width || picture.height != height) {
            picture?.recycle()
            picture = Bitmap.createBitmap(width, height, Bitmap.Config.RGB_565)
            bitmap = picture
            pixels = IntArray(width * height)
            detector = FaceDetector(width, height, faces.size)
        }

        val plane = image.planes[0]
        val buffer = plane.buffer
        val offset = buffer.position()
        for (y in 0 until height) for (x in 0 until width) {
            // A front-camera Y plane rotates counterclockwise from its sensor orientation.
            val sx = when (degrees) { 90 -> rawWidth - 1 - y; 180 -> rawWidth - 1 - x; 270 -> y; else -> x }
            val sy = when (degrees) { 90 -> x; 180 -> rawHeight - 1 - y; 270 -> rawHeight - 1 - x; else -> y }
            val px = crop.left + sx * crop.width() / rawWidth
            val py = crop.top + sy * crop.height() / rawHeight
            pixels[y * width + x] = buffer.get(offset + py * plane.rowStride + px * plane.pixelStride).toInt() and 0xff
        }
        if (!PresenceLighting.usable(pixels, width, height)) return "unknown"

        for (i in pixels.indices) pixels[i] = 0xff000000.toInt() or (pixels[i] * 0x010101)
        picture.setPixels(pixels, 0, width, 0, 0, width, height)
        faces.fill(null)
        val count = detector?.findFaces(picture, faces) ?: return "unknown"
        if (faces.any { it != null && it.confidence() >= 0.55f && it.eyesDistance() >= 10f }) return "present"
        return if (count == 0) "absent" else "unknown"
    }

    private fun retry() {
        report("unknown")
        release()
        if (enabled) handler.postDelayed(reopen, 8_000)
    }

    private fun stop() {
        manager?.unregisterAvailabilityCallback(availability)
        available.clear()
        release()
    }

    private fun closeDevice(camera: CameraDevice) {
        if (closingDevices.add(camera)) camera.close()
    }

    private fun confirmReleased() {
        if (releaseId == 0 || enabled || opening || closingDevices.isNotEmpty()) return
        report("released:$releaseId")
        releaseId = 0
    }

    private fun release() {
        generation++
        handler.removeCallbacks(reopen)
        handler.removeCallbacks(timeout)
        handler.removeCallbacks(take)
        session?.close()
        device?.let(::closeDevice)
        reader?.close()
        bitmap?.recycle()
        session = null
        device = null
        reader = null
        request = null
        bitmap = null
        detector = null
        pixels = IntArray(0)
        faces.fill(null)
        sampleNumber = 0
    }
}

internal object PresenceLighting {
    fun usable(luma: IntArray, width: Int, height: Int): Boolean {
        var useful = 0
        for (row in 0 until 3) for (column in 0 until 4) {
            var sum = 0
            var squares = 0
            var samples = 0
            for (y in row * height / 3 until (row + 1) * height / 3) {
                for (x in column * width / 4 until (column + 1) * width / 4) {
                    val level = luma[y * width + x]
                    sum += level
                    squares += level * level
                    samples++
                }
            }
            val mean = sum.toDouble() / samples
            val variance = squares.toDouble() / samples - mean * mean
            if (mean in 50.0..222.0 && variance >= 25.0) useful++
        }
        return useful >= 8
    }
}
