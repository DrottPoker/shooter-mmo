using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.ContentPipeline;
using Mmorpg.Tools.SimulationBotClient;
using Mmorpg.Tools.WorldActorCompiler;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Content;

namespace Mmorpg.Tools.StackStressGenerator;

public sealed record StressWorldVariantResult(
    string WorldId,
    string WorkerWorldDataPath,
    int AddedInstances,
    string SpawnRevision,
    string CollisionRevision,
    string CheckedInCollisionRevision)
{
    public bool KeepsCollisionRevision =>
        string.Equals(CollisionRevision, CheckedInCollisionRevision, StringComparison.Ordinal);
}

// Writes a load variant of a World into a run folder: it copies the World's authoring data and
// the actor definitions from content/, adds the scenario's Mob areas and node groups to the
// spawn authoring, and runs the real World actor and World collision compilers into a
// generated root inside the run folder. The worker and the bots then read the variant's World
// data folder. content/ and the checked-in generated data are only read. Every added actor
// must stand on walkable ground; otherwise the variant fails with the area's name.
public static class StressWorldVariant
{
    public const string VariantIdPrefix = "load_";

    // A Mob of a variant area must be this far from the World spawn.
    public const float MinimumMobDistanceFromSpawn = 12f;

    // A reachable spawn point lies this close to the end of the route the planner finds to it.
    private const float ReachTolerance = 1f;

    public static string WorkerWorldsPath(string variantRoot)
    {
        return Path.Combine(variantRoot, "worker");
    }

