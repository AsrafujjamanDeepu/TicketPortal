using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Tests.Infrastructure;
using Xunit;

namespace TicketPortal.Api.Tests.Integration;

[Collection(SharedApiCollection.Name)]
public sealed class BookingCreateIdempotencyTests(TicketPortalWebApplicationFactory factory)
{
    [Fact]
    public async Task ConcurrentCreateRequests_ForOneHold_ReturnOneBookingAndBothSucceed()
    {
        Guid tripId;
        Guid seatId;
        Guid boardingTerminalId;
        Guid droppingTerminalId;

        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var candidate = await db.TripSeats
                .Where(seat => seat.Status == TripSeatStatus.Available)
                .Where(seat => seat.Trip.Status == TripStatus.Scheduled && seat.Trip.DepartureTimeUtc > DateTime.UtcNow)
                .Where(seat => seat.Trip.InventoryMode == OperatorInventoryMode.PlatformManaged)
                .Select(seat => new
                {
                    seat.Id,
                    seat.TripId,
                    seat.Trip.DepartureTerminalId,
                    seat.Trip.ArrivalTerminalId,
                })
                .FirstAsync();

            tripId = candidate.TripId;
            seatId = candidate.Id;
            boardingTerminalId = candidate.DepartureTerminalId;
            droppingTerminalId = candidate.ArrivalTerminalId;
        }

        var customer = await factory.CreateAuthenticatedClientAsync(DemoAccounts.Customer, DemoAccounts.Password);
        var holdResponse = await customer.PostAsJsonAsync("/api/seatholds", new
        {
            TripId = tripId,
            TripSeatIds = new[] { seatId },
        });
        Assert.Equal(HttpStatusCode.Created, holdResponse.StatusCode);
        var hold = await holdResponse.Content.ReadFromJsonAsync<HoldResponse>();
        Assert.NotNull(hold);

        var bookingPayload = new
        {
            TripId = tripId,
            HoldToken = hold!.HoldToken,
            BoardingTerminalId = boardingTerminalId,
            DroppingTerminalId = droppingTerminalId,
            ContactName = "Idempotency Test",
            ContactPhone = "01700000000",
            Passengers = new[]
            {
                new { TripSeatId = seatId, FullName = "Idempotency Passenger" },
            },
        };

        var requests = await Task.WhenAll(
            customer.PostAsJsonAsync("/api/bookings", bookingPayload),
            customer.PostAsJsonAsync("/api/bookings", bookingPayload));

        Assert.All(requests, response => Assert.True(
            response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Expected booking creation/replay success, got {(int)response.StatusCode}: {response.StatusCode}"));

        using var verifyScope = factory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bookings = await verifyDb.Bookings.AsNoTracking().Where(booking => booking.SeatHoldId == hold.Id).ToListAsync();
        Assert.Single(bookings);
        Assert.True(bookings[0].Id != Guid.Empty);
    }

    private sealed record HoldResponse(Guid Id, string HoldToken);
}
