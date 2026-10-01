using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using YaxinLicense.Server;

string baseDirectory = AppContext.BaseDirectory;
string dataDirectory = Environment.GetEnvironmentVariable("YAXIN_LICENSE_DATA") ?? Path.Combine(baseDirectory, "data");
string certificatePath = Environment.GetEnvironmentVariable("YAXIN_LICENSE_CERT") ?? Path.Combine(baseDirectory, "cert", "server.pfx");
int port = int.TryParse(Environment.GetEnvironmentVariable("YAXIN_LICENSE_PORT"), out int configuredPort) ? configuredPort : 38443;
Directory.CreateDirectory(dataDirectory);
var db = new LicenseDb(Path.Combine(dataDirectory, "license.db"));
db.Initialize();
var updates = new UpdateStore(Environment.GetEnvironmentVariable("YAXIN_LICENSE_UPDATES") ?? Path.Combine(dataDirectory, "updates"));

// 维护命令：yaxin-license set-admin-password <密码> / add-user <用户名> <密码>
if (args.Length > 0)
{
    switch (args[0])
    {
        case "set-admin-password" when args.Length == 2:
            db.SetSetting("admin_password", Passwords.Hash(Passwords.RequireStrong(args[1])));
            Console.WriteLine("管理员密码已更新。");
            return 0;
        case "add-user" when args.Length == 3:
            db.CreateUser(args[1], Passwords.RequireStrong(args[2]), "");
            Console.WriteLine("用户已创建。");
            return 0;
        default:
            Console.WriteLine("用法：yaxin-license [set-admin-password <密码> | add-user <用户名> <密码>]");
            return 2;
    }
}
if (db.GetSetting("admin_password") is null)
{
    Console.Error.WriteLine("尚未设置管理员密码，请先运行：yaxin-license set-admin-password <密码>");
    return 1;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.Configure<JsonOptions>(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, "");
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 1024 * 1024;
    options.AddServerHeader = false;
    options.ListenAnyIP(port, listen => listen.UseHttps(certificate));
});
var app = builder.Build();

var limiter = new FailureLimiter();
var adminSessions = new ConcurrentDictionary<string, DateTimeOffset>();
var mobileSessions = new ConcurrentDictionary<string, (string Username, DateTimeOffset Expires)>();
var remote = new RemoteHub();
const string AdminCookie = "yx_admin";
const string MobileCookie = "yx_m";
const int ReportIntervalSeconds = 60;
string adminHtml = LoadResource("admin.html");
string mobileHtml = LoadResource("m.html");

string ClientIp(HttpContext context) => context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "";

bool IsAdmin(HttpContext context)
{
    if (!context.Request.Cookies.TryGetValue(AdminCookie, out string? token) || string.IsNullOrEmpty(token)) return false;
    string key = Passwords.Sha256(token);
    if (!adminSessions.TryGetValue(key, out DateTimeOffset expires)) return false;
    if (expires < DateTimeOffset.UtcNow)
    {
        adminSessions.TryRemove(key, out _);
        return false;
    }
    return true;
}

// ---------- 客户端接口 ----------

app.MapPost("/api/v1/login", (HttpContext context, ClientLoginRequest request) =>
{
    string ip = ClientIp(context);
    if (limiter.IsBlocked(ip)) return Results.Json(new { ok = false, message = "尝试次数过多，请 10 分钟后再试。" }, statusCode: 429);
    string username = (request.Username ?? "").Trim();
    string machineId = (request.MachineId ?? "").Trim();
    if (username.Length is 0 or > 64 || machineId.Length is < 16 or > 128)
        return Results.Json(new { ok = false, message = "登录信息无效。" }, statusCode: 400);

    UserRow? user = db.FindUser(username);
    if (user is null || !Passwords.Verify(request.Password ?? "", user.PasswordHash))
    {
        limiter.Fail(ip);
        db.AddEvent("login-failed", username, machineId, ip, "用户名或密码错误");
        return Results.Json(new { ok = false, message = "用户名或密码错误。" }, statusCode: 401);
    }
    limiter.Reset(ip);
    if (!user.Enabled)
    {
        db.AddEvent("login-denied", user.Username, machineId, ip, "账号已禁用");
        return Results.Json(new { ok = false, message = "账号已被禁用，请联系管理员。" }, statusCode: 403);
    }

    MachineRow machine = db.UpsertMachine(machineId, user.Username, request, ip);
    if (!machine.Enabled)
    {
        db.AddEvent("login-denied", user.Username, machineId, ip, "机器已禁用");
        return Results.Json(new { ok = false, message = "本机已被禁止使用，请联系管理员。" }, statusCode: 403);
    }
    string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    db.CreateSession(Passwords.Sha256(token), user.Username, machineId);
    db.TouchUserLogin(user.Username);
    db.AddEvent("login", user.Username, machineId, ip, $"{request.MachineName} {request.AppVersion}");
    return Results.Json(new { ok = true, token, message = "授权成功", reportIntervalSeconds = ReportIntervalSeconds });
});

