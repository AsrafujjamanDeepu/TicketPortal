import { Routes } from '@angular/router';
import { authGuard } from '../../core/guards/auth.guard';
import { roleGuard } from '../../core/guards/role.guard';

/**
 * Piece 4 — Operator & Fleet Management Panel. Every screen sits behind the OperatorShell (tab
 * nav + OperatorContextService resolution — see that component). Reachable by an operator's own
 * "Operator" account, or by platform "Staff"/"Admin" acting on an operator's behalf (matches
 * what the backend controllers in this piece actually allow — see e.g.
 * OperatorBranchesController's header comment on the three login-permission tiers).
 */
export const OPERATOR_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () => import('./shell/operator-shell.component').then((m) => m.OperatorShellComponent),
    canActivate: [authGuard, roleGuard],
    data: { roles: ['Operator', 'Staff', 'Admin'] },
    title: 'Operator Panel — TicketPortal',
    children: [
      { path: '', redirectTo: 'profile', pathMatch: 'full' },
      {
        path: 'profile',
        loadComponent: () =>
          import('./screens/profile-branches/profile-branches.component').then((m) => m.ProfileBranchesComponent),
        title: 'Operator Profile — TicketPortal',
      },
      {
        path: 'fleet',
        loadComponent: () => import('./screens/fleet/fleet.component').then((m) => m.FleetComponent),
        title: 'Fleet — TicketPortal',
      },
      {
        path: 'network',
        loadComponent: () =>
          import('./screens/network-setup/network-setup.component').then((m) => m.NetworkSetupComponent),
        title: 'Network Setup — TicketPortal',
      },
      {
        path: 'trips',
        loadComponent: () =>
          import('./screens/trips-scheduling/trips-scheduling.component').then((m) => m.TripsSchedulingComponent),
        title: 'Trips & Scheduling — TicketPortal',
      },
      {
        path: 'crew',
        loadComponent: () =>
          import('./screens/crew-assignment/crew-assignment.component').then((m) => m.CrewAssignmentComponent),
        title: 'Crew Assignment — TicketPortal',
      },
      {
        path: 'policies',
        loadComponent: () => import('./screens/fare-policy/fare-policy.component').then((m) => m.FarePolicyComponent),
        title: 'Fare & Cancellation Policies — TicketPortal',
      },
    ],
  },
];
