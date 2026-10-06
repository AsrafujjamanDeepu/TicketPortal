using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Tests.Infrastructure;
using Xunit;

namespace TicketPortal.Api.Tests.Integration
{
    // C7-6 QR contract. The displayed QR code encodes Ticket.QrCodePayload ("PNR|seat|GUID"); scanning
    // it must find the right ticket on both public verification and staff check-in, without ever
    // trusting the PNR/seat text inside it, and operator-scoped check-in rules must still apply.
    [Collection(SharedApiCollection.Name)]
    public class TicketQrScanTests
    {
        private readonly TicketPortalWebApplicationFactory _factory;

        public TicketQrScanTests(TicketPortalWebApplicationFactory factory) => _factory = factory;

        private sealed record Target(Guid Id, string TicketNumber, string Pnr, string Seat, string Payload, TicketStatus OriginalStatus, Guid OperatorId);

        // Picks a seeded ticket of the given status for the operator of `staffUserName` (or any
        // operator when null) and gives it a realistic server-style payload.
        private async Task<Target> ArrangeAsync(TicketStatus status, string? staffUserName)
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Guid? operatorId = null;
            if (staffUserName is not null)
                operatorId = await db.StaffProfiles.Where(sp => sp.User.UserName == staffUserName)
                    .Select(sp => sp.BusOperatorId).FirstOrDefaultAsync();

            var query = db.Tickets.Where(t => t.Status == TicketStatus.Issued);
            if (operatorId is not null)
                query = query.Where(t => db.Bookings.Any(b => b.Id == t.BookingId && b.BusOperatorId == operatorId));
            var ticket = await query.OrderBy(t => t.TicketNumber).Include(t => t.Booking).FirstOrDefaultAsync();
            Assert.True(ticket is not null, "No Issued ticket available in the seeded demo data.");

            var payload = $"{ticket!.Booking.Pnr}|{ticket.SeatNumberSnapshot}|{Guid.NewGuid():N}";
            ticket.QrCodePayload = payload;
            ticket.Status = status;
            await db.SaveChangesAsync();

            return new Target(ticket.Id, ticket.TicketNumber, ticket.Booking.Pnr, ticket.SeatNumberSnapshot,
                payload, TicketStatus.Issued, ticket.Booking.BusOperatorId);
        }

        private async Task RestoreAsync(Target target)
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Tickets.IgnoreQueryFilters().Where(t => t.Id == target.Id).ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, target.OriginalStatus)
                .SetProperty(t => t.CheckedInAtUtc, (DateTime?)null));
        }

        private static string Escape(string value) => Uri.EscapeDataString(value);

        [Fact]
        public async Task PublicVerify_ResolvesTheDisplayedQrPayload_ToTheSameTicketAsItsNumber()
        {
            var target = await ArrangeAsync(TicketStatus.Issued, staffUserName: null);
            try
            {
                var anonymous = _factory.CreateClient();
                var byQr = await anonymous.GetAsync($"/api/tickets/verify/{Escape(target.Payload)}");
                var byNumber = await anonymous.GetAsync($"/api/tickets/verify/{Escape(target.TicketNumber)}");

                Assert.Equal(HttpStatusCode.OK, byQr.StatusCode);
                Assert.Equal(HttpStatusCode.OK, byNumber.StatusCode);
                Assert.Contains(target.TicketNumber, await byQr.Content.ReadAsStringAsync());
            }
            finally { await RestoreAsync(target); }
        }

        [Fact]
        public async Task SeatAndPnrText_InsideAPayload_AreNeverTrustedAsProof()
        {
            var target = await ArrangeAsync(TicketStatus.Issued, staffUserName: null);
            try
            {
                var anonymous = _factory.CreateClient();

                // Right PNR + right seat, but a payload the server never issued -> not found.
                var forged = $"{target.Pnr}|{target.Seat}|{Guid.NewGuid():N}";
                Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/tickets/verify/{Escape(forged)}")).StatusCode);

                // A truncated copy of the real payload (PNR|seat only) is not a match either.
                var truncated = $"{target.Pnr}|{target.Seat}";
                Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/tickets/verify/{Escape(truncated)}")).StatusCode);

                // And a bare PNR is still not a ticket number (the separate verify-pnr lookup owns that).
                Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/tickets/verify/{Escape(target.Pnr)}")).StatusCode);
            }
            finally { await RestoreAsync(target); }
        }

        [Fact]
        public async Task ScanningAnIssuedTicketsQr_ChecksItIn_AndASecondScanIsIdempotent()
        {
            var target = await ArrangeAsync(TicketStatus.Issued, DemoAccounts.ShohaghSupervisor);
            try
            {
                var supervisor = await _factory.CreateAuthenticatedClientAsync(DemoAccounts.ShohaghSupervisor, DemoAccounts.Password);

                var first = await supervisor.PostAsync($"/api/tickets/{Escape(target.Payload)}/check-in", new StringContent(string.Empty));
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);

                var second = await supervisor.PostAsync($"/api/tickets/{Escape(target.Payload)}/check-in", new StringContent(string.Empty));
                Assert.Equal(HttpStatusCode.OK, second.StatusCode);
                using var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
                Assert.Contains("alreadycheckedin", body.RootElement.ToString().Replace("_", "").ToLowerInvariant());

                using var scope = _factory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(TicketStatus.CheckedIn, (await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == target.Id)).Status);
            }
            finally { await RestoreAsync(target); }
        }

        [Theory]
        [InlineData(TicketStatus.Cancelled)]
        [InlineData(TicketStatus.Refunded)]
        public async Task ScanningACancelledOrRefundedTicketsQr_IsRefused_AndLeavesItUntouched(TicketStatus status)
        {
            var target = await ArrangeAsync(status, DemoAccounts.ShohaghSupervisor);
            try
            {
                var supervisor = await _factory.CreateAuthenticatedClientAsync(DemoAccounts.ShohaghSupervisor, DemoAccounts.Password);

                var response = await supervisor.PostAsync($"/api/tickets/{Escape(target.Payload)}/check-in", new StringContent(string.Empty));
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

                using var scope = _factory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(status, (await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == target.Id)).Status);
            }
            finally { await RestoreAsync(target); }
        }

        [Fact]
        public async Task OperatorScopeStillApplies_AnotherOperatorsSupervisorCannotCheckInViaTheQr()
        {
            // Pick a ticket of an operator other than Shohagh's, then scan it as the Shohagh supervisor.
            Guid shohaghOperatorId;
            using (var scope = _factory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                shohaghOperatorId = await db.StaffProfiles.Where(sp => sp.User.UserName == DemoAccounts.ShohaghSupervisor)
                    .Select(sp => sp.BusOperatorId!.Value).FirstAsync();
            }

            var target = await ArrangeAsync(TicketStatus.Issued, DemoAccounts.GreenLineManager);
            try
            {
                Assert.NotEqual(shohaghOperatorId, target.OperatorId);
                var supervisor = await _factory.CreateAuthenticatedClientAsync(DemoAccounts.ShohaghSupervisor, DemoAccounts.Password);

                var response = await supervisor.PostAsync($"/api/tickets/{Escape(target.Payload)}/check-in", new StringContent(string.Empty));
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

                using var scope = _factory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(TicketStatus.Issued, (await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == target.Id)).Status);
            }
            finally { await RestoreAsync(target); }
        }
    }
}