SessionRow? FindClientSession(HttpContext context)
{
    string header = context.Request.Headers.Authorization.ToString();
    string token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..].Trim() : "";
    return token.Length == 0 ? null : db.FindSession(Passwords.Sha256(token));
}

string? ClientDenial(SessionRow session)
{
    UserRow? user = db.FindUser(session.Username);
    MachineRow? machine = db.FindMachine(session.MachineId);
    return user is null || !user.Enabled ? "账号已被禁用，请联系管理员。"
        : machine is null || !machine.Enabled ? "本机已被禁止使用，请联系管理员。"
        : null;
}

app.MapPost("/api/v1/report", (HttpContext context, ClientReport report) =>
{
    SessionRow? session = FindClientSession(context);
    if (session is null) return Results.Json(new { allowed = true, relogin = true, message = "会话已失效" }, statusCode: 401);

    string? denial = ClientDenial(session);
    if (denial is not null)
    {
        db.DeleteSession(session.TokenHash);
        db.AddEvent("revoked", session.Username, session.MachineId, ClientIp(context), denial);
        return Results.Json(new { allowed = false, message = denial });
    }
    db.SaveReport(session, report, ClientIp(context));
    return Results.Json(new { allowed = true, message = "", reportIntervalSeconds = ReportIntervalSeconds });
});

app.MapGet("/api/v1/update", (HttpContext context, string? runtime, string? version) =>
{
    SessionRow? session = FindClientSession(context);
    if (session is null) return Results.Json(new { message = "会话已失效" }, statusCode: 401);
    if (ClientDenial(session) is { } denial) return Results.Json(new { message = denial }, statusCode: 403);
    UpdatePackage? latest = updates.Latest(runtime ?? "");
    bool newer = latest is not null && (!Version.TryParse(version, out Version? current) || latest.ParsedVersion > current);
    if (!newer) return Results.Json(new { available = false, version = latest?.Version });
    return Results.Json(new { available = true, latest!.Version, latest.Size, latest.Sha256, latest.Notes });
});

app.MapGet("/api/v1/update/download", (HttpContext context, string? runtime, string? version) =>
{
    SessionRow? session = FindClientSession(context);
    if (session is null) return Results.Json(new { message = "会话已失效" }, statusCode: 401);
    if (ClientDenial(session) is { } denial) return Results.Json(new { message = denial }, statusCode: 403);
    UpdatePackage? package = updates.Find(runtime ?? "", version ?? "");
    if (package is null) return Results.Json(new { message = "更新包不存在，可能已被管理员删除。" }, statusCode: 404);
    if (context.Request.Headers.Range.Count == 0)
        db.AddEvent("update-download", session.Username, session.MachineId, ClientIp(context), package.FileName);
    return Results.File(updates.PathOf(package), "application/zip", package.FileName, enableRangeProcessing: true);
});

// 手机远程控制通道：客户端长轮询领取指令、回传结果；有手机在看时附带实时快照。
app.MapPost("/api/v1/sync", async (HttpContext context, ClientSync sync) =>
{
    SessionRow? session = FindClientSession(context);
    if (session is null) return Results.Json(new { message = "会话已失效" }, statusCode: 401);
    if (ClientDenial(session) is { } denial) return Results.Json(new { message = denial }, statusCode: 403);
    if (sync.Snapshot is { } snapshot && snapshot.GetRawText().Length > 256 * 1024) sync = sync with { Snapshot = null };
    try
    {
        var (commands, live) = await remote.SyncAsync(session.MachineId, sync, context.RequestAborted);
        return Results.Json(new { commands, live });
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        return Results.Empty;
    }
});

// ---------- 手机远程查看 ----------

