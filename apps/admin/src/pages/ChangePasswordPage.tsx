import { FormEvent, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Button } from '../components/Button';
import { Card } from '../components/Card';
import { useAuth } from '../lib/auth';
import { apiFetch } from '../lib/apiClient';
import type { ApiError } from '@ticketportal-mono/models';

export function ChangePasswordPage() {
  const auth = useAuth();
  const navigate = useNavigate();
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      await apiFetch<void>('account/change-password', {
        method: 'POST',
        body: { currentPassword, newPassword },
      });
      auth.logout();
      navigate('/login?passwordChanged=1', { replace: true });
    } catch (cause) {
      setError((cause as ApiError).message ?? 'Could not change the password.');
      setSubmitting(false);
    }
  }

  return (
    <div className="auth-page">
      <div className="auth-page__logo">TicketPortal</div>
      <Card className="auth-card">
        <h2>Change your password</h2>
        <p>This administrator account must use a new password before continuing.</p>
        <form onSubmit={submit}>
          <label>
            Current password
            <input
              type="password"
              value={currentPassword}
              onChange={(event) => setCurrentPassword(event.target.value)}
              autoComplete="current-password"
              required
            />
          </label>
          <label>
            New password
            <input
              type="password"
              value={newPassword}
              onChange={(event) => setNewPassword(event.target.value)}
              autoComplete="new-password"
              minLength={6}
              required
            />
          </label>
          {error && <p className="error">{error}</p>}
          <Button type="submit" disabled={submitting} style={{ width: '100%' }}>
            {submitting ? 'Updating…' : 'Change password'}
          </Button>
        </form>
      </Card>
    </div>
  );
}
