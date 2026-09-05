import { Card } from '../components/Card';

export function PagePlaceholder({ title, message }: { title: string; message: string }) {
  return (
    <Card>
      <div className="page-placeholder">
        <div className="page-placeholder__icon tp-icon-badge">
          <svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
            <rect x="4" y="4" width="16" height="16" rx="3" stroke="currentColor" strokeWidth="2" />
            <path d="M9 4V20M4 9H20" stroke="currentColor" strokeWidth="2" />
          </svg>
        </div>
        <h3>{title}</h3>
        <p className="tp-muted">{message}</p>
      </div>
    </Card>
  );
}