string? MobileUser(HttpContext context)
{
    if (!context.Request.Cookies.TryGetValue(MobileCookie, out string? token) || string.IsNullOrEmpty(token)) return null;
    string key = Passwords.Sha256(token);
    if (!mobileSessions.TryGetValue(key, out var entry)) return null;
    if (entry.Expires < DateTimeOffset.UtcNow || db.FindUser(entry.Username) is not { Enabled: true })
    {
        mobileSessions.TryRemove(key, out _);
        return null;
    }
    return entry.Username;
}

bool OwnsMachine(string username, string machineId) =>
    string.Equals(db.MachineOwner(machineId), username, StringComparison.OrdinalIgnoreCase);

MobileMachineView MobileMachine(object machine)
{
    JsonElement json = JsonSerializer.SerializeToElement(machine, machine.GetType());
    string id = json.GetProperty("machineId").GetString()!;
    return new MobileMachineView(id, json.GetProperty("name").GetString(), json.GetProperty("note").GetString(),
        json.GetProperty("version").GetString(), json.GetProperty("enabled").GetBoolean(),
        json.GetProperty("lastSeen"), json.GetProperty("telemetry"), remote.IsClientConnected(id));
}

app.MapGet("/m", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; frame-ancestors 'none'";
    return Results.Content(mobileHtml, "text/html; charset=utf-8");
});

app.MapPost("/m/api/login", (HttpContext context, MobileLoginRequest request) =>
{
    string ip = ClientIp(context);
    if (limiter.IsBlocked(ip)) return Results.Json(new { error = "尝试次数过多，请 10 分钟后再试。" }, statusCode: 429);
    UserRow? user = db.FindUser((request.Username ?? "").Trim());
    if (user is null || !Passwords.Verify(request.Password ?? "", user.PasswordHash))
    {
        limiter.Fail(ip);
        db.AddEvent("mobile-login-failed", request.Username ?? "", "", ip, "");
        return Results.Json(new { error = "用户名或密码错误。" }, statusCode: 401);
    }
    limiter.Reset(ip);
    if (!user.Enabled) return Results.Json(new { error = "账号已被禁用，请联系管理员。" }, statusCode: 403);
    string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    mobileSessions[Passwords.Sha256(token)] = (user.Username, DateTimeOffset.UtcNow.AddDays(7));
    context.Response.Cookies.Append(MobileCookie, token, new CookieOptions
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/m", MaxAge = TimeSpan.FromDays(7)
    });
    db.AddEvent("mobile-login", user.Username, "", ip, context.Request.Headers.UserAgent.ToString());
    return Results.Json(new { ok = true, username = user.Username });
});

var mobile = app.MapGroup("/m/api").AddEndpointFilter(async (invocation, next) =>
{
    HttpContext context = invocation.HttpContext;
    if (context.Request.Path.StartsWithSegments("/m/api/login")) return await next(invocation);
    if (MobileUser(context) is null) return Results.Json(new { error = "请先登录。" }, statusCode: 401);
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.Headers["X-Mobile"] != "1")
        return Results.Json(new { error = "请求无效。" }, statusCode: 403);
    return await next(invocation);
});

