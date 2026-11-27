# Moby camera exposure compensation

The Moby profile stores the requested EV. Plugcam passes it to scrcpy, which converts it into the selected camera lens's exposure compensation steps and sets it on Android's repeating Camera2 request. It applies when video starts. This build uses the existing Plugcam Camera registration and can use Moby's shared ADB executable.

## Rebuild the bridge

1. Apply `scrcpy-4.1-exposure.patch` to scrcpy v4.1 at `2926c06c`. Build `:server:assembleRelease` using the checked-in Gradle wrapper and Android SDK 36.
2. Apply `plugcam-0.2.2-exposure.patch` to Plugcam 0.2.2 at `67f9366`. Put the built Android APK in `src-tauri/resources/scrcpy-server`. Set `MOBY_EXPOSURE_SERVER_SHA256` to the lowercase SHA-256 of that file when building the Plugcam executable. Use the upstream lockfiles for the frontend build and `cargo build --locked --release --manifest-path src-tauri/Cargo.toml`.
3. Stage the executable and matching server in a separate Plugcam portable folder. Include the stock Plugcam camera DLLs, portable marker, and upstream notices. Add an empty `moby-exposure.txt` next to the patched executable so Moby recognizes the compatible bridge.
4. Point Moby's **Portable Plugcam executable** setting at that folder's `Plugcam.exe` and keep the existing ADB path. Test exposure with the phone camera pointing at a stable scene. Start video with 0 EV, then start a fresh video session with a nonzero setting and check the Camera2 metadata and resulting image.

The bridge starts and stops its own scrcpy session. The phone mic can run independently while you restart video.
