using Xunit;
using Xunit.Sdk;
using Xunit.v3;

// Test classes run in parallel. Every PostgreSQL test owns a fresh database, so only tests
// that observe process-wide state, such as MeterListener tests, need SerialTestCollection.
// Test assemblies also run in parallel with each other.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerClass)]
[assembly: Parallelization(Mode = ParallelMode.Collections, MaxThreads = 4)]
