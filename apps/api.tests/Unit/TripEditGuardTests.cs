using TicketPortal.Api.DTO;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Scheduling;
using TicketPortal.Api.Services;
using Xunit;

namespace TicketPortal.Api.Tests.Unit
{
    // Chunk 6 / C6-1 (D6) — the pure decisions behind "what may be edited once a trip has sales".
    // Integration/TripEditAfterSalesTests proves the same rules over HTTP; this file covers the
    // cases that are impractical to set up there (a Booked seat, every locked field, rounding).
    public class TripEditGuardTests
    {
        private static readonly DateTime Departure = new(2030, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        private static Trip NewTrip(params (string Number, TripSeatStatus Status, decimal Fare)[] seats)
        {
            var trip = new Trip
            {
                BusOperatorId = Guid.NewGuid(),
                BusRouteId = Guid.NewGuid(),
                BusId = Guid.NewGuid(),
                DepartureTerminalId = Guid.NewGuid(),
                ArrivalTerminalId = Guid.NewGuid(),
                DepartureTimeUtc = Departure,
                ArrivalTimeUtc = Departure.AddHours(5),
                Currency = "BDT",
            };

            foreach (var (number, status, fare) in seats)
            {
                trip.TripSeats.Add(new TripSeat
                {
                    TripId = trip.Id,
                    SeatId = Guid.NewGuid(),
                    SeatNumber = number,
                    Status = status,
                    Fare = fare,
                });
            }

            return trip;
        }

        // An update request identical to the trip's current state.
        private static TripUpdateDto Unchanged(Trip trip) => new()
        {
            BusOperatorId = trip.BusOperatorId,
            BusRouteId = trip.BusRouteId,
            BusId = trip.BusId,
            DepartureTerminalId = trip.DepartureTerminalId,
            ArrivalTerminalId = trip.ArrivalTerminalId,
            DepartureTimeUtc = trip.DepartureTimeUtc,
            ArrivalTimeUtc = trip.ArrivalTimeUtc,
            Currency = trip.Currency,
            TripSeats = trip.TripSeats
                .Select(s => new TripSeatCreateDto { SeatId = s.SeatId, SeatNumber = s.SeatNumber, Fare = s.Fare })
                .ToList(),
        };

        // ---------------------------------------------------------------- snapshot

        [Theory]
        [InlineData(0, 0, 0, false)]
        [InlineData(1, 0, 0, true)]     // someone is mid-checkout
        [InlineData(0, 1, 0, true)]     // a paid seat
        [InlineData(0, 0, 1, true)]     // a live ticket
        [InlineData(2, 3, 4, true)]
        public void HasSales_IsTrueForAnyHeldBookedOrTicketedSeat(int held, int booked, int tickets, bool expected)
        {
            Assert.Equal(expected, new TripSalesSnapshot(held, booked, tickets).HasSales);
        }

        // ---------------------------------------------------------------- locked trip fields

        [Fact]
        public void AnUnchangedRequest_ChangesNothingLocked()
        {
            var trip = NewTrip(("A1", TripSeatStatus.Booked, 500m));

            Assert.Empty(TripEditGuard.FindLockedFieldChanges(trip, Unchanged(trip)));
            Assert.Empty(TripEditGuard.FindLockedSeatChanges(trip, Unchanged(trip).TripSeats));
        }

        [Fact]
        public void EveryFieldCustomersRelyOn_IsReportedWhenChanged()
        {
            var trip = NewTrip(("A1", TripSeatStatus.Booked, 500m));
            var dto = Unchanged(trip);

            dto.BusOperatorId = Guid.NewGuid();
            dto.BusRouteId = Guid.NewGuid();
            dto.BusId = Guid.NewGuid();
            dto.DepartureTerminalId = Guid.NewGuid();
            dto.ArrivalTerminalId = Guid.NewGuid();
            dto.DepartureTimeUtc = trip.DepartureTimeUtc.AddHours(1);
            dto.ArrivalTimeUtc = trip.ArrivalTimeUtc.AddHours(1);
            dto.Currency = "USD";

            var changed = TripEditGuard.FindLockedFieldChanges(trip, dto);

            Assert.Equal(
                new[]
                {
                    "busOperatorId", "busRouteId", "busId", "departureTerminalId", "arrivalTerminalId",
                    "departureTimeUtc", "arrivalTimeUtc", "currency",
                },
                changed);
        }

        [Fact]
        public void EditableFields_AreNeverReportedAsLocked()
        {
            var trip = NewTrip(("A1", TripSeatStatus.Booked, 500m));
            var dto = Unchanged(trip);

            dto.TripCode = "RENAMED-001";
            dto.BaseFare = 999m;
            dto.IsWheelchairAccessible = true;
            dto.Status = TripStatus.Delayed;
            dto.DelayReason = "Traffic";

            Assert.Empty(TripEditGuard.FindLockedFieldChanges(trip, dto));
        }

        [Fact]
        public void TimesWithinAMinute_AreTheSameTime_ButTwoMinutesIsAChange()
        {
            var trip = NewTrip(("A1", TripSeatStatus.Held, 500m));

            // An <input type="datetime-local"> holds minutes only, so a stored 10:00:30 comes back as 10:00.
            var rounded = Unchanged(trip);
            rounded.DepartureTimeUtc = trip.DepartureTimeUtc.AddSeconds(-30);
            rounded.ArrivalTimeUtc = trip.ArrivalTimeUtc.AddSeconds(45);
            Assert.Empty(TripEditGuard.FindLockedFieldChanges(trip, rounded));

            var moved = Unchanged(trip);
            moved.DepartureTimeUtc = trip.DepartureTimeUtc.AddMinutes(2);
            Assert.Equal(new[] { "departureTimeUtc" }, TripEditGuard.FindLockedFieldChanges(trip, moved));
        }

        [Fact]
        public void CurrencyCaseAndPadding_AreNotAChange()
        {
            var trip = NewTrip(("A1", TripSeatStatus.Held, 500m));
            var dto = Unchanged(trip);
            dto.Currency = " bdt ";

            Assert.Empty(TripEditGuard.FindLockedFieldChanges(trip, dto));
        }

        // ---------------------------------------------------------------- locked seats

        [Fact]
        public void HeldAndBookedSeats_CannotBeRepricedOrRemoved()
        {
            var trip = NewTrip(
                ("A1", TripSeatStatus.Held, 500m),
                ("A2", TripSeatStatus.Booked, 500m),
                ("A3", TripSeatStatus.Booked, 500m),
                ("A4", TripSeatStatus.Available, 500m));

            var dto = Unchanged(trip);
            dto.TripSeats.Single(s => s.SeatNumber == "A1").Fare = 550m;            // held: reprice
            dto.TripSeats.Remove(dto.TripSeats.Single(s => s.SeatNumber == "A2"));  // booked: remove
            // A3 untouched; A4 (free) repriced and not reported:
            dto.TripSeats.Single(s => s.SeatNumber == "A4").Fare = 1m;

            var changed = TripEditGuard.FindLockedSeatChanges(trip, dto.TripSeats);

            Assert.Equal(new[] { "A1 (fare)", "A2 (removed)" }, changed.OrderBy(c => c, StringComparer.Ordinal));
        }

        [Fact]
        public void FreeAndBlockedSeats_MayBeRepricedOrRemoved()
        {
            var trip = NewTrip(
                ("B1", TripSeatStatus.Available, 400m),
                ("B2", TripSeatStatus.Blocked, 400m),
                ("B3", TripSeatStatus.Available, 400m));

            var dto = Unchanged(trip);
            dto.TripSeats.Single(s => s.SeatNumber == "B1").Fare = 450m;
            dto.TripSeats.Single(s => s.SeatNumber == "B2").Fare = 450m;
            dto.TripSeats.Remove(dto.TripSeats.Single(s => s.SeatNumber == "B3"));

            Assert.Empty(TripEditGuard.FindLockedSeatChanges(trip, dto.TripSeats));
        }

        // ---------------------------------------------------------------- reconciliation plan

        [Fact]
        public void ThePlan_KeepsMatchingSeats_RemovesMissingOnes_AndAddsNewOnes()
        {
            var trip = NewTrip(
                ("A1", TripSeatStatus.Available, 100m),
                ("A2", TripSeatStatus.Blocked, 100m));
            var seatA1 = trip.TripSeats.Single(s => s.SeatNumber == "A1");
            var seatA2 = trip.TripSeats.Single(s => s.SeatNumber == "A2");

            var requested = new List<TripSeatCreateDto>
            {
                new() { SeatId = seatA2.SeatId, SeatNumber = "A2", Fare = 120m },   // stays
                new() { SeatId = Guid.NewGuid(), SeatNumber = "A3", Fare = 100m },  // new
            };

            var plan = TripEditGuard.PlanSeatChanges(trip, requested);

            Assert.Equal(new[] { seatA1 }, plan.ToRemove);
            Assert.Single(plan.ToKeep);
            Assert.Same(seatA2, plan.ToKeep[0].Existing);
            Assert.Equal(120m, plan.ToKeep[0].Requested.Fare);
            Assert.Equal(new[] { "A3" }, plan.ToAdd.Select(s => s.SeatNumber));
        }

        [Fact]
        public void ThePlan_ForAnUnchangedRequest_TouchesNothing()
        {
            var trip = NewTrip(("A1", TripSeatStatus.Held, 100m), ("A2", TripSeatStatus.Available, 100m));

            var plan = TripEditGuard.PlanSeatChanges(trip, Unchanged(trip).TripSeats);

            Assert.Empty(plan.ToRemove);
            Assert.Empty(plan.ToAdd);
            Assert.Equal(2, plan.ToKeep.Count);
        }
    }
}
