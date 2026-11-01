using System.Reflection;
using System.Runtime.Loader;
using YetiHub.Mods;

namespace YetiHub.Core;

public sealed record LoadedMod(IYetiMod Mod, string Path);

public sealed record ModLoadFailure(string Path, string Reason);

/// <summary>
/// Discovers mods in <c>mods/&lt;ModName&gt;/&lt;ModName&gt;.dll</c> folders and hosts them.
/// </summary>
public sealed class ModHost : IDisposable
{
    private static readonly int SupportedApiMajor = typeof(IYetiMod).Assembly.GetName().Version!.Major;

    private readonly string _dataRoot;
    private readonly List<LoadedMod> _loaded = [];
    private readonly List<ModLoadFailure> _failures = [];
    private IMicState? _mic;
    private AudioEngine? _engine;
    private readonly List<IAudioProcessor> _pendingProcessors = [];

    public ModHost(string dataRoot) => _dataRoot = dataRoot;

    public event EventHandler<string>? Logged;
    public IReadOnlyList<LoadedMod> Loaded => _loaded;
    public IReadOnlyList<ModLoadFailure> Failures => _failures;

    /// <summary>All processors mods have registered, whether or not an engine is attached yet.</summary>
    public IReadOnlyList<IAudioProcessor> Processors => _pendingProcessors;

    public void LoadFrom(params string[] modRoots)
    {
        foreach (var root in modRoots.Where(Directory.Exists))
        {
            foreach (var dir in Directory.GetDirectories(root))
            {
                var dll = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
                if (File.Exists(dll))
                    TryLoad(dll);
            }
        }
    }

    private void TryLoad(string dllPath)
    {
        try
        {
            var context = new ModLoadContext(dllPath);
            var assembly = context.LoadFromAssemblyPath(dllPath);

            var api = assembly.GetReferencedAssemblies().FirstOrDefault(a => a.Name == typeof(IYetiMod).Assembly.GetName().Name);
            if (api?.Version is { } v && v.Major != SupportedApiMajor)
            {
                _failures.Add(new ModLoadFailure(dllPath, $"built for mod API {v.Major}.x, this YetiHub supports {SupportedApiMajor}.x"));
                return;
            }

            var types = assembly.GetExportedTypes().Where(t => typeof(IYetiMod).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });
            foreach (var type in types)
            {
                var mod = (IYetiMod)Activator.CreateInstance(type)!;
                if (_loaded.Any(m => m.Mod.Id == mod.Id))
                {
                    _failures.Add(new ModLoadFailure(dllPath, $"duplicate mod id '{mod.Id}'"));
                    continue;
                }
                mod.OnLoad(new Context(this, mod));
                _loaded.Add(new LoadedMod(mod, dllPath));
                Log(mod, $"loaded v{mod.Version}");
            }
        }
        catch (Exception ex)
        {
            _failures.Add(new ModLoadFailure(dllPath, ex.GetBaseException().Message));
        }
    }

    /// <summary>Connects mods to a (new) device. Pass nulls when the Yeti is unplugged.</summary>
    public void Attach(IMicState? mic, AudioEngine? engine)
    {
        _mic = mic;
        _engine = engine;
        if (engine != null)
        {
            foreach (var p in _pendingProcessors)
                engine.AddProcessor(p);
        }
        MicChanged?.Invoke(this, mic);
    }

    private event EventHandler<IMicState?>? MicChanged;

    private void Log(IYetiMod mod, string message) => Logged?.Invoke(this, $"[{mod.Name}] {message}");

    public void Dispose()
    {
        foreach (var m in _loaded)
        {
            try { m.Mod.OnUnload(); } catch { /* a broken mod must not block shutdown */ }
        }
        _loaded.Clear();
    }

    private sealed class Context(ModHost host, IYetiMod mod) : IModContext
    {
        public string DataDirectory { get; } = Directory.CreateDirectory(Path.Combine(host._dataRoot, mod.Id)).FullName;
        public IMicState? Mic => host._mic;

        public event EventHandler<IMicState?> MicChanged
        {
            add => host.MicChanged += value;
            remove => host.MicChanged -= value;
        }

        public void RegisterProcessor(IAudioProcessor processor)
        {
            host._pendingProcessors.Add(processor);
            host._engine?.AddProcessor(processor);
        }

        public void Log(string message) => host.Log(mod, message);
    }

    /// <summary>
    /// Isolates each mod's dependencies, but shares the mod API with the host so the interface types match.
    /// </summary>
    private sealed class ModLoadContext(string mainAssemblyPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainAssemblyPath))
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IYetiMod).Assembly.GetName().Name)
                return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
