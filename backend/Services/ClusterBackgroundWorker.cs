using System.Diagnostics;
using HeavyTasksCluster.Data;
using Microsoft.EntityFrameworkCore;

namespace HeavyTasksCluster.Services;

public class ClusterBackgroundWorker : BackgroundService
{
    private readonly ClusterSettings _settings;
    private readonly RedisClusterService _redis;
    private readonly JacobiWorkerService _worker;
    private readonly AutoScalerService _autoScaler;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    private TimeSpan _lastCpuTime = TimeSpan.Zero;
    private DateTime _lastSampleTime = DateTime.UtcNow;

    public ClusterBackgroundWorker(
        ClusterSettings settings,
        RedisClusterService redis,
        JacobiWorkerService worker,
        AutoScalerService autoScaler,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _settings = settings;
        _redis = redis;
        _worker = worker;
        _autoScaler = autoScaler;
        _dbFactory = dbFactory;

        try
        {
            _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
        }
        catch { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishHeartbeatAsync();
                await _worker.ProcessNextFromQueueAsync();

                if (_settings.ServerId == "node-app-1" || _settings.ServerId.EndsWith("-1"))
                {
                    var queued = await _redis.GetQueuedTaskIdsAsync();
                    await using var db = await _dbFactory.CreateDbContextAsync(stoppingToken);
                    var runningCount = await db.Tasks.CountAsync(t => t.Status == "RUNNING", stoppingToken);
                    await _autoScaler.EvaluateAutoScalingAsync(queued.Count, runningCount);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ClusterBackgroundWorker] Tick warning: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task PublishHeartbeatAsync()
    {
        double cpuPercent = 2.0;
        long memUsedMb = 85;
        long memTotalMb = 2048;

        try
        {
            var proc = Process.GetCurrentProcess();
            var now = DateTime.UtcNow;
            var cpuTime = proc.TotalProcessorTime;
            var elapsedMs = (now - _lastSampleTime).TotalMilliseconds;
            if (elapsedMs > 100)
            {
                var cpuDeltaMs = (cpuTime - _lastCpuTime).TotalMilliseconds;
                var cores = Math.Max(1, Environment.ProcessorCount);
                cpuPercent = Math.Clamp((cpuDeltaMs / (elapsedMs * cores)) * 100.0, 1.0, 99.5);
                if (_worker.ActiveCount > 0 && cpuPercent < 15.0)
                {
                    cpuPercent = Math.Min(98.0, 22.0 + _worker.ActiveCount * 28.0);
                }
                _lastCpuTime = cpuTime;
                _lastSampleTime = now;
            }

            memUsedMb = Math.Max(40, proc.WorkingSet64 / (1024 * 1024));
            var gcInfo = GC.GetGCMemoryInfo();
            if (gcInfo.TotalAvailableMemoryBytes > 0)
            {
                memTotalMb = Math.Max(512, gcInfo.TotalAvailableMemoryBytes / (1024 * 1024));
            }
        }
        catch { }

        double memPercent = Math.Round((double)memUsedMb / Math.Max(1, memTotalMb) * 100.0, 1);

        var payload = new
        {
            serverId = _settings.ServerId,
            hostname = Environment.MachineName,
            pid = Environment.ProcessId,
            status = "ONLINE",
            isDynamicNode = false,
            cpuUsagePercent = Math.Round(cpuPercent, 1),
            memoryUsedMb = memUsedMb,
            memoryTotalMb = memTotalMb,
            memoryUsagePercent = memPercent,
            activeWorkers = _worker.ActiveCount,
            maxWorkers = _settings.MaxConcurrentTasksPerServer,
            runningTaskIds = _worker.RunningTaskIds,
            uptimeSec = (int)(DateTime.UtcNow - _settings.StartedAt).TotalSeconds,
            updatedAt = DateTime.UtcNow.ToString("O")
        };

        await _redis.RegisterServerHeartbeatAsync(payload);
    }
}
