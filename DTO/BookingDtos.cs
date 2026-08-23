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
    public class BookingCreateDto
    {
        [Required]
        public Guid TripId { get; set; }

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

        // SubTotal + Tax + ServiceCharge - Discount = GrandTotal. The controller trusts these as
        // given rather than recomputing them — fine for an exam/demo, but a real checkout flow
        // would recompute GrandTotal server-side from the trip's seat fares instead of accepting
        // whatever the client sends.
        [Range(typeof(decimal), "0", "1000000")]
        public decimal SubTotal { get; set; }

        public decimal DiscountAmount { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal ServiceChargeAmount { get; set; }

        [Range(typeof(decimal), "0", "1000000")]
        public decimal GrandTotal { get; set; }

        // Bool-type demo field for this master-detail pair — true until the operator's own ERP
        // confirms the seat is really held for us (only meaningful for externally-managed
        // operators, but settable here so this pair exercises a bool field end-to-end).
        public bool RequiresExternalConfirmation { get; set; }

        // Date-type demo field for this master-detail pair — the payment deadline for this booking.
        public DateTime? ExpiresAtUtc { get; set; }

        // Details — every traveller this booking covers. At least one passenger is required;
        // a booking with zero passengers isn't a booking.
        [MinLength(1)]
        public List<BookingPassengerCreateDto> Passengers { get; set; } = new();
    }

    public class BookingUpdateDto : BookingCreateDto
    {
        public BookingStatus Status { get; set; } = BookingStatus.PendingPayment;
        public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    }

    public class BookingResponseDto
    {
        public Guid Id { get; set; }

        // Generated server-side (see BookingsController.GeneratePnr) — never accepted from the client.
        public string Pnr { get; set; } = string.Empty;
        public Guid TripId { get; set; }
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
        public decimal GrandTotal { get; set; }
        public string Currency { get; set; } = string.Empty;
        public List<BookingPassengerResponseDto> Passengers { get; set; } = new();
        public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    }
}
