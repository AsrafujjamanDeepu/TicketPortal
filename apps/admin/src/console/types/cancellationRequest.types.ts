// cancellationRequest.types.ts
// Mirrors CancellationRequestResponseDto (CancellationRequestsController).
// No raw PUT/DELETE by design — a request only ever moves through
// Approve/Reject/Complete (CancellationProcessingService).

export interface CancellationRequestResponseDto {
  id: string;
  bookingId: string;
  ticketId?: string | null;
  requestedByUserId?: string | null;
  approvedByUserId?: string | null;
  status: string; // Requested | Approved | Rejected | Completed
  reason: string;
  rejectedReason?: string | null;
  requestedRefundAmount: number;
  approvedRefundAmount?: number | null;
  requestedAtUtc: string;
  approvedAtUtc?: string | null;
  completedAtUtc?: string | null;
  createdAtUtc: string;
  updatedAtUtc?: string | null;
  rowVersion: string;
}

// Best-effort — Booking/User DTO shapes weren't given, so a few plausible
// field names are tried with fallbacks (same defensive pattern used for
// Bus/Trip resolution in seatHoldService).
export interface BookingSummary {
  id: string;
  bookingCode?: string;
  bookingNumber?: string;
  tripId?: string;
  totalAmount?: number;
}

export interface UserSummary {
  id: string;
  fullName?: string;
  name?: string;
  email?: string;
}

export interface CancellationRequestDisplayDto extends CancellationRequestResponseDto {
  bookingLabel: string;
  requestedByLabel: string;
  approvedByLabel: string;
}
