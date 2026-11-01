# YetiHub

An open-source control app for the **Blue Yeti** on Windows, for Yetis that Logitech G HUB no longer
lists. Gain, mute, headphone volume, a live level meter, a mod system, and self-updates.

## How the Yeti actually works on Windows

There is no "Yeti driver". The original Yeti (`USB VID_B58C / PID_9E84`) is a class-compliant USB device and
runs entirely on Microsoft's in-box drivers:

| USB function | Windows driver | What it is |
|---|---|---|
| Composite parent | `usbccgp` (`usb.inf`) | Splits the device into functions |
| Audio (`MI_00`) | `usbaudio` (`wdma_usb.inf`) | Microphone + headphone jack |
| HID (`MI_03`) | `HidUsb` (`input.inf`) | Mute button / headset controls |

G HUB was a software layer on top of Windows Core Audio. YetiHub is the same, and shows you these drivers
live in the **Device** panel.

**Firmware:** Logitech never published a firmware-update protocol for the original Yeti, and YetiHub does
not write firmware. "Updates" here means YetiHub app updates.

## Features

- Detects the Yeti in milliseconds: USB IDs, firmware revision, bound drivers, audio format; handles hot-plug.
- Mic gain and Yeti headphone volume on rotary knobs; mute; **Make default mic**.
- 60 fps LED input/output meters (dBFS, peak hold).
- **Voice FX**, a 13-stage broadcast chain: Mono → Low Cut → Noise Reduction → Noise Gate → Voice EQ → De-Esser →
  Auto Level → Compressor → Warmth → Exciter → Room Reverb → Output Gain → Limiter.
- **Standard profiles** tuned for the Yeti (read-only; "Customize a copy" to edit): Studio Natural, Broadcast Pro,
  Cinematic Deep, Podcast Clean, Streamer Punch, Gaming Comms, Bright & Clear, Warm & Intimate, Arena Announcer, Raw.
- **Your profiles:** unlimited, one click to switch, auto-saved as JSON in `%APPDATA%\YetiHub\profiles`.
- **Low-latency engine** with a live measured latency badge (~24 ms mic-to-ear on a Yeti; Ultra-low mode uses
  exclusive 3 ms capture). The Yeti's own headphone jack stays zero-latency raw.
- **Virtual mic:** route the processed voice to VB-CABLE/Voicemeeter so Discord, OBS and games hear your effects.
- **Mods:** drop-in DLL plugins with real-time audio processors. Ships with a Robot Voice example. See [docs/MODS.md](docs/MODS.md).
- **Auto-update** from GitHub Releases (Velopack, delta updates).

### Using your effects in Discord / OBS

1. Install the free [VB-CABLE](https://vb-audio.com/Cable/) virtual audio device.
2. In YetiHub → Microphone → *Send to Discord / OBS*, pick **CABLE Input**.
3. In Discord/OBS/your game, choose **CABLE Output** as the microphone.

## Install

Download `YetiHub-win-Setup.exe` from [Releases](https://github.com/i-am-epic/YetiHub/releases).
It installs per-user (no admin) and keeps itself updated.

## Build from source

Requires the .NET 10 SDK.

```bash
dotnet build YetiHub.slnx
```

```bash
dotnet test YetiHub.slnx
```

```bash
dotnet run --project src/YetiHub.App
```

## Repository layout

```
src/YetiHub.App                 WPF desktop app (UI, settings, updater)
src/YetiHub.Core                Device detection, Core Audio control, audio engine, mod host
src/YetiHub.Core/Effects        Voice FX DSP (biquads, gate, EQ, de-esser, compressor, limiter)
src/YetiHub.Core/Profiles       Profile model, JSON store, built-in presets
src/YetiHub.Mods.Abstractions   Public mod API (versioned separately; breaking change = major bump)
mods/YetiHub.Mods.RobotVoice    Sample/bundled mod
tests/YetiHub.Core.Tests        Unit tests
docs/                           MODS.md, RELEASING.md
```

## Releasing

Tag `vX.Y.Z` and push; GitHub Actions does the rest. See [docs/RELEASING.md](docs/RELEASING.md).

## Roadmap

- AI noise suppression (RNNoise) as a Voice FX stage.
- Per-app profiles (switch automatically when a game or OBS starts) and profile hotkeys.
- Import/export profiles to share them.
- Mods contributing their own knobs to the Voice FX page.
- Tray icon, start with Windows, global mute hotkey.
- Support for Yeti Nano / Yeti X / Snowball (add IDs to `KnownDevices`).

## License

MIT
