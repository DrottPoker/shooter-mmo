using System.Globalization;
using Mmorpg.Server.Common.Progression;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Progression;

// Prints the level table of the checked-in progression rules for balancing: what every level
// needs, what a Mob of the character's own level gives, and how long that takes at a steady
// kill rate.
const string Usage = "Usage: ProgressionTable [--kills-per-hour <number>] [--every <levels>]";

if (!TryParse(args, out var killsPerHour, out var every, out var argumentError))
{
    Console.Error.WriteLine(argumentError);
    Console.Error.WriteLine(Usage);
    return 1;
}

ProgressionRules rules;
try
{
    var repositoryRoot = ContentLayout.FindRepositoryRoot(Directory.GetCurrentDirectory());
    rules = ProgressionRulesFileLoader.Load(
        ContentLayout.ProgressionRulesPath(ContentLayout.ContentRoot(repositoryRoot)));
}
catch (Exception exception) when (exception is IOException or InvalidDataException)
{
    Console.Error.WriteLine("Could not read the progression rules: " + exception.Message);
    return 1;
}

var culture = CultureInfo.InvariantCulture;
var header = new[]
{
    "Level", "XP to next", "Mob XP", "Mobs", "Minutes", "Hours to reach", "Counted up to", "No XP at or below"
};
var rows = new List<string[]>();
var hoursBefore = 0d;
for (var level = 1; level <= rules.MaximumCharacterLevel; level++)
{
    var experience = rules.ExperienceToNextLevel(level);
    var mobExperience = rules.MobBaseExperience(level);
    var mobs = experience == 0 ? 0 : (experience + mobExperience - 1) / mobExperience;
    var minutes = mobs * 60d / killsPerHour;
    if (level == 1 || level % every == 0 || level == rules.MaximumCharacterLevel)
    {
        var withoutExperience = rules.HighestMobLevelWithoutExperience(level);
        rows.Add(
        [
            level.ToString(culture),
            experience.ToString(culture),
            mobExperience.ToString(culture),
            mobs.ToString(culture),
            minutes.ToString("0.0", culture),
            hoursBefore.ToString("0.0", culture),
            rules.HighestCountedMobLevel(level).ToString(culture),
            withoutExperience < 1 ? "none" : withoutExperience.ToString(culture)
        ]);
    }

    hoursBefore += minutes / 60d;
}

var widths = header.Select((title, column) => Math.Max(title.Length, rows.Max(row => row[column].Length))).ToArray();
Console.WriteLine(Format(header, widths));
Console.WriteLine(string.Join("  ", widths.Select(width => new string('-', width))));
foreach (var row in rows)
{
    Console.WriteLine(Format(row, widths));
}

var totalExperience = 0L;
for (var level = 1; level < rules.MaximumCharacterLevel; level++)
{
    totalExperience += rules.ExperienceToNextLevel(level);
}

Console.WriteLine();
Console.WriteLine(
    $"Reaching level {rules.MaximumCharacterLevel.ToString(culture)} takes {totalExperience.ToString(culture)} experience, "
    + $"{hoursBefore.ToString("0.0", culture)} hours at {killsPerHour.ToString(culture)} kills per hour of Mobs of the character's own level.");
return 0;

static string Format(string[] cells, int[] widths)
{
    return string.Join("  ", cells.Select((cell, column) => cell.PadLeft(widths[column]))).TrimEnd();
}

static bool TryParse(string[] arguments, out double killsPerHour, out int every, out string error)
{
    killsPerHour = 120d;
    every = 10;
    error = string.Empty;
    for (var index = 0; index < arguments.Length; index++)
    {
        var name = arguments[index];
        if (index + 1 >= arguments.Length)
        {
            error = $"{name} needs a value.";
            return false;
        }

        var value = arguments[++index];
        switch (name)
        {
            case "--kills-per-hour":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out killsPerHour)
                    || !double.IsFinite(killsPerHour)
                    || killsPerHour <= 0d)
                {
                    error = "--kills-per-hour must be a number above 0.";
                    return false;
                }

                break;
            case "--every":
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out every) || every < 1)
                {
                    error = "--every must be a whole number of at least 1.";
                    return false;
                }

                break;
            default:
                error = $"Unknown argument {name}.";
                return false;
        }
    }

    return true;
}
