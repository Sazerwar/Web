using System.Collections.Concurrent;
using System.Text.Json;
using HeavyTasksCluster.Data;
using StackExchange.Redis;

namespace HeavyTasksCluster.Services;

public class SseClientConnection
{
    public Guid ConnectionId { get; init; } = Guid.NewGuid();
    public int UserId { get; init; }
    public string Role { get; init; } = "user";
    public HttpResponse Response { get; init; } = null!;
    public SemaphoreSlim WriteLock { get; } = new(1, 1);
}

public class SseHubService
{
    private readonly ConcurrentDictionary<Guid, SseClientConnection> _clients = new();
    private readonly RedisClusterService _redis;
    private readonly ClusterSettings _settings;

    public SseHubService(RedisClusterService redis, ClusterSettings settings)
    {
        _redis = redis;
        _settings = settings;

        _redis.Sub.Subscribe(RedisChannel.Literal(RedisClusterService.TaskEventsChannel), async (_, message) =>
        {
            if (message.IsNullOrEmpty) return;
            await BroadcastTaskEventAsync(message!);
        });
    }

    public async Task HandleSseStreamAsync(HttpContext ctx, int userId, string role)
    {
        ctx.Response.Headers["Content-Type"] = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache, no-transform";
        ctx.Response.Headers["Connection"] = "keep-alive";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        var conn = new SseClientConnection
        {
            UserId = userId,
            Role = role,
            Response = ctx.Response
        };

        _clients[conn.ConnectionId] = conn;

        try
        {
            var helloJson = JsonSerializer.Serialize(new
            {
                message = "Real-time SSE stream connected (ASP.NET Core)",
                connectedServerId = _settings.ServerId,
                userId,
                timestamp = DateTime.UtcNow.ToString("O")
            });

            await SendEventAsync(conn, "connected", helloJson);

            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ctx.RequestAborted);
                await SendRawCommentAsync(conn, $": heartbeat {_settings.ServerId} {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\n\n");
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _clients.TryRemove(conn.ConnectionId, out _);
        }
    }

    private async Task BroadcastTaskEventAsync(string rawJson)
    {
        int ownerId = -1;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("userId", out var uidProp) && uidProp.TryGetInt32(out var uid))
            {
                ownerId = uid;
            }
        }
        catch
        {
            return;
        }

        foreach (var pair in _clients)
        {
            var client = pair.Value;
            if (client.UserId == ownerId || client.Role == "admin")
            {
                await SendEventAsync(client, "task:update", rawJson);
            }
        }
    }

    private static async Task SendEventAsync(SseClientConnection conn, string eventName, string jsonData)
    {
        await conn.WriteLock.WaitAsync();
        try
        {
            await conn.Response.WriteAsync($"event: {eventName}\ndata: {jsonData}\n\n");
            await conn.Response.Body.FlushAsync();
        }
        catch
        {
        }
        finally
        {
            conn.WriteLock.Release();
        }
    }

    private static async Task SendRawCommentAsync(SseClientConnection conn, string comment)
    {
        await conn.WriteLock.WaitAsync();
        try
        {
            await conn.Response.WriteAsync(comment);
            await conn.Response.Body.FlushAsync();
        }
        catch { }
        finally
        {
            conn.WriteLock.Release();
        }
    }
}
