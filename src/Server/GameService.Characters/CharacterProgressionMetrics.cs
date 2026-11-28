using System.Diagnostics.Metrics;

namespace Mmorpg.GameService.Characters;

// The experience that grants counted and the levels they gained, by source: kill, quest, or admin.
// A grant counts in the transaction that makes it, so one whose transaction rolls back later still
// counts. Setting a level is no level-up and counts nothing.
internal sealed class CharacterProgressionMetrics : IDisposable
{
    public const string MeterName = "Mmorpg.GameService.Characters";
    public const string SourceTag = "source";

    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> experienceGranted;
    private readonly Counter<long> levelUps;

    public CharacterProgressionMetrics()
    {
        experienceGranted = meter.CreateCounter<long>("game_service.characters.experience.granted");
        levelUps = meter.CreateCounter<long>("game_service.characters.level_ups");
    }

    public void RecordGrant(string source, long experience, int levels)
    {
        var tag = new KeyValuePair<string, object?>(SourceTag, source);
        if (experience > 0)
        {
            experienceGranted.Add(experience, tag);
        }

        if (levels > 0)
        {
            levelUps.Add(levels, tag);
        }
    }

    public void Dispose()
    {
        meter.Dispose();
    }
}
