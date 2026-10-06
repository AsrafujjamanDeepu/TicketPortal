using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Bookings;
using TicketPortal.Api.Models.Enums;
using Xunit;

namespace TicketPortal.Api.Tests.Unit
{
    // C7-1 / C7-6: pins the pieces the database-level rule depends on, without needing SQL Server.
    // (The behaviour itself is proven against a real database in
    // Integration/ActiveTicketUniquenessTests.cs.)
    public class TicketIntegrityModelTests
    {
        private static Microsoft.EntityFrameworkCore.Metadata.IModel BuildModel()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer("Server=(localdb)\\unused;Database=unused;Trusted_Connection=True;")
                .Options;
            using var db = new AppDbContext(options);
            return db.GetService<IDesignTimeModel>().Model; // the full model, relational annotations included
        }

        [Fact]
        public void TheFilterNumbersInTheIndex_AreTheRealCancelledAndRefundedValues()
        {
            // The index filter is raw SQL written against these numbers. If TicketStatus is ever
            // renumbered, this fails - and a new migration must change the filter.
            Assert.Equal(5, (int)TicketStatus.Cancelled);
            Assert.Equal(6, (int)TicketStatus.Refunded);
        }

        [Fact]
        public void TripSeatIdIndex_IsUnique_AndExcludesCancelledRefundedAndSoftDeletedRows()
        {
            var ticket = BuildModel().FindEntityType(typeof(Ticket))!;
            var index = ticket.GetIndexes().Single(i =>
                i.Properties.Count == 1 && i.Properties[0].Name == nameof(Ticket.TripSeatId));

            Assert.True(index.IsUnique);
            Assert.NotNull(index.GetFilter());
            Assert.Contains("[Status] NOT IN (5, 6)", index.GetFilter());
            Assert.Contains("[IsDeleted] = 0", index.GetFilter());
        }

        [Fact]
        public void QrPayloadAndTicketNumber_AreIndexedForScanLookups()
        {
            var ticket = BuildModel().FindEntityType(typeof(Ticket))!;
            Assert.Contains(ticket.GetIndexes(), i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(Ticket.QrCodePayload));
            Assert.Contains(ticket.GetIndexes(), i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(Ticket.TicketNumber));
        }
    }
}
