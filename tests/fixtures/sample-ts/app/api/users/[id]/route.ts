import { getSession } from "../../../../src/auth/session";

/**
 * GET /api/users/[id] - fetch a single user.
 */
export async function GET(
  request: Request,
  context: { params: { id: string } },
): Promise<Response> {
  const session = getSession(request.headers.get("x-session-id") ?? "");
  if (!session) {
    return new Response("Forbidden", { status: 403 });
  }
  return Response.json({ id: context.params.id, email: session.email });
}
