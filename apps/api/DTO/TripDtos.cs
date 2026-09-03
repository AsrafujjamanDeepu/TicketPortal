using TicketPortal.Api.Models.Enums;
using System.ComponentModel.DataAnnotations;

namespace TicketPortal.Api.DTO
{
    // What the caller sends to add ONE seat to a trip's seat map. Note this does NOT create a
    // new physical seat — SeatId must point at a Seat row that already exists on the Bus assigned
    // to this trip (see Task 1 / BusesController). A trip only prices and tracks availability for
    // seats that physically exist; it never invents them.
    //
    // Deliberately has NO Status field: TripSeat.Status (Available/Held/Booked/Blocked) is only
    // ever allowed to change through SeatHoldService — see the comment on that class. Letting a
    // client set it directly here (or on Update) would let someone silently mark a seat Booked
    // without ever going through the hold-and-pay flow.
    public class TripSeatCreateDto
    {
        [Required]
        public Guid SeatId { get; set; }

        // Duplicated from the physical Seat on purpose — it's what search results and tickets
        // actually display, and copying it here avoids an extra join every time a trip is read.
        [Required, MaxLength(10)]
        public string SeatNumber { get; set; } = string.Empty;

        public SeatType SeatType { get; set; } = SeatType.Regular;

        // Per-trip price, NOT the seat's fixed extra fare. The same physical seat can be priced
        // differently on different trips (peak vs off-peak, promo trips, etc.).
        [Range(typeof(decimal), "0", "100000")]
        public decimal Fare { get; set; }
    }

    public class TripSeatResponseDto
    {
        public Guid Id { get; set; }
        public Guid SeatId { get; set; }
        public string SeatNumber { get; set; } = string.Empty;
        public SeatType SeatType { get; set; }
        public decimal Fare { get; set; }

        // Available / Held / Booked / Blocked — this is the field SeatHoldService's conditional
        // UPDATE statements race to change safely when two customers click the same seat.
        public TripSeatStatus Status { get; set; }
    }

    // Master = Trip. Every Guid field below is a required foreign key — a trip only makes sense
    // in the context of an operator, a unified route, a physical bus, and two terminals, so none
    // of these can be optional the way CancellationPolicy.BusOperatorId is.
    public class TripCreateDto
    {
        [Required]
        public Guid BusOperatorId { get; set; }

        [Required]
        public Guid BusRouteId { get; set; }

        [Required]
        public Guid BusId { get; set; }

        [Required]
        public Guid DepartureTerminalId { get; set; }

        [Required]
        public Guid ArrivalTerminalId { get; set; }

        // Human-facing code shown on tickets/search results, e.g. "DHK-CTG-0630" — not the
        // database Id, which the customer never sees.
        [Required, MaxLength(40)]
        public string TripCode { get; set; } = string.Empty;

        [Required]
        public DateTime DepartureTimeUtc { get; set; }

        [Required]
        public DateTime ArrivalTimeUtc { get; set; }

        // Fallback/reference price for the trip as a whole; the real per-seat price that gets
        // charged is TripSeatCreateDto.Fare below, which can vary seat-to-seat.
        [Range(typeof(decimal), "0", "100000")]
        public decimal BaseFare { get; set; }

        [MaxLength(3)]
        public string Currency { get; set; } = "BDT";

        // Bool-type demo field for this master-detail pair — whether this trip's bus is
        // running with wheelchair-accessible boarding for this journey.
        public bool IsWheelchairAccessible { get; set; }

        // Details — at least one seat is required, otherwise there's nothing to sell on this trip.
        [MinLength(1)]
        public List<TripSeatCreateDto> TripSeats { get; set; } = new();
    }

    // Reuses every field from Create and adds the two fields that only make sense once a trip
    // already exists and is running: its live status, and why it's delayed (if it is).
    public class TripUpdateDto : TripCreateDto
    {
        public TripStatus Status { get; set; } = TripStatus.Scheduled;

        [MaxLength(250)]
        public string? DelayReason { get; set; }
        [Required]
        public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    }

    public class TripResponseDto
    {
        public Guid Id { get; set; }

        // The client needs these to know which operator/route/bus/terminals a search result or
        // booking actually belongs to — a Trip is meaningless in isolation.
        public Guid BusOperatorId { get; set; }
        public Guid BusRouteId { get; set; }
        public Guid BusId { get; set; }

        public Guid DepartureTerminalId { get; set; }
        public Guid ArrivalTerminalId { get; set; }

        public string TripCode { get; set; } = string.Empty;
        public DateTime DepartureTimeUtc { get; set; }
        public DateTime ArrivalTimeUtc { get; set; }
        public decimal BaseFare { get; set; }
        public string Currency { get; set; } = string.Empty;
        public TripStatus Status { get; set; }
        public string? DelayReason { get; set; }
        public bool IsWheelchairAccessible { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public DateTime? DeletedAtUtc { get; set; }

        // Set via POST /api/trips/{id}/images — a single field, unlike Bus's gallery-style
        // BusImage collection, because a trip only ever needs one representative cover photo.
        public string? CoverImageUrl { get; set; }
        public List<TripSeatResponseDto> TripSeats { get; set; } = new();
        public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    }

    // The result shape for GET /api/trips/search — "client will search route like 'Dhaka to
    // Chittagong' and will see available buses and their seats" (business plan section 4/5).
    // Deliberately flatter/friendlier than TripResponseDto: a search results list needs
    // human-readable operator/bus/terminal names right away, not just their Guids, and needs
    // live seat availability as a simple count rather than the full per-seat TripSeats array
    // (the seat map itself is fetched separately, via GET /api/trips/{id}, once the customer
    // picks one specific trip to book).
    public class TripSearchResultDto
    {
        public Guid TripId { get; set; }
        public string TripCode { get; set; } = string.Empty;

        public Guid BusOperatorId { get; set; }
        public string BusOperatorName { get; set; } = string.Empty;
        public string? BusOperatorLogoUrl { get; set; }

        public Guid BusId { get; set; }
        public string? BusBrand { get; set; }
        public string? BusModel { get; set; }
        public BusType BusType { get; set; }
        public bool HasWifi { get; set; }
        public bool HasToilet { get; set; }

        public Guid DepartureTerminalId { get; set; }
        public string DepartureTerminalName { get; set; } = string.Empty;
        public Guid ArrivalTerminalId { get; set; }
        public string ArrivalTerminalName { get; set; } = string.Empty;

        public DateTime DepartureTimeUtc { get; set; }
        public DateTime ArrivalTimeUtc { get; set; }

        public TripStatus Status { get; set; }
        public bool IsWheelchairAccessible { get; set; }
        public string Currency { get; set; } = string.Empty;

        // Read straight from TripSeat.Status, per the completion plan's spec for this endpoint —
        // never a cached/denormalized count, so it can never drift from what SeatHoldService and
        // PaymentConfirmationService are actually doing to these same rows.
        public int TotalSeatCount { get; set; }
        public int AvailableSeatCount { get; set; }

        // Cheapest seat a customer could actually buy right now on this trip; null if the trip
        // is fully sold out. Distinct from BaseFare, which is just a reference/fallback price —
        // this is the real floor of what TripSeat.Fare currently charges for an Available seat.
        public decimal? LowestAvailableFare { get; set; }

        public string? CoverImageUrl { get; set; }
    }
}
