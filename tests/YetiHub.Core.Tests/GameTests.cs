using YetiHub.Core.Games;
using YetiHub.Core.Profiles;

namespace YetiHub.Core.Tests;

public class GameTests
{
    // Bands: Bass, Low mids, Mids, Footsteps, Highs (dB).
    private static GameAudioStats Stats(double lufs, double clip, params double[] bands) => new(lufs, -3, clip, bands);

    [Fact]
    public void Silence_waits_for_game_audio()
    {
        var insights = Stats(-70, 0, -100, -100, -100, -100, -100).Insights();
        Assert.Single(insights);
        Assert.Equal("Waiting for game audio", insights[0].Title);
    }

    [Fact]
    public void Heavy_bass_is_reported_as_masking_footsteps()
    {
        var insights = Stats(-18, 0, -20, -28, -32, -42, -48).Insights();
        Assert.Contains(insights, i => i.Title == "Bass is masking footsteps" && i.Level == "warn");
    }

    [Fact]
    public void Clipping_and_loudness_are_flagged_first()
    {
        var insights = Stats(-9, 0.4, -25, -27, -28, -30, -36).Insights();
        Assert.Equal("Game audio is clipping", insights[0].Title);
        Assert.Contains(insights, i => i.Title == "Very loud mix");
    }

    [Fact]
    public void Close_bass_and_footstep_levels_read_as_clear()
    {
        var insights = Stats(-20, 0, -30, -31, -32, -34, -40).Insights();
        Assert.Contains(insights, i => i.Title == "Footsteps are clear");
    }

    [Fact]
    public void Footsteps_louder_than_bass_read_as_standing_out()
    {
        var insights = Stats(-20, 0, -40, -38, -36, -34, -38).Insights();
        Assert.Contains(insights, i => i.Title == "Footsteps stand out" && i.Detail.Contains("6 dB louder"));
    }

    [Fact]
    public void Quiet_game_mix_gets_advice()
    {
        var insights = Stats(-38, 0, -50, -52, -53, -55, -58).Insights();
        Assert.Contains(insights, i => i.Title == "Quiet mix");
    }

    [Fact]
    public void Every_known_game_recommends_an_existing_standard_profile()
    {
        var ids = Presets.All.Select(p => p.Id).ToHashSet();
        Assert.All(GameDetector.Games, g => Assert.Contains(g.RecommendedProfileId!, ids));
        Assert.Equal(GameDetector.Games.Count, GameDetector.Games.Select(g => g.Id).Distinct().Count());
        Assert.Contains(GameDetector.Games, g => g.ProcessNames.Contains("VALORANT-Win64-Shipping"));
    }
}
