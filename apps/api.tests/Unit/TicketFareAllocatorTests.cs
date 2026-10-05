using TicketPortal.Api.Models.Bookings;
using TicketPortal.Api.Models.Scheduling;
using TicketPortal.Api.Services;
using Xunit;

namespace TicketPortal.Api.Tests.Unit;

public sealed class TicketFareAllocatorTests
{
    [Fact]
    public void Allocate_SplitsDiscountTaxAndFeeExactlyAcrossTickets()
    {
        var booking = new Booking
        {
            SubTotal = 300m,
            DiscountAmount = 50m,
            TaxAmount = 12.50m,
            ServiceChargeAmount = 10m,
            GrandTotal = 272.50m,
        };
        var seats = new[]
        {
            new TripSeat { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Fare = 100m },
            new TripSeat { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Fare = 200m },
        };

        var allocation = TicketFareAllocator.Allocate(booking, seats);

        Assert.Equal(50m, allocation.Values.Sum(value => value.DiscountAmount));
        Assert.Equal(12.50m, allocation.Values.Sum(value => value.TaxAmount));
        Assert.Equal(10m, allocation.Values.Sum(value => value.ServiceChargeAmount));
        Assert.Equal(272.50m, allocation.Values.Sum(value => value.FinalFare));
        Assert.Equal(16.67m, allocation[seats[0].Id].DiscountAmount);
        Assert.Equal(33.33m, allocation[seats[1].Id].DiscountAmount);
    }

    [Fact]
    public void Allocate_RejectsTotalsThatDoNotMatchTheHeldFares()
    {
        var booking = new Booking { SubTotal = 101m, GrandTotal = 101m };
        var seats = new[] { new TripSeat { Id = Guid.NewGuid(), Fare = 100m } };

        Assert.Throws<InvalidOperationException>(() => TicketFareAllocator.Allocate(booking, seats));
    }
}
