using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace SerialMemory.Api.Realtime;

public class ContextHub(IConnectionMultiplexer redis) : Hub
{
    public async Task Subscribe(string key)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, key);
    }

    public async Task Publish(string key, string json)
    {
        var db = redis.GetDatabase();
        await db.StringSetAsync("context:" + key, json);
        await Clients.Group(key).SendAsync("ContextUpdated", json);
    }
}