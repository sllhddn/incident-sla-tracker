using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SlaTracker.Api.Data;

/// <summary>
/// Minimal Flyway-style migrator for SQL Server. Runs embedded scripts named V###__description.sql once each,
/// in order, inside a transaction, and records a SHA-256 checksum. If an applied script is later edited,
/// start-up fails instead of silently running a different schema.
/// </summary>
public sealed partial class SchemaMigrator(string connectionString, ILogger<SchemaMigrator> log)
{
    public sealed record Script(string Name, string Sql)
    {
        public string Checksum { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sql.Replace("\r\n", "\n"))));
    }

    public static IReadOnlyList<Script> EmbeddedScripts()
    {
        var asm = typeof(SchemaMigrator).Assembly;
        return asm.GetManifestResourceNames()
            .Where(n => n.Contains(".Data.Scripts.") && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(n => new Script(n[(n.IndexOf(".Data.Scripts.", StringComparison.Ordinal) + 14)..], Read(asm, n)))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Creates the database if needed, then applies pending scripts. Returns the names applied.</summary>
    public async Task<IReadOnlyList<string>> MigrateAsync(IReadOnlyList<Script>? scripts = null, CancellationToken ct = default)
    {
        scripts ??= EmbeddedScripts();
        await EnsureDatabaseAsync(ct);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await ExecAsync(conn, null, """
            IF OBJECT_ID(N'dbo.SchemaVersion', N'U') IS NULL
            CREATE TABLE dbo.SchemaVersion (
                ScriptName  nvarchar(200) NOT NULL CONSTRAINT PK_SchemaVersion PRIMARY KEY,
                Checksum    char(64)      NOT NULL,
                AppliedAt   datetimeoffset(3) NOT NULL CONSTRAINT DF_SchemaVersion_AppliedAt DEFAULT SYSDATETIMEOFFSET()
            );
            """, ct);

        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var cmd = new SqlCommand("SELECT ScriptName, Checksum FROM dbo.SchemaVersion", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) applied[r.GetString(0)] = r.GetString(1);

        var ran = new List<string>();
        foreach (var s in scripts)
        {
            if (applied.TryGetValue(s.Name, out var checksum))
            {
                if (!string.Equals(checksum, s.Checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Migration {s.Name} was changed after it was applied. Add a new script instead of editing an old one.");
                continue;
            }

            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            foreach (var batch in SplitBatches(s.Sql)) await ExecAsync(conn, tx, batch, ct);
            await using (var ins = new SqlCommand("INSERT INTO dbo.SchemaVersion (ScriptName, Checksum) VALUES (@n, @c)", conn, tx))
            {
                ins.Parameters.AddWithValue("@n", s.Name);
                ins.Parameters.AddWithValue("@c", s.Checksum);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            log.LogInformation("Applied migration {Script}", s.Name);
            ran.Add(s.Name);
        }
        return ran;
    }

    /// <summary>Splits a script on lines containing only GO, as sqlcmd and SSMS do.</summary>
    public static IEnumerable<string> SplitBatches(string sql) =>
        GoLine().Split(sql.Replace("\r\n", "\n")).Select(b => b.Trim()).Where(b => b.Length > 0);

    private async Task EnsureDatabaseAsync(CancellationToken ct)
    {
        var csb = new SqlConnectionStringBuilder(connectionString);
        var name = csb.InitialCatalog;
        if (string.IsNullOrEmpty(name) || name.Equals("master", StringComparison.OrdinalIgnoreCase)) return;
        csb.InitialCatalog = "master";
        await using var conn = new SqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "IF DB_ID(@db) IS NULL BEGIN DECLARE @sql nvarchar(400) = N'CREATE DATABASE ' + QUOTENAME(@db); EXEC (@sql); END", conn);
        cmd.Parameters.AddWithValue("@db", name);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecAsync(SqlConnection conn, SqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Read(Assembly asm, string resource)
    {
        using var s = asm.GetManifestResourceStream(resource)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    [GeneratedRegex(@"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();
}
