using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace XRaySystem.Infrastructure.Persistence;

/// <summary>
/// EnsureCreated ile oluşturulmuş eski veritabanlarını ilk migration'a veri kaybı
/// olmadan bağlar. Migration geçmişi bulunan veritabanlarında hiçbir işlem yapmaz.
/// </summary>
public static class LegacyDatabaseBaseliner
{
    public const string InitialMigrationId = "20260818000000_InitialCreate";
    private const string ProductVersion = "8.0.0";

    public static void Prepare(XRayDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) connection.Open();

        try
        {
            if (!TableExists(connection, "Measurements")) return;

            var columns = ReadColumns(connection, "Measurements");
            var requiredLegacyColumns = new[]
            {
                "Id", "Timestamp", "Readings_TubeCurrent", "Readings_Voltage",
                "Readings_XRayIntensity", "Readings_ExposureTime"
            };
            var missingRequired = requiredLegacyColumns.Where(column => !columns.Contains(column)).ToArray();
            if (missingRequired.Length > 0)
                throw new InvalidOperationException(
                    $"Eski Measurements şeması tanınmıyor. Eksik kolonlar: {string.Join(", ", missingRequired)}");

            EnsureColumn(connection, columns, "ImagePath", "TEXT");
            EnsureColumn(connection, columns, "Profile", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, columns, "IsSimulator", "INTEGER NOT NULL DEFAULT 0");

            Execute(connection,
                "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (" +
                "\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, " +
                "\"ProductVersion\" TEXT NOT NULL);");
            Execute(connection,
                "INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") " +
                $"VALUES ('{InitialMigrationId}', '{ProductVersion}');");
        }
        finally
        {
            if (shouldClose) connection.Close();
        }
    }

    private static bool TableExists(DbConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return command.ExecuteScalar() is not null;
    }

    private static HashSet<string> ReadColumns(DbConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }

    private static void EnsureColumn(
        DbConnection connection,
        HashSet<string> columns,
        string name,
        string definition)
    {
        if (columns.Contains(name)) return;
        Execute(connection, $"ALTER TABLE \"Measurements\" ADD COLUMN \"{name}\" {definition};");
        columns.Add(name);
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
