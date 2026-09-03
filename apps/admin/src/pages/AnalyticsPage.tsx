import { PagePlaceholder } from '../components/PagePlaceholder';

// Home/landing page. Replace with: headline KPIs (bookings today, revenue,
// active operators, top routes), booking-trend chart, revenue by channel
// (online vs counter — see BookingResponseDto.saleChannel), operator
// performance leaderboard. Pull aggregate figures from Bookings/Payments/
// Trips via apiFetch — there's no dedicated analytics endpoint yet, so
// you're aggregating client-side or asking the backend team for one.
export function AnalyticsPage() {
  return (
    <PagePlaceholder
      title="Analytics Dashboard"
      message="Headline KPIs, booking trends, revenue by channel, and operator leaderboard go here."
    />
  );
}
