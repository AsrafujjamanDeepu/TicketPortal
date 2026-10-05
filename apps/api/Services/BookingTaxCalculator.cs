using TicketPortal.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace TicketPortal.Api.Services
{
    /// <summary>
    /// Calculates the project's configured demo tax rules. This intentionally does not encode
    /// statutory tax classes or rates; the current TaxRule schema is global and cannot identify
    /// which operator/service class a rule applies to.
    /// </summary>
    public static class BookingTaxCalculator
    {
        public static async Task<decimal> CalculateAsync(AppDbContext db, decimal discountedFareBase)
        {
            if (discountedFareBase <= 0m) return 0m;

            var percentages = await db.TaxRules
                .Where(rule => rule.IsActive)
                .Select(rule => rule.Percentage)
                .ToListAsync();

            return Math.Round(discountedFareBase * percentages.Sum() / 100m, 2, MidpointRounding.AwayFromZero);
        }
    }
}
