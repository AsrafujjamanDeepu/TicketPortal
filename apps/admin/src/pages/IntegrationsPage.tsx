import { ConsoleShortcuts } from '../components/ConsoleShortcuts';

// Backend: OperatorIntegrationsController, OperatorIntegrationEndpointsController,
// IntegrationSyncLogsController, IntegrationWebhookLogsController, External*MappingsController.
// Concept §3.2: API-connected operators sell only online through TicketPortal, which calls the
// operator's own API to sync and check booking status.
export function IntegrationsPage() {
  return (
    <ConsoleShortcuts
      title="Integration Monitoring"
      message="API-connected operators: connection settings, sync results and webhook events."
      links={[
        { label: 'Operator integrations', hint: 'Base URL, auth type, timeouts', resource: 'OperatorIntegrations' },
        { label: 'Integration endpoints', resource: 'OperatorIntegrationEndpoints' },
        { label: 'Sync logs', hint: 'Succeeded / Failed / Retrying', resource: 'IntegrationSyncLogs' },
        { label: 'Webhook logs', resource: 'IntegrationWebhookLogs' },
        { label: 'External trip mappings', resource: 'ExternalTripMappings' },
        { label: 'External booking mappings', resource: 'ExternalBookingMappings' },
        { label: 'External route mappings', resource: 'ExternalRouteMappings' },
        { label: 'External seat mappings', resource: 'ExternalSeatMappings' },
      ]}
    />
  );
}
