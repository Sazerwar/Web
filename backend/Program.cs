using System.Text;
using System.Text.Json;
using HeavyTasksCluster.Data;
using HeavyTasksCluster.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var serverId = Environment.GetEnvironmentVariable("SERVER_ID")
               ?? Environment.GetEnvironmentVariable("HOSTNAME")
               ?? $"csharp-app-{Environment.ProcessId}";

var dbHost = Environment.GetEnvironmentVariable("DB_HOST") ?? "localhost";
var dbPort = Environment.GetEnvironmentVariable("DB_PORT") ?? "5432";
var dbUser = Environment.GetEnvironmentVariable("DB_USER") ?? "postgres";
var dbPass = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "postgres";
var dbName = Environment.GetEnvironmentVariable("DB_NAME") ?? "heavy_tasks_db";
var connString = $"Host={dbHost};Port={dbPort};Database={dbName};Username={dbUser};Password={dbPass}";

var redisHost = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "localhost";
var redisPort = Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379";

var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? "super_secret_jwt_key_for_heavy_tasks_2026_csharp";

builder.Services.AddSingleton(new ClusterSettings
{
    ServerId = serverId,
    JwtSecret = jwtSecret,
    MaxMatrixSize = int.TryParse(Environment.GetEnvironmentVariable("MAX_MATRIX_SIZE"), out var mms) ? mms : 3000,
    MaxIterations = int.TryParse(Environment.GetEnvironmentVariable("MAX_ITERATIONS"), out var mi) ? mi : 50000,
    MaxExecutionTimeSec = int.TryParse(Environment.GetEnvironmentVariable("MAX_EXECUTION_TIME_SEC"), out var met) ? met : 60,
    MaxUserTasksHistory = int.TryParse(Environment.GetEnvironmentVariable("MAX_USER_TASKS_HISTORY"), out var muth) ? muth : 20,
    MaxConcurrentTasksPerServer = int.TryParse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_TASKS_PER_SERVER"), out var mct) ? mct : 2
});

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(connString));

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect($"{redisHost}:{redisPort},abortConnect=false"));

builder.Services.AddSingleton<RedisClusterService>();
builder.Services.AddSingleton<SseHubService>();
builder.Services.AddSingleton<JacobiWorkerService>();
builder.Services.AddSingleton<AutoScalerService>();
builder.Services.AddHostedService<ClusterBackgroundWorker>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Query.TryGetValue("token", out var qToken) && !string.IsNullOrWhiteSpace(qToken))
                {
                    context.Token = qToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders("X-Server-Id", "X-Handled-By");
    }));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await DbSeeder.EnsureDefaultAdminAsync(db);
}

app.UseCors();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Server-Id"] = serverId;
    ctx.Response.Headers["X-Handled-By"] = serverId;
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/api/health", (ClusterSettings settings, JacobiWorkerService workerSvc) => Results.Ok(new
{
    status = "ok",
    stack = "ASP.NET Core 8 (C#) + React",
    serverId = settings.ServerId,
    hostname = Environment.MachineName,
    pid = Environment.ProcessId,
    activeWorkers = workerSvc.ActiveCount,
    maxConcurrentTasksPerServer = settings.MaxConcurrentTasksPerServer,
    uptimeSec = (int)(DateTime.UtcNow - settings.StartedAt).TotalSeconds,
    timestamp = DateTime.UtcNow.ToString("O"),
    limits = new
    {
        maxMatrixSize = settings.MaxMatrixSize,
        maxIterations = settings.MaxIterations,
        maxExecutionTimeSec = settings.MaxExecutionTimeSec,
        maxUserTasksHistory = settings.MaxUserTasksHistory,
        maxConcurrentTasksPerServer = settings.MaxConcurrentTasksPerServer
    }
}));

app.MapGet("/api/cluster/nodes", async (ClusterSettings settings, RedisClusterService redisSvc) =>
{
    var nodes = await redisSvc.GetActiveClusterNodesAsync();
    return Results.Ok(new
    {
        handledByServerId = settings.ServerId,
        totalActiveNodes = nodes.Count,
        nodes
    });
});

app.MapFallbackToFile("index.html");

var port = Environment.GetEnvironmentVariable("PORT") ?? "3000";
app.Run($"http://0.0.0.0:{port}");
