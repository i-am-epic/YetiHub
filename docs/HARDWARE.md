# How the Blue Yeti and Logitech's software actually work

Notes from inspecting a real Blue Yeti (`USB VID_B58C / PID_9E84`, firmware 1.00) on Windows 11, plus public research.
Everything here was observed from the device's own USB descriptors and Windows' audio stack. Nothing comes from
decompiling Logitech software.

## USB functions

| Interface | Windows driver | What it is |
|---|---|---|
| Composite parent | `usbccgp` (`usb.inf`) | Splits the device into functions |
| `MI_00` audio | `usbaudio` (`wdma_usb.inf`) | 48 kHz stereo mic + headphone output (UAC 1.0) |
| `MI_03` HID | `HidUsb` (`input.inf`) | Two HID collections (below) |

There is no Logitech/Blue driver. Firmware updates for the original Yeti were never published.

## HID collections (from the report descriptor)

**Consumer Control** (usage page `0x0C`, report 1, 16-byte input / 16-byte output)
- Buttons: Volume Up/Down, Mute (`0xE2`), Play/Pause, Next/Previous/Stop
- An 11-byte input field and a 15-byte output report on undefined pages (`0x01FF`/`0x07FF`). This is the usual shape
  of a USB audio controller chip's vendor command channel. YetiHub never writes to it: unknown vendor commands can
  change chip settings or EEPROM.

**Telephony Headset** (usage page `0x0B`, report 2): a Hook Switch button.

**Observed:** with both collections opened for reading for 60 s while pressing the Yeti's mute button, the device sent
**no reports**. The front mute button is handled inside the mic (it mutes the capsule and blinks the LED) and is not
reported to the PC. The HID buttons come from the generic controller chip and are not wired.

**What YetiHub does with this:** a hardware-muted Yeti delivers exact digital zeros, which a real room never produces.
YetiHub treats 300 ms of exact silence as "muted on the Yeti" and shows a warning (Microphone page + title bar).

## How Logitech G HUB applies Blue VO!CE

Driver databases list Blue VO!CE as a software component `SWC\VEN_GHUB&CID_BLUEVOICE_APO`. It is an **Audio
Processing Object (APO)**: a COM DLL that Windows' audio engine (`audiodg.exe`) loads into the microphone endpoint's
effects chain. Every app that records from the Yeti then gets the processed sound, with no virtual cable.

The same mechanism is used by the open-source [Equalizer APO](https://sourceforge.net/projects/equalizerapo/).

### What it would take for YetiHub (roadmap)

1. A native C++ APO DLL (`IAudioProcessingObject`, `IAudioProcessingObjectRT`) running the same DSP chain.
   The C# effects would need a C++ port, since APOs run in-process in `audiodg.exe`.
2. Registration on the Yeti's capture endpoint (`FxProperties` of the endpoint, admin rights, audio service restart),
   or as a Windows "software component" via a signed driver package.
3. A shared-memory or registry channel so the YetiHub UI can change profiles while the APO runs.

Until then YetiHub uses the virtual-cable approach (VB-CABLE), which needs no admin rights or driver signing.

## Latency facts measured on this hardware

- Shared-mode engine period: **10 ms** minimum on the Yeti, the MSI monitor's HDMI audio and DroidCam (IAudioClient3).
- Exclusive-mode minimum device period: **3 ms** (YetiHub's Ultra-low latency mode).
- Measured YetiHub mic-to-ear: about 20–25 ms (shared), plus effects (Noise Cancelling +5.3 ms, AutoTune/pitch +12.5 ms).
