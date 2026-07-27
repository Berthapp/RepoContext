const sessions = new Map<string, number>();

/** Opens a session for the given subject and returns its token. */
export function createSession(subject: string): string {
  const token = `sess_${subject}_${sessions.size}`;
  sessions.set(token, sessions.size);
  return token;
}

/** Whether a session token is still known. */
export function isSessionValid(token: string): boolean {
  return sessions.has(token);
}
