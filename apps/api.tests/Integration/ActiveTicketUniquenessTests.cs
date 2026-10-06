using Microsoft.EntityFrameworkCore;
using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Bookings;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Tests.Infrastructure;
using Xunit;

namespace TicketPortal.Api.Tests.Integration
{
    // C7-1: SQL Server itself rejects a second ACTIVE ticket for one trip seat, while cancelled /
    // refunded / soft-deleted history is allowed to pile up. Also the concurrency check the plan asks
    // for: several simultaneous issues for one free seat -> exactly one succeeds.
    //
    // The tests borrow one seeded Issued ticket (and its seat) and put it back afterwards; the
    // collection runs tests one at a time, so nothing else touches it meanwhile.
    [Collection(SharedApiCollection.Name)]
    public class ActiveTicketUniquenessTests
    {
        private readonly TicketPortalWebApplicationFactory _factory;

        public ActiveTicketUniquenessTests(TicketPortalWebApplicationFactory factory) => _factory = factory;

        private static bool IsActiveSeatViolation(DbUpdateException ex) =>
            ex.GetBaseException().Message.Contains("IX_Tickets_TripSeatId", StringComparison.OrdinalIgnoreCase);

        private async Task<Guid> PickAnIssuedTicketIdAsync()
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var id = await db.Tickets.AsNoTracking().Where(t => t.Status == TicketStatus.Issued)
                .OrderByDescending(t => t.TicketNumber).Select(t => t.Id).FirstOrDefaultAsync();
            Assert.True(id != Guid.Empty, "No Issued ticket in the seeded demo data - DemoDataSeeder may have changed.");
            return id;
        }

        // A copy of `source` as another ticket row on the SAME seat (every required column copied).
        private static Ticket Clone(AppDbContext db, Ticket source, TicketStatus status, bool softDeleted = false)
        {
            var copy = new Ticket();
            db.Entry(copy).CurrentValues.SetValues(source);
            copy.Id = Guid.NewGuid();
            copy.TicketNumber = "C7T" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            copy.QrCodePayload = $"C7|{copy.TicketNumber}|{Guid.NewGuid():N}";
            copy.Status = status;
            copy.IsDeleted = softDeleted;
            return copy;
        }

        private async Task CleanUpAsync(Guid sourceId, TicketStatus originalStatus, params string[] cloneNumberPrefixes)
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Tickets.IgnoreQueryFilters().Where(t => t.TicketNumber.StartsWith("C7T")).ExecuteDeleteAsync();
            await db.Tickets.IgnoreQueryFilters().Where(t => t.Id == sourceId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, originalStatus));
        }

        [Fact]
        public async Task ASecondActiveTicketForTheSameSeat_IsRejectedByTheDatabase()
        {
            var sourceId = await PickAnIssuedTicketIdAsync();
            try
            {
                foreach (var duplicateStatus in new[] { TicketStatus.Issued, TicketStatus.CheckedIn, TicketStatus.Used, TicketStatus.NoShow, TicketStatus.PendingPayment })
                {
                    using var scope = _factory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var source = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == sourceId);

                    db.Tickets.Add(Clone(db, source, duplicateStatus));
                    var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                    Assert.True(IsActiveSeatViolation(ex), $"{duplicateStatus}: expected IX_Tickets_TripSeatId, got: {ex.GetBaseException().Message}");
                }
            }
            finally { await CleanUpAsync(sourceId, TicketStatus.Issued); }
        }

        [Fact]
        public async Task CancelledRefundedAndSoftDeletedHistory_NeverBlocksTheSeat()
        {
            var sourceId = await PickAnIssuedTicketIdAsync();
            try
            {
                using var scope = _factory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var source = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == sourceId);

                // Many history rows may coexist with the one live ticket on this seat...
                db.Tickets.AddRange(
                    Clone(db, source, TicketStatus.Cancelled),
                    Clone(db, source, TicketStatus.Cancelled),
                    Clone(db, source, TicketStatus.Refunded),
                    Clone(db, source, TicketStatus.Refunded),
                    Clone(db, source, TicketStatus.Issued, softDeleted: true));
                await db.SaveChangesAsync();

                // ...and once the live ticket is cancelled the seat can be sold again.
                await db.Tickets.Where(t => t.Id == sourceId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TicketStatus.Cancelled));
                db.Tickets.Add(Clone(db, source, TicketStatus.Issued));
                await db.SaveChangesAsync();

                // The re-sold seat is protected again.
                db.Tickets.Add(Clone(db, source, TicketStatus.Issued));
                var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                Assert.True(IsActiveSeatViolation(ex));
            }
            finally { await CleanUpAsync(sourceId, TicketStatus.Issued); }
        }

        [Fact]
        public async Task EightSimultaneousIssuesForOneFreeSeat_ExactlyOneWins()
        {
            var sourceId = await PickAnIssuedTicketIdAsync();
            try
            {
                // Free the seat first, so the race is between the new rows only.
                using (var setup = _factory.CreateScope())
                {
                    var setupDb = setup.ServiceProvider.GetRequiredService<AppDbContext>();
                    await setupDb.Tickets.Where(t => t.Id == sourceId)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TicketStatus.Cancelled));
                }

                Ticket template;
                using (var read = _factory.CreateScope())
                {
                    var readDb = read.ServiceProvider.GetRequiredService<AppDbContext>();
                    template = await readDb.Tickets.AsNoTracking().SingleAsync(t => t.Id == sourceId);
                }

                using var gate = new ManualResetEventSlim(false);
                var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
                {
                    using var scope = _factory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    db.Tickets.Add(Clone(db, template, TicketStatus.Issued));
                    gate.Wait();
                    try { await db.SaveChangesAsync(); return true; }
                    catch (DbUpdateException ex) when (IsActiveSeatViolation(ex)) { return false; }
                })).ToArray();

                await Task.Delay(300); // let all eight reach the gate
                gate.Set();
                var outcomes = await Task.WhenAll(attempts);

                Assert.Equal(1, outcomes.Count(won => won));
                Assert.Equal(7, outcomes.Count(won => !won));
            }
            finally { await CleanUpAsync(sourceId, TicketStatus.Issued); }
        }
    }
}
