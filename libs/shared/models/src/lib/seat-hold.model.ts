import { SeatHoldStatus } from './enums';

// Mirrors DTO/BookingsExtraDtos.cs -> SeatHoldCreateDto. POST /api/seatholds.
export interface SeatHoldCreateRequest {
  tripId: string;
  tripSeatIds: string[];
}

// Mirrors DTO/BookingsExtraDtos.cs -> SeatHoldResponseDto.
// `secondsRemaining` is computed server-side — always trust this over doing
// your own clock math against holdExpiresAtUtc, since it already accounts
// for server/client clock skew. Piece 2's countdown timer should tick this
// value down locally but re-sync from the server on any refetch.
export interface SeatHold {
  id: string;
  tripId: string;
  heldByUserId: string | null;
  holdToken: string;
  holdStartedAtUtc: string;
  holdExpiresAtUtc: string;
  status: SeatHoldStatus;
  secondsRemaining: number;
  clientIpAddress: string | null;
  userAgent: string | null;
  createdAtUtc: string;
  updatedAtUtc: string | null;
  rowVersion: string;
}
