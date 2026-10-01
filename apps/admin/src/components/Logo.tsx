import { useId } from 'react';

interface LogoProps {
  size?: number;
  wordmark?: boolean;
  tone?: 'dark' | 'light';
}

/**
 * The TicketPortal brand mark — mirrors Angular's TpLogoComponent
 * (apps/frontend/src/app/shared/ui/logo/) exactly: a real ticket shape
 * (rounded stub, two punched side-notches, a dashed tear-line) on the brand
 * gradient.
 *
 * Gradient stops read --tp-brand-1/2/3 from libs/shared/design-tokens/tokens.css
 * (loaded by main.tsx), exactly like the Angular logo does, so this mark, the
 * customer app's mark and both apps' favicon.svg stay one colour. The hex values
 * are only fallbacks, and they match those tokens.
 */
export function Logo({ size = 32, wordmark = false, tone = 'dark' }: LogoProps) {
  const gradientId = `tp-logo-grad-${useId()}`;

  return (
    <span className={`logo${tone === 'light' ? ' logo--light' : ''}`}>
      <svg width={size} height={size} viewBox="0 0 40 40" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
        <defs>
          <linearGradient id={gradientId} x1="4" y1="2" x2="36" y2="38" gradientUnits="userSpaceOnUse">
            <stop offset="0" style={{ stopColor: 'var(--tp-brand-1, #e07a5f)' }} />
            <stop offset="0.5" style={{ stopColor: 'var(--tp-brand-2, #d97706)' }} />
            <stop offset="1" style={{ stopColor: 'var(--tp-brand-3, #1e251c)' }} />
          </linearGradient>
        </defs>
        <rect width="40" height="40" rx="11" fill={`url(#${gradientId})`} />
        <rect x="7" y="13" width="26" height="14" rx="3.2" stroke="white" strokeWidth="2" fill="none" />
        <circle cx="7" cy="20" r="2.8" fill={`url(#${gradientId})`} />
        <circle cx="33" cy="20" r="2.8" fill={`url(#${gradientId})`} />
        <line x1="21.5" y1="15.2" x2="21.5" y2="24.8" stroke="white" strokeWidth="2" strokeDasharray="2.2 2.4" strokeLinecap="round" />
      </svg>
      {wordmark && <span className="logo__word">TicketPortal</span>}
    </span>
  );
}
