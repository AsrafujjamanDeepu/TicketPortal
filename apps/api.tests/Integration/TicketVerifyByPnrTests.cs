using System.Net;
using Microsoft.EntityFrameworkCore;
using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Tests.Infrastructure;
using Xunit;

namespace TicketPortal.Api.Tests.Integration
{
    // Regression tests for "Verify ticket by PNR says ticket not found": the verify page used to
    // send the PNR to GET /api/tickets/verify/{ticketNumber}, which only matches
    // Ticket.TicketNumber, so a real PNR could never be found. GET /api/tickets/verify-pnr/{pnr}
    // (TicketsController.VerifyByPnr) is the PNR-keyed lookup. Both are [AllowAnonymous].
    [Collection(SharedApiCollection.Name)]
    public class TicketVerifyByPnrTests
    {
        private readonly TicketPortalWebApplicationFactory _factory;

        public TicketVerifyByPnrTests(TicketPortalWebApplicationFactory factory)
        {
            _factory = factory;
        }

        // Resolved from the seeded DB (not hardcoded) so this survives DemoDataSeeder changes.
        private async Task<(string Pnr, string TicketNumber)> FindAnIssuedTicketAsync()
        {
            using var scope = _factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var found = await db.Tickets
                .AsNoTracking()
                .Where(t => t.Status == TicketStatus.Issued)
                .Select(t => new { t.Booking.Pnr, t.TicketNumber })
                .FirstOrDefaultAsync();

            Assert.True(found != null, "No Issued ticket in seeded demo data — DemoDataSeeder may have changed.");
            return (found!.Pnr, found.TicketNumber);
        }

        [Fact]
        public async Task VerifyByPnr_WithARealPnr_ReturnsItsTickets_WithoutAuthentication()
        {
            var (pnr, ticketNumber) = await FindAnIssuedTicketAsync();
            var anonymousClient = _factory.CreateClient();

            var response = await anonymousClient.GetAsync($"/api/tickets/verify-pnr/{pnr}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains(pnr, body);
            Assert.Contains(ticketNumber, body);
        }

        [Fact]
        public async Task VerifyByPnr_IsCaseInsensitive_AndTrimsWhitespace()
        {
            var (pnr, ticketNumber) = await FindAnIssuedTicketAsync();
            var anonymousClient = _factory.CreateClient();

            var response = await anonymousClient.GetAsync($"/api/tickets/verify-pnr/%20{pnr.ToLowerInvariant()}%20");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(ticketNumber, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task VerifyByPnr_WithAnUnknownPnr_IsNotFound()
        {
            var anonymousClient = _factory.CreateClient();

            var response = await anonymousClient.GetAsync("/api/tickets/verify-pnr/PNR00000000");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task VerifyByTicketNumber_StillWorks_AndAPnrIsStillNotATicketNumber()
        {
            var (pnr, ticketNumber) = await FindAnIssuedTicketAsync();
            var anonymousClient = _factory.CreateClient();

            var byTicketNumber = await anonymousClient.GetAsync($"/api/tickets/verify/{ticketNumber}");
            Assert.Equal(HttpStatusCode.OK, byTicketNumber.StatusCode);

            // The original bug, pinned: the ticket-number endpoint does not accept a PNR.
            var pnrAsTicketNumber = await anonymousClient.GetAsync($"/api/tickets/verify/{pnr}");
            Assert.Equal(HttpStatusCode.NotFound, pnrAsTicketNumber.StatusCode);
        }
    }
}
