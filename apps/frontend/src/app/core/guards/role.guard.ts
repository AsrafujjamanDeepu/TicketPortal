import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../services/auth.service';
import { AppRole } from '@ticketportal-mono/models';

/**
 * Restricts a route to one or more roles. Set `data: { roles: ['Operator'] }`
 * on the route and add BOTH guards:
 *
 *   {
 *     path: 'operator',
 *     canActivate: [authGuard, roleGuard],
 *     data: { roles: ['Operator'] },
 *     loadChildren: () => import('./features/operator/operator.routes')...
 *   }
 *
 * Remember: the backend only issues 'Admin' | 'Staff' | 'Operator' |
 * 'Customer' (see role.model.ts) — Counter (Piece 5) and Finance (Piece 6)
 * both check for 'Staff', not a role of their own.
 */
export const roleGuard: CanActivateFn = (route) => {
  const authService = inject(AuthService);
  const router = inject(Router);

  const requiredRoles = (route.data['roles'] as AppRole[] | undefined) ?? [];
  if (requiredRoles.length === 0 || authService.hasRole(...requiredRoles)) {
    return true;
  }

  return router.createUrlTree(['/not-authorized']);
};
