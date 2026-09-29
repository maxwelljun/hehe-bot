using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace YaxinLicense.Server;

public sealed record ClientLoginRequest(
    string? Username, string? Password, string? MachineId, string? MachineName,
    string? Os, string? AppVersion, JsonElement? MachineInfo);

public sealed record ClientReport(JsonElement? Telemetry, string[]? Logs);
public sealed record AdminLoginRequest(string? Password);
public sealed record AdminPasswordRequest(string? Current, string? Next);
public sealed record UserCreateRequest(string? Username, string? Password, string? Note);
public sealed record UserUpdateRequest(string? Username, bool? Enabled, string? Password, string? Note);
public sealed record MachineUpdateRequest(string? MachineId, bool? Enabled, string? Note);
public sealed record UpdateDeleteRequest(string? FileName);
public sealed record UpdateNotesRequest(string? Version, string? Notes);

public sealed record UserRow(string Username, string PasswordHash, bool Enabled, string Note, string CreatedAt, string? LastLogin);
public sealed record MachineRow(string MachineId, bool Enabled);
public sealed record SessionRow(string TokenHash, string Username, string MachineId);

public sealed class LicenseDb(string path)
{
    private const int MaxLogLinesPerMachine = 20_000;
    private const int MaxLogLinesPerReport = 1_000;
    private const int MaxLogLineLength = 2_000;
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString();
    private readonly object _writeLock = new();

