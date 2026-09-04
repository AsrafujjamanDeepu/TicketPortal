import { LicenseType, StaffRole } from './enums';

// Mirrors DTO/PeopleDtos.cs -> StaffProfileResponseDto/CreateDto/UpdateDto.
// GET /api/staffprofiles is auto-scoped server-side (an operator's own Operator/Staff account
// only ever sees their own operator's staff) — this is also what Piece 4's OperatorContextService
// uses to resolve "which BusOperator does the logged-in user belong to" (see
// features/operator/services/operator-context.service.ts): find the entry whose userId matches
// the current session and read its busOperatorId.
export interface StaffProfile {
  id: string;
  userId: string;
  busOperatorId: string | null;
  employeeCode: string;
  role: StaffRole;
  nationalIdNumber: string | null;
  joiningDate: string | null; // yyyy-MM-dd (DateOnly)
  address: string | null;
  totalTripsCompleted: number;
  isActive: boolean;
  createdAtUtc: string;
  updatedAtUtc: string | null;
  rowVersion: string;
}

export interface StaffProfileCreateRequest {
  userId: string;
  busOperatorId?: string | null;
  employeeCode: string;
  role: StaffRole;
  nationalIdNumber?: string | null;
  joiningDate?: string | null;
  address?: string | null;
  totalTripsCompleted: number;
  isActive: boolean;
}

export interface StaffProfileUpdateRequest {
  employeeCode: string;
  role: StaffRole;
  nationalIdNumber?: string | null;
  joiningDate?: string | null;
  address?: string | null;
  totalTripsCompleted: number;
  isActive: boolean;
  rowVersion: string;
}

// Mirrors DTO/PeopleDtos.cs -> DriverLicenseResponseDto/CreateDto/UpdateDto. Piece 4's Fleet
// screen (driver license records) — scoped to StaffProfiles belonging to this operator.
export interface DriverLicense {
  id: string;
  staffProfileId: string;
  licenseNumber: string;
  type: LicenseType;
  issueDate: string; // yyyy-MM-dd (DateOnly)
  expiryDate: string;
  createdAtUtc: string;
  updatedAtUtc: string | null;
  rowVersion: string;
}

export interface DriverLicenseCreateRequest {
  staffProfileId: string;
  licenseNumber: string;
  type: LicenseType;
  issueDate: string;
  expiryDate: string;
}

export interface DriverLicenseUpdateRequest {
  licenseNumber: string;
  type: LicenseType;
  issueDate: string;
  expiryDate: string;
  rowVersion: string;
}
