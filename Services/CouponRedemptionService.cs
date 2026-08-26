using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Marketing;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Services
{
    // This is the ONLY place in the codebase allowed to create a CouponUsage or bump
    // Coupon.UsedCount. Before this existed, CouponUsagesController let any authenticated
    // client POST a CouponUsage with any CouponId/BookingId/DiscountApplied it liked —
    // Coupon.IsActive, the valid-date window, UsageLimit and PerUserLimit were never
    // actually checked, so a client could apply any coupon to any booking for any discount.
    public class CouponRedemptionService
    {
        private readonly AppDbContext _db;

        public CouponRedemptionService(AppDbContext db)
        {
            _db = db;
        }

        // Validates every rule the Coupon itself carries, then records one CouponUsage row
        // and increments Coupon.UsedCount to match — the two always move together so
        // UsedCount can never drift from the real number of CouponUsage rows.
        public async Task<CouponUsage> RedeemAsync(Guid couponId, Guid bookingId, Guid customerProfileId)
        {
            var coupon = await _db.Coupons.FirstOrDefaultAsync(c => c.Id == couponId)
                ?? throw new InvalidOperationException($"Coupon {couponId} does not exist.");

            var booking = await _db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId)
                ?? throw new InvalidOperationException($"Booking {bookingId} does not exist.");

            if (!coupon.IsActive)
            {
                throw new InvalidOperationException($"Coupon '{coupon.Code}' is not active.");
            }

            var now = DateTime.UtcNow;
            if (now < coupon.ValidFromUtc || now > coupon.ValidToUtc)
            {
                throw new InvalidOperationException(
                    $"Coupon '{coupon.Code}' is only valid from {coupon.ValidFromUtc:yyyy-MM-dd} " +
                    $"to {coupon.ValidToUtc:yyyy-MM-dd}.");
            }

            if (coupon.UsageLimit.HasValue)
            {
                var totalUses = await _db.CouponUsages.CountAsync(u => u.CouponId == couponId);
                if (totalUses >= coupon.UsageLimit.Value)
                {
                    throw new InvalidOperationException(
                        $"Coupon '{coupon.Code}' has reached its usage limit ({coupon.UsageLimit.Value}).");
                }
            }

            if (coupon.PerUserLimit.HasValue)
            {
                var customerUses = await _db.CouponUsages.CountAsync(
                    u => u.CouponId == couponId && u.CustomerProfileId == customerProfileId);
                if (customerUses >= coupon.PerUserLimit.Value)
                {
                    throw new InvalidOperationException(
                        $"You've already used coupon '{coupon.Code}' the maximum " +
                        $"({coupon.PerUserLimit.Value}) number of times allowed.");
                }
            }

            if (coupon.MinBookingAmount.HasValue && booking.GrandTotal < coupon.MinBookingAmount.Value)
            {
                throw new InvalidOperationException(
                    $"Coupon '{coupon.Code}' requires a minimum booking amount of " +
                    $"{coupon.MinBookingAmount.Value:0.##} {booking.Currency}.");
            }

            if (booking.CouponId.HasValue)
            {
                throw new InvalidOperationException($"Booking {bookingId} already has a coupon applied.");
            }

            var discount = coupon.Type switch
            {
                CouponType.FixedAmount => coupon.DiscountAmount ?? 0m,
                CouponType.Percentage => booking.GrandTotal * ((coupon.DiscountPercentage ?? 0m) / 100m),
                _ => 0m
            };

            if (coupon.Type == CouponType.Percentage && coupon.MaxDiscountAmount.HasValue)
            {
                discount = Math.Min(discount, coupon.MaxDiscountAmount.Value);
            }

            // A discount can never exceed what the booking is actually worth — guards against
            // a large FixedAmount coupon pushing the effective total negative.
            discount = Math.Round(Math.Min(discount, booking.GrandTotal), 2);

            var usage = new CouponUsage
            {
                CouponId = couponId,
                BookingId = bookingId,
                CustomerProfileId = customerProfileId,
                DiscountApplied = discount,
            };

            _db.CouponUsages.Add(usage);
            coupon.UsedCount += 1;
            coupon.UpdatedAtUtc = DateTime.UtcNow;

            // Links the booking back to the coupon that was used on it. Recomputing the
            // booking's own SubTotal/DiscountAmount/GrandTotal from this is a separate,
            // pre-existing gap (see BookingCreateDto's own comment on those fields being
            // trusted from the client) — not something this fix takes on.
            booking.CouponId = couponId;

            await _db.SaveChangesAsync();

            return usage;
        }
    }
}