    private static string Now => DateTimeOffset.UtcNow.ToString("O");

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        Execute(connection, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS users(
                username TEXT PRIMARY KEY COLLATE NOCASE, password_hash TEXT NOT NULL, enabled INTEGER NOT NULL DEFAULT 1,
                note TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL, last_login TEXT);
            CREATE TABLE IF NOT EXISTS machines(
                machine_id TEXT PRIMARY KEY, username TEXT NOT NULL DEFAULT '', name TEXT NOT NULL DEFAULT '',
                os TEXT NOT NULL DEFAULT '', info TEXT NOT NULL DEFAULT '{}', ip TEXT NOT NULL DEFAULT '',
                version TEXT NOT NULL DEFAULT '', enabled INTEGER NOT NULL DEFAULT 1, note TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL, last_login TEXT, last_seen TEXT, telemetry TEXT);
            CREATE TABLE IF NOT EXISTS sessions(
                token_hash TEXT PRIMARY KEY, username TEXT NOT NULL, machine_id TEXT NOT NULL,
                created_at TEXT NOT NULL, last_seen TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS logs(
                id INTEGER PRIMARY KEY AUTOINCREMENT, machine_id TEXT NOT NULL, ts TEXT NOT NULL, line TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_logs_machine ON logs(machine_id, id);
            CREATE TABLE IF NOT EXISTS telemetry(
                id INTEGER PRIMARY KEY AUTOINCREMENT, machine_id TEXT NOT NULL, ts TEXT NOT NULL,
                balance REAL, daily_stake REAL, reserved_stake REAL, running INTEGER, orders_paused INTEGER, tables INTEGER, mode TEXT);
            CREATE INDEX IF NOT EXISTS ix_telemetry_machine ON telemetry(machine_id, id);
            CREATE TABLE IF NOT EXISTS events(
                id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, kind TEXT NOT NULL, username TEXT NOT NULL,
                machine_id TEXT NOT NULL, ip TEXT NOT NULL, message TEXT NOT NULL);
            """);
    }

    public string? GetSetting(string key)
    {
        using var connection = Open();
        return Scalar(connection, "SELECT value FROM settings WHERE key=$k", ("$k", key)) as string;
    }

    public void SetSetting(string key, string value) => Write(connection =>
        Execute(connection, "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", value)));

    // ---------- 用户 ----------

    public void CreateUser(string username, string password, string note)
    {
        if (username.Length is < 2 or > 64 || username.Any(char.IsWhiteSpace))
            throw new ArgumentException("用户名需 2–64 个字符且不能包含空格。");
        Write(connection =>
        {
            if (Scalar(connection, "SELECT 1 FROM users WHERE username=$u", ("$u", username)) is not null)
                throw new ArgumentException("用户名已存在。");
            Execute(connection, "INSERT INTO users(username,password_hash,note,created_at) VALUES($u,$p,$n,$t)",
                ("$u", username), ("$p", Passwords.Hash(password)), ("$n", Trim(note, 200)), ("$t", Now));
        });
    }

    public UserRow? FindUser(string username)
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT username,password_hash,enabled,note,created_at,last_login FROM users WHERE username=$u", ("$u", username));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new UserRow(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0, reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5))
            : null;
    }

    public void TouchUserLogin(string username) => Write(connection =>
        Execute(connection, "UPDATE users SET last_login=$t WHERE username=$u", ("$u", username), ("$t", Now)));

    public void UpdateUser(string username, bool? enabled, string? password, string? note) => Write(connection =>
    {
        if (Scalar(connection, "SELECT 1 FROM users WHERE username=$u", ("$u", username)) is null)
            throw new ArgumentException("用户不存在。");
        if (enabled is not null)
            Execute(connection, "UPDATE users SET enabled=$e WHERE username=$u", ("$u", username), ("$e", enabled.Value ? 1 : 0));
        if (!string.IsNullOrEmpty(password))
        {
            Execute(connection, "UPDATE users SET password_hash=$p WHERE username=$u", ("$u", username), ("$p", Passwords.Hash(password)));
            Execute(connection, "DELETE FROM sessions WHERE username=$u", ("$u", username));
        }
        if (note is not null)
            Execute(connection, "UPDATE users SET note=$n WHERE username=$u", ("$u", username), ("$n", Trim(note, 200)));
    });

    public void DeleteUser(string username) => Write(connection =>
    {
        Execute(connection, "DELETE FROM users WHERE username=$u", ("$u", username));
        Execute(connection, "DELETE FROM sessions WHERE username=$u", ("$u", username));
    });

    public List<object> ListUsers()
    {
        using var connection = Open();
        using var command = Command(connection, """
            SELECT u.username,u.enabled,u.note,u.created_at,u.last_login,
                   (SELECT COUNT(*) FROM machines m WHERE m.username=u.username)
            FROM users u ORDER BY u.username
            """);
        using var reader = command.ExecuteReader();
        var result = new List<object>();
        while (reader.Read())
            result.Add(new
            {
                username = reader.GetString(0), enabled = reader.GetInt64(1) != 0, note = reader.GetString(2),
                createdAt = reader.GetString(3), lastLogin = reader.IsDBNull(4) ? null : reader.GetString(4),
                machines = reader.GetInt64(5)
            });
        return result;
    }

    // ---------- 机器 ----------

    public MachineRow UpsertMachine(string machineId, string username, ClientLoginRequest request, string ip)
    {
        string info = request.MachineInfo is { ValueKind: JsonValueKind.Object } element ? Trim(element.GetRawText(), 8_000) : "{}";
        return Write(connection =>
        {
            Execute(connection, """
                INSERT INTO machines(machine_id,username,name,os,info,ip,version,created_at,last_login,last_seen)
                VALUES($id,$u,$name,$os,$info,$ip,$v,$t,$t,$t)
                ON CONFLICT(machine_id) DO UPDATE SET username=$u,name=$name,os=$os,info=$info,ip=$ip,version=$v,last_login=$t,last_seen=$t
                """,
                ("$id", machineId), ("$u", username), ("$name", Trim(request.MachineName, 120)), ("$os", Trim(request.Os, 200)),
                ("$info", info), ("$ip", ip), ("$v", Trim(request.AppVersion, 40)), ("$t", Now));
            bool enabled = Convert.ToInt64(Scalar(connection, "SELECT enabled FROM machines WHERE machine_id=$id", ("$id", machineId))) != 0;
            return new MachineRow(machineId, enabled);
        });
    }

    public MachineRow? FindMachine(string machineId)
    {
        using var connection = Open();
        object? enabled = Scalar(connection, "SELECT enabled FROM machines WHERE machine_id=$id", ("$id", machineId));
        return enabled is null ? null : new MachineRow(machineId, Convert.ToInt64(enabled) != 0);
    }

    public void UpdateMachine(string machineId, bool? enabled, string? note) => Write(connection =>
    {
        if (Scalar(connection, "SELECT 1 FROM machines WHERE machine_id=$id", ("$id", machineId)) is null)
            throw new ArgumentException("机器不存在。");
        if (enabled is not null)
            Execute(connection, "UPDATE machines SET enabled=$e WHERE machine_id=$id", ("$id", machineId), ("$e", enabled.Value ? 1 : 0));
        if (note is not null)
            Execute(connection, "UPDATE machines SET note=$n WHERE machine_id=$id", ("$id", machineId), ("$n", Trim(note, 200)));
    });

    public void DeleteMachine(string machineId) => Write(connection =>
    {
        foreach (string table in new[] { "machines", "sessions", "logs", "telemetry" })
            Execute(connection, $"DELETE FROM {table} WHERE machine_id=$id", ("$id", machineId));
    });

    public List<object> ListMachines()
    {
        using var connection = Open();
        using var command = Command(connection, """
            SELECT machine_id,username,name,os,info,ip,version,enabled,note,created_at,last_login,last_seen,telemetry
            FROM machines ORDER BY last_seen DESC
            """);
        using var reader = command.ExecuteReader();
        var result = new List<object>();
        while (reader.Read())
            result.Add(new
            {
                machineId = reader.GetString(0), username = reader.GetString(1), name = reader.GetString(2), os = reader.GetString(3),
                info = ParseJson(reader.GetString(4)), ip = reader.GetString(5), version = reader.GetString(6),
                enabled = reader.GetInt64(7) != 0, note = reader.GetString(8), createdAt = reader.GetString(9),
                lastLogin = reader.IsDBNull(10) ? null : reader.GetString(10), lastSeen = reader.IsDBNull(11) ? null : reader.GetString(11),
                telemetry = reader.IsDBNull(12) ? (JsonElement?)null : ParseJson(reader.GetString(12))
            });
        return result;
    }

    // ---------- 会话与上报 ----------

    public void CreateSession(string tokenHash, string username, string machineId) => Write(connection =>
    {
        // 每台机器只保留最近一次登录的会话。
        Execute(connection, "DELETE FROM sessions WHERE machine_id=$m", ("$m", machineId));
        Execute(connection, "INSERT INTO sessions(token_hash,username,machine_id,created_at,last_seen) VALUES($h,$u,$m,$t,$t)",
            ("$h", tokenHash), ("$u", username), ("$m", machineId), ("$t", Now));
    });

    public SessionRow? FindSession(string tokenHash)
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT token_hash,username,machine_id FROM sessions WHERE token_hash=$h", ("$h", tokenHash));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new SessionRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    public void DeleteSession(string tokenHash) => Write(connection =>
        Execute(connection, "DELETE FROM sessions WHERE token_hash=$h", ("$h", tokenHash)));

    public void SaveReport(SessionRow session, ClientReport report, string ip) => Write(connection =>
    {
        Execute(connection, "BEGIN IMMEDIATE");
        string now = Now;
        Execute(connection, "UPDATE sessions SET last_seen=$t WHERE token_hash=$h", ("$h", session.TokenHash), ("$t", now));
        if (report.Telemetry is { ValueKind: JsonValueKind.Object } telemetry)
        {
            Execute(connection, "UPDATE machines SET last_seen=$t,ip=$ip,telemetry=$j WHERE machine_id=$m",
                ("$m", session.MachineId), ("$t", now), ("$ip", ip), ("$j", Trim(telemetry.GetRawText(), 32_000)));
            Execute(connection, """
                INSERT INTO telemetry(machine_id,ts,balance,daily_stake,reserved_stake,running,orders_paused,tables,mode)
                VALUES($m,$t,$b,$d,$r,$run,$p,$tb,$mode)
                """,
                ("$m", session.MachineId), ("$t", now), ("$b", Number(telemetry, "balance")), ("$d", Number(telemetry, "dailyStake")),
                ("$r", Number(telemetry, "reservedStake")), ("$run", Bool(telemetry, "running")), ("$p", Bool(telemetry, "ordersPaused")),
                ("$tb", Number(telemetry, "tables")), ("$mode", Text(telemetry, "mode")));
        }
        else
        {
            Execute(connection, "UPDATE machines SET last_seen=$t,ip=$ip WHERE machine_id=$m", ("$m", session.MachineId), ("$t", now), ("$ip", ip));
        }
        foreach (string line in (report.Logs ?? []).TakeLast(MaxLogLinesPerReport))
            Execute(connection, "INSERT INTO logs(machine_id,ts,line) VALUES($m,$t,$l)",
                ("$m", session.MachineId), ("$t", now), ("$l", Trim(line, MaxLogLineLength)));
        Execute(connection, "COMMIT");
    });

    public List<object> ListLogs(string machineId, int limit)
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT ts,line FROM logs WHERE machine_id=$m ORDER BY id DESC LIMIT $n", ("$m", machineId), ("$n", limit));
        using var reader = command.ExecuteReader();
        var result = new List<object>();
        while (reader.Read()) result.Add(new { ts = reader.GetString(0), line = reader.GetString(1) });
        result.Reverse();
        return result;
    }

    public List<object> ListTelemetry(string machineId, int limit)
    {
        using var connection = Open();
        using var command = Command(connection, """
            SELECT ts,balance,daily_stake,reserved_stake,running,orders_paused,tables,mode
            FROM telemetry WHERE machine_id=$m ORDER BY id DESC LIMIT $n
            """, ("$m", machineId), ("$n", limit));
        using var reader = command.ExecuteReader();
        var result = new List<object>();
        while (reader.Read())
            result.Add(new
            {
                ts = reader.GetString(0), balance = NullableDouble(reader, 1), dailyStake = NullableDouble(reader, 2),
                reservedStake = NullableDouble(reader, 3), running = !reader.IsDBNull(4) && reader.GetInt64(4) != 0,
                ordersPaused = !reader.IsDBNull(5) && reader.GetInt64(5) != 0, tables = NullableDouble(reader, 6),
                mode = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        return result;
    }

    public void AddEvent(string kind, string username, string machineId, string ip, string message) => Write(connection =>
        Execute(connection, "INSERT INTO events(ts,kind,username,machine_id,ip,message) VALUES($t,$k,$u,$m,$ip,$msg)",
            ("$t", Now), ("$k", kind), ("$u", Trim(username, 64)), ("$m", Trim(machineId, 128)), ("$ip", ip), ("$msg", Trim(message, 300))));

    public List<object> ListEvents(int limit)
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT ts,kind,username,machine_id,ip,message FROM events ORDER BY id DESC LIMIT $n", ("$n", limit));
        using var reader = command.ExecuteReader();
        var result = new List<object>();
        while (reader.Read())
            result.Add(new
            {
                ts = reader.GetString(0), kind = reader.GetString(1), username = reader.GetString(2),
                machineId = reader.GetString(3), ip = reader.GetString(4), message = reader.GetString(5)
            });
        return result;
    }

    public void Prune() => Write(connection =>
    {
        string telemetryCutoff = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");
        string eventCutoff = DateTimeOffset.UtcNow.AddDays(-90).ToString("O");
        string sessionCutoff = DateTimeOffset.UtcNow.AddDays(-7).ToString("O");
        Execute(connection, "DELETE FROM telemetry WHERE ts < $t", ("$t", telemetryCutoff));
        Execute(connection, "DELETE FROM events WHERE ts < $t", ("$t", eventCutoff));
        Execute(connection, "DELETE FROM sessions WHERE last_seen < $t", ("$t", sessionCutoff));
        Execute(connection, """
            DELETE FROM logs WHERE id IN (
                SELECT id FROM (SELECT id, ROW_NUMBER() OVER (PARTITION BY machine_id ORDER BY id DESC) AS n FROM logs) WHERE n > $max)
            """, ("$max", MaxLogLinesPerMachine));
    });

    // ---------- 工具 ----------

    private void Write(Action<SqliteConnection> action) => Write<object?>(connection => { action(connection); return null; });

    private T Write<T>(Func<SqliteConnection, T> action)
    {
        lock (_writeLock)
        {
            using var connection = Open();
            return action(connection);
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        object? value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static string Trim(string? value, int max)
    {
        value ??= "";
        return value.Length <= max ? value : value[..max];
    }

    private static JsonElement ParseJson(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    private static object? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static object? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? (value.GetBoolean() ? 1 : 0) : null;

    private static object? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? Trim(value.GetString(), 40) : null;

    private static double? NullableDouble(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetDouble(index);
}

public static class Passwords
{
    private const int Iterations = 120_000;

    public static string RequireStrong(string password)
    {
        if (password.Length < 8) throw new ArgumentException("密码至少 8 位。");
        return password;
    }

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        string[] parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out int iterations)) return false;
        try
        {
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class FailureLimiter
{
    private const int MaxFailures = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Since)> _failures = new();

    public bool IsBlocked(string key) =>
        _failures.TryGetValue(key, out var entry) && entry.Count >= MaxFailures && DateTimeOffset.UtcNow - entry.Since < Window;

    public void Fail(string key) => _failures.AddOrUpdate(key,
        _ => (1, DateTimeOffset.UtcNow),
        (_, entry) => DateTimeOffset.UtcNow - entry.Since >= Window ? (1, DateTimeOffset.UtcNow) : (entry.Count + 1, entry.Since));

    public void Reset(string key) => _failures.TryRemove(key, out _);
}
