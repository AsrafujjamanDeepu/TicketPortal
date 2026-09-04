import { CancellationRequestStatus } from './enums';

// Mirrors DTO/BookingsExtraDtos.cs -> CancellationRequestCreateDto. POST /api/cancellationrequests.
// Deliberately carries no Status/refund-amount field — CancellationProcessingService prices the
// refund itself from the trip's real CancellationPolicy, never from anything the client sends.
export interface CancellationRequestCreateRequest {
  bookingId: string;
  ticketId?: string; // Omit to cancel the whole booking; set to cancel just one ticket in it.
  reason: string;
}

// Mirrors DTO/BookingsExtraDtos.cs -> CancellationRequestResponseDto.
export interface CancellationRequest {
  id: string;
  bookingId: string;
  ticketId: string | null;
  requestedByUserId: string | null;
  approvedByUserId: string | null;
  status: CancellationRequestStatus;
  reason: string;
  rejectedReason: string | null;
  requestedRefundAmount: number;
  approvedRefundAmount: number | null;
  requestedAtUtc: string;
  approvedAtUtc: string | null;
  completedAtUtc: string | null;
  createdAtUtc: string;
  updatedAtUtc: string | null;
  rowVersion: string;
}
