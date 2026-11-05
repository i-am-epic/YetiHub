namespace YetiHub.Core;

/// <summary>Something the live graphs can draw: the mic engine, or the game-audio monitor.</summary>
public interface IAnalyzerSource
{
    /// <summary>Unprocessed signal (drawn as the outline).</summary>
    AnalyzerTap InputTap { get; }

    /// <summary>Processed signal (drawn as the filled spectrum). May be the same tap as the input.</summary>
    AnalyzerTap OutputTap { get; }

    int SampleRate { get; }
}
