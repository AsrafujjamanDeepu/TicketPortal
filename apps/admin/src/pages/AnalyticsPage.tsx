import { ConsoleShortcuts } from '../components/ConsoleShortcuts';

// The management console's dashboard aggregates live API data (bookings, payments, trips,
// operators) - nothing on it is hard-coded.
export function AnalyticsPage() {
  return (
    <ConsoleShortcuts
      title="Analytics Dashboard"
      message="Live platform figures and the money flow: online sales, counter sales, commission, settlements."
      links={[
        { label: 'Live dashboard', hint: 'Bookings, revenue and operator charts from the API', href: '/admin' },
        { label: 'Bookings', hint: 'Online vs counter (sale channel)', resource: 'Bookings' },
        { label: 'Payments', hint: 'Gateway and cash collection records', resource: 'Payments' },
        { label: 'Platform ledger', hint: 'Every platform money movement', resource: 'PlatformLedgers' },
        { label: 'Operator settlements', hint: 'Who owes whom, per period', resource: 'OperatorSettlements' },
        { label: 'Operator payouts', hint: 'Payments made to operators', resource: 'OperatorPayouts' },
      ]}
    />
  );
}
