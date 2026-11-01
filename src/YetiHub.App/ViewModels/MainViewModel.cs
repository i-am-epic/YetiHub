using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Velopack;
using YetiHub.App.Services;
using YetiHub.Core;
using YetiHub.Core.Effects;
using YetiHub.Core.Profiles;
using YetiHub.Mods;

namespace YetiHub.App.ViewModels;

public sealed class MainViewModel : Observable, IDisposable
{
    private readonly DeviceScanner _scanner = new();
    private readonly ModHost _mods = new(Path.Combine(AppSettings.DataRoot, "mod-data"));
    private readonly UpdateService _updates = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ProfileStore _profileStore = new(Path.Combine(AppSettings.DataRoot, "profiles"));
    private readonly EffectChain _effects = new();
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _saveTimer;

    private YetiDevice? _device;
    private AudioEngine? _engine;
    private UpdateInfo? _pendingUpdate;
    private bool _syncingFromDevice;
    private bool _applyingProfile;
    private bool _scanning = true;
    private bool _routingOutputs;

    public MainViewModel()
    {
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => SaveActiveProfile(), _ui);
        _saveTimer.Stop();

        Effects = _effects.Effects.Select(e => new EffectViewModel(e, OnSoundChanged)).ToList();

        ProfilesView = CollectionViewSource.GetDefaultView(Profiles);
        ProfilesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProfileViewModel.Group)));

        RescanCommand = new RelayCommand(RescanAsync);
        MakeDefaultCommand = new RelayCommand(MakeDefault, () => _device != null && !IsDefault);
        OpenModsFolderCommand = new RelayCommand(OpenModsFolder);
        OpenProfilesFolderCommand = new RelayCommand(() => OpenFolder(_profileStore.Directory));
        CheckUpdatesCommand = new RelayCommand(CheckForUpdatesAsync, () => _updates.IsInstalled);
        InstallUpdateCommand = new RelayCommand(InstallUpdateAsync, () => _pendingUpdate != null);
        NewProfileCommand = new RelayCommand(() => AddProfile(CaptureCurrent(UniqueName("My profile"))));
        DuplicateProfileCommand = new RelayCommand(DuplicateActive, () => ActiveProfile != null);
        DeleteProfileCommand = new RelayCommand(DeleteActiveProfile, () => ActiveProfile is { IsBuiltIn: false });
        GetVirtualCableCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true }));
        OpenSoundSettingsCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }));

        _scanner.DevicesChanged += (_, _) => _ui.InvokeAsync(RescanAsync);
        _mods.Logged += (_, line) => _ui.InvokeAsync(() => Log(line));
    }

    public static string UserModsFolder => Path.Combine(AppSettings.DataRoot, "mods");

    // ---------------- device ----------------
    public bool IsConnected => _device != null;
    public bool IsScanning => _scanning && _device is null;
    public string DeviceTitle => _device?.Name ?? (_scanning ? "Looking for your Yeti…" : "No Yeti detected");
    public string DeviceStatus => _device is null
        ? (_scanning ? "Scanning USB devices" : "Plug your Yeti in over USB")
        : DeviceProblem ?? "Connected · " + _device.EndpointName;
    public string UsbIds => _device is null ? "—" : $"VID {_device.Usb.VendorId:X4} · PID {_device.Usb.ProductId:X4}";
    public string Firmware => _device?.Usb.FirmwareRevision ?? "—";
    public string Format => _engine?.CaptureFormat is { Length: > 0 } f ? f : "—";
    public ObservableCollection<DriverInfo> Drivers { get; } = [];
    public bool IsDefault => _device?.IsDefaultFor(Role.Communications) == true && _device.IsDefaultFor(Role.Console);

    private double _gain;
    public double Gain
    {
        get => _gain;
        set
        {
            value = Math.Round(Math.Clamp(value, 0, 100));
            if (!Set(ref _gain, value)) return;
            if (!_syncingFromDevice && _device != null) _device.Gain = (float)(value / 100);
            Raise(nameof(GainLabel));
            OnSoundChanged();
        }
    }

    public string GainLabel => _device is null ? "—" : $"{_device.GainDecibels:+0.0;-0.0;0} dB";

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (Set(ref _isMuted, value) && !_syncingFromDevice && _device != null)
                _device.IsMuted = value;
        }
    }

    public bool HasHeadphones => _device?.Headphones != null;

    private double _headphoneVolume;
    public double HeadphoneVolume
    {
        get => _headphoneVolume;
        set
        {
            value = Math.Round(Math.Clamp(value, 0, 100));
            if (!Set(ref _headphoneVolume, value)) return;
            if (!_syncingFromDevice && _device != null) _device.HeadphoneVolume = (float)(value / 100);
            OnSoundChanged();
        }
    }

    // ---------------- meters (updated every frame) ----------------
    private double _inputLevel, _inputPeak, _outputLevel, _outputPeak, _peakHoldIn, _peakHoldOut, _glow, _rawIn, _rawOut;

    private void SetIfMoved(ref double field, double value, string name)
    {
        var a = Controls.LevelMeter.ToPosition(field);
        var b = Controls.LevelMeter.ToPosition(value);
        if (Math.Abs(a - b) < 1.0 / 96 && !(value == 0 && field != 0)) return;
        field = value;
        Raise(name);
    }
    public double InputLevel { get => _inputLevel; private set => Set(ref _inputLevel, value); }
    public double InputPeak { get => _inputPeak; private set => Set(ref _inputPeak, value); }
    public double OutputLevel { get => _outputLevel; private set => Set(ref _outputLevel, value); }
    public double OutputPeak { get => _outputPeak; private set => Set(ref _outputPeak, value); }

    /// <summary>0..1, follows the voice: drives the hero glow.</summary>
    public double Glow { get => _glow; private set => Set(ref _glow, value); }

    private string _inputDb = "-∞";
    public string InputDb { get => _inputDb; private set => Set(ref _inputDb, value); }

    private string _compression = "";
    public string Compression { get => _compression; private set => Set(ref _compression, value); }

    // ---------------- analysis (updated 4x per second) ----------------
    private string _loudnessText = "—";
    public string LoudnessText { get => _loudnessText; private set => Set(ref _loudnessText, value); }

    private string _loudnessGrade = "off";
    /// <summary>"great" (on target -16 ±2), "good", "high" (way off), "off".</summary>
    public string LoudnessGrade { get => _loudnessGrade; private set => Set(ref _loudnessGrade, value); }

    private string _noiseLevel = "—";
    public string NoiseLevel { get => _noiseLevel; private set => Set(ref _noiseLevel, value); }

    private string _roomGrade = "—";
    public string RoomGrade { get => _roomGrade; private set => Set(ref _roomGrade, value); }

    public ObservableCollection<string> NoiseTypes { get; } = [];

    private bool _voiceActive;
    public bool VoiceActive { get => _voiceActive; private set => Set(ref _voiceActive, value); }

    private string _healthText = "—";
    public string HealthText { get => _healthText; private set => Set(ref _healthText, value); }

    private bool _healthy = true;
    public bool Healthy { get => _healthy; private set => Set(ref _healthy, value); }

    private bool _hardwareMuted;
    /// <summary>The Yeti's own red mute button is on (it never tells Windows, so we detect it from the audio).</summary>
    public bool HardwareMuted
    {
        get => _hardwareMuted;
        private set
        {
            if (!Set(ref _hardwareMuted, value)) return;
            Log(value ? "The Yeti's mute button is ON: nobody can hear you. Press the red button on the mic." : "The Yeti's mute button is off again.");
        }
    }

    private string _pitchText = "";
    public string PitchText { get => _pitchText; private set => Set(ref _pitchText, value); }

    // ---------------- latency ----------------
    private string _latencyText = "—";
    /// <summary>Measured mic-to-ear delay of the monitor (or virtual mic) route.</summary>
    public string LatencyText { get => _latencyText; private set => Set(ref _latencyText, value); }

    private string _latencyGrade = "off";
    /// <summary>"great" / "good" / "high" / "off": colours the badge.</summary>
    public string LatencyGrade { get => _latencyGrade; private set => Set(ref _latencyGrade, value); }

    /// <summary>The running engine, for the live graphs (null while no Yeti).</summary>
    public AudioEngine? Engine => _engine;

    public string EngineInfo => _engine is null
        ? "Not running"
        : $"{(_engine.IsExclusive ? "Exclusive" : "Shared · raw")} · {_engine.CapturePeriodMs:0.#} ms packets · {_engine.CaptureFormat}";

    public bool UltraLowLatency
    {
        get => _settings.UltraLowLatency;
        set
        {
            if (_settings.UltraLowLatency == value) return;
            _settings.UltraLowLatency = value;
            _settings.Save();
            Raise();
            _ = RestartEngineAsync();
        }
    }

    /// <summary>Takes effect on next start.</summary>
    public bool SoftwareRendering
    {
        get => _settings.SoftwareRendering;
        set
        {
            if (_settings.SoftwareRendering == value) return;
            _settings.SoftwareRendering = value;
            _settings.Save();
            Raise();
            Log(value ? "Compatibility graphics on: restart YetiHub to apply." : "GPU graphics on: restart YetiHub to apply.");
        }
    }

    // ---------------- Voice FX ----------------
    public IReadOnlyList<EffectViewModel> Effects { get; }

    public bool EffectsEnabled
    {
        get => _effects.Enabled;
        set
        {
            if (_effects.Enabled == value) return;
            _effects.Enabled = value;
            Raise();
            OnSoundChanged();
        }
    }

    // ---------------- outputs ----------------
    public ObservableCollection<OutputOption> MonitorOutputs { get; } = [];
    public ObservableCollection<OutputOption> VirtualMicOutputs { get; } = [];

    private OutputOption _monitorOutput = OutputOption.Off;
    public OutputOption MonitorOutput
    {
        get => _monitorOutput;
        set
        {
            value ??= OutputOption.Off;
            if (!value.IsAvailable || !Set(ref _monitorOutput, value) || _routingOutputs) return;
            _settings.MonitorDeviceId = value.Id;
            _settings.Save();
            _ = RouteAsync(OutputRole.Monitor, value);
        }
    }

    private OutputOption _virtualMicOutput = OutputOption.Off;
    public OutputOption VirtualMicOutput
    {
        get => _virtualMicOutput;
        set
        {
            value ??= OutputOption.Off;
            if (!value.IsAvailable || !Set(ref _virtualMicOutput, value) || _routingOutputs) return;
            _settings.VirtualMicDeviceId = value.Id;
            _settings.Save();
            _ = RouteAsync(OutputRole.VirtualMic, value);
        }
    }

    public bool HasVirtualCable => VirtualMicOutputs.Any(o => o.IsVirtualCable && o.IsAvailable);
    public bool HasUnpluggedOutputs => MonitorOutputs.Any(o => o.Unavailable == "unplugged");

    // ---------------- profiles ----------------
    public ObservableCollection<ProfileViewModel> Profiles { get; } = [];
    public ICollectionView ProfilesView { get; }

    private ProfileViewModel? _activeProfile;
    public ProfileViewModel? ActiveProfile
    {
        get => _activeProfile;
        set
        {
            if (value is null || value == _activeProfile) return;
            SaveActiveProfile();
            if (_activeProfile != null) _activeProfile.IsActive = false;
            _activeProfile = value;
            _activeProfile.IsActive = true;
            Raise();
            Raise(nameof(IsLocked));
            Raise(nameof(CanEditFx));
            ApplyProfile(value);
            _settings.ActiveProfileId = value.Model.Id;
            _settings.Save();
        }
    }

    /// <summary>Standard profiles can't be changed; the Voice FX page shows a "duplicate to customise" bar.</summary>
    public bool IsLocked => _activeProfile?.IsBuiltIn == true;
    public bool CanEditFx => !IsLocked;

    // ---------------- mods ----------------
    public ObservableCollection<ModRow> Mods { get; } = [];
    public ObservableCollection<ProcessorRow> Processors { get; } = [];
    public ObservableCollection<string> ModFailures { get; } = [];

    /// <summary>Character voices from mods: a gallery where only one is active at a time.</summary>
    public ObservableCollection<ProcessorRow> Characters { get; } = [];

    public bool HasCharacters => Characters.Count > 0;
    public bool HasOtherProcessors => Processors.Count > 0;

    private string _activeCharacter = "None";
    public string ActiveCharacter { get => _activeCharacter; private set => Set(ref _activeCharacter, value); }

    public RelayCommand ClearCharacterCommand { get; private set; } = null!;

    // ---------------- updates & log ----------------
    public string AppVersion => _updates.CurrentVersion;

    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }

    public bool UpdateAvailable => _pendingUpdate != null;

    public ObservableCollection<string> LogLines { get; } = [];

    public RelayCommand RescanCommand { get; }
    public RelayCommand MakeDefaultCommand { get; }
    public RelayCommand OpenModsFolderCommand { get; }
    public RelayCommand OpenProfilesFolderCommand { get; }
    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand InstallUpdateCommand { get; }
    public RelayCommand NewProfileCommand { get; }
    public RelayCommand DuplicateProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand GetVirtualCableCommand { get; }
    public RelayCommand OpenSoundSettingsCommand { get; }

    public async Task InitializeAsync()
    {
        LoadProfiles();
        LoadMods();
        Controls.UiClock.Tick += OnFrame;

        await RescanAsync();

        UpdateStatus = _updates.IsInstalled ? "" : "Development build: auto-update works in installed copies only.";
        if (_updates.IsInstalled && _settings.CheckForUpdatesOnStart)
            await CheckForUpdatesAsync();
    }

    // ---------------- profiles ----------------
    private void LoadProfiles()
    {
        foreach (var p in Presets.All)
            Profiles.Add(new ProfileViewModel(p, Renamed));
        foreach (var p in _profileStore.LoadUserProfiles().OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            Profiles.Add(new ProfileViewModel(p, Renamed));
        ActiveProfile = Profiles.FirstOrDefault(p => p.Model.Id == _settings.ActiveProfileId) ?? Profiles[0];
    }

    private void Renamed(ProfileViewModel p)
    {
        if (!p.IsBuiltIn) _profileStore.Save(p.Model);
    }

    private void ApplyProfile(ProfileViewModel p)
    {
        _applyingProfile = true;
        try
        {
            _effects.ApplyState(p.Model.Effects);
            _effects.Enabled = p.Model.EffectsEnabled;
            Raise(nameof(EffectsEnabled));
            foreach (var e in Effects) e.Refresh();

            if (_device != null)
            {
                if (p.Model.Gain is { } g) Gain = g * 100;
                if (p.Model.HeadphoneVolume is { } hp) HeadphoneVolume = hp * 100;
            }
        }
        finally { _applyingProfile = false; }
        Log($"Profile \"{p.Name}\" applied");
    }

    private Profile CaptureCurrent(string name) => new()
    {
        Name = name,
        Gain = _device is null ? null : (float)(_gain / 100),
        HeadphoneVolume = HasHeadphones ? (float)(_headphoneVolume / 100) : null,
        EffectsEnabled = _effects.Enabled,
        Effects = _effects.CaptureState(),
    };

    /// <summary>Any user change to the sound: copy it into the active profile and save shortly after.</summary>
    private void OnSoundChanged()
    {
        if (_applyingProfile || _syncingFromDevice || _activeProfile is null || _activeProfile.IsBuiltIn) return;
        var m = _activeProfile.Model;
        if (_device != null)
        {
            m.Gain = (float)(_gain / 100);
            if (HasHeadphones) m.HeadphoneVolume = (float)(_headphoneVolume / 100);
        }
        m.EffectsEnabled = _effects.Enabled;
        m.Effects = _effects.CaptureState();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveActiveProfile()
    {
        _saveTimer.Stop();
        if (_activeProfile is { IsBuiltIn: false }) _profileStore.Save(_activeProfile.Model);
    }

    private void DuplicateActive()
    {
        if (_activeProfile is null) return;
        var copy = _activeProfile.Model.Clone(UniqueName(_activeProfile.Name + (_activeProfile.IsBuiltIn ? " (custom)" : " copy")));
        // A copy of a standard profile also remembers your current gain, so it's a full snapshot of your setup.
        if (_activeProfile.IsBuiltIn && _device != null) copy.Gain = (float)(_gain / 100);
        AddProfile(copy);
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var i = 2; Profiles.Any(p => p.Name.Equals(candidate, StringComparison.CurrentCultureIgnoreCase)); i++)
            candidate = $"{name} {i}";
        return candidate;
    }

    private void AddProfile(Profile profile)
    {
        _profileStore.Save(profile);
        var vm = new ProfileViewModel(profile, Renamed);
        Profiles.Add(vm);
        ActiveProfile = vm;
        Log($"Profile \"{profile.Name}\" created");
    }

    private void DeleteActiveProfile()
    {
        if (_activeProfile is not { IsBuiltIn: false } doomed) return;
        _saveTimer.Stop();
        _activeProfile = null;
        Profiles.Remove(doomed);
        _profileStore.Delete(doomed.Model);
        ActiveProfile = Profiles.LastOrDefault(p => !p.IsBuiltIn) ?? Profiles[0];
        Log($"Profile \"{doomed.Name}\" deleted");
    }

    // ---------------- mods ----------------
    private void LoadMods()
    {
        Directory.CreateDirectory(UserModsFolder);
        _mods.LoadFrom(Path.Combine(AppContext.BaseDirectory, "mods"), UserModsFolder);

        foreach (var m in _mods.Loaded)
            Mods.Add(new ModRow(m.Mod));
        foreach (var f in _mods.Failures)
        {
            ModFailures.Add($"{Path.GetFileName(f.Path)}: {f.Reason}");
            Log($"Mod failed to load: {Path.GetFileName(f.Path)} ({f.Reason})");
        }
        foreach (var p in _mods.Processors)
        {
            if (_settings.ProcessorStates.TryGetValue(p.Name, out var on)) p.Enabled = on;
            var row = new ProcessorRow(p, OnProcessorToggled);
            if (row.IsCharacter) Characters.Add(row);
            else Processors.Add(row);
        }

        // Only one character at a time, even if old settings had several on.
        foreach (var extra in Characters.Where(c => c.Enabled).Skip(1).ToList()) extra.Enabled = false;
        ActiveCharacter = Characters.FirstOrDefault(c => c.Enabled)?.Name ?? "None";
        ClearCharacterCommand = new RelayCommand(() => { foreach (var c in Characters) c.Enabled = false; }, () => Characters.Any(c => c.Enabled));
        Raise(nameof(ClearCharacterCommand));
        Raise(nameof(HasCharacters));
        Raise(nameof(HasOtherProcessors));
    }

    private void OnProcessorToggled(IAudioProcessor p)
    {
        // Picking a character switches off the previous one.
        if (p.Enabled && p is IProcessorInfo { Group: IProcessorInfo.CharacterGroup })
        {
            foreach (var other in Characters.Where(c => c.Enabled && !c.Is(p)).ToList())
                other.Enabled = false;
        }
        _settings.ProcessorStates[p.Name] = p.Enabled;
        _settings.Save();

        var active = Characters.FirstOrDefault(c => c.Enabled);
        ActiveCharacter = active?.Name ?? "None";
        if (p is IProcessorInfo { Group: IProcessorInfo.CharacterGroup })
            Log(p.Enabled ? $"Character: {p.Name}" : $"Character off: {p.Name}");
    }

    // ---------------- device lifecycle ----------------
    private async Task RescanAsync()
    {
        IReadOnlyList<UsbDeviceInfo> usb;
        try
        {
            usb = await Task.Run(UsbProbe.FindBlueDevices);
        }
        catch (Exception ex)
        {
            Log($"USB scan failed: {ex.Message}");
            usb = [];
        }

        RefreshOutputs();
        var found = _scanner.FindYeti(usb);
        _scanning = false;

        if (found?.Capture.ID == _device?.Capture.ID && found?.Usb.InstanceId == _device?.Usb.InstanceId)
        {
            found?.Dispose();
            RaiseDevice(); // default device may have changed
            return;
        }

        DetachDevice();
        if (found is null)
        {
            Log(usb.Count > 0 ? "Blue device on USB, but Windows has no active audio endpoint for it." : "No Yeti connected.");
            RaiseDevice();
            return;
        }

        _device = found;
        _device.StateChanged += (_, _) => _ui.InvokeAsync(SyncFromDevice);
        Log($"Found {found.Name} (firmware {found.Usb.FirmwareRevision})");

        foreach (var d in found.Usb.Drivers)
            Drivers.Add(d);

        await StartEngineAsync();
        if (_activeProfile != null) ApplyProfile(_activeProfile);
        SyncFromDevice();
        RaiseDevice();
    }

    /// <summary>
    /// Opens the audio engine off the UI thread with a watchdog. A wedged Windows audio driver can block these calls
    /// forever; the UI must stay responsive and tell the user what to do instead of freezing.
    /// </summary>
    private async Task StartEngineAsync()
    {
        if (_device is null) return;
        var engine = new AudioEngine(_device, _effects);
        engine.Error += (_, msg) => _ui.InvokeAsync(() => Log(msg));
        var exclusive = _settings.UltraLowLatency;

        var start = Task.Run(() => engine.Start(exclusive));
        if (await Task.WhenAny(start, Task.Delay(AudioTimeout)) != start)
        {
            DeviceProblem = "Yeti not responding. Unplug it and plug it back in.";
            Log("Windows audio did not answer within 5 s (the Yeti's driver is stuck). Replug the Yeti or restart Windows Audio.");
            _ = start.ContinueWith(_ => engine.Dispose(), TaskScheduler.Default); // clean up if it ever returns
            RaiseEngine();
            return;
        }
        if (start.IsFaulted)
        {
            Log($"Could not open the mic: {start.Exception!.GetBaseException().Message}. Check Settings > Privacy > Microphone.");
            engine.Dispose();
            RaiseEngine();
            return;
        }

        DeviceProblem = null;
        _engine = engine;
        _mods.Attach(_device, engine);
        if (exclusive && !engine.IsExclusive)
            Log("Ultra-low latency unavailable (another app is using the Yeti); using shared mode.");
        Log($"Audio engine: {EngineInfo}");
        await RouteAsync(OutputRole.Monitor, _monitorOutput);
        await RouteAsync(OutputRole.VirtualMic, _virtualMicOutput);
        RaiseEngine();
    }

    private static readonly TimeSpan AudioTimeout = TimeSpan.FromSeconds(5);

    private void RaiseEngine()
    {
        Raise(nameof(EngineInfo));
        Raise(nameof(Format));
        Raise(nameof(Engine));
        Raise(nameof(DeviceStatus));
    }

    private string? _deviceProblem;
    /// <summary>Shown instead of the normal status when the audio driver misbehaves.</summary>
    public string? DeviceProblem { get => _deviceProblem; private set { if (Set(ref _deviceProblem, value)) Raise(nameof(DeviceStatus)); } }

    private async Task RestartEngineAsync()
    {
        _mods.Attach(null, null);
        var old = _engine;
        _engine = null;
        RaiseEngine();
        if (old != null) await Task.Run(old.Dispose);
        await StartEngineAsync();
    }

    private void RefreshOutputs()
    {
        var outputs = _scanner.ListOutputs();
        _routingOutputs = true;
        try
        {
            MonitorOutputs.Clear();
            VirtualMicOutputs.Clear();
            MonitorOutputs.Add(OutputOption.Off);
            VirtualMicOutputs.Add(OutputOption.Off);
            foreach (var o in outputs.OrderBy(o => !o.IsAvailable).ThenByDescending(o => o.IsVirtualCable).ThenBy(o => o.Name))
            {
                var option = new OutputOption(o.Id, o.Name, o.IsVirtualCable, o.Unavailable);
                // Speakers go to "hear yourself"; only virtual cables can act as a mic for other apps.
                // (Allowing both on one device would play your voice twice.)
                if (o.IsVirtualCable) VirtualMicOutputs.Add(option);
                else MonitorOutputs.Add(option);
            }
            _monitorOutput = MonitorOutputs.FirstOrDefault(o => o.Id == _settings.MonitorDeviceId && o.IsAvailable) ?? OutputOption.Off;
            _virtualMicOutput = VirtualMicOutputs.FirstOrDefault(o => o.Id == _settings.VirtualMicDeviceId && o.IsAvailable) ?? OutputOption.Off;
            Raise(nameof(MonitorOutput));
            Raise(nameof(VirtualMicOutput));
        }
        finally { _routingOutputs = false; }
        Raise(nameof(HasVirtualCable));
        Raise(nameof(HasUnpluggedOutputs));
    }

    private async Task RouteAsync(OutputRole role, OutputOption option)
    {
        var engine = _engine;
        if (engine is null) return;
        var route = Task.Run(() => engine.SetOutput(role, option.Id));
        if (await Task.WhenAny(route, Task.Delay(AudioTimeout)) != route)
        {
            Log($"{option.Name} did not respond; try another output.");
            return;
        }
        if (route.IsFaulted)
            Log($"Could not open {option.Name}: {route.Exception!.GetBaseException().Message}");
        else if (option.Id != null)
            Log($"{(role == OutputRole.Monitor ? "Monitoring" : "Virtual mic")} → {option.Name}");
    }

    private void DetachDevice()
    {
        _mods.Attach(null, null);
        var old = _engine;
        _engine = null;
        // Never block the UI on driver teardown; give it a moment on app exit, then move on.
        if (old != null) Task.Run(old.Dispose).Wait(TimeSpan.FromMilliseconds(1500));
        _device?.Dispose();
        _device = null;
        Drivers.Clear();
        Raise(nameof(EngineInfo));
        Raise(nameof(Engine));
    }

    private void SyncFromDevice()
    {
        if (_device is null) return;
        _syncingFromDevice = true;
        try
        {
            Gain = _device.Gain * 100;
            IsMuted = _device.IsMuted;
            HeadphoneVolume = _device.HeadphoneVolume * 100;
        }
        finally { _syncingFromDevice = false; }
        Raise(nameof(GainLabel));
    }

    private void RaiseDevice()
    {
        foreach (var p in new[] { nameof(IsConnected), nameof(IsScanning), nameof(DeviceTitle), nameof(DeviceStatus), nameof(UsbIds),
                     nameof(Firmware), nameof(Format), nameof(IsDefault), nameof(HasHeadphones), nameof(GainLabel), nameof(EngineInfo) })
            Raise(p);
    }

    private double _statsTimer;

    private void OnFrame(double dt)
    {
        var decay = Math.Pow(0.02, dt);       // ~-34 dB per second release
        var holdDecay = Math.Pow(0.3, dt);

        var inPeak = _device?.PeakLevel ?? 0;
        var outPeak = _engine?.OutputPeak ?? 0;
        _rawIn = inPeak > _rawIn ? inPeak : _rawIn * decay;
        _rawOut = outPeak > _rawOut ? outPeak : _rawOut * decay;
        _peakHoldIn = Math.Max(_rawIn, _peakHoldIn * holdDecay);
        _peakHoldOut = Math.Max(_rawOut, _peakHoldOut * holdDecay);

        // Only push values to the UI when they move a visible amount (about one meter segment), so bindings and
        // redraws don't fire for invisible changes.
        SetIfMoved(ref _inputLevel, _rawIn, nameof(InputLevel));
        SetIfMoved(ref _outputLevel, _rawOut, nameof(OutputLevel));
        SetIfMoved(ref _inputPeak, _peakHoldIn, nameof(InputPeak));
        SetIfMoved(ref _outputPeak, _peakHoldOut, nameof(OutputPeak));
        var glow = Math.Round(Controls.LevelMeter.ToPosition(_rawIn) * 20) / 20;
        if (glow != _glow) Glow = glow;

        var db = Dsp.ToDecibels((float)_rawIn);
        InputDb = _device is null || db < -60 ? "-∞" : $"{db:0}";

        // Text stats don't need animation rate.
        _statsTimer += dt;
        if (_statsTimer < 0.25) return;
        _statsTimer = 0;

        var comp = (CompressorEffect)_effects["compressor"];
        Compression = comp.Enabled && _effects.Enabled && comp.GainReductionDb < -0.5 ? $"-{-comp.GainReductionDb:0.0} dB" : "";

        UpdateAnalysis();

        var latency = _engine?.LatencyMs(OutputRole.Monitor) ?? _engine?.LatencyMs(OutputRole.VirtualMic);
        LatencyText = latency is { } ms ? $"{ms:0} ms" : "off";
        LatencyGrade = latency switch { null => "off", < 25 => "great", < 40 => "good", _ => "high" };
    }

    private static readonly string[] NoteNames = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];

    private void UpdateAnalysis()
    {
        if (_engine is null)
        {
            LoudnessText = NoiseLevel = RoomGrade = HealthText = "—";
            LoudnessGrade = "off";
            NoiseTypes.Clear();
            return;
        }

        var lufs = _effects.Loudness.ShortTermLufs;
        // Below about -50 LUFS nobody is talking; show a dash instead of a misleading number.
        var talking = lufs > -50;
        LoudnessText = talking ? $"{lufs:0.0}" : "—";
        LoudnessGrade = !talking ? "off" : Math.Abs(lufs + 16) <= 2.5 ? "great" : Math.Abs(lufs + 16) <= 6 ? "good" : "high";

        // Classify the room from the noise canceller's learned noise spectrum (it analyses even with low strength).
        var nr = _effects.Get<NoiseReductionEffect>();
        var gate = _effects.Get<NoiseGateEffect>();
        var hum = _effects.Get<HumRemoverEffect>();
        var floor = nr.Enabled && _effects.Enabled ? nr.NoiseLevelDb : gate.NoiseFloorDb;
        NoiseLevel = $"{floor:0} dBFS";
        RoomGrade = floor < -62 ? "Studio quiet" : floor < -52 ? "Quiet room" : floor < -44 ? "Some noise" : "Noisy room";
        VoiceActive = nr.Enabled ? nr.VoiceActive : gate.IsOpen;

        var types = new List<string>();
        if (nr.Enabled && _effects.Enabled)
        {
            if (nr.LowShare > 0.45f) types.Add("Fan / rumble");
            if (nr.HissShare > 0.35f) types.Add("Hiss");
            if (nr.ClicksPerMinute >= 6) types.Add($"Keyboard · {nr.ClicksPerMinute:0}/min");
        }
        if (hum.Enabled && _effects.Enabled && hum.HumLevelDb > -72) types.Add($"{hum.MainsHz} Hz hum");
        if (types.Count == 0) types.Add(floor < -62 ? "Clean" : "Broadband");
        if (!types.SequenceEqual(NoiseTypes))
        {
            NoiseTypes.Clear();
            foreach (var t in types) NoiseTypes.Add(t);
        }

        HardwareMuted = _engine.HardwareMuted && !IsMuted;

        var h = _engine.Health;
        Healthy = h.MicGlitches + h.Underruns == 0;
        HealthText = $"{h.Underruns} dropouts · {h.MicGlitches} mic glitches · drift {h.DriftPercent:+0.00;-0.00}% · DSP {h.DspLoadPercent:0.0}%";

        var tune = _effects.Get<AutoTuneEffect>();
        if (tune.Enabled && _effects.Enabled && tune.DetectedHz > 0)
        {
            var midi = 69 + 12 * Math.Log2(tune.DetectedHz / 440);
            var note = (int)Math.Round(midi);
            var cents = (midi - note) * 100;
            PitchText = $"{NoteNames[((note % 12) + 12) % 12]}{note / 12 - 1}  {cents:+0;-0} ¢";
        }
        else PitchText = "";
    }

    private void MakeDefault()
    {
        try
        {
            _device?.MakeDefault();
            Log("Yeti set as the Windows default microphone and headphones.");
        }
        catch (Exception ex)
        {
            Log($"Could not set default device: {ex.Message}");
        }
        Raise(nameof(IsDefault));
    }

    private static void OpenModsFolder() => OpenFolder(UserModsFolder);

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async Task CheckForUpdatesAsync()
    {
        UpdateStatus = "Checking for updates…";
        try
        {
            _pendingUpdate = await _updates.CheckAsync();
            UpdateStatus = _pendingUpdate is null
                ? $"You're on the latest version ({AppVersion})."
                : $"Version {_pendingUpdate.TargetFullRelease.Version} is available.";
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update check failed: {ex.Message}";
        }
        Raise(nameof(UpdateAvailable));
    }

    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is null) return;
        try
        {
            await _updates.DownloadAsync(_pendingUpdate, p => _ui.InvokeAsync(() => UpdateStatus = $"Downloading… {p}%"));
            UpdateStatus = "Restarting to finish the update…";
            SaveActiveProfile();
            _updates.ApplyAndRestart(_pendingUpdate);
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update failed: {ex.Message}";
        }
    }

    private void Log(string line)
    {
        LogLines.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
        while (LogLines.Count > 200) LogLines.RemoveAt(LogLines.Count - 1);
    }

    public void Dispose()
    {
        Controls.UiClock.Tick -= OnFrame;
        SaveActiveProfile();
        DetachDevice();
        _mods.Dispose();
        _scanner.Dispose();
    }
}

public sealed class ModRow(IYetiMod mod)
{
    public string Name => mod.Name;
    public string Description => mod.Description;
    public string Version => $"v{mod.Version}";
}

public sealed class ProcessorRow(IAudioProcessor processor, Action<IAudioProcessor> toggled) : Observable
{
    private readonly IProcessorInfo? _info = processor as IProcessorInfo;

    public string Name => processor.Name;
    public string Emoji => _info?.Emoji ?? "🎛️";
    public string Description => _info?.Description ?? "";
    public bool IsCharacter => _info?.Group == IProcessorInfo.CharacterGroup;

    public bool Is(IAudioProcessor p) => ReferenceEquals(p, processor);

    public bool Enabled
    {
        get => processor.Enabled;
        set
        {
            if (processor.Enabled == value) return;
            processor.Enabled = value;
            toggled(processor);
            Raise();
        }
    }
}
