using Microsoft.EntityFrameworkCore;
using TicketPortal.Api.Data;

namespace TicketPortal.Api.Realtime;

/// <summary>Aborts established SignalR connections after their account security state changes.</summary>
public sealed class RealtimeSessionRevocationService(
    IServiceScopeFactory scopeFactory,
    RealtimeConnectionTracker connections,
    ILogger<RealtimeSessionRevocationService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RevokeStaleConnectionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not check realtime session security state.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task RevokeStaleConnectionsAsync(CancellationToken cancellationToken)
    {
        var activeConnections = connections.AuthenticatedConnections();
        if (activeConnections.Count == 0) return;

        var expectedStamps = activeConnections
            .GroupBy(connection => connection.UserId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(connection => connection.SecurityStamp).Distinct().ToArray(), StringComparer.Ordinal);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userIds = expectedStamps.Keys
            .Select(value => Guid.TryParse(value, out var id) ? (Guid?)id : null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToArray();
        var users = await db.Users.AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .Select(user => new { user.Id, user.IsActive, user.SecurityStamp })
            .ToDictionaryAsync(user => user.Id.ToString(), cancellationToken);

        foreach (var connection in activeConnections)
        {
            var isCurrent = users.TryGetValue(connection.UserId, out var user)
                && user.IsActive
                && !string.IsNullOrWhiteSpace(connection.SecurityStamp)
                && string.Equals(user.SecurityStamp, connection.SecurityStamp, StringComparison.Ordinal);
            if (isCurrent) continue;

            logger.LogInformation("Closing realtime connection after an account security change.");
            connections.Abort(connection.ConnectionId);
        }
    }
}
