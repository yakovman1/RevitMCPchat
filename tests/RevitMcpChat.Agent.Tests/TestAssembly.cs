using Xunit;

// Several tests clear Microsoft.Data.Sqlite's process-wide connection pools before deleting their temporary databases.
// Keep test classes serialized so one test cannot clear another test's active SQLite connection.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
