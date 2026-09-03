import { OperatorInventoryMode } from './enums';

// Mirrors DTO/BusOperatorDtos.cs -> OperatorRouteResponseDto (kept minimal —
// expand as Piece 4 needs more fields; this is the shape Piece 1 already
// verified against the backend).
export interface OperatorRoute {
  id: string;
  busOperatorId: string;
}

// Mirrors DTO/BusOperatorDtos.cs -> BusOperatorResponseDto.
// `inventoryMode` matters everywhere: Piece 4's write screens should treat
// ExternalApiManaged operators as read-only for seat/trip-inventory actions
// — their own ERP is the source of truth, not TicketPortal.
export interface BusOperator {
  id: string;
  name: string;
  legalName: string | null;
  registrationNumber: string | null;
  addressLine: string;
  contactPhone: string;
  email: string | null;
  logoUrl: string | null;
  city: string;
  district: string;
  country: string;
  foundedYear: number | null;
  registeredOnUtc: string | null;
  inventoryMode: OperatorInventoryMode;
  isActive: boolean;
  operatorRoutes: OperatorRoute[];
  createdAtUtc: string;
  updatedAtUtc: string | null;
  deletedAtUtc: string | null;
}
