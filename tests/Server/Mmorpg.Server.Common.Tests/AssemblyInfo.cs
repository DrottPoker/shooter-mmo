using Xunit.Sdk;
using Xunit.v3;

// Tests in this assembly run one at a time, as they did in the single backend test project:
// MeterListener tests, static stores, and timing budgets assume it. Test assemblies still
// run in parallel with each other.
[assembly: Parallelization(Mode = ParallelMode.None)]
