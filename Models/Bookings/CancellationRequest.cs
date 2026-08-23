using TicketPortal.Api.Models.Common;
using TicketPortal.Api.Models.Enums;
using TicketPortal.Api.Models.Identity;
using TicketPortal.Api.Models.Payments;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace TicketPortal.Api.Models.Bookings
{
    // A customer (or staff, on their behalf) asking to cancel a booking or one ticket in it.
    // Kept as its own request/approval record — separate from just flipping the booking's
    // status straight to Cancelled — so there's a clear trail of who asked, who approved it,
    // and how much refund was agreed, before any Refund actually gets created/paid.
    public class CancellationRequest : AuditableEntity
    {
        public Guid BookingId { get; set; }
        public Guid? TicketId { get; set; } // Null if the whole booking is being cancelled, not just one ticket.
        public Guid? RequestedByUserId { get; set; }
        public Guid? ApprovedByUserId { get; set; }

        public CancellationRequestStatus Status { get; set; } = CancellationRequestStatus.Requested;

        [MaxLength(250)]
        public string Reason { get; set; } = string.Empty;

        [MaxLength(250)]
        public string? RejectedReason { get; set; }

        public decimal RequestedRefundAmount { get; set; }
        public decimal? ApprovedRefundAmount { get; set; } // May differ from requested, per the CancellationPolicy rules.
        public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? ApprovedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; } // When the refund actually finished.

        public Booking Booking { get; set; } = default!;
        public Ticket? Ticket { get; set; }
        public ApplicationUser? RequestedByUser { get; set; }
        public ApplicationUser? ApprovedByUser { get; set; }
        public ICollection<Refund> Refunds { get; set; } = new List<Refund>();
    }
}
