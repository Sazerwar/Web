using System.Text.Json;
using HeavyTasksCluster.Data;
using StackExchange.Redis;

namespace HeavyTasksCluster.Services;

public class RedisClusterService
{
    public const string TaskStatePrefix = "task:state:";
    public const string TaskCancelPrefix = "task:cancel:";
    public const string ServerNodePrefix = "cluster:node:";
    public const string TaskQueueKey = "tasks:queue";
    public const string TaskEventsChannel = "tasks:events";
    public const string TaskCancelChannel = "tasks:cancel";
    public const string TaskQueueChannel = "tasks:queue:notify";

    private readonly IConnectionMultiplexer _mux;
    private readonly ClusterSettings _settings;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RedisClusterService(IConnectionMultiplexer mux, ClusterSettings settings)
    {
        _mux = mux;
        _settings = settings;
    }

    public IDatabase Db => _mux.GetDatabase();
    public ISubscriber Sub => _mux.GetSubscriber();

    public async Task SetTaskProgressAsync(Guid taskId, object stateData)
    {
        var key = $"{TaskStatePrefix}{taskId}";
        var json = JsonSerializer.Serialize(stateData, JsonOpts);
        await Db.StringSetAsync(key, json, TimeSpan.FromHours(1));
        await Sub.PublishAsync(RedisChannel.Literal(TaskEventsChannel), json);
    }

    public async Task<JsonElement?> GetTaskProgressAsync(Guid taskId)
    {
        var raw = await Db.StringGetAsync($"{TaskStatePrefix}{taskId}");
        if (raw.IsNullOrEmpty) return null;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(raw!);
        }
        catch
        {
            return null;
        }
    }

    public async Task<long> EnqueueTaskAsync(Guid taskId)
    {
        var idStr = taskId.ToString();
        await Db.ListRemoveAsync(TaskQueueKey, idStr);
        var len = await Db.ListRightPushAsync(TaskQueueKey, idStr);
        var notifyJson = JsonSerializer.Serialize(new { @event = "enqueued", taskId = idStr, queueLength = len }, JsonOpts);
        await Sub.PublishAsync(RedisChannel.Literal(TaskQueueChannel), notifyJson);
        return len;
    }

    public async Task<Guid?> DequeueNextTaskAsync()
    {
        var val = await Db.ListLeftPopAsync(TaskQueueKey);
        if (val.IsNullOrEmpty) return null;
        return Guid.TryParse(val!, out var g) ? g : null;
    }

    public async Task RemoveTaskFromQueueAsync(Guid taskId)
    {
        await Db.ListRemoveAsync(TaskQueueKey, taskId.ToString());
    }

    public async Task<List<Guid>> GetQueuedTaskIdsAsync()
    {
        var vals = await Db.ListRangeAsync(TaskQueueKey, 0, -1);
        var list = new List<Guid>();
        foreach (var v in vals)
        {
            if (Guid.TryParse(v!, out var g)) list.Add(g);
        }
        return list;
    }

    public async Task PublishCancelCommandAsync(Guid taskId, string requestedBy)
    {
        await Db.StringSetAsync($"{TaskCancelPrefix}{taskId}", "1", TimeSpan.FromMinutes(10));
        var msg = JsonSerializer.Serialize(new
        {
            taskId = taskId.ToString(),
            requestedBy,
            timestamp = DateTime.UtcNow.ToString("O")
        }, JsonOpts);
        await Sub.PublishAsync(RedisChannel.Literal(TaskCancelChannel), msg);
    }

    public async Task<bool> IsTaskCancelledAsync(Guid taskId)
    {
        var val = await Db.StringGetAsync($"{TaskCancelPrefix}{taskId}");
        return val == "1";
    }

    public async Task RegisterServerHeartbeatAsync(object metricsPayload)
    {
        var key = $"{ServerNodePrefix}{_settings.ServerId}";
        var json = JsonSerializer.Serialize(metricsPayload, JsonOpts);
        await Db.StringSetAsync(key, json, TimeSpan.FromSeconds(15));
    }

    public async Task<List<JsonElement>> GetActiveClusterNodesAsync()
    {
        var result = new List<JsonElement>();
        foreach (var ep in _mux.GetEndPoints())
        {
            var server = _mux.GetServer(ep);
            if (!server.IsConnected) continue;

            await foreach (var key in server.KeysAsync(pattern: $"{ServerNodePrefix}*"))
            {
                var raw = await Db.StringGetAsync(key);
                if (!raw.IsNullOrEmpty)
                {
                    try
                    {
                        result.Add(JsonSerializer.Deserialize<JsonElement>(raw!));
                    }
                    catch { }
                }
            }
            break;
        }

        return result
            .OrderBy(n => n.TryGetProperty("serverId", out var sid) ? sid.GetString() : "")
            .ToList();
    }
}
