using Xunit;

// Tests in this assembly run one at a time, as they did in the single backend test project:
// MeterListener tests, static stores, the shared per-assembly database, and timing budgets
// assume it. Test assemblies still run in parallel with each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
