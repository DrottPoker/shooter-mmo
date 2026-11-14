using System.Globalization;
using System.Text.Json;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;

namespace Mmorpg.Tools.StackStressGenerator;

// The churn of a playing scenario: the share of the bots that leaves and comes back every minute
// of the steady interval.
public sealed record PlayingChurnSettings(int PercentPerMinute)
{
    public static PlayingChurnSettings Parse(JsonElement element, string source)
    {
        StrictJson.RequireObject(element, source, "'playing.churn'");
        StrictJson.RequireOnly(element, source, "'playing.churn'", "percentPerMinute");
        if (!element.TryGetProperty("percentPerMinute", out var percent))
        {
            throw PlayingScenario.Invalid(source, "'playing.churn' needs 'percentPerMinute'.");
        }

        return new PlayingChurnSettings(PlayingScenario.ReadInt(percent, source, "'playing.churn.percentPerMinute'", 1, 50));
    }
}

public enum PlayingChurnKind
{
    // The bot comes back while its body still stands and takes it over.
    Body,

    // The bot comes back after its body is gone and enters from its saved place.
    Saved
}

// One leave and return of the churn: when it is due after the start of the steady interval, the
// bot, the kind, whether the bot logs its account out, and the time it stays away, counted from
// the leave for the body kind and from the end of the body's time for the saved kind.
public sealed record PlayingChurnEvent(
    int Sequence,
    TimeSpan Offset,
    int BotIndex,
    PlayingChurnKind Kind,
    bool Logout,
    TimeSpan Delay);

// The schedule of the churn, from the seed alone: the same seed, settings, bots, and steady
// interval give the same events. The events are spread evenly over the steady interval with a
// seeded jitter; the bots take turns in a seeded order, so a bot leaves again only after every
// other bot did. The kinds alternate and the logouts alternate per pair, so any four aligned
// events hold every combination of kind and logout.
public static class PlayingChurnSchedule
{
    // No event is due in this last part of the steady interval: the longest wait for a safe
    // moment, the body's 20 seconds, the longest wait after it, and the join and its check fit
    // before the steady interval ends.
    public static readonly TimeSpan QuietTail = TimeSpan.FromSeconds(75);

    // A body-kind return starts this long after the leave, well inside the body's 20 seconds.
    public static readonly TimeSpan MinimumBodyDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaximumBodyDelay = TimeSpan.FromSeconds(8);

    // A saved-kind return starts this long after the body's time is up, when its release is done.
    public static readonly TimeSpan MinimumSavedDelay = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan MaximumSavedDelay = TimeSpan.FromSeconds(10);

    public static IReadOnlyList<PlayingChurnEvent> Create(
        PlayingChurnSettings settings,
        IReadOnlyCollection<int> botIndexes,
        TimeSpan steadyDuration,
        int seed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(botIndexes);
        var window = steadyDuration - QuietTail;
        var perMinute = botIndexes.Count * settings.PercentPerMinute / 100d;
        if (botIndexes.Count == 0 || window <= TimeSpan.Zero || perMinute <= 0d)
        {
            return [];
        }

        var order = botIndexes.Order().ToArray();
        var random = new Random(PlayingRoster.Mix(seed, 0x43485552));
        for (var index = order.Length - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (order[index], order[other]) = (order[other], order[index]);
        }

        var interval = TimeSpan.FromSeconds(60d / perMinute);
        var kindStart = random.Next(2);
        var logoutStart = random.Next(2);
        var events = new List<PlayingChurnEvent>();
        for (var sequence = 0; ; sequence++)
        {
            var jitter = (random.NextDouble() - 0.5d) * 0.5d;
            var offset = interval * (sequence + 0.5d + jitter);
            if (offset >= window)
            {
                break;
            }

            var kind = (sequence + kindStart) % 2 == 0 ? PlayingChurnKind.Body : PlayingChurnKind.Saved;
            var pairLogout = ((sequence / 2) + logoutStart) % 2 == 0;
            var logout = sequence % 2 == 0 ? pairLogout : !pairLogout;
            var delay = kind == PlayingChurnKind.Body
                ? Between(random, MinimumBodyDelay, MaximumBodyDelay)
                : Between(random, MinimumSavedDelay, MaximumSavedDelay);
            events.Add(new PlayingChurnEvent(sequence, offset, order[sequence % order.Length], kind, logout, delay));
        }

        return events;
    }

    private static TimeSpan Between(Random random, TimeSpan minimum, TimeSpan maximum)
    {
        return minimum + TimeSpan.FromMilliseconds(Math.Round((maximum - minimum).TotalMilliseconds * random.NextDouble()));
    }
}

