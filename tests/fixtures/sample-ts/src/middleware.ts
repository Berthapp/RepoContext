import { getSession, Session } from "./auth/session";

/**
 * Next.js style middleware: reject requests without an active session.
 */
export function middleware(request: { headers: Map<string, string> }): Response {
  const sessionId = request.headers.get("x-session-id") ?? "";
  const session: Session | undefined = getSession(sessionId);
  if (!session || !session.active) {
    return new Response("Unauthorized", { status: 401 });
  }
  return new Response("OK", { status: 200 });
}

export const config = {
  matcher: ["/app/:path*"],
};
