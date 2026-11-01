# Changelog

All notable changes to YetiHub. The newest release is at the top; the release workflow ships this file as the release notes.

## 0.1.0

- Detects the Blue Yeti over USB (VID/PID, firmware revision, bound Windows drivers) via the PnP registry, in milliseconds.
- G HUB-style dark UI: profile sidebar, rotary knobs, LED meters, custom title bar.
- Mic gain, mute and Yeti headphone volume, kept in sync with changes made in Windows. One-click "Make default mic".
- Voice FX chain: Low Cut, Noise Gate, Voice EQ, De-Esser, Compressor, Output Gain, Limiter.
- Profiles with six built-in presets, auto-saved per change.
- Low-latency monitoring (≈40 ms, drift-controlled) and virtual-mic routing for Discord/OBS.
- Mod system with the bundled Robot Voice example.
- Self-update from GitHub Releases.
