# Moby on-demand camera prototype

The patch applies to Plugcam 0.2.2 at commit `67f9366`. It builds a distinct **Moby On-Demand Camera** DirectShow device and preserves the upstream Plugcam Camera registration. Build both the patched DLL and executable. The stock executable stops every scrcpy session at camera startup, including the phone mic.

Apply `plugcam-0.2.2.patch` in a clean Plugcam 0.2.2 checkout and run Plugcam's `scripts\vcam-build.ps1` to build both Windows architectures. Use the upstream lockfiles to build the frontend with `pnpm install --frozen-lockfile` and `pnpm build`, then run `cargo build --locked --release --manifest-path src-tauri/Cargo.toml`. From the Moby checkout, stage the resulting executable and DLLs:

```powershell
$vcam = '..\.moby-spike\plugcam-on-demand-source\vcam\dist\bin'
.\extras\moby-on-demand\Stage-MobyCamera.ps1 -PlugcamExecutable '..\.moby-spike\plugcam-on-demand-source\src-tauri\target\release\plugcam.exe' -CameraDllX64 "$vcam\x64\plugcam_cam.dll" -CameraDllX86 "$vcam\Win32\plugcam_cam.dll"
```

Register the staged x64 DLL with `System32\regsvr32.exe` and the staged x86 DLL with `SysWOW64\regsvr32.exe`, using an elevated shell. The test registration uses its own CLSID, `{8AA4F694-432C-44AB-9383-88A0F819FF90}`. Verify both registration paths point to the staged files. To remove the test registration, run the same commands with `/u`.

When the vivo is free, stop the currently running Moby agent. Start the prototype with `scripts\moby.ps1 start -AgentPath <prototype-agent-exe> -ConfigPath <staged-profile>`. Use the same `-ConfigPath` with `status` and `stop`. The staged profile keeps the existing shared ADB binary and enables its existing mic route. Pick **Moby On-Demand Camera** in Discord and **Virtual Cable 01 (Microphone)** as the NVIDIA Broadcast mic source. Use **NVIDIA Broadcast** as Discord's microphone if you want its filtering.

Verify that listing camera devices leaves the phone lens free, starting a Discord preview wakes the vivo and delivers moving video, and closing the last video stream releases the phone camera after 60 seconds. Check that audio continues across both video transitions and through normal scene swipes. Use the test camera only during this verification.
