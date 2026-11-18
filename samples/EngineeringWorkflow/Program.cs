using AgenticForge.Samples.EngineeringWorkflow;

// Usage: dotnet run --project samples/EngineeringWorkflow [-- --review-rounds N] [--db PATH]
// --review-rounds 0: first review passes. 1 (default): one bounded rework. 2: rework is exhausted and a human decides.
var rounds = 1;
string? db = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--review-rounds" && i + 1 < args.Length) rounds = int.Parse(args[++i]);
    else if (args[i] == "--db" && i + 1 < args.Length) db = args[++i];
}

var path = db ?? Path.Combine(Path.GetTempPath(), $"engineering-sample-{Guid.NewGuid():N}.db");
try
{
    await SampleRunner.RunAsync(Console.Out, path, rounds);
}
finally
{
    if (db is null) { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(f); }
}
