import { AppRole } from './role.model';

// Mirrors DTO/AuthDtos.cs -> RegisterDto exactly (field names and casing
// matter — ASP.NET model binding is case-insensitive for JSON but keep it
// consistent anyway).
export interface RegisterRequest {
  fullName: string;
  userName: string;
  email: string;
  password: string;
}

// Mirrors DTO/AuthDtos.cs -> LoginDto.
export interface LoginRequest {
  userName: string;
  password: string;
}

// Mirrors DTO/AuthDtos.cs -> AuthResponseDto. `expiresAtUtc` comes back as
// an ISO string over JSON — convert to a Date only where you actually need
// to do date math (AuthService does this once, on login).
export interface AuthResponse {
  token: string;
  expiresAtUtc: string;
  userId: string;
  userName: string;
  roles: AppRole[];
}

// What AuthService exposes to the rest of the app — decoded/derived from
// the stored token, not a raw backend DTO.
export interface CurrentUser {
  userId: string;
  userName: string;
  roles: AppRole[];
  expiresAtUtc: string;
}
