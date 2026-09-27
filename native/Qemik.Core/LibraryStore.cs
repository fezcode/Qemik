using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Qemik.Core;

public sealed class LibraryStore : IDisposable
{
    private readonly SqliteConnection db;
    public string Root { get; }
    public LibraryStore(string? root = null)
    {
        Root = root ?? AppPaths.DefaultRoot;
        Directory.CreateDirectory(Root);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "library.db") }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS vms(id TEXT PRIMARY KEY, config TEXT NOT NULL); CREATE TABLE IF NOT EXISTS settings(id TEXT PRIMARY KEY, value TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }
    public List<VmConfig> List()
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT config FROM vms ORDER BY json_extract(config, '$.Name') COLLATE NOCASE";
        using var reader = cmd.ExecuteReader(); var result = new List<VmConfig>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<VmConfig>(reader.GetString(0)) ?? throw new InvalidDataException("Invalid VM record."));
        return result;
    }
    public void Save(VmConfig vm)
    {
        QemuCommand.Validate(vm);
        using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO vms VALUES($id,$config) ON CONFLICT(id) DO UPDATE SET config=$config";
        cmd.Parameters.AddWithValue("$id", vm.Id); cmd.Parameters.AddWithValue("$config", JsonSerializer.Serialize(vm)); cmd.ExecuteNonQuery();
    }
    // Removing a library record never deletes a user's disk images or firmware.
    public void Remove(string id)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM vms WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }
    public Preferences LoadPreferences()
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT value FROM settings WHERE id='preferences'";
        var prefs = cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<Preferences>(json)! : new Preferences();
        if (string.IsNullOrWhiteSpace(prefs.LibraryDirectory)) prefs.LibraryDirectory = Path.Combine(Root, "Machines");
        return prefs;
    }
    public void SavePreferences(Preferences prefs)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO settings VALUES('preferences',$value) ON CONFLICT(id) DO UPDATE SET value=$value";
        cmd.Parameters.AddWithValue("$value", JsonSerializer.Serialize(prefs)); cmd.ExecuteNonQuery();
    }
    public void Dispose() => db.Dispose();
}
