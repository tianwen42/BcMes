using Microsoft.AspNetCore.SignalR;

namespace BcMes.Server.Hubs;

/// <summary>工位机实时消息通道。客户端进来什么组都不用加，服务端推 Clients.All。</summary>
public sealed class MesHub : Hub;
