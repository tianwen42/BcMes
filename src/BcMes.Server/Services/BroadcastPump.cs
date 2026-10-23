using BcMes.Contracts;
using BcMes.Server.Data;
using BcMes.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace BcMes.Server.Services;

/// <summary>
/// 把落库成功的记录广播出去。
/// 刻意放在独立 channel 的另一侧：写线程只负责 TryWrite，绝不等网络 IO。
/// 否则一次网络抖动就会反压到 SQLite 的写入路径。
/// </summary>
public sealed class BroadcastPump : BackgroundService
{
    private readonly WriteQueue _q;
    private readonly IHubContext<MesHub> _hub;
    private readonly ILogger<BroadcastPump> _log;

    public BroadcastPump(WriteQueue q, IHubContext<MesHub> hub, ILogger<BroadcastPump> log)
    {
        _q = q; _hub = hub; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var b in _q.Broadcasts.ReadAllAsync(ct))
        {
            try { await _hub.Clients.All.SendAsync("Detection", b, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "广播失败"); }
        }
    }
}
