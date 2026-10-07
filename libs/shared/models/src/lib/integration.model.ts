import type { OperatorInventoryMode } from './enums';

export type IntegrationAuthType = 'None' | 'ApiKey' | 'BearerToken' | 'Basic' | 'OAuth2';
export type IntegrationSyncStatus = 'Pending' | 'Succeeded' | 'Failed' | 'Skipped' | 'Retrying';

// Mirrors OperatorIntegrationStatusDto (apps/api/DTO/IntegrationsDtos.cs) — the redacted view
// Permissions.IntegrationsRead unlocks (Admin, or an operator manager for their OWN operator —
// see OperatorIntegrationsController.GetStatus). Deliberately has no BaseUrl, no auth details,
// nothing secret-shaped: RBAC Amendment v3 §8 never lets that reach a non-Admin view.
export interface OperatorIntegrationStatus {
  busOperatorId: string;
  busOperatorName: string;
  inventoryMode: OperatorInventoryMode;
  hasIntegrationConfigured: boolean;
  integrationName: string | null;
  isActive: boolean;
  lastSuccessfulSyncAtUtc: string | null;
  lastSyncStatus: IntegrationSyncStatus | null;
  lastSyncAtUtc: string | null;
  recentFailureCount: number;
  // Chunk 6 / C6-4. What customers get on this operator's trips when its system can't confirm seat
  // availability: 'Closed' = the seat can't be held (503, try again shortly); 'Open' = holds go
  // ahead on our own seat map. needsAttention is only ever true for ExternalApiManaged operators.
  availabilityFailureMode: AvailabilityFailureMode;
  hasAvailabilityEndpoint: boolean;
  needsAttention: boolean;
  attentionReason: string | null;
}

export type AvailabilityFailureMode = 'Closed' | 'Open';

// Mirrors IntegrationPolicyDto — OperatorIntegrationsController.GetPolicy (Permissions.IntegrationsRead).
export interface IntegrationPolicy {
  availabilityFailureMode: AvailabilityFailureMode;
  availabilityCacheSeconds: number;
  allowLocalDestinations: boolean;
}

// Mirrors OperatorIntegrationResponseDto — Permissions.IntegrationsManage (Admin) only. There is
// deliberately no `secretReference` field here: the raw value is never returned by any endpoint,
// only `hasSecret` + a masked preview safe to display (see the C# DTO's own comment).
export interface OperatorIntegration {
  id: string;
  busOperatorId: string;
  name: string;
  baseUrl: string;
  authType: IntegrationAuthType;
  apiKeyHeaderName: string | null;
  hasSecret: boolean;
  secretReferenceMasked: string | null;
  // Chunk 6 / C6-3: non-null when the stored reference is not the accepted 'env:NAME' form (a row
  // saved before the rule existed) — the integration is not called until it is fixed.
  secretReferenceProblem: string | null;
  // True when the referenced value is present in server configuration (never the value itself).
  secretConfigured: boolean;
  timeoutSeconds: number;
  isActive: boolean;
  lastSuccessfulSyncAtUtc: string | null;
  createdAtUtc: string;
  updatedAtUtc: string | null;
  rowVersion: string;
}

// Mirrors IntegrationSyncLogResponseDto (IntegrationSyncLogsController) — one attempt to call an
// operator's own ERP: ConfirmBooking, GetSeatAvailability, CancelBooking, or TestConnection (see
// docs/docs/03-Remaining-Fix-Plan.md).
export interface IntegrationSyncLog {
  id: string;
  operatorIntegrationId: string;
  entityName: string;
  entityKey: string | null;
  operation: string;
  status: IntegrationSyncStatus;
  startedAtUtc: string;
  completedAtUtc: string | null;
  requestJson: string | null;
  responseJson: string | null;
  errorMessage: string | null;
  createdAtUtc: string;
  updatedAtUtc: string | null;
}

// Mirrors ExternalBookingSyncService.TestConnectionResult — the "Test connection" button's result.
export interface TestConnectionResult {
  success: boolean;
  message: string;
  statusCode: number | null;
  durationMs: number;
}
