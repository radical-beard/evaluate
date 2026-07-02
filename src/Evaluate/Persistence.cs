using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Microsoft.Data.Sqlite;

namespace Evaluate;

// Runtime save & player-config persistence, backed by SQLite. This is for data
// chosen/produced at runtime (saves, settings) — distinct from TOML dev config.
// The database lives in Godot's per-project user data directory (`user://`): the
// platform-native, per-game location (Windows %APPDATA%, macOS ~/Library/
// Application Support, Linux ~/.local/share) and what Steam Cloud syncs from. A
// game chooses its own folder via project.godot's use_custom_user_dir; without
// one Godot falls back to a per-project app_userdata path. Either way each game
// gets its own store — saves are no longer shared across every Evaluate game.
public sealed class Persistence : IDisposable
{
    private readonly SqliteConnection _conn;

    public Persistence()
    {
        var dir = ProjectSettings.GlobalizePath("user://");
        Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={Path.Combine(dir, "save.db")}");
        _conn.Open();
        Exec("CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, type TEXT NOT NULL, value TEXT NOT NULL)");
        // The player's control REBINDS (profile-level, not per-save-slot): one row
        // per (scenario, binding token) applied over the controls TOML at map build.
        // An empty action means "unbind the token".
        Exec("CREATE TABLE IF NOT EXISTS control_overrides (" +
             "scenario TEXT NOT NULL, binding TEXT NOT NULL, action TEXT NOT NULL, " +
             "PRIMARY KEY(scenario, binding))");
    }

    public List<(string scenario, string binding, string action)> Overrides()
    {
        var list = new List<(string, string, string)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT scenario, binding, action FROM control_overrides ORDER BY scenario, binding";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    public void SetOverride(string scenario, string binding, string action)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO control_overrides(scenario,binding,action) VALUES($s,$b,$a) " +
            "ON CONFLICT(scenario,binding) DO UPDATE SET action=$a";
        cmd.Parameters.AddWithValue("$s", scenario);
        cmd.Parameters.AddWithValue("$b", binding);
        cmd.Parameters.AddWithValue("$a", action);
        cmd.ExecuteNonQuery();
    }

    public void DeleteOverride(string scenario, string binding)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM control_overrides WHERE scenario=$s AND binding=$b";
        cmd.Parameters.AddWithValue("$s", scenario);
        cmd.Parameters.AddWithValue("$b", binding);
        cmd.ExecuteNonQuery();
    }

    public void ClearOverrides() => Exec("DELETE FROM control_overrides");

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
