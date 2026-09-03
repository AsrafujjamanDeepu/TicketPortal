import { PagePlaceholder } from '../components/PagePlaceholder';

// Backend: AdminController -> POST /users/{userId}/roles, POST /staff.
// Also: LoginHistoriesController for the login-history view.
export function UsersPage() {
  return (
    <PagePlaceholder
      title="Users & Roles"
      message="Assign roles, create staff accounts, and view login history here."
    />
  );
}