mobile.MapPost("/logout", (HttpContext context) =>
{
    if (context.Request.Cookies.TryGetValue(MobileCookie, out string? token))
        mobileSessions.TryRemove(Passwords.Sha256(token), out _);
    context.Response.Cookies.Delete(MobileCookie, new CookieOptions { Path = "/m" });
    return Results.Json(new { ok = true });
});
mobile.MapGet("/machines", (HttpContext context) =>
{
    string username = MobileUser(context)!;
    var machines = db.ListMachines(username).Select(MobileMachine);
    return Results.Json(new { username, now = DateTimeOffset.UtcNow, onlineSeconds = ReportIntervalSeconds * 3, machines });
});
mobile.MapGet("/machine", (HttpContext context, string machineId) =>
{
    // 旧版本工具（1.6.5 及以前）不连远程通道，手机端用定时上报的数据只读展示。
    MobileMachineView? machine = db.ListMachines(MobileUser(context)!).Select(MobileMachine).FirstOrDefault(item => item.MachineId == machineId);
    if (machine is null) return Results.Json(new { error = "设备不存在。" }, statusCode: 404);
    var (snapshot, snapshotAt, connected, commands) = remote.View(machineId);
    return Results.Json(new { now = DateTimeOffset.UtcNow, onlineSeconds = ReportIntervalSeconds * 3, machine, connected, snapshot, snapshotAt, commands });
});
mobile.MapPost("/command", (HttpContext context, MobileCommandRequest request) =>
{
    string username = MobileUser(context)!;
    string machineId = request.MachineId ?? "";
    if (!OwnsMachine(username, machineId)) return Results.Json(new { error = "设备不存在。" }, statusCode: 404);
    if (db.FindMachine(machineId) is not { Enabled: true }) return Results.Json(new { error = "设备已被禁用。" }, statusCode: 403);
    string action = request.Action ?? "";
    if (!RemoteHub.Actions.Contains(action)) return Results.Json(new { error = "不支持的操作。" }, statusCode: 400);
    if (action == "reconcile" && (string.IsNullOrEmpty(request.OrderKey)
        || request.Resolution is not ("ConfirmedNotPlaced" or "ConfirmedSettled")))
        return Results.Json(new { error = "对账参数无效。" }, statusCode: 400);
    if (!remote.IsClientConnected(machineId))
        return Results.Json(new { error = "设备不在线，或工具版本不支持远程控制。" }, statusCode: 409);
    var command = new RemoteCommand(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)), action,
        action == "reconcile" ? request.OrderKey : null, action == "reconcile" ? request.Resolution : null);
    RemoteCommandView view = remote.Enqueue(machineId, command);
    db.AddEvent("remote-command", username, machineId, ClientIp(context),
        action + (command.OrderKey is null ? "" : $" {command.OrderKey} {command.Resolution}"));
    return Results.Json(view);
});

// ---------- 公开下载页 ----------

app.MapGet("/", () => Results.Redirect("/download"));
app.MapGet("/download", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'";
    return Results.Content(DownloadPage.Render(updates), "text/html; charset=utf-8");
});
app.MapGet("/download/latest/{runtime}", (string runtime) =>
{
    UpdatePackage? latest = DownloadPage.Packages(updates).Where(package => package.Runtime == runtime).MaxBy(package => package.ParsedVersion);
    return latest is null ? Results.NotFound() : Results.Redirect("/download/" + latest.FileName);
});
app.MapGet("/download/{fileName}", (HttpContext context, string fileName) =>
{
    UpdatePackage? package = DownloadPage.Packages(updates).FirstOrDefault(item => item.FileName == fileName);
    if (package is null) return Results.NotFound();
    if (context.Request.Headers.Range.Count == 0)
        db.AddEvent("package-download", "", "", ClientIp(context), package.FileName);
    return Results.File(updates.PathOf(package), "application/zip", package.FileName, enableRangeProcessing: true);
});

// ---------- 管理后台 ----------

app.MapGet("/admin", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; frame-ancestors 'none'";
    return Results.Content(adminHtml, "text/html; charset=utf-8");
});

app.MapPost("/admin/api/login", (HttpContext context, AdminLoginRequest request) =>
{
    string ip = ClientIp(context);
    if (limiter.IsBlocked("admin:" + ip)) return Results.Json(new { error = "尝试次数过多，请 10 分钟后再试。" }, statusCode: 429);
    if (!Passwords.Verify(request.Password ?? "", db.GetSetting("admin_password") ?? ""))
    {
        limiter.Fail("admin:" + ip);
        db.AddEvent("admin-login-failed", "admin", "", ip, "");
        return Results.Json(new { error = "密码错误。" }, statusCode: 401);
    }
    limiter.Reset("admin:" + ip);
    string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    adminSessions[Passwords.Sha256(token)] = DateTimeOffset.UtcNow.AddHours(12);
    context.Response.Cookies.Append(AdminCookie, token, new CookieOptions
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/admin", MaxAge = TimeSpan.FromHours(12)
    });
    db.AddEvent("admin-login", "admin", "", ip, "");
    return Results.Json(new { ok = true });
});

var admin = app.MapGroup("/admin/api").AddEndpointFilter(async (invocation, next) =>
{
    HttpContext context = invocation.HttpContext;
    if (context.Request.Path.StartsWithSegments("/admin/api/login")) return await next(invocation);
    if (!IsAdmin(context)) return Results.Json(new { error = "请先登录。" }, statusCode: 401);
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.Headers["X-Admin"] != "1")
        return Results.Json(new { error = "请求无效。" }, statusCode: 403);
    return await next(invocation);
});

