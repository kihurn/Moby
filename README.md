# Moby

I built Moby around a spare vivo Y33s on my desk. Plugged into my Windows PC, it shows photos and the time, launches apps, and puts temperatures and fan speeds where I can see them without leaving what I’m doing. During a call I can use the phone as my webcam and microphone and still swipe through the deck.

## Set up the deck

1. Build the Windows agent and Android APK from this checkout using the [development guide](docs/DEVELOPMENT.md).
2. Enable USB debugging on the phone, connect it with a data cable, and accept the authorization prompt. Install the APK and open the panel.
3. Start the Windows agent and open its [local editor](http://127.0.0.1:8765). Under **Pages**, pick a template or edit an existing layout.

I use the default theme. Liquid Glass is an optional choice under **Display → Theme**.

| Page | What it does |
| --- | --- |
| Idle | Plays a slideshow from a folder on the PC with a clock over the photos. |
| Shortcuts | Launches apps and runs shortcuts from a grid of keys. |
| System | Shows CPU frequency, disk activity, and the temperatures and fan speeds exposed by the PC. |
| Call | Starts or stops USB camera and microphone capture. Capture stays active while you use other pages. |

The Call page needs Plugcam and a virtual audio cable on Windows. The [Moby setup guide](docs/MOBY.md) covers the exact devices to select in your call app.

## When I step away

Desk presence is off by default. **Away only** dims the phone and puts the monitor in standby after sustained absence and inactivity. **Away + return** also uses the front camera to wake the displays when someone sits down. In a dark room, mouse or keyboard input still wakes the desk.

The phone processes presence locally and sends only the result over USB. Camera sampling pauses during call capture. The absence delay and camera setup are in the [desk presence guide](docs/MOBY.md#desk-presence-on-windows).

Moby builds on [PhoneBox](https://github.com/ersinkoc/phonebox). See the [license](LICENSE).
