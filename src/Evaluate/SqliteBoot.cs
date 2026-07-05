namespace Evaluate;

// Microsoft.Data.Sqlite.Core does not self-initialize a SQLitePCLRaw provider
// (the meta package did). bundle_green picks e_sqlite3 on desktop/Android and
// the system sqlite3 on iOS — exactly the split Godot mobile exports need.
// Idempotent; call before the first SqliteConnection anywhere in the library.
internal static class SqliteBoot
{
    private static bool _done;

    internal static void EnsureInit()
    {
        if (_done) return;
        _done = true;
        SQLitePCL.Batteries_V2.Init();
    }
}
