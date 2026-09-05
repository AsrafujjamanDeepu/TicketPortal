import { useId } from 'react';

interface LogoProps {
  size?: number;
  wordmark?: boolean;
  tone?: 'dark' | 'light';
}

/**
 * The TicketPortal brand mark — mirrors Angular's TpLogoComponent
 * (apps/frontend/src/app/shared/ui/logo/) exactly, so the badge looks
 * identical between the customer app and this admin dashboard. Replaces
 * the old placeholder emoji icon in the sidebar.
 */
export function Logo({ size = 32, wordmark = false, tone = 'dark' }: LogoProps) {
  const gradientId = `tp-logo-grad-${useId()}`;

  return (
    <span className={`logo${tone === 'light' ? ' logo--light' : ''}`}>
      <svg width={size} height={size} viewBox="0 0 40 40" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
        <defs>
          <linearGradient id={gradientId} x1="4" y1="2" x2="36" y2="38" gradientUnits="userSpaceOnUse">
            <stop offset="0" stopColor="#FFD25E" />
            <stop offset="0.55" stopColor="#FFA726" />
            <stop offset="1" stopColor="#FF7A1A" />
          </linearGradient>
        </defs>
        <rect width="40" height="40" rx="11" fill={`url(#${gradientId})`} />
        <path
          d="M10.5 27.5C10.5 18 15.5 13.5 20 13.5C24.5 13.5 24 19.5 18.5 20.7C14 21.7 14.5 26.3 19 27.2C23 28 26.5 25 27.5 19.5"
          stroke="white"
          strokeWidth="2.6"
          strokeLinecap="round"
          fill="none"
        />
        <circle cx="27.7" cy="14" r="2.9" fill="white" />
      </svg>
      {wordmark && <span className="logo__word">TicketPortal</span>}
    </span>
  );
}
