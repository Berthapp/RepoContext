import { randomId } from "../lib/crypto";

export interface Session {
  id: string;
  email: string;
  token: string;
  active: boolean;
}

export type SessionStore = Map<string, Session>;

const store: SessionStore = new Map();

/**
 * Create and persist a new session for the given user.
 */
export function createSession(email: string, token: string): Session {
  const session: Session = { id: randomId(), email, token, active: true };
  store.set(session.id, session);
  return session;
}

export function getSession(id: string): Session | undefined {
  return store.get(id);
}
