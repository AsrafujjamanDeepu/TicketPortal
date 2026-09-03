import { Card } from '../components/Card';

export function PagePlaceholder({ title, message }: { title: string; message: string }) {
  return (
    <Card>
      <div className="page-placeholder">
        <div className="page-placeholder__icon">🍌</div>
        <h3>{title}</h3>
        <p className="tp-muted">{message}</p>
      </div>
    </Card>
  );
}
