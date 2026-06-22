using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Godot;
using Microsoft.Data.Sqlite;

namespace Evaluate;

// Full SQL access for scripts (the `sql` capability). The game owns its schema; this
// owns the connection, durability, async, and crash-safety. A single background writer
// thread owns the one connection and serializes EVERY statement through a queue, so:
//   - sql.exec / sql.query / sql.transaction are synchronous (enqueue + wait),
//   - sql.exec_async is fire-and-forget (enqueue, return immediately, ordered),
//   - sql.flush drains the queue (e.g. from on_quit).
// WAL mode makes commits atomic + durable, so a crash mid-write never corrupts the DB.
// Lua values never cross the thread boundary — params/rows are plain CLR objects; the
// Loader marshals to/from Lua on the main thread (the Lua state is single-threaded).
public sealed class Sql : IDisposable
{
    private sealed class Cmd
    {
        public Action<SqliteConnection> Work = _ => { };
        public ManualResetEventSlim? Done;   // non-null => the caller waits for completion
        public Exception? Error;
    }

    private readonly BlockingCollection<Cmd> _queue = new();
    private readonly Thread _worker;
    private readonly Action<string> _log;
    private bool _disposed;

    // The DB lives next to the kv store in Godot's per-project user:// dir (save.db).
    public Sql(Action<string> log)
    {
        _log = log;
        var dir = ProjectSettings.GlobalizePath("user://");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "save.db");

        _worker = new Thread(() => Run(path)) { IsBackground = true, Name = "evaluate-sql" };
        _worker.Start();
    }

    private void Run(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        Exec(conn, "PRAGMA journal_mode=WAL");
        Exec(conn, "PRAGMA synchronous=NORMAL");      // WAL + NORMAL = durable across crashes, fast
        Exec(conn, "PRAGMA busy_timeout=5000");
        Exec(conn, "PRAGMA foreign_keys=ON");

        foreach (var cmd in _queue.GetConsumingEnumerable())
        {
            try { cmd.Work(conn); }
            catch (Exception e)
            {
                cmd.Error = e;
                if (cmd.Done is null) _log($"sql async error: {e.Message}");   // fire-and-forget can't surface
            }
            finally { cmd.Done?.Set(); }
        }
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var c = conn.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    // Run a unit of work on the writer thread. wait=true blocks until it completes and
    // rethrows any error (sync ops); wait=false is fire-and-forget (async ops).
    private void Submit(Action<SqliteConnection> work, bool wait)
    {
        if (_disposed) throw new EvaluateException("sql used after shutdown");
        var cmd = new Cmd { Work = work, Done = wait ? new ManualResetEventSlim(false) : null };
        _queue.Add(cmd);
        if (!wait) return;
        cmd.Done!.Wait();
        if (cmd.Error is not null) throw new EvaluateException(cmd.Error.Message);
    }

    private static void Bind(SqliteCommand c, IReadOnlyList<object?> ps)
    {
        // Positional binding to @p1, @p2, ... (Microsoft.Data.Sqlite has no anonymous `?`).
        for (int i = 0; i < ps.Count; i++)
            c.Parameters.AddWithValue("@p" + (i + 1), ps[i] ?? (object)DBNull.Value);
    }

    // A write: returns (rows changed, last inserted rowid). wait=false => async.
    public (long changes, long lastId) ExecStmt(string sql, IReadOnlyList<object?> ps, bool wait)
    {
        long changes = 0, lastId = 0;
        Submit(conn =>
        {
            using (var c = conn.CreateCommand()) { c.CommandText = sql; Bind(c, ps); changes = c.ExecuteNonQuery(); }
            using var idc = conn.CreateCommand();
            idc.CommandText = "SELECT last_insert_rowid()";
            lastId = Convert.ToInt64(idc.ExecuteScalar() ?? 0L);
        }, wait);
        return (changes, lastId);
    }

    // A read: rows as a list of column->value maps (CLR objects; Loader marshals to Lua).
    public List<Dictionary<string, object?>> QueryStmt(string sql, IReadOnlyList<object?> ps)
    {
        var rows = new List<Dictionary<string, object?>>();
        Submit(conn =>
        {
            using var c = conn.CreateCommand();
            c.CommandText = sql;
            Bind(c, ps);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var row = new Dictionary<string, object?>(r.FieldCount);
                for (int i = 0; i < r.FieldCount; i++)
                    row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(row);
            }
        }, wait: true);
        return rows;
    }

    // Drain everything queued so far (all prior async writes are committed on return).
    public void Flush() => Submit(_ => { }, wait: true);

    // Crash-safe rotating backup: a consistent copy of the whole DB (VACUUM INTO needs a
    // string literal, so the path is escaped, not bound).
    public void Snapshot(string path)
    {
        var safe = path.Replace("'", "''");
        Submit(conn => Exec(conn, $"VACUUM INTO '{safe}'"), wait: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();   // writer drains the rest, then exits
        _worker.Join(2000);
        _queue.Dispose();
    }
}
