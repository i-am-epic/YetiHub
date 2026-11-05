# Changelog

All notable changes to YetiHub. The newest release is at the top; the release workflow ships this file as the release notes.

## 0.5.0

- **New GAMES tab** for competitive play.
  - **Game mode:** detects VALORANT, CS2, Apex, Fortnite, Overwatch 2, Rainbow Six Siege, League and Call of Duty by
    process name only (never touches the game, safe with anti-cheat), switches to that game's profile, and switches
    back when you quit. Choose the profile per game.
  - **Game audio analysis:** listens read-only to what your PC plays and reports loudness, peak, clipping and the
    balance between bass and the footstep band (2–4.5 kHz), averaged over the last ~15 s of sound, with plain advice
    (bass masking footsteps, clipping, very loud or quiet mix). Runs only while the tab is open or a game runs.
  - **Hear what teammates hear:** your monitor output can imitate game voice chat (about 100 Hz–10 kHz, like Opus at
    gaming bitrates) so you can tune callouts. What teammates receive is unchanged.
  - **VALORANT setup checklist** (mic input, voice threshold, HRTF, spatial sound).
- **Valorant Comms** standard profile for tactical shooters: heavy keyboard/mouse click suppression, a fast leveler
  and firm compressor so whispers and shouted callouts land at the same level, a clarity lift in the 2.5–4 kHz callout
  band, and extra limiter headroom for the voice codec. Passes the speech-quality tests at normal and soft levels.
- Fixed: profile pickers showed internal type names instead of profile names.

## 0.4.1

- **Much lower CPU use.** Meters and graphs now run on one shared 30 fps clock instead of every display refresh
  (165 times a second on high-refresh monitors), redraw only when something visibly changes, and stop completely
  while YetiHub is minimized. Measured on a 6-core PC (% of one core, steady state):
  visible 79% → **10%** with Compatibility graphics on, about **4%** with GPU graphics; minimized 12% → **1–6%**.
- **"Muted on your Yeti" warning.** The Yeti's red mute button never tells Windows, so YetiHub detects it from the
  audio (a muted Yeti sends exact digital silence) and shows a warning on the Microphone page and in the title bar.
- New [docs/HARDWARE.md](docs/HARDWARE.md): what the Yeti exposes over USB (HID collections, the vendor channel YetiHub
  deliberately doesn't touch), how G HUB applies Blue VO!CE (an Audio Processing Object inside Windows' audio
  engine), and the measured latency limits of this hardware.

## 0.4.0

- **New noise cancelling engine** (OM-LSA with MCRA noise tracking): per-frequency speech-presence estimation,
  decision-directed log-spectral gain, frame-level and per-frequency voice guards, and release hangover.
  Measured on a speech bench (TTS speech + fan, hiss, keyboard, hum, TV): **9.5 dB of noise removed vs 7.3 dB before
  (fan noise 9.4 vs 6.0 dB, hiss 14.6 vs 10.6 dB)** with audible voice cuts at 0.3 %. The Strength knob now follows the
  measured removal vs voice-safety curve; new **High quality** mode (512-point frames) for low fan rumble.
- **Character Voice Pack** (mod): Chipmunk, Giant, Demon, Dark Lord, Alien, Goblin, Angel Choir, Ghost, Underwater,
  Canyon Echo, Astronaut (with mission-control beeps), Walkie-Talkie (with squelch), 1940s Radio, Telephone, Megaphone,
  8-Bit Hero, Cyborg.
- **Tactical Agents Pack** (mod): 10 original agent voices for team shooters: Shadow Operative, Toxin Specialist,
  Combat Android, Field Medic, Recon Drone, Demolitions, Command HQ, Cryo Agent, Storm Duelist, Sentinel AI.
- **Character gallery** in the Mods tab: animated cards, one character at a time, active character shown in the title bar.
- Mod API 1.1: optional `IProcessorInfo` (emoji, description, group) for gallery cards. Existing mods keep working.
- Output safety limiter after mods, so no voice mod can clip or send NaN to Discord.

## 0.3.0

- **Fixed: words getting cut off.** The noise gate and Auto Level now adapt to your room's noise floor (gate with
  hysteresis and a 150 ms hold) instead of fixed thresholds. Verified by new automated speech tests on every profile at
  normal and soft speaking levels: no chopped syllables, output about −16 LUFS.
- **Glitch-free output:** clock drift is corrected by inaudible adaptive resampling instead of dropping audio; the
  output buffer self-tunes per device. New Audio Health monitor (dropouts, mic glitches, drift, DSP load).
- **Noise cancelling upgrade:** learns your room without learning your voice, suppresses keyboard clicks, and
  classifies noise (fan/rumble, hiss, hum, keyboard). New Hum Remover (auto 50/60 Hz) and Pop Filter.
- **AutoTune** (key, scale, retune speed) and **Pitch Shift**; new Singer, Hard Tune and Deep Voice profiles.
- **Live graphs:** spectrum (raw vs processed), voiceprint spectrogram, live waveform, and a LUFS loudness meter.
- Research-based chain order (de-esser after compression) and loudness-matched standard profiles.
- New logo and app icon; the installer uses it too.
- The app never freezes if Windows audio stops responding (5 s watchdog with a clear message); visual errors are
  logged to `crash.log` instead of closing the app; **Compatibility graphics** option for blank-window GPU issues.
- Fixed: garbled characters in the Device page.

## 0.2.0

- **New low-latency audio engine** (raw WASAPI, IAudioClient3, MMCSS "Pro Audio" thread): audio is written straight
  from the capture callback into the output with a 2–8 ms drift-controlled cushion. Measured on a Blue Yeti:
  ~60 ms → ~24 ms mic-to-ear.
- **Ultra-low latency mode**: exclusive Yeti capture with 3 ms packets.
- Live, measured latency badge in the title bar.
- **6 new effects**: Noise Reduction (spectral, +5.3 ms), Auto Level, Warmth (tube saturation), Exciter, Room Reverb,
  Mono Voice. The chain is now 13 stages, with a signal-chain view.
- **Standard profiles** (read-only, tuned for the Yeti): Studio Natural, Broadcast Pro, Cinematic Deep, Podcast Clean,
  Streamer Punch, Gaming Comms, Bright & Clear, Warm & Intimate, Arena Announcer, Raw. "Customize a copy" makes an
  editable duplicate.
- Output pickers list unplugged/disabled devices (e.g. Realtek with no jack detected) with an explanation.
- Fixed: the virtual-mic picker allowed ordinary speakers, which could play your voice twice.
- Fixed: knob reduction amounts now read as negative dB.

## 0.1.0

- Detects the Blue Yeti over USB (VID/PID, firmware revision, bound Windows drivers) via the PnP registry, in milliseconds.
- G HUB-style dark UI: profile sidebar, rotary knobs, LED meters, custom title bar.
- Mic gain, mute and Yeti headphone volume, kept in sync with changes made in Windows. One-click "Make default mic".
- Voice FX chain: Low Cut, Noise Gate, Voice EQ, De-Esser, Compressor, Output Gain, Limiter.
- Profiles with six built-in presets, auto-saved per change.
- Low-latency monitoring (≈40 ms, drift-controlled) and virtual-mic routing for Discord/OBS.
- Mod system with the bundled Robot Voice example.
- Self-update from GitHub Releases.
