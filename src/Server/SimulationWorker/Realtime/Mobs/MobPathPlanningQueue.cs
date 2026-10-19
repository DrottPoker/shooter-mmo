using Mmorpg.GameSimulation;

namespace Mmorpg.SimulationWorker.Realtime;

// Route requests of the Mobs in arrival order, planned within a fixed budget per tick so
// a crowd that starts to chase never stalls the tick. A Mob has at most one request;
// asking again only moves its destination. Loop-confined.
internal sealed class MobPathPlanningQueue
{
    private readonly Queue<MobAgent> requests = new();

    public int Count => requests.Count;

    public void Request(MobAgent agent, SimulationVector3 destination)
    {
        ArgumentNullException.ThrowIfNull(agent);
        agent.PlanDestination = destination;
        if (agent.PlanQueued)
        {
            return;
        }

        agent.PlanQueued = true;
        requests.Enqueue(agent);
    }

    // Plans up to the budget of waiting routes. A Mob that left the world or stopped
    // needing its route is skipped without spending the budget.
    public void Process(int budget, Action<MobAgent, MovementPathPlan> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var planned = 0;
        while (planned < budget && requests.TryDequeue(out var agent))
        {
            if (agent.Removed || !agent.PlanQueued || !agent.HasMovement)
            {
                agent.PlanQueued = false;
                continue;
            }

            agent.PlanQueued = false;
            completed(agent, agent.Locomotion.Planner.Plan(agent.Movement.Position, agent.PlanDestination));
            planned++;
        }
    }

    public void Clear()
    {
        while (requests.TryDequeue(out var agent))
        {
            agent.PlanQueued = false;
        }
    }
}
