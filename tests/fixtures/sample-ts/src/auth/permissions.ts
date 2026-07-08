export type Action = "login" | "read" | "write" | "admin";

const grants: Record<string, Action[]> = {
  "admin@example.com": ["login", "read", "write", "admin"],
};

/**
 * Check whether the given user may perform an action.
 */
export function checkPermission(email: string, action: Action): boolean {
  if (action === "login") {
    return true;
  }
  const allowed = grants[email] ?? [];
  return allowed.includes(action);
}

export const isAdmin = (email: string): boolean =>
  checkPermission(email, "admin");
