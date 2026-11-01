using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
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
    private TimeSpan _lastFrame;

    public MainViewModel()
    {
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => SaveActiveProfile(), _ui);
        _saveTimer.Stop();

        Effects = _effects.Effects.Select(e => new EffectViewModel(e, OnSoundChanged)).ToList();

        RescanCommand = new RelayCommand(RescanAsync);
        MakeDefaultCommand = new RelayCommand(MakeDefault, () => _device != null && !IsDefault);
        ToggleMuteCommand = new RelayCommand(() => IsMuted = !IsMuted, () => _device != null);
        OpenModsFolderCommand = new RelayCommand(OpenModsFolder);
        OpenProfilesFolderCommand = new RelayCommand(() => OpenFolder(_profileStore.Directory));
        CheckUpdatesCommand = new RelayCommand(CheckForUpdatesAsync, () => _updates.IsInstalled);
        InstallUpdateCommand = new RelayCommand(InstallUpdateAsync, () => _pendingUpdate != null);
        NewProfileCommand = new RelayCommand(() => AddProfile(CaptureCurrent("New profile")));
        DuplicateProfileCommand = new RelayCommand(() => AddProfile(ActiveProfile!.Model.Clone(ActiveProfile.Name + " copy")), () => ActiveProfile != null);
        DeleteProfileCommand = new RelayCommand(DeleteActiveProfile, () => Profiles.Count > 1);
        GetVirtualCableCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true }));

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
        : "Connected · " + _device.EndpointName;
    public string UsbIds => _device is null ? "—" : $"VID {_device.Usb.VendorId:X4} · PID {_device.Usb.ProductId:X4}";
    public string Firmware => _device?.Usb.FirmwareRevision ?? "—";
    public string Format => _device?.CaptureFormat?.Replace("WAVE_FORMAT_EXTENSIBLE ", "") ?? "—";
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
    private double _inputLevel, _inputPeak, _outputLevel, _outputPeak, _peakHoldIn, _peakHoldOut;
    public double InputLevel { get => _inputLevel; private set => Set(ref _inputLevel, value); }
    public double InputPeak { get => _inputPeak; private set => Set(ref _inputPeak, value); }
    public double OutputLevel { get => _outputLevel; private set => Set(ref _outputLevel, value); }
    public double OutputPeak { get => _outputPeak; private set => Set(ref _outputPeak, value); }

    private string _inputDb = "-∞";
    public string InputDb { get => _inputDb; private set => Set(ref _inputDb, value); }

    private string _compression = "";
    public string Compression { get => _compression; private set => Set(ref _compression, value); }

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
            if (!Set(ref _monitorOutput, value ?? OutputOption.Off) || _routingOutputs) return;
            _settings.MonitorDeviceId = _monitorOutput.Id;
            _settings.Save();
            Route(OutputRole.Monitor, _monitorOutput);
        }
    }

    private OutputOption _virtualMicOutput = OutputOption.Off;
    public OutputOption VirtualMicOutput
    {
        get => _virtualMicOutput;
        set
        {
            if (!Set(ref _virtualMicOutput, value ?? OutputOption.Off) || _routingOutputs) return;
            _settings.VirtualMicDeviceId = _virtualMicOutput.Id;
            _settings.Save();
            Route(OutputRole.VirtualMic, _virtualMicOutput);
        }
    }

    public bool HasVirtualCable => VirtualMicOutputs.Any(o => o.IsVirtualCable);
    public string LatencyText => $"≈ {AudioEngine.EstimatedLatencyMs} ms processing delay";

    // ---------------- profiles ----------------
    public ObservableCollection<ProfileViewModel> Profiles { get; } = [];

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
            ApplyProfile(value);
            _settings.ActiveProfileId = value.Model.Id;
            _settings.Save();
        }
    }

    // ---------------- mods ----------------
    public ObservableCollection<ModRow> Mods { get; } = [];
    public ObservableCollection<ProcessorRow> Processors { get; } = [];
    public ObservableCollection<string> ModFailures { get; } = [];

    // ---------------- updates & log ----------------
    public string AppVersion => _updates.CurrentVersion;

    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }

    public bool UpdateAvailable => _pendingUpdate != null;

    public ObservableCollection<string> LogLines { get; } = [];

    public RelayCommand RescanCommand { get; }
    public RelayCommand MakeDefaultCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }
    public RelayCommand OpenModsFolderCommand { get; }
    public RelayCommand OpenProfilesFolderCommand { get; }
    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand InstallUpdateCommand { get; }
    public RelayCommand NewProfileCommand { get; }
    public RelayCommand DuplicateProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand GetVirtualCableCommand { get; }

    public async Task InitializeAsync()
    {
        LoadProfiles();
        LoadMods();
        CompositionTarget.Rendering += OnFrame;

        await RescanAsync();

        UpdateStatus = _updates.IsInstalled ? "" : "Development build: auto-update works in installed copies only.";
        if (_updates.IsInstalled && _settings.CheckForUpdatesOnStart)
            await CheckForUpdatesAsync();
    }

    // ---------------- profiles ----------------
    private void LoadProfiles()
    {
        foreach (var p in _profileStore.LoadAll().OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            Profiles.Add(new ProfileViewModel(p, Renamed));
        ActiveProfile = Profiles.FirstOrDefault(p => p.Model.Id == _settings.ActiveProfileId) ?? Profiles[0];
    }

    private void Renamed(ProfileViewModel p) => _profileStore.Save(p.Model);

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
                Gain = p.Model.Gain * 100;
                if (p.Model.HeadphoneVolume is { } hp) HeadphoneVolume = hp * 100;
            }
        }
        finally { _applyingProfile = false; }
        Log($"Profile \"{p.Name}\" applied");
    }

    private Profile CaptureCurrent(string name) => new()
    {
        Name = name,
        Gain = (float)(_gain / 100),
        HeadphoneVolume = HasHeadphones ? (float)(_headphoneVolume / 100) : null,
        EffectsEnabled = _effects.Enabled,
        Effects = _effects.CaptureState(),
    };

    /// <summary>Any user change to the sound: copy it into the active profile and save shortly after.</summary>
    private void OnSoundChanged()
    {
        if (_applyingProfile || _syncingFromDevice || _activeProfile is null) return;
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
        if (_activeProfile != null) _profileStore.Save(_activeProfile.Model);
    }

    private void AddProfile(Profile profile)
    {
        _profileStore.Save(profile);
        var vm = new ProfileViewModel(profile, Renamed);
        Profiles.Add(vm);
        ActiveProfile = vm;
    }

    private void DeleteActiveProfile()
    {
        if (_activeProfile is null || Profiles.Count <= 1) return;
        var doomed = _activeProfile;
        var index = Profiles.IndexOf(doomed);
        _saveTimer.Stop();
        _activeProfile = null;
        Profiles.Remove(doomed);
        _profileStore.Delete(doomed.Model);
        ActiveProfile = Profiles[Math.Min(index, Profiles.Count - 1)];
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
            Processors.Add(new ProcessorRow(p, OnProcessorToggled));
        }
    }

    private void OnProcessorToggled(IAudioProcessor p)
    {
        _settings.ProcessorStates[p.Name] = p.Enabled;
        _settings.Save();
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

        _engine = new AudioEngine(found, _effects);
        _engine.Error += (_, msg) => _ui.InvokeAsync(() => Log(msg));
        _mods.Attach(found, _engine);
        try
        {
            _engine.Start();
            Route(OutputRole.Monitor, _monitorOutput);
            Route(OutputRole.VirtualMic, _virtualMicOutput);
        }
        catch (Exception ex)
        {
            Log($"Could not open the mic: {ex.Message}. Check Settings > Privacy > Microphone.");
        }

        if (_activeProfile != null) ApplyProfile(_activeProfile);
        SyncFromDevice();
        RaiseDevice();
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
            foreach (var o in outputs.OrderByDescending(o => o.IsVirtualCable).ThenBy(o => o.Name))
            {
                var option = new OutputOption(o.Id, o.Name, o.IsVirtualCable);
                if (!o.IsVirtualCable) MonitorOutputs.Add(option);
                VirtualMicOutputs.Add(option);
            }
            MonitorOutput = MonitorOutputs.FirstOrDefault(o => o.Id == _settings.MonitorDeviceId) ?? OutputOption.Off;
            VirtualMicOutput = VirtualMicOutputs.FirstOrDefault(o => o.Id == _settings.VirtualMicDeviceId) ?? OutputOption.Off;
        }
        finally { _routingOutputs = false; }
        Raise(nameof(HasVirtualCable));
    }

    private void Route(OutputRole role, OutputOption option)
    {
        if (_engine is null) return;
        try
        {
            _engine.SetOutput(role, option.Id is null ? null : _scanner.GetDevice(option.Id));
            if (option.Id != null) Log($"{(role == OutputRole.Monitor ? "Monitoring" : "Virtual mic")} → {option.Name}");
        }
        catch (Exception ex)
        {
            Log($"Could not open {option.Name}: {ex.Message}");
        }
    }

    private void DetachDevice()
    {
        _mods.Attach(null, null);
        _engine?.Dispose();
        _engine = null;
        _device?.Dispose();
        _device = null;
        Drivers.Clear();
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
                     nameof(Firmware), nameof(Format), nameof(IsDefault), nameof(HasHeadphones), nameof(GainLabel) })
            Raise(p);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // Rendering fires once per display refresh; scale the meter release by real elapsed time.
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;
        var dt = Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.1);
        _lastFrame = now;

        var decay = Math.Pow(0.02, dt);       // ~-34 dB per second release
        var holdDecay = Math.Pow(0.3, dt);

        var inPeak = _device?.PeakLevel ?? 0;
        var outPeak = _engine?.OutputPeak ?? 0;
        InputLevel = inPeak > _inputLevel ? inPeak : _inputLevel * decay;
        OutputLevel = outPeak > _outputLevel ? outPeak : _outputLevel * decay;
        _peakHoldIn = Math.Max(InputLevel, _peakHoldIn * holdDecay);
        _peakHoldOut = Math.Max(OutputLevel, _peakHoldOut * holdDecay);
        InputPeak = _peakHoldIn;
        OutputPeak = _peakHoldOut;

        var db = Dsp.ToDecibels((float)InputLevel);
        InputDb = _device is null || db < -60 ? "-∞" : $"{db:0}";

        var comp = (CompressorEffect)_effects["compressor"];
        Compression = comp.Enabled && _effects.Enabled && comp.GainReductionDb < -0.5 ? $"-{-comp.GainReductionDb:0.0} dB" : "";
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
        CompositionTarget.Rendering -= OnFrame;
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
    public string Name => processor.Name;

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
