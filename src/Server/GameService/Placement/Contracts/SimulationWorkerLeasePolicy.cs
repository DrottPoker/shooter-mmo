namespace Mmorpg.GameService.Placement.Contracts;

// How long a worker registry heartbeat keeps a SimulationWorker's lease fresh. Other
// modules use it to judge worker authority with the same timeout as placement.
public sealed record SimulationWorkerLeasePolicy(TimeSpan HeartbeatTimeout);
