import { createSession, Session } from "./session";

export interface Credentials {
  email: string;
  password: string;
}

/**
 * Validates credentials and starts a session on success.
 */
export function loginUser(credentials: Credentials): Session | null {
  if (!validateCredentials(credentials)) {
    return null;
  }

  return createSession(credentials.email, 3600);
}

/**
 * Rejects obviously malformed credentials before hitting the store.
 */
export function validateCredentials(credentials: Credentials): boolean {
  return credentials.email.includes("@") && credentials.password.length >= 8;
}
