using System.Text.Json;
using HeavyTasksCluster.Data;

namespace HeavyTasksCluster.Services;

public class AutoScalerConfig
{
    public bool AutoScaleEnabled { get; set; } = true;
    public int DesiredInstances { get; set; } = 2;
    public int MinInstances { get; set; } = 2;
    public int MaxInstances { get; set; } = 5;
    public string LastScaleEvent { get; set; } = "Кластер ініціалізовано (2 базових вузли)";
    public DateTime LastScaleAt { get; set; } = DateTime.UtcNow;
}

public class AutoScalerService
{
    private const string ConfigRedisKey = "cluster:autoscaler:config";
    private readonly RedisClusterService _redis;
    private readonly ClusterSettings _settings;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public AutoScalerService(RedisClusterService redis, ClusterSettings settings)
    {
        _redis = redis;
        _settings = settings;
    }

    public async Task<AutoScalerConfig> GetConfigAsync()
    {
        var raw = await _redis.Db.StringGetAsync(ConfigRedisKey);
        if (!raw.IsNullOrEmpty)
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<AutoScalerConfig>((string)raw!, JsonOpts);
                if (cfg != null) return cfg;
            }
            catch { }
        }

        var defaultCfg = new AutoScalerConfig();
        await SaveConfigAsync(defaultCfg);
        return defaultCfg;
    }

    public async Task SaveConfigAsync(AutoScalerConfig cfg)
    {
        var json = JsonSerializer.Serialize(cfg, JsonOpts);
        await _redis.Db.StringSetAsync(ConfigRedisKey, json);
    }

    public async Task<(bool Scaled, string Message, AutoScalerConfig Config)> ScaleUpAsync(string reason = "Ручне масштабування адміністратором")
    {
        var cfg = await GetConfigAsync();
        if (cfg.DesiredInstances >= cfg.MaxInstances)
        {
            return (false, $"Досягнуто максимум серверів ({cfg.MaxInstances})", cfg);
        }

        cfg.DesiredInstances++;
        var newServerId = $"node-app-{cfg.DesiredInstances}";
        cfg.LastScaleEvent = $"+ Запущено {newServerId} ({reason})";
        cfg.LastScaleAt = DateTime.UtcNow;
        await SaveConfigAsync(cfg);
        await SyncVirtualNodesHeartbeatAsync(cfg);

        return (true, $"Запущено додатковий вузол {newServerId} (активно: {cfg.DesiredInstances}/{cfg.MaxInstances})", cfg);
    }

    public async Task<(bool Scaled, string Message, AutoScalerConfig Config)> ScaleDownAsync(string reason = "Ручне масштабування адміністратором")
    {
        var cfg = await GetConfigAsync();
        if (cfg.DesiredInstances <= cfg.MinInstances)
        {
            return (false, $"Досягнуто мінімальну кількість серверів ({cfg.MinInstances})", cfg);
        }

        var removedServerId = $"node-app-{cfg.DesiredInstances}";
        cfg.DesiredInstances--;
        cfg.LastScaleEvent = $"- Зупинено {removedServerId} ({reason})";
        cfg.LastScaleAt = DateTime.UtcNow;
        await SaveConfigAsync(cfg);

        await _redis.Db.KeyDeleteAsync($"{RedisClusterService.ServerNodePrefix}{removedServerId}");
        await SyncVirtualNodesHeartbeatAsync(cfg);

        return (true, $"Зупинено вузол {removedServerId} (активно: {cfg.DesiredInstances}/{cfg.MaxInstances})", cfg);
    }

    public async Task<AutoScalerConfig> ToggleAutoScaleAsync(bool? enabled)
    {
        var cfg = await GetConfigAsync();
        cfg.AutoScaleEnabled = enabled ?? !cfg.AutoScaleEnabled;
        cfg.LastScaleEvent = cfg.AutoScaleEnabled
            ? "Автоматичне масштабування увімкнено"
            : "Автоматичне масштабування вимкнено";
        cfg.LastScaleAt = DateTime.UtcNow;
        await SaveConfigAsync(cfg);
        return cfg;
    }

    public async Task EvaluateAutoScalingAsync(int queueLength, int totalRunningTasks)
    {
        var cfg = await GetConfigAsync();
        await SyncVirtualNodesHeartbeatAsync(cfg);

        if (!cfg.AutoScaleEnabled) return;

        var cooldownSec = (DateTime.UtcNow - cfg.LastScaleAt).TotalSeconds;
        if (cooldownSec < 8) return;

        if (queueLength >= 1 && cfg.DesiredInstances < cfg.MaxInstances)
        {
            await ScaleUpAsync($"Авто-масштабування: у черзі {queueLength} задач");
        }
        else if (queueLength == 0 && totalRunningTasks <= 1 && cfg.DesiredInstances > cfg.MinInstances && cooldownSec >= 20)
        {
            await ScaleDownAsync("Авто-масштабування: низьке навантаження");
        }
    }

    public async Task SyncVirtualNodesHeartbeatAsync(AutoScalerConfig cfg)
    {
        for (int idx = 3; idx <= cfg.MaxInstances; idx++)
        {
            var sid = $"node-app-{idx}";
            var key = $"{RedisClusterService.ServerNodePrefix}{sid}";
            if (idx <= cfg.DesiredInstances)
            {
                var payload = new
                {
                    serverId = sid,
                    hostname = $"scaled-csharp-node-{idx}",
                    pid = 4000 + idx,
                    status = "ONLINE",
                    isDynamicNode = true,
                    cpuUsagePercent = Math.Round(4.5 + (idx * 1.3), 1),
                    memoryUsedMb = 92 + idx * 4,
                    memoryTotalMb = 2048,
                    memoryUsagePercent = 4.6,
                    activeWorkers = 0,
                    maxWorkers = _settings.MaxConcurrentTasksPerServer,
                    runningTaskIds = Array.Empty<string>(),
                    uptimeSec = Math.Max(1, (int)(DateTime.UtcNow - cfg.LastScaleAt).TotalSeconds),
                    updatedAt = DateTime.UtcNow.ToString("O")
                };
                var json = JsonSerializer.Serialize(payload, JsonOpts);
                await _redis.Db.StringSetAsync(key, json, TimeSpan.FromSeconds(15));
            }
            else
            {
                await _redis.Db.KeyDeleteAsync(key);
            }
        }
    }

    public async Task<object> GetStatusAsync()
    {
        var cfg = await GetConfigAsync();
        var nodes = await _redis.GetActiveClusterNodesAsync();
        return new
        {
            autoScaleEnabled = cfg.AutoScaleEnabled,
            activeCount = Math.Max(nodes.Count, cfg.DesiredInstances),
            desiredInstances = cfg.DesiredInstances,
            minInstances = cfg.MinInstances,
            maxInstances = cfg.MaxInstances,
            lastScaleEvent = cfg.LastScaleEvent,
            lastScaleAt = cfg.LastScaleAt.ToString("O")
        };
    }
}
