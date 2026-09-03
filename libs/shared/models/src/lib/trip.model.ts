import { BusType, SeatType, TripSeatStatus, TripStatus } from './enums';

// Mirrors DTO/TripDtos.cs -> TripSeatResponseDto.
export interface TripSeat {
  id: string;
  seatId: string;
  seatNumber: string;
  seatType: SeatType;
  fare: number;
  status: TripSeatStatus;
}

// Mirrors DTO/TripDtos.cs -> TripResponseDto. Used for the trip-details screen
// (GET /api/trips/{id}) — has the full seat map, unlike the lighter search result.
export interface Trip {
  id: string;
  busOperatorId: string;
  busRouteId: string;
  busId: string;
  departureTerminalId: string;
  arrivalTerminalId: string;
  tripCode: string;
  departureTimeUtc: string;
  arrivalTimeUtc: string;
  baseFare: number;
  currency: string;
  status: TripStatus;
  delayReason: string | null;
  isWheelchairAccessible: boolean;
  coverImageUrl: string | null;
  tripSeats: TripSeat[];
  rowVersion: string;
}

// Mirrors DTO/TripDtos.cs -> TripSearchResultDto. This is what
// GET /api/trips/search returns — one row per bus/operator on the route,
// already carrying the operator name/logo and seat-count summary so the
// results list doesn't need N follow-up calls.
export interface TripSearchResult {
  tripId: string;
  tripCode: string;
  busOperatorId: string;
  busOperatorName: string;
  busOperatorLogoUrl: string | null;
  busId: string;
  busBrand: string | null;
  busModel: string | null;
  busType: BusType;
  hasWifi: boolean;
  hasToilet: boolean;
  departureTerminalId: string;
  departureTerminalName: string;
  arrivalTerminalId: string;
  arrivalTerminalName: string;
  departureTimeUtc: string;
  arrivalTimeUtc: string;
  status: TripStatus;
  isWheelchairAccessible: boolean;
  currency: string;
  totalSeatCount: number;
  availableSeatCount: number;
  lowestAvailableFare: number | null;
  coverImageUrl: string | null;
}

// Query params for GET /api/trips/search — build this into an HttpParams in
// the search feature module, don't hand-roll query strings.
export interface TripSearchQuery {
  fromTerminalId: string;
  toTerminalId: string;
  date: string; // yyyy-MM-dd — backend binds this to a DateOnly
  minAvailableSeats?: number;
}
