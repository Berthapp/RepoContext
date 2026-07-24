export interface Session {
  userId: string;
  expiresAt: number;
}

/**
 * Creates a session for a user with a fixed lifetime.
 */
export function createSession(userId: string, ttlSeconds: number): Session {
  return { userId, expiresAt: Date.now() + ttlSeconds * 1000 };
}

/**
 * Whether a session is still valid at the given instant.
 */
export function isSessionValid(session: Session, now: number): boolean {
  return session.expiresAt > now;
}

/**
 * Revokes a session by expiring it immediately.
 */
export function revokeSession(session: Session): Session {
  return { ...session, expiresAt: 0 };
}
