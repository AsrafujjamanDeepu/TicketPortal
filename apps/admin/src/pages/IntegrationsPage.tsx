import { PagePlaceholder } from '../components/PagePlaceholder';

// Backend: OperatorIntegrationsController, OperatorIntegrationEndpointsController,
// IntegrationSyncLogsController, IntegrationWebhookLogsController.
// This is a health/status board for ExternalApiManaged operators, not a CRUD form.
export function IntegrationsPage() {
  return (
    <PagePlaceholder
      title="Integration Monitoring"
      message="Sync log status (Succeeded/Failed/Retrying) and webhook event logs for API-connected operators go here."
    />
  );
}
