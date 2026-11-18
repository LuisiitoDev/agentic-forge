using System.Text.Json;
using AgenticForge.Application;
using AgenticForge.Domain;
using Microsoft.Data.Sqlite;

namespace AgenticForge.Persistence.Sqlite;

/// <summary>
/// Durable <see cref="IRunStore"/> on a local SQLite file (WAL, synchronous=FULL, so a returned save survives a crash).
/// Every write runs in a <c>BEGIN IMMEDIATE</c> transaction, so competing workers (threads or processes sharing the file)
/// are serialized and the revision check is race-free. Snapshots are stored as JSON using <see cref="ContractJson"/>, next to
/// the few columns needed for queries and uniqueness. Stored schema: see <c>docs/design/persistence-and-recovery.md</c>.
/// </summary>
public sealed class SqliteRunStore : IRunStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = new(ContractJson.Options) { WriteIndented = false };

    private readonly string _connectionString;

    /// <param name="databasePath">Path of the database file (created, with its schema, if missing).</param>
    public SqliteRunStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, "PRAGMA busy_timeout = 10000; PRAGMA synchronous = FULL;");
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode = WAL;");
        using var tx = c.BeginTransaction();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS runs (
                id TEXT PRIMARY KEY, seq INTEGER NOT NULL UNIQUE, workflow_id TEXT NOT NULL, idempotency_key TEXT,
                status TEXT NOT NULL, revision INTEGER NOT NULL, json TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_runs_idempotency ON runs (workflow_id, idempotency_key) WHERE idempotency_key IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_runs_status ON runs (status);
            CREATE TABLE IF NOT EXISTS step_runs (
                seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE, run_id TEXT NOT NULL, revision INTEGER NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_step_runs_run ON step_runs (run_id, seq);
            CREATE TABLE IF NOT EXISTS checkpoints (
                run_id TEXT NOT NULL, sequence INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY (run_id, sequence));
            """, tx);
        var existing = Scalar(c, "SELECT value FROM meta WHERE key = 'schemaVersion'", tx);
        if (existing is null) Exec(c, $"INSERT INTO meta (key, value) VALUES ('schemaVersion', '{SchemaVersion}')", tx);
        else if (int.Parse(existing.ToString()!) > SchemaVersion)
            throw new InvalidOperationException($"The database uses store schema {existing}, newer than the supported {SchemaVersion}.");
        tx.Commit();
    }

    public ValueTask<WorkflowRunSnapshot> SaveAsync(WorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        using var c = Open();
        using var tx = Immediate(c);
        var stored = Scalar(c, "SELECT revision FROM runs WHERE id = $id", tx, ("$id", run.Id.Value));
        var storedRevision = stored is null ? (int?)null : Convert.ToInt32(stored);
        if (run.Revision != (storedRevision ?? 0) || (storedRevision is not null && run.Revision == 0))
            throw new RunConcurrencyException("WorkflowRun", run.Id.Value, run.Revision, storedRevision);

        var saved = run with { Revision = run.Revision + 1 };
        var json = JsonSerializer.Serialize(saved, Json);
        if (storedRevision is null)
        {
            if (run.IdempotencyKey is { } key
                && Scalar(c, "SELECT id FROM runs WHERE workflow_id = $w AND idempotency_key = $k", tx,
                    ("$w", run.WorkflowId.Value), ("$k", key)) is string other)
                throw new DuplicateIdempotencyKeyException(run.WorkflowId, key, new WorkflowRunId(other));
            Exec(c, """
                INSERT INTO runs (id, seq, workflow_id, idempotency_key, status, revision, json)
                VALUES ($id, (SELECT COALESCE(MAX(seq), 0) + 1 FROM runs), $w, $k, $s, $r, $j)
                """, tx, ("$id", run.Id.Value), ("$w", run.WorkflowId.Value), ("$k", run.IdempotencyKey),
                ("$s", saved.Status.ToString()), ("$r", saved.Revision), ("$j", json));
        }
        else
        {
            Exec(c, "UPDATE runs SET status = $s, revision = $r, json = $j WHERE id = $id", tx,
                ("$id", run.Id.Value), ("$s", saved.Status.ToString()), ("$r", saved.Revision), ("$j", json));
        }
        tx.Commit();
        return ValueTask.FromResult(saved);
    }

    public ValueTask<StepRunSnapshot> SaveAsync(StepRunSnapshot stepRun, CancellationToken cancellationToken)
    {
        using var c = Open();
        using var tx = Immediate(c);
        var stored = Scalar(c, "SELECT revision FROM step_runs WHERE id = $id", tx, ("$id", stepRun.Id.Value));
        var storedRevision = stored is null ? (int?)null : Convert.ToInt32(stored);
        if (stepRun.Revision != (storedRevision ?? 0) || (storedRevision is not null && stepRun.Revision == 0))
            throw new RunConcurrencyException("StepRun", stepRun.Id.Value, stepRun.Revision, storedRevision);

        var saved = stepRun with { Revision = stepRun.Revision + 1 };
        var json = JsonSerializer.Serialize(saved, Json);
        if (storedRevision is null)
            Exec(c, "INSERT INTO step_runs (id, run_id, revision, json) VALUES ($id, $run, $r, $j)", tx,
                ("$id", stepRun.Id.Value), ("$run", stepRun.RunId.Value), ("$r", saved.Revision), ("$j", json));
        else
            Exec(c, "UPDATE step_runs SET revision = $r, json = $j WHERE id = $id", tx,
                ("$id", stepRun.Id.Value), ("$r", saved.Revision), ("$j", json));
        tx.Commit();
        return ValueTask.FromResult(saved);
    }

    public ValueTask<WorkflowRunSnapshot?> GetRunAsync(WorkflowRunId id, CancellationToken cancellationToken)
    {
        using var c = Open();
        return ValueTask.FromResult(Read<WorkflowRunSnapshot>(c, "SELECT json FROM runs WHERE id = $id", ("$id", id.Value)).FirstOrDefault());
    }

    public ValueTask<IReadOnlyList<StepRunSnapshot>> GetStepRunsAsync(WorkflowRunId id, CancellationToken cancellationToken)
    {
        using var c = Open();
        return ValueTask.FromResult<IReadOnlyList<StepRunSnapshot>>(
            Read<StepRunSnapshot>(c, "SELECT json FROM step_runs WHERE run_id = $id ORDER BY seq", ("$id", id.Value)));
    }

    public ValueTask<WorkflowRunSnapshot?> FindRunByIdempotencyKeyAsync(WorkflowId workflow, string key, CancellationToken cancellationToken)
    {
        using var c = Open();
        return ValueTask.FromResult(Read<WorkflowRunSnapshot>(c,
            "SELECT json FROM runs WHERE workflow_id = $w AND idempotency_key = $k", ("$w", workflow.Value), ("$k", key)).FirstOrDefault());
    }

    public ValueTask<IReadOnlyList<WorkflowRunSnapshot>> ListRunsAsync(IReadOnlyCollection<WorkflowRunStatus> statuses, CancellationToken cancellationToken)
    {
        if (statuses.Count == 0) return ValueTask.FromResult<IReadOnlyList<WorkflowRunSnapshot>>([]);
        using var c = Open();
        var names = statuses.Select((s, i) => ($"$s{i}", (object?)s.ToString())).ToArray();
        var sql = $"SELECT json FROM runs WHERE status IN ({string.Join(",", names.Select(n => n.Item1))}) ORDER BY seq";
        return ValueTask.FromResult<IReadOnlyList<WorkflowRunSnapshot>>(Read<WorkflowRunSnapshot>(c, sql, names));
    }

    public ValueTask<RunCheckpoint> AppendCheckpointAsync(RunCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        using var c = Open();
        using var tx = Immediate(c);
        var next = Convert.ToInt64(Scalar(c, "SELECT COALESCE(MAX(sequence), 0) + 1 FROM checkpoints WHERE run_id = $id", tx,
            ("$id", checkpoint.RunId.Value)));
        var saved = checkpoint with { Sequence = next };
        Exec(c, "INSERT INTO checkpoints (run_id, sequence, json) VALUES ($id, $n, $j)", tx,
            ("$id", checkpoint.RunId.Value), ("$n", next), ("$j", JsonSerializer.Serialize(saved, Json)));
        tx.Commit();
        return ValueTask.FromResult(saved);
    }

    public ValueTask<IReadOnlyList<RunCheckpoint>> GetCheckpointsAsync(WorkflowRunId id, CancellationToken cancellationToken)
    {
        using var c = Open();
        return ValueTask.FromResult<IReadOnlyList<RunCheckpoint>>(
            Read<RunCheckpoint>(c, "SELECT json FROM checkpoints WHERE run_id = $id ORDER BY sequence", ("$id", id.Value)));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Starts a transaction that takes the write lock immediately, so read-check-write cannot interleave with another writer.</summary>
    private static SqliteTransaction Immediate(SqliteConnection c) => c.BeginTransaction(deferred: false);

    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = Command(c, sql, tx, args);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = Command(c, sql, tx, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private static List<T> Read<T>(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(c, sql, null, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read())
            list.Add(JsonSerializer.Deserialize<T>(r.GetString(0), Json)
                     ?? throw new InvalidOperationException("Stored snapshot is empty."));
        return list;
    }

    private static SqliteCommand Command(SqliteConnection c, string sql, SqliteTransaction? tx, (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
