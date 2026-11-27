# Moby add-ons for PhoneBox

Install the Android APK over the existing debug installation with `adb install -r`. Run the Windows agent with your existing profile. Keep the same Android package and signing key when upgrading so the phone retains its data and pairing.

The existing editor offers four optional page templates. You can also add the individual widgets to your own pages.

- **Moby Idle** uses the Photo frame widget. Choose a local folder in **General → Photo frame**. Turn **Clock over photos** on for any Photo frame widget.
- **Moby Shortcuts** uses your existing shortcuts. Choose the keys and number of columns in the editor.
- **Moby System** uses the existing metrics and adds a **CPU, disk & fans** widget. Windows supplies CPU frequency and disk throughput when the counters are available. LibreHardwareMonitor on the PC can supply CPU temperature and board fan RPM. The GPU widget shows vendor readings where available.
- **Moby Call** can start both feeds or just the camera. The USB capture runs while you visit other pages. A red badge in the top-right corner identifies any requested feed that failed.

Choose **Liquid Glass** under **Display → Theme** if you want it. Your normal theme and layouts are used until you change them.

## Set up Call on Windows

1. Use [Plugcam 0.2.2 portable](https://github.com/Qwinty/plugcam/releases/tag/v0.2.2) in a dedicated folder. Run it once and register **Plugcam Camera** using its setup prompt. Close Plugcam from its tray menu. Moby starts it without opening the preview. It uses the front camera at 1280 × 720 and 30 fps.
2. Run [Virtual Cables](https://github.com/tarekwasfy01/Virtual-Cables) with one cable and its signed [USBip transport](https://github.com/vadimgrn/usbip-win2). Minimize Virtual Cables to keep its audio endpoints available. On this PC, use **Speakers (Virtual Cable 01)** for playback into the cable.
3. In PhoneBox **General**, set the **Portable Plugcam executable** path and set the ADB path to its `resources\adb.exe`. In **Advanced → JSON**, set `phoneMic.endpoint` to the full playback device name, `phoneMic.source` to `mic`, and `phoneMic.enabled` to `false`. Set `phoneMic.serverPath` to the official scrcpy 5.0.1 server if it is not already available beside the agent or profile. An 80 ms latency and 300 ms buffer ceiling were used for the successful Discord check.
4. Start capture from the phone and select **Plugcam Camera** and **Virtual Cable 01 (Microphone)** in your call app. Choose your normal speaker instead of the cable for call playback. Stop from the Call widget. If USB disconnects, reconnect and start a fresh capture.

Plugcam, Virtual Cables and USBip are separate installs. Moby uses the PhoneBox .NET agent for the mic. It requires no paid unlock and saves no media.

### Camera exposure

With the [Moby exposure build of Plugcam](../extras/moby-exposure/README.md), set **General → Camera exposure compensation (EV)** and save. The setting applies the next time you start video. On the vivo Y33s, use values from −2 to +2 EV in 0.1 EV steps. Start at 0 for normal auto-exposure.

Camera2 applies this setting during capture using the selected lens's reported step and range. The virtual device remains **Plugcam Camera**.

### Start and stop from a terminal

The launcher uses port 18765 and the existing `.moby-spike\baseline-config.json` profile. For another installation, supply `-AgentPath` and `-ConfigPath`.

To run `moby` from any folder, create `moby.cmd` in a folder on your `PATH` that contains this line, with the path of your checkout:

```bat
@call "C:\path\to\Moby\scripts\moby.cmd" %*
```

Then open a new terminal:

```powershell
moby start
moby camera
moby standby
moby status
moby stop
```

Without that file, run `.\scripts\moby.cmd start` from the checkout.

`start` starts Virtual Cables if needed, starts the agent with the saved profile, and waits for both feeds. If the phone is missing and the bundled ADB server stops answering, the launcher checks that it is Moby's shared server and attempts one restart while capture is inactive. Use `camera` for video only. You can add the mic with `start` while the camera stays live. For mic only, use **General → Phone mic** in Moby Settings.

`standby` keeps the agent, USB deck, and Windows devices available while stopping Call capture. Start again from the Call widget or the CLI. Plugcam 0.2.2 needs that explicit start to resume from standby. If standalone mic is enabled in **General**, it stays on until you turn it off there. The CLI `stop` shuts down the agent and both feeds. Virtual Cables stays available to Windows and other apps.

If Plugcam reports `scCreateCamera failed` after a previous session, close any camera preview that was using **Plugcam Camera**. If the error persists, exit the app that opened that preview, including its tray icon if it runs in the background, then retry.

## Desk presence on Windows

In **Display → Desk presence** in the Windows agent settings, choose a mode and two delays. **Absent before dimming** defaults to 120 seconds and accepts 10 to 3,600 seconds. **Dimmed before monitor standby** defaults to 60 seconds and accepts 5 to 3,600 seconds.

- **Disabled** is the default. Desk presence does not open the camera.
- **Away only** dims the phone and supported monitors after sustained absence and inactivity. Continued absence puts the monitors into standby after the second delay. Use the mouse or keyboard to wake the monitors. Touching the phone while the monitors are in standby brightens the phone.
- **Away + return** also restores a dimmed monitor or requests Windows display wake after the first confirmed face reading. Mouse and keyboard wake still work in darkness.

Keep one Android panel connected over USB and open Moby on the phone. Grant camera permission when Android prompts. The front camera samples locally about every 1.6 seconds on the vivo Y33s. Moby sends only presence states over USB and saves no frames. A clear face reading must precede a sustained absence. Low light, a covered camera, and interrupted readings break the absence timer. Frame the phone so it can see your face in normal desk lighting.

While Desk presence is active over USB, it takes priority over **Brightness follows the room** and **Show the night screen whenever the room is dark**. Those preferences resume when presence is disabled or suspended. Moby restores supported monitors to 100% with a single brightness command. Monitor dimming uses the same DDC/CI brightness control as the Windows monitor widget. Displays without that control can still enter standby after the second delay.

The Windows agent requests that the PC stay awake while its monitors are dimmed or in standby. Presence sampling pauses while Moby streams the phone camera. The standalone phone mic can remain active. In **Away only**, the camera also pauses once the monitors enter standby. Android pauses sampling when PhoneBox leaves the foreground.
