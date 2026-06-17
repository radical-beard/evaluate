using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Evaluate;

// Runtime save & player-config persistence, backed by SQLite. This is for data
// chosen/produced at runtime (saves, settings) — distinct from TOML dev config.
// The database lives under ~/.local/share/evaluate/ (XDG data convention).
public sealed class Persistence : IDisposable
{
    private readonly SqliteConnection _conn;

    public Persistence()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "evaluate");
        Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={Path.Combine(dir, "save.db")}");
        _conn.Open();
        Exec("CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, type TEXT NOT NULL, value TEXT NOT NULL)");
    }

    public void Set(string key, string type, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO kv(key,type,value) VALUES($k,$t,$v) " +
            "ON CONFLICT(key) DO UPDATE SET type=$t, value=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public (string type, string value)? Get(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT type, value FROM kv WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetString(1)) : null;
    }

    public void Delete(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM kv WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
