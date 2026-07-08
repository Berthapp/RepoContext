export interface User {
  id: string;
  email: string;
  createdAt: string;
}

export type Result<T> = { ok: true; value: T } | { ok: false; error: string };
