namespace YetiHub.Mods;

/// <summary>
/// Entry point of a mod. YetiHub creates one instance per mod assembly using a public parameterless constructor.
/// </summary>
public interface IYetiMod
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    Version Version { get; }

    void OnLoad(IModContext context);
    void OnUnload();
}

/// <summary>
/// What YetiHub exposes to a mod.
/// </summary>
public interface IModContext
{
    /// <summary>Folder the mod may write its own settings and data to.</summary>
    string DataDirectory { get; }

    /// <summary>Current state of the microphone; null while no Yeti is connected.</summary>
    IMicState? Mic { get; }

    event EventHandler<IMicState?> MicChanged;

    /// <summary>Adds a processor to the live audio chain (monitoring path). Processors run in registration order.</summary>
    void RegisterProcessor(IAudioProcessor processor);

    void Log(string message);
}

public interface IMicState
{
    string Name { get; }
    float Gain { get; set; }
    bool IsMuted { get; set; }
    float PeakLevel { get; }
}

/// <summary>
/// Real-time audio hook. Called on the audio thread: never block, allocate, or throw.
/// </summary>
public interface IAudioProcessor
{
    string Name { get; }
    bool Enabled { get; set; }

    /// <param name="samples">Interleaved 32-bit float samples, modified in place.</param>
    void Process(Span<float> samples, int sampleRate, int channels);
}

/// <summary>
/// Optional extra details an <see cref="IAudioProcessor"/> can expose so YetiHub can show it nicely (API 1.1+).
/// Processors in the <see cref="CharacterGroup"/> are shown as a character gallery where only one is active at a time.
/// </summary>
public interface IProcessorInfo
{
    public const string CharacterGroup = "Character";

    string Description { get; }

    /// <summary>A single emoji used as the card icon.</summary>
    string Emoji { get; }

    /// <summary><see cref="CharacterGroup"/> for voice characters; anything else is listed as a regular processor.</summary>
    string Group { get; }
}