admin.MapPost("/logout", (HttpContext context) =>
{
    if (context.Request.Cookies.TryGetValue(AdminCookie, out string? token))
        adminSessions.TryRemove(Passwords.Sha256(token), out _);
    context.Response.Cookies.Delete(AdminCookie, new CookieOptions { Path = "/admin" });
    return Results.Json(new { ok = true });
});
admin.MapGet("/overview", () => Results.Json(new
{
    now = DateTimeOffset.UtcNow,
    onlineSeconds = ReportIntervalSeconds * 3,
    users = db.ListUsers(),
    machines = db.ListMachines()
}));
admin.MapPost("/users", (UserCreateRequest request) =>
{
    db.CreateUser((request.Username ?? "").Trim(), Passwords.RequireStrong(request.Password ?? ""), request.Note ?? "");
    return Results.Json(new { ok = true });
});
admin.MapPost("/users/update", (UserUpdateRequest request) =>
{
    if (!string.IsNullOrEmpty(request.Password)) Passwords.RequireStrong(request.Password);
    db.UpdateUser(request.Username ?? "", request.Enabled, request.Password, request.Note);
    return Results.Json(new { ok = true });
});
admin.MapPost("/users/delete", (UserUpdateRequest request) =>
{
    db.DeleteUser(request.Username ?? "");
    return Results.Json(new { ok = true });
});
admin.MapPost("/machines/update", (MachineUpdateRequest request) =>
{
    db.UpdateMachine(request.MachineId ?? "", request.Enabled, request.Note);
    return Results.Json(new { ok = true });
});
admin.MapPost("/machines/delete", (MachineUpdateRequest request) =>
{
    db.DeleteMachine(request.MachineId ?? "");
    return Results.Json(new { ok = true });
});
admin.MapGet("/machines/logs", (string machineId, int? limit) =>
    Results.Json(db.ListLogs(machineId, Math.Clamp(limit ?? 500, 1, 5000))));
admin.MapGet("/machines/telemetry", (string machineId, int? limit) =>
    Results.Json(db.ListTelemetry(machineId, Math.Clamp(limit ?? 300, 1, 5000))));
admin.MapGet("/updates", () => Results.Json(new { runtimes = UpdateStore.Runtimes, packages = updates.List() }));
admin.MapPost("/updates/upload", async (HttpContext context, string? fileName, string? notes) =>
{
    if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        limit.MaxRequestBodySize = UpdateStore.MaxPackageBytes;
    UpdatePackage package = await updates.SaveAsync(fileName ?? "", context.Request.Body, notes, context.RequestAborted);
    db.AddEvent("update-upload", "admin", "", ClientIp(context), package.FileName);
    return Results.Json(package);
});
admin.MapPost("/updates/delete", (HttpContext context, UpdateDeleteRequest request) =>
{
    updates.Delete(request.FileName ?? "");
    db.AddEvent("update-delete", "admin", "", ClientIp(context), request.FileName ?? "");
    return Results.Json(new { ok = true });
});
admin.MapPost("/updates/notes", (UpdateNotesRequest request) =>
{
    updates.SetNotes(request.Version ?? "", request.Notes ?? "");
    return Results.Json(new { ok = true });
});
admin.MapGet("/events", (int? limit) => Results.Json(db.ListEvents(Math.Clamp(limit ?? 200, 1, 2000))));
admin.MapPost("/password", (AdminPasswordRequest request) =>
{
    if (!Passwords.Verify(request.Current ?? "", db.GetSetting("admin_password") ?? ""))
        return Results.Json(new { error = "当前密码错误。" }, statusCode: 400);
    db.SetSetting("admin_password", Passwords.Hash(Passwords.RequireStrong(request.Next ?? "")));
    adminSessions.Clear();
    return Results.Json(new { ok = true });
});

app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (ArgumentException exception)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = exception.Message });
    }
});

_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
    do
    {
        try { db.Prune(); } catch (Exception exception) { Console.Error.WriteLine("清理失败：" + exception.Message); }
    }
    while (await timer.WaitForNextTickAsync());
});

Console.WriteLine($"授权服务已启动：https://0.0.0.0:{port}/admin");
app.Run();
return 0;

static string LoadResource(string name)
{
    Assembly assembly = typeof(LicenseDb).Assembly;
    string resource = assembly.GetManifestResourceNames().Single(value => value.EndsWith(name, StringComparison.Ordinal));
    using Stream stream = assembly.GetManifestResourceStream(resource)!;
    using var reader = new StreamReader(stream, Encoding.UTF8);
    return reader.ReadToEnd();
}
