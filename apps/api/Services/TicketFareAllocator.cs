using TicketPortal.Api.Models.Bookings;
using TicketPortal.Api.Models.Scheduling;

namespace TicketPortal.Api.Services
{
    /// <summary>
    /// Allocates the immutable booking price over its seats. Every component uses largest
    /// remainder rounding, so discounts, tax, service charge, and final fares each sum exactly
    /// to their booking-level amounts, including when a booking has several seats.
    /// </summary>
    public static class TicketFareAllocator
    {
        public sealed record Allocation(decimal DiscountAmount, decimal TaxAmount, decimal ServiceChargeAmount, decimal FinalFare);

        public static IReadOnlyDictionary<Guid, Allocation> Allocate(Booking booking, IReadOnlyCollection<TripSeat> seats)
        {
            if (seats.Count == 0)
            {
                if (booking.GrandTotal != 0m)
                    throw new InvalidOperationException("A booking with a non-zero total must have at least one seat.");
                return new Dictionary<Guid, Allocation>();
            }

            var orderedSeats = seats.OrderBy(seat => seat.Id).ToList();
            var fareTotal = orderedSeats.Sum(seat => seat.Fare);
            if (fareTotal != booking.SubTotal)
            {
                throw new InvalidOperationException(
                    $"Booking {booking.Id} subtotal ({booking.SubTotal}) does not match held seat fares ({fareTotal}).");
            }

            var grossWeights = orderedSeats.Select(seat => seat.Fare).ToList();
            var discounts = AllocateCents(booking.DiscountAmount, grossWeights);
            var netWeights = orderedSeats.Select((seat, index) => Math.Max(0m, seat.Fare - discounts[index])).ToList();
            if (netWeights.Sum() == 0m) netWeights = grossWeights;

            var taxes = AllocateCents(booking.TaxAmount, netWeights);
            var serviceCharges = AllocateCents(booking.ServiceChargeAmount, netWeights);
            var output = new Dictionary<Guid, Allocation>();
            for (var i = 0; i < orderedSeats.Count; i++)
            {
                var finalFare = orderedSeats[i].Fare - discounts[i] + taxes[i] + serviceCharges[i];
                output.Add(orderedSeats[i].Id,
                    new Allocation(discounts[i], taxes[i], serviceCharges[i], finalFare));
            }

            if (output.Values.Sum(value => value.FinalFare) != booking.GrandTotal)
            {
                throw new InvalidOperationException(
                    $"Ticket allocations for booking {booking.Id} do not add up to its charged total.");
            }

            return output;
        }

        private static List<decimal> AllocateCents(decimal amount, IReadOnlyList<decimal> weights)
        {
            if (amount < 0m) throw new InvalidOperationException("A booking price component cannot be negative.");
            var weightTotal = weights.Sum();
            if (weightTotal <= 0m)
            {
                if (amount != 0m) throw new InvalidOperationException("A price component cannot be allocated without a positive fare base.");
                return Enumerable.Repeat(0m, weights.Count).ToList();
            }

            var cents = decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
            var exactShares = weights.Select(weight => cents * weight / weightTotal).ToList();
            var allocatedCents = exactShares.Select(decimal.Floor).Select(decimal.ToInt64).ToArray();
            var centsLeft = cents - allocatedCents.Sum();

            foreach (var index in Enumerable.Range(0, weights.Count)
                         .OrderByDescending(index => exactShares[index] - allocatedCents[index])
                         .ThenBy(index => index)
                         .Take(checked((int)centsLeft)))
            {
                allocatedCents[index]++;
            }

            return allocatedCents.Select(value => value / 100m).ToList();
        }
    }
}