public enum PlayingLeaveReadiness
{
    // The bot may leave now.
    Ready,

    // The bot is calm, but an aggressive Mob stands too close to leave a body there.
    TooCloseToMobs,

    // The bot fights, is dead, waits for an answer, or is not set up yet.
    Busy
}

// When a churning bot leaves, like a player who logs out: out of combat, with no Mob after it and
// no intent in flight, and away from aggressive Mobs, so its body is not attacked while it
// stands. A bot too close to them first walks away from them.
public static class PlayingLeaveSafety
{
    // An aggressive Mob detects a player within 8 m, 10 m for the alpha, so a body this far from
    // every aggressive Mob is not found in the first seconds after the leave, in which the check
    // could not tell a hit from lost health.
    public const float SafeMobDistance = 12f;

    // A bot too close to aggressive Mobs walks to a point this far beyond the safe distance from
    // the nearest one.
    public const float RetreatDistance = 20f;

    // A bot whose speed is below this, on the ground, stands.
    public const float StandingSpeed = 0.05f;

    public static PlayingLeaveReadiness Evaluate(IPlayingBotBody body, bool roleSetUp)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!roleSetUp
            || !body.IsReady
            || body.HasPendingIntent
            || body.View.OwnCombat is not { LifeState: RealtimeCombatLifeState.Alive, InCombat: false }
            || IsAttacked(body))
        {
            return PlayingLeaveReadiness.Busy;
        }

        return NearestAggressiveMob(body) is { } mob
            && Distance(body.Position, mob) < SafeMobDistance
                ? PlayingLeaveReadiness.TooCloseToMobs
                : PlayingLeaveReadiness.Ready;
    }

    // A point with ground away from the nearest aggressive Mob, or null when there is none.
    public static SimulationVector3? RetreatPoint(IPlayingBotBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (NearestAggressiveMob(body) is not { } mob)
        {
            return null;
        }

        var dx = body.Position.X - mob.X;
        var dz = body.Position.Z - mob.Z;
        var length = MathF.Sqrt((dx * dx) + (dz * dz));
        if (length < 0.01f)
        {
            (dx, dz, length) = (1f, 0f, 1f);
        }

        var x = mob.X + (dx / length * (RetreatDistance + SafeMobDistance));
        var z = mob.Z + (dz / length * (RetreatDistance + SafeMobDistance));
        return body.TryFindGround(x, z, out var ground) ? ground : null;
    }

    // On the ground and without planar speed. A grounded character keeps the movement settings'
    // small downward velocity that holds it to the ground, so the vertical speed does not count.
    public static bool IsStanding(IPlayingBotBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return body.View.OwnState is { IsGrounded: true } state
            && MathF.Sqrt((state.VelocityX * state.VelocityX) + (state.VelocityZ * state.VelocityZ)) < StandingSpeed;
    }

    private static bool IsAttacked(IPlayingBotBody body)
    {
        var own = body.View.OwnEntityId;
        return body.View.Combatants.Values.Any(combatant =>
            combatant.TargetEntityId == own
            && combatant.LifeState == RealtimeCombatLifeState.Alive
            && combatant.EntityId != own);
    }

    private static SimulationVector3? NearestAggressiveMob(IPlayingBotBody body)
    {
        SimulationVector3? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var actor in body.View.Actors.Values)
        {
            if (actor.Kind != RealtimeWorldActorKind.Mob
                || body.GameData.AggroRange(actor.ActorDefinitionId) <= 0f
                || !body.View.Combatants.TryGetValue(actor.EntityId, out var combatant)
                || combatant.LifeState != RealtimeCombatLifeState.Alive)
            {
                continue;
            }

            var position = new SimulationVector3(actor.Position.X, actor.Position.Y, actor.Position.Z);
            var distance = Distance(body.Position, position);
            if (distance < nearestDistance)
            {
                nearest = position;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    private static float Distance(SimulationVector3 left, SimulationVector3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}

// What the bot knew when it left: where it stood, its vitals, when the leave was accepted, its
// simulation session, and how long the worker said its body stays.
public sealed record PlayingLeaveSnapshot(
    SimulationVector3 Position,
    int Health,
    int Mana,
    int MaximumHealth,
    DateTime LeftAt,
    string SimulationSessionId,
    int BodySeconds);

// What the bot sees after its join: whether GameService placed it as the reconnect of its body,
// the place of the join, and its first owner combat state.
public sealed record PlayingRejoinObservation(
    bool IsReconnect,
    SimulationVector3 Position,
    int Health,
    int MaximumHealth,
    RealtimeCombatLifeState LifeState,
    DateTime ObservedAt);

// The character's saved place and vitals as GameService stores them, and the session that wrote
// them.
public sealed record PlayingSavedState(SimulationVector3 Position, int Health, int Mana, Guid SimulationSessionId);

// What the disposable database says about a body that is gone, read after the join ticket showed
// that its session ended, with every time on the generator's clock: when the session was
// released, the character's latest death, and the saved state.
public sealed record PlayingRejoinFacts(DateTime? ReleasedAt, DateTime? LastDeathAt, PlayingSavedState? Saved);

// The judgement of one rejoin: whether the player finds its character as it left it, the
// outcome, and on a mismatch the failure code and the evidence.
public sealed record PlayingRejoinVerdict(bool Passed, string Outcome, string? Code, string Detail)
{
    public bool BodyWasHit => Outcome == PlayingRejoinCheck.RestoredAfterHit;

    public bool BodyDied => Outcome == PlayingRejoinCheck.RespawnedAfterDeath;
}

// What a player sees when it comes back, checked like a player would notice it:
// - A body it takes over stands where it left it, since a body never moves, and has at most the
//   health it regenerated since. It may have been hit while it stood, which the bot cannot see,
//   so its health has no lower bound.
// - A character that enters from its saved place stands where it left and has the health it had,
//   plus what its body regenerated until it was gone, unless the body died, which brings it back
//   at the World spawn with full health. The release time tells a body that was hit: every hit
//   keeps a body its full time after the hit, so a body released clearly after its time was hit,
//   and only the place and the most it can have regenerated count then. The join must also give
//   the bot exactly the state that GameService saved, and that state must be the one the body's
//   session wrote.
public static class PlayingRejoinCheck
{
    public const string Resumed = "resumed";
    public const string ResumedDead = "resumed_dead";
    public const string Restored = "restored";
    public const string RestoredAfterHit = "restored_after_hit";
    public const string RespawnedAfterDeath = "respawned_after_death";

    // The bot stands, on the ground and without speed, before it leaves, so its body stays
    // exactly where the owner snapshot showed it, and the saved state keeps the simulation's
    // floats. Five centimeters cover the rounding of a float and a standing character's ground
    // contact, far below anything a player would notice.
    public const float PositionTolerance = 0.05f;

    // A body is released this long after its time at most when nothing hit it: the tick of its
    // end and the release call to GameService.
    public static readonly TimeSpan ReleaseSlack = TimeSpan.FromSeconds(1.5);

    // A death this long before the leave still belongs to the body; a bot leaves only out of
    // combat, so any death before that is an earlier one.
    private static readonly TimeSpan DeathSlack = TimeSpan.FromSeconds(1);

    public static PlayingRejoinVerdict Judge(
        PlayingLeaveSnapshot leave,
        PlayingRejoinObservation rejoin,
        PlayingRejoinFacts? facts,
        int healthPerSecond)
    {
        ArgumentNullException.ThrowIfNull(leave);
        ArgumentNullException.ThrowIfNull(rejoin);
        var evidence = Invariant(
            $"left at {Format(leave.Position)} with health {leave.Health}/{leave.MaximumHealth}; came back {(rejoin.ObservedAt - leave.LeftAt).TotalSeconds:0.0} s later at {Format(rejoin.Position)} with health {rejoin.Health}/{rejoin.MaximumHealth}");
        return rejoin.IsReconnect
            ? JudgeBody(leave, rejoin, healthPerSecond, evidence)
            : JudgeSaved(leave, rejoin, facts, healthPerSecond, evidence);
    }

    // The most health a character can have after regenerating for a while from a start.
    public static int MostHealth(int start, int maximum, int healthPerSecond, TimeSpan elapsed)
    {
        var seconds = Math.Max(0d, Math.Ceiling(elapsed.TotalSeconds)) + 1d;
        return (int)Math.Min(maximum, start + (healthPerSecond * seconds));
    }

    private static PlayingRejoinVerdict JudgeBody(
        PlayingLeaveSnapshot leave,
        PlayingRejoinObservation rejoin,
        int healthPerSecond,
        string evidence)
    {
        if (Distance(leave.Position, rejoin.Position) > PositionTolerance)
        {
            return Fail("rejoin_place_mismatch", "the body it took over stood elsewhere", evidence);
        }

        if (rejoin.LifeState == RealtimeCombatLifeState.Dead)
        {
            // A body that a Mob killed while the player came back; the death is the player's.
            return new PlayingRejoinVerdict(true, ResumedDead, null, evidence);
        }

        var most = MostHealth(leave.Health, leave.MaximumHealth, healthPerSecond, rejoin.ObservedAt - leave.LeftAt);
        return rejoin.Health > most
            ? Fail("rejoin_health_mismatch", Invariant($"the body has more health than {most}, all it could regenerate"), evidence)
            : new PlayingRejoinVerdict(true, Resumed, null, evidence);
    }

    private static PlayingRejoinVerdict JudgeSaved(
        PlayingLeaveSnapshot leave,
        PlayingRejoinObservation rejoin,
        PlayingRejoinFacts? facts,
        int healthPerSecond,
        string evidence)
    {
        if (facts?.Saved is not { } saved)
        {
            return Fail("rejoin_state_not_saved", "GameService holds no saved state for the character", evidence);
        }

        evidence += Invariant($"; saved at {Format(saved.Position)} with health {saved.Health} by session {saved.SimulationSessionId:D}");
        if (!Guid.TryParse(leave.SimulationSessionId, out var session) || session != saved.SimulationSessionId)
        {
            return Fail("rejoin_state_not_saved", $"the saved state is not the one of the body's session {leave.SimulationSessionId}", evidence);
        }

        if (Distance(saved.Position, rejoin.Position) > PositionTolerance
            || rejoin.Health < saved.Health
            || rejoin.Health > Math.Min(rejoin.MaximumHealth, saved.Health + (2 * healthPerSecond)))
        {
            return Fail("rejoin_restore_mismatch", "the join did not restore the saved state", evidence);
        }

        if (facts.LastDeathAt is { } died && died >= leave.LeftAt - DeathSlack)
        {
            return saved.Health == leave.MaximumHealth
                ? new PlayingRejoinVerdict(true, RespawnedAfterDeath, null, evidence + "; the body died")
                : Fail("rejoin_health_mismatch", "the body died, but the character did not come back with full health", evidence);
        }

        if (Distance(leave.Position, saved.Position) > PositionTolerance)
        {
            return Fail("rejoin_place_mismatch", "the character did not come back where it left", evidence);
        }

        var standing = facts.ReleasedAt is { } released ? released - leave.LeftAt : rejoin.ObservedAt - leave.LeftAt;
        var most = MostHealth(leave.Health, leave.MaximumHealth, healthPerSecond, standing);
        if (saved.Health > most)
        {
            return Fail("rejoin_health_mismatch", Invariant($"the character has more health than {most}, all its body could regenerate"), evidence);
        }

        var hit = facts.ReleasedAt is null || standing > TimeSpan.FromSeconds(leave.BodySeconds) + ReleaseSlack;
        evidence += Invariant($"; the body stood {standing.TotalSeconds:0.0} s");
        if (!hit && saved.Health < leave.Health)
        {
            return Fail("rejoin_health_mismatch", "the character lost health although nothing hit its body", evidence);
        }

        return new PlayingRejoinVerdict(true, hit ? RestoredAfterHit : Restored, null, evidence);
    }

    private static PlayingRejoinVerdict Fail(string code, string reason, string evidence)
    {
        return new PlayingRejoinVerdict(false, "mismatch", code, reason + ": " + evidence);
    }

    private static float Distance(SimulationVector3 left, SimulationVector3 right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        var dz = left.Z - right.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static string Format(SimulationVector3 position)
    {
        return Invariant($"({position.X:0.000}, {position.Y:0.000}, {position.Z:0.000})");
    }

    private static string Invariant(FormattableString text)
    {
        return text.ToString(CultureInfo.InvariantCulture);
    }
}

// How fast a character regenerates out of combat, from the combat rules the worker loads.
public sealed record PlayingRegeneration(int HealthPerSecond, int ManaPerSecond)
{
    public static PlayingRegeneration Load(string combatRulesPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(combatRulesPath));
        var player = document.RootElement.GetProperty("player");
        return new PlayingRegeneration(
            player.GetProperty("healthRegenerationPerSecond").GetInt32(),
            player.GetProperty("manaRegenerationPerSecond").GetInt32());
    }
}

// The way back of one churning bot, which the admission provider walks: the account logout
// right after the leave or none, the wait until the return, the login after a logout, the
// character list, the Shards, and the join ticket.
public sealed record PlayingRejoinPlan(
    int BotIndex,
    bool Logout,
    long RejoinAtTimestamp,
    DateTime LeftAt,
    string SimulationSessionId);

// The join ticket of the way back, whether GameService placed it as the reconnect of the body,
// when the way back started, and for a body that is gone what the database says about it.
public sealed record PlayingRejoinTicket(
    StressBotAdmission Admission,
    bool IsReconnect,
    long WayBackStartedTimestamp,
    PlayingRejoinFacts? Facts);

// A rejoin whose check failed, with its evidence, for the report.
public sealed record PlayingRejoinMismatch(int BotIndex, string Operation, string Code, string Detail);
