import { createSession, Session } from "./session";
import { checkPermission } from "./permissions";
import { hashPassword } from "../lib/crypto";

export interface Credentials {
  email: string;
  password: string;
}

/**
 * Authenticate a user with email + password and start a session.
 */
export async function loginUser(credentials: Credentials): Promise<Session> {
  const hashed = hashPassword(credentials.password);
  if (!checkPermission(credentials.email, "login")) {
    throw new Error("login not permitted");
  }
  return createSession(credentials.email, hashed);
}

export function logoutUser(session: Session): void {
  session.active = false;
}
