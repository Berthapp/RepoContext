/**
 * Deterministic, dependency-free helpers used across the sample app.
 * (Not real cryptography - fixture code only.)
 */

let counter = 0;

export function randomId(): string {
  counter += 1;
  return `id-${counter}`;
}

export function hashPassword(password: string): string {
  let hash = 0;
  for (let i = 0; i < password.length; i += 1) {
    hash = (hash * 31 + password.charCodeAt(i)) | 0;
  }
  return `h${hash}`;
}
