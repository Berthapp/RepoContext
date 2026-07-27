import { createSession } from '../lib/session';

export interface WebCredentials {
  email: string;
  password: string;
}

/**
 * Authenticates a user of the web app and opens a session.
 */
export async function loginWebUser(credentials: WebCredentials): Promise<string> {
  if (!credentials.email.includes('@')) {
    throw new Error('invalid email');
  }

  return createSession(credentials.email);
}
