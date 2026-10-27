using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class MobPathPlanningQueueTests
{
    [Fact]
    public void TheQueuePlansWithinItsBudgetOncePerMobAndSkipsRemovedMobs()
    {
        var agents = CreateAgents(4);
        var queue = new MobPathPlanningQueue();
        foreach (var agent in agents)
        {
            queue.Request(agent, new SimulationVector3(5f, 0f, 5f));
        }

        // A second request only moves the destination of the waiting one.
        queue.Request(agents[0], new SimulationVector3(-5f, 0f, -5f));
        agents[1].Removed = true;
        var planned = new List<(MobAgent Agent, MovementPathPlan Plan)>();

        queue.Process(2, (agent, plan) => planned.Add((agent, plan)));

        Assert.Equal([agents[0], agents[2]], planned.Select(value => value.Agent));
        Assert.Equal(new SimulationVector3(-5f, 0f, -5f), agents[0].PlanDestination);
        Assert.All(planned, value => Assert.Equal(MovementPathPlanStatus.Reached, value.Plan.Status));
        Assert.Equal(1, queue.Count);

        queue.Process(MobSystem.PlansPerTick, (agent, plan) => planned.Add((agent, plan)));

        Assert.Equal([agents[0], agents[2], agents[3]], planned.Select(value => value.Agent));
        Assert.Equal(0, queue.Count);
        Assert.All(agents, agent => Assert.False(agent.PlanQueued));
    }

    private static MobAgent[] CreateAgents(int count)
    {
        var document = WorldActorTestData.Compile();
        var store = new WorldActorStore(document, new SimulationEntityRegistry());
        var actor = store.ActivateAssignment("development-world-1", "local-shard-1", "test-runtime")
            .First(value => value.Definition.Kind == WorldActorKindIds.Mob);
        var collisionWorld = CollisionTestWorldFactory.Create();
        var settings = new MovementSimulationSettings(30, 5f, 720f, -24f, -2f, 0f, -100f, 100f, -100f, 100f);
        var locomotion = new MobLocomotion(
            document.LocomotionProfiles.Single(profile => profile.Id == actor.Definition.LocomotionProfileId),
            settings,
            collisionWorld,
            40f);
        var brain = document.BrainProfiles.Single(profile => profile.Id == actor.Definition.BrainProfileId);
        return Enumerable.Range(0, count)
            .Select(_ =>
            {
                var agent = new MobAgent(
                    Combatant.ForMob(actor, 50, new CombatAttackProfile("test", "melee", "normal", 1, 1, 30, 1.5f, 0f, null, 0)),
                    locomotion,
                    brain,
                    [],
                    3)
                {
                    HasMovement = true,
                    Movement = PlayerMovementSimulation.CreateInitialState(
                        locomotion.Settings,
                        collisionWorld,
                        0f,
                        0f,
                        0f,
                        0f)
                };
                return agent;
            })
            .ToArray();
    }
}
