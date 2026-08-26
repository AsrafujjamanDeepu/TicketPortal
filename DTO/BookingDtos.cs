using TicketPortal.Api.Models.Enums;
using System.ComponentModel.DataAnnotations;

namespace TicketPortal.Api.DTO
{
    // Details — ONE traveller on a booking. A booking can carry several passengers (a family
    // buying 4 seats in one purchase), each with their own identity and, optionally, ID photo.
    public class BookingPassengerCreateDto
    {
        [Required, MaxLength(120)]
        public string FullName { get; set; } = string.Empty;

        [MaxLength(30)]
        public string? Phone { get; set; }

        [MaxLength(120)]
        public string? Email { get; set; }

        public Gender Gender { get; set; } = Gender.Unknown;

        // Drives child/student fare rules elsewhere in the system.
        public PassengerType PassengerType { get; set; } = PassengerType.Adult;
        public int? Age { get; set; }

        [MaxLength(30)]
        public string? NationalIdNumber { get; set; }
    }

    public class BookingPassengerResponseDto
    {
        public Guid Id { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public Gender Gender { get; set; }
        public PassengerType PassengerType { get; set; }
        public int? Age { get; set; }
        public string? NationalIdNumber { get; set; }

        // Set via POST /api/bookings/{bookingId}/passengers/{passengerId}/images — the one image
        // field in this whole exercise that lives on a DETAIL row instead of the master.
        public string? NationalIdPhotoUrl { get; set; }
    }

    // Master = Booking, one purchase / one PNR. Deliberately has NO BusOperatorId field —
    // BookingsController resolves it server-side from the Trip, so a caller can never forge
    // which operator gets credited for a sale.
    //
    // Also deliberately has NO price fields (SubTotal/DiscountAmount/TaxAmount/
    // ServiceChargeAmount/GrandTotal) and no ExpiresAtUtc — all of those are computed
    // server-side from the SeatHold this booking is created from (see HoldToken below), exactly
    // the same way BusOperatorId is resolved from the Trip. A booking's price is not something
    // a client gets to declare: PaymentConfirmationService.InitiatePaymentAsync charges exactly
    // booking.GrandTotal, so a client-writable GrandTotal used to mean a customer could book
    // real seats and pay whatever amount they liked.
    public class BookingCreateDto
    {
        [Required]
        public Guid TripId { get; set; }

        // The token returned by POST /api/seatholds for the seats this booking covers. This,
        // not anything in the rest of this DTO, is the single source of truth for which seats
        // — and at what frozen price (SeatHoldItem.FareAtHold) — this booking is for.
        [Required, MaxLength(100)]
        public string HoldToken { get; set; } = string.Empty;

        [Required]
        public Guid BoardingTerminalId { get; set; }

        [Required]
        public Guid DroppingTerminalId { get; set; }

        [Required, MaxLength(120)]
        public string ContactName { get; set; } = string.Empty;

        [Required, MaxLength(30)]
        public string ContactPhone { get; set; } = string.Empty;

        [MaxLength(120)]
        public string? ContactEmail { get; set; }

        // Details — every traveller this booking covers. Must be exactly one passenger per
        // seat in the hold — BookingsController.Create rejects a mismatch outright, since
        // PaymentConfirmationService later pairs passengers to booked seats 1-for-1.
        [MinLength(1)]
        public List<BookingPassengerCreateDto> Passengers { get; set; } = new();
    }

    // Deliberately NOT a BookingCreateDto subclass any more — that used to accidentally imply
    // Status and pricing were editable through this endpoint too. This only covers what a
    // customer (or staff, on their behalf) can legitimately change about a not-yet-paid
    // booking: trip-detail fields, never price or status. Status changes go through
    // Booking.Confirm()/Cancel() — called from PaymentConfirmationService and the future
    // cancellation flow — never a raw field edit here.
    public class BookingUpdateDto
    {
        [Required]
        public Guid BoardingTerminalId { get; set; }

        [Required]
        public Guid DroppingTerminalId { get; set; }

        [Required, MaxLength(120)]
        public string ContactName { get; set; } = string.Empty;

        [Required, MaxLength(30)]
        public string ContactPhone { get; set; } = string.Empty;

        [MaxLength(120)]
        public string? ContactEmail { get; set; }

        // Editing passenger DETAILS is fine; changing how MANY passengers there are is not —
        // that count is fixed at Create time by the number of seats in the hold. See
        // BookingsController.Update for the check that enforces this.
        [MinLength(1)]
        public List<BookingPassengerCreateDto> Passengers { get; set; } = new();

        public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    }

    public class BookingResponseDto
    {
        public Guid Id { get; set; }

        // Generated server-side (see BookingsController.GeneratePnr) — never accepted from the client.
        public string Pnr { get; set; } = string.Empty;
        public Guid TripId { get; set; }
        public Guid? SeatHoldId { get; set; }
        public Guid BoardingTerminalId { get; set; }
        public Guid DroppingTerminalId { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAtUtc { get; set; }
        public DateTime? DeletedAtUtc { get; set; }

        public string ContactName { get; set; } = string.Empty;
        public string ContactPhone { get; set; } = string.Empty;
        public string? ContactEmail { get; set; }
        public BookingStatus Status { get; set; }
        public bool RequiresExternalConfirmation { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }

        // Full breakdown, not just GrandTotal — now that these are real server-computed values
        // (see BookingCreateDto) rather than client-declared ones, they're worth showing on a
        // receipt/confirmation screen instead of collapsing straight to the total.
        public decimal SubTotal { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal ServiceChargeAmount { get; set; }
        public decimal GrandTotal { get; set; }
        public string Currency { get; set; } = string.Empty;
        public List<BookingPassengerResponseDto> Passengers { get; set; } = new();
        public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    }
}
