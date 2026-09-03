import { PagePlaceholder } from '../components/PagePlaceholder';

// Backend: ActivityLogsController, AuditLogsController, NotificationLogsController.
export function AuditLogsPage() {
  return (
    <PagePlaceholder
      title="Audit & Activity Logs"
      message="A searchable/filterable log viewer (who did what, when) plus notification delivery logs go here."
    />
  );
}
