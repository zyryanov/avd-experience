// Assembly-level test configuration. MUST be the first file compiled in this project.
//
// Microsoft.Data.Sqlite's connection pool is process-global, and the teardown
// below (Fixtures.withTempDb) calls SqliteConnection.ClearAllPools(), which can
// dispose the native sqlite3 handle of a connection that is still leased and
// mid-command on another thread (seen in CI as ObjectDisposedException
// 'SQLitePCL.sqlite3' out of DbRepository.initDatabase). xUnit parallelizes test
// collections (classes) by default, so one class's withTempDb teardown raced
// another class's in-flight DB work. Disabling test parallelization makes pool
// teardown strictly sequential with respect to every other test's connections.
// The suite is small, so serialization costs nothing.
module AvdStats.IntegrationTests.AssemblyInfo

open Xunit

[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()
