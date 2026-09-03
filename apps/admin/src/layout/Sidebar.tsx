import { NavLink } from 'react-router-dom';
import { useAuth } from '../lib/auth';
import { Button } from '../components/Button';

const NAV_ITEMS = [
  { to: '/', label: 'Analytics', end: true },
  { to: '/users', label: 'Users & Roles' },
  { to: '/settings', label: 'System Settings' },
  { to: '/audit-logs', label: 'Audit & Activity Logs' },
  { to: '/marketing', label: 'Marketing' },
  { to: '/integrations', label: 'Integration Monitoring' },
  { to: '/complaints', label: 'Complaints' },
];

export function Sidebar() {
  const { currentUser, logout } = useAuth();

  return (
    <aside className="sidebar">
      <div className="sidebar__brand">
        <span className="sidebar__logo">🚌</span>
        TicketPortal <span className="sidebar__badge">Admin</span>
      </div>

      <nav className="sidebar__nav">
        {NAV_ITEMS.map((item) => (
          <NavLink
            key={item.to}
            to={item.to}
            end={item.end}
            className={({ isActive }) => 'sidebar__link' + (isActive ? ' sidebar__link--active' : '')}
          >
            {item.label}
          </NavLink>
        ))}
      </nav>

      <div className="sidebar__footer">
        <span className="sidebar__user">{currentUser?.userName}</span>
        <Button variant="secondary" size="sm" onClick={logout}>
          Log out
        </Button>
      </div>
    </aside>
  );
}
