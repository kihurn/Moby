# Moby

Moby turns a spare Android phone into a desk panel for my Windows PC. Plugged in over USB, it shows photos and the time, launches apps, and keeps temperatures and fan speeds in view. During a call it can be my webcam and microphone while I carry on swiping through the other pages.

I built it around a vivo Y33s running Android 13, on Windows 11.

## Pages

| Page | What it does |
| --- | --- |
| Idle | A slideshow from a folder on the PC, with a clock over the photos. |
| Shortcuts | A grid of keys that launch apps and run shortcuts. |
| System | CPU frequency, disk activity, temperatures and fan speeds. |
| Call | Starts and stops the phone's camera and microphone over USB. |

These are templates. Pages can be edited, or built from individual widgets, in the editor on the PC. Liquid Glass is an optional theme under **Display → Theme**.

CPU temperature and board fan speeds need [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) running on the PC.

## Setup

You need the .NET 10 SDK, JDK 17, Android SDK Platform 35, `adb`, and a phone with USB debugging turned on.

Build the phone app and install it:

```powershell
.\android\gradlew.bat -p android assembleDebug
adb install -r android\app\build\outputs\apk\debug\app-debug.apk
```

Run the PC agent:

```powershell
cd agent\*.Agent
dotnet run -f net10.0-windows10.0.19041.0
```

Connect the phone with a data cable, accept the USB debugging prompt and open the app. The editor is at <http://127.0.0.1:8765>.

## Starting and stopping

```powershell
moby start     # agent, camera and mic
moby camera    # camera only
moby standby   # keep the panel running, stop capture
moby status
moby stop      # shut everything down
```

To run `moby` from any folder, create a `moby.cmd` somewhere on your `PATH` with this line, using the path of your checkout:

```bat
@call "C:\path\to\Moby\scripts\moby.cmd" %*
```

The launcher uses port 18765, so the editor is at <http://127.0.0.1:18765> when Moby is started this way. It expects an agent build and a profile in `.moby-spike\`, which is not in the repository. Point it at your own with `-AgentPath` and `-ConfigPath`.

## Camera and microphone

Calls need three separate installs:

- [Plugcam 0.2.2 portable](https://github.com/Qwinty/plugcam/releases/tag/v0.2.2), in its own folder. Run it once, register **Plugcam Camera**, then close it from the tray.
- [Virtual Cables](https://github.com/tarekwasfy01/Virtual-Cables) with one cable.
- [usbip-win2](https://github.com/vadimgrn/usbip-win2), which Virtual Cables uses. Leave Virtual Cables minimised.

Then, in the editor:

1. Under **General**, set the path to the portable Plugcam executable, and set the ADB path to its `resources\adb.exe`.
2. Under **Advanced → JSON**, set `phoneMic.endpoint` to the full name of the cable's playback device, `phoneMic.source` to `mic`, and `phoneMic.enabled` to `false`.
3. If the mic does not start, set `phoneMic.serverPath` to the scrcpy 5.0.1 server file.

In your call app, pick **Plugcam Camera** and **Virtual Cable 01 (Microphone)**, and keep your normal speakers for playback.

For camera exposure control, see the [patched Plugcam build](extras/moby-exposure/README.md). If Plugcam reports `scCreateCamera failed`, close any app still previewing the camera, including from the tray, and try again.

## Desk presence

Off by default. Turn it on under **Display → Desk presence**.

- **Away only** dims the phone and monitors after you have been absent and idle, then puts the monitors into standby. Mouse or keyboard wakes them.
- **Away + return** also uses the front camera to wake them when you sit back down.

The phone does the detection itself, sends only the result over USB and saves no frames. It needs to see your face in normal light: darkness or a covered camera never counts as being away. Sampling pauses while the camera is being used for a call.

## License

[MIT](docs/LICENSE)