    public static StressWorldVariantResult Write(
        StressLoadScenario scenario,
        string variantRoot,
        string repositoryRoot,
        TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(log);
        var variant = scenario.WorldVariant
            ?? throw new StressGeneratorOptionException($"The scenario '{scenario.Name}' has no World variant.");
        variantRoot = Path.GetFullPath(variantRoot);
        if (Directory.Exists(variantRoot) && Directory.EnumerateFileSystemEntries(variantRoot).Any())
        {
            throw new StressGeneratorOptionException(
                $"The World variant folder '{variantRoot}' is not empty; every run writes its own.");
        }

        var worldId = scenario.WorldId;
        var repositoryContent = ContentLayout.ContentRoot(repositoryRoot);
        var repositoryGenerated = ContentLayout.GeneratedRoot(repositoryRoot);
        var content = Path.Combine(variantRoot, "content");
        var generated = Path.Combine(variantRoot, "generated");

        CopyDirectory(
            ContentLayout.ActorsDirectory(repositoryContent),
            ContentLayout.ActorsDirectory(content));
        var worldDirectory = ContentLayout.WorldDirectory(content, worldId);
        Directory.CreateDirectory(worldDirectory);
        foreach (var fileName in new[] { "world.json", ContentLayout.CollisionSourceFileName })
        {
            File.Copy(
                Path.Combine(ContentLayout.WorldDirectory(repositoryContent, worldId), fileName),
                Path.Combine(worldDirectory, fileName));
        }

        var spawns = WorldActorContent.LoadSpawns(repositoryContent, worldId);
        AddVariantActors(spawns, variant);
        File.WriteAllText(
            ContentLayout.ActorSpawnsPath(content, worldId),
            StrictContentJson.Serialize(spawns));
        Directory.CreateDirectory(Path.GetDirectoryName(ContentLayout.GeneratedItemCatalogPath(generated))!);
        File.Copy(
            ContentLayout.GeneratedItemCatalogPath(repositoryGenerated),
            ContentLayout.GeneratedItemCatalogPath(generated));

        string[] compilerArguments = ["--content-root", content, "--generated-root", generated];
        RunCompiler(typeof(WorldActorContent).Assembly, compilerArguments, log);
        RunCompiler(Assembly.Load("Mmorpg.Tools.WorldCollisionCompiler"), compilerArguments, log);

        // The worker keeps loading its own actor catalog, so the variant's must be the same.
        if (!string.Equals(
                File.ReadAllText(ContentLayout.GeneratedActorCatalogPath(generated)),
                StrictContentJson.NormalizeLineEndings(File.ReadAllText(ContentLayout.GeneratedActorCatalogPath(repositoryGenerated))),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The variant's compiled actor catalog differs from the checked-in one; compile the content first.");
        }

        var workerWorlds = WorkerWorldsPath(variantRoot);
        var workerWorld = Path.Combine(workerWorlds, worldId);
        Directory.CreateDirectory(workerWorld);
        File.Copy(Path.Combine(worldDirectory, "world.json"), Path.Combine(workerWorld, "world.json"));
        File.Copy(
            ContentLayout.GeneratedWorldActorSpawnsPath(generated, worldId),
            Path.Combine(workerWorld, ContentLayout.WorldActorSpawnsFileName));
        CopyDirectory(
            ContentLayout.GeneratedCollisionDirectory(generated, worldId),
            Path.Combine(workerWorld, "Collision"));

        var compiled = WorldActorJson.DeserializeSpawnRuntime(
            File.ReadAllText(ContentLayout.GeneratedWorldActorSpawnsPath(generated, worldId)));
        var collision = SimulationBotNavigation.LoadCollisionWorld(workerWorlds, worldId);
        var checkedInCollision = SimulationBotNavigation.LoadCollisionDirectory(
            ContentLayout.GeneratedCollisionDirectory(repositoryGenerated, worldId),
            worldId);
        var settings = MovementSettings(repositoryRoot, Path.Combine(workerWorld, "world.json"));
        var spawn = WorldSpawn(Path.Combine(workerWorld, "world.json"));
        var added = 0;
        foreach (var area in variant.Areas)
        {
            added += ValidateInstances(
                area.Name,
                VariantId(worldId, area.Name),
                new SimulationVector3(area.Area.CenterX, 0f, area.Area.CenterZ),
                compiled,
                collision,
                settings,
                spawn,
                checkSpawnDistance: true);
        }

        foreach (var group in variant.Groups)
        {
            // A node blocks movement where it stands, so its ground is checked on the World
            // without the variant's nodes.
            added += ValidateInstances(
                group.Name,
                VariantId(worldId, group.Name),
                new SimulationVector3(group.CenterX, 0f, group.CenterZ),
                compiled,
                checkedInCollision,
                settings,
                spawn,
                checkSpawnDistance: false);
        }

        return new StressWorldVariantResult(
            worldId,
            workerWorlds,
            added,
            compiled.Revision,
            collision.Revision,
            checkedInCollision.Revision);
    }

    // The spawn ids of a variant's actors, unique beside the World's own spawns.
    public static string VariantId(string worldId, string name)
    {
        return worldId.Replace('-', '_') + "." + VariantIdPrefix + name.Replace('-', '_');
    }

    private static void AddVariantActors(WorldActorSpawnAuthoringDocument spawns, WorldVariantScenario variant)
    {
        var areas = (spawns.SpawnAreas ?? []).ToList();
        for (var index = 0; index < variant.Areas.Count; index++)
        {
            var area = variant.Areas[index];
            areas.Add(new WorldActorSpawnAreaAuthoringEntry
            {
                Id = VariantId(spawns.WorldId, area.Name),
                ActorId = area.ActorId,
                MinimumX = area.Area.MinimumX,
                MinimumZ = area.Area.MinimumZ,
                MaximumX = area.Area.MaximumX,
                MaximumZ = area.Area.MaximumZ,
                Y = 0f,
                YawDegrees = 0f,
                Count = area.Count,
                Seed = 9000 + index,
                PatrolPathId = string.Empty
            });
        }

        var groups = (spawns.SpawnGroups ?? []).ToList();
        foreach (var group in variant.Groups)
        {
            groups.Add(new WorldActorSpawnGroupAuthoringEntry
            {
                Id = VariantId(spawns.WorldId, group.Name),
                ActorId = group.ActorId,
                CenterX = group.CenterX,
                CenterY = 0f,
                CenterZ = group.CenterZ,
                YawDegrees = 0f,
                Rows = group.Rows,
                Columns = group.Columns,
                Spacing = group.Spacing,
                PatrolPathId = string.Empty
            });
        }

        spawns.SpawnAreas = areas.ToArray();
        spawns.SpawnGroups = groups.ToArray();
    }

    // Every instance of one area or group stands on walkable ground that a route from the
    // area's center reaches, as the worker places a Mob on the ground under its spawn.
    private static int ValidateInstances(
        string name,
        string spawnDefinitionId,
        SimulationVector3 center,
        WorldActorSpawnRuntimeDocument compiled,
        ICollisionWorld collision,
        MovementSimulationSettings settings,
        SimulationVector3 worldSpawn,
        bool checkSpawnDistance)
    {
        var probe = new PlayingGroundProbe(collision, settings);
        if (!probe.TryFindGround(center.X, center.Y, center.Z, out var centerGround))
        {
            throw new StressGeneratorOptionException(
                $"The variant area '{name}' has no walkable ground at its center ({Format(center.X)}, {Format(center.Z)}).");
        }

        var planner = new MovementPathPlanner(collision, settings, MovementPathPlannerSettings.Default);
        var instances = compiled.SpawnInstances
            .Where(instance => string.Equals(instance.SpawnDefinitionId, spawnDefinitionId, StringComparison.Ordinal))
            .ToArray();
        if (instances.Length == 0)
        {
            throw new InvalidDataException($"The compiled spawns have no instance of the variant area '{name}'.");
        }

        foreach (var instance in instances)
        {
            var at = $"({Format(instance.X)}, {Format(instance.Z)})";
            if (checkSpawnDistance
                && PlanarDistance(instance.X, instance.Z, worldSpawn.X, worldSpawn.Z) < MinimumMobDistanceFromSpawn)
            {
                throw new StressGeneratorOptionException(
                    $"The variant area '{name}' places a Mob at {at}, closer than {Format(MinimumMobDistanceFromSpawn)} m to the World spawn.");
            }

            if (!probe.TryFindGround(instance.X, instance.Y, instance.Z, out var ground))
            {
                throw new StressGeneratorOptionException(
                    $"The variant area '{name}' places an actor at {at}, where no walkable ground is.");
            }

            if (PlanarDistance(ground.X, ground.Z, centerGround.X, centerGround.Z) <= ReachTolerance)
            {
                continue;
            }

            var plan = planner.Plan(centerGround, ground);
            var end = plan.Waypoints.Count == 0 ? centerGround : plan.Waypoints[^1];
            if (plan.Status == MovementPathPlanStatus.NoPath
                || PlanarDistance(end.X, end.Z, ground.X, ground.Z) > ReachTolerance)
            {
                throw new StressGeneratorOptionException(
                    $"The variant area '{name}' places an actor at {at}, which no walk from the area's center reaches.");
            }
        }

        return instances.Length;
    }

    // Runs a content compiler's own entry point, so the variant is exactly what the compiler
    // would write, and keeps its console output in the log.
    private static void RunCompiler(Assembly compiler, string[] arguments, TextWriter log)
    {
        var entryPoint = compiler.EntryPoint
            ?? throw new InvalidOperationException($"{compiler.GetName().Name} has no entry point.");
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        Console.SetOut(output);
        Console.SetError(output);
        object? exitCode;
        try
        {
            exitCode = entryPoint.Invoke(null, [arguments]);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }

        log.Write(output.ToString());
        if (exitCode is not 0)
        {
            throw new InvalidDataException(
                $"{compiler.GetName().Name} failed with exit code {exitCode}: {output.ToString().Trim()}");
        }
    }

    // The worker's checked-in movement settings with the World's bounds.
    private static MovementSimulationSettings MovementSettings(string repositoryRoot, string worldManifestPath)
    {
        using var settingsDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Server",
            "SimulationWorker",
            "Config",
            "appsettings.json")));
        var movement = settingsDocument.RootElement.GetProperty("SimulationWorker").GetProperty("Movement");
        using var worldDocument = JsonDocument.Parse(File.ReadAllText(worldManifestPath));
        var world = worldDocument.RootElement;
        var bounds = world.GetProperty("bounds");
        return new MovementSimulationSettings(
            movement.GetProperty("TickRateHz").GetInt32(),
            movement.GetProperty("WalkSpeed").GetSingle(),
            movement.GetProperty("RotationSpeedDegrees").GetSingle(),
            movement.GetProperty("Gravity").GetSingle(),
            movement.GetProperty("MaximumFallSpeed").GetSingle(),
            movement.GetProperty("GroundedVerticalVelocity").GetSingle(),
            world.GetProperty("groundHeight").GetSingle(),
            bounds.GetProperty("minimumX").GetSingle(),
            bounds.GetProperty("maximumX").GetSingle(),
            bounds.GetProperty("minimumZ").GetSingle(),
            bounds.GetProperty("maximumZ").GetSingle(),
            new CharacterCollisionSettings(
                movement.GetProperty("CharacterRadius").GetSingle(),
                movement.GetProperty("CharacterHeight").GetSingle(),
                movement.GetProperty("StepHeight").GetSingle(),
                movement.GetProperty("MaximumSlopeDegrees").GetSingle(),
                movement.GetProperty("GroundSnapDistance").GetSingle(),
                movement.GetProperty("MaximumSubstepDistance").GetSingle(),
                movement.GetProperty("MaximumPenetrationIterations").GetInt32()));
    }

    private static SimulationVector3 WorldSpawn(string worldManifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(worldManifestPath));
        var spawn = document.RootElement.GetProperty("spawn");
        return new SimulationVector3(
            spawn.GetProperty("x").GetSingle(),
            spawn.GetProperty("y").GetSingle(),
            spawn.GetProperty("z").GetSingle());
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if (!file.EndsWith(".meta", StringComparison.Ordinal))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static float PlanarDistance(float ax, float az, float bx, float bz)
    {
        var dx = ax - bx;
        var dz = az - bz;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static string Format(float value)
    {
        return value.ToString("0.#", CultureInfo.InvariantCulture);
    }
}

// Finds walkable ground under a point as the worker places a Mob or a teleported player: the
// first walkable surface below it, searched from above, on which a character stands grounded.
public sealed class PlayingGroundProbe(ICollisionWorld collision, MovementSimulationSettings settings)
{
    private const float SearchHeight = 20f;
    private readonly CollisionQueryBuffer buffer = new();

    public bool TryFindGround(float x, float y, float z, out SimulationVector3 ground)
    {
        ground = default;
        if (!CollisionRaycast.TryRaycast(
                collision,
                new SimulationVector3(x, y + SearchHeight, z),
                new SimulationVector3(0f, -1f, 0f),
                SearchHeight * 2f,
                CollisionLayers.CharacterMovement,
                buffer,
                out var hit))
        {
            return false;
        }

        var state = PlayerMovementSimulation.CreateInitialState(settings, collision, x, hit.Point.Y, z, 0f);
        if (!state.IsGrounded)
        {
            return false;
        }

        ground = state.Position;
        return true;
    }
}
