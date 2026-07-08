import { getSession } from "../../../src/auth/session";
import { checkPermission } from "../../../src/auth/permissions";

interface UserDto {
  email: string;
}

/**
 * GET /api/users - list users (requires read permission).
 */
export async function GET(request: Request): Promise<Response> {
  const sessionId = request.headers.get("x-session-id") ?? "";
  const session = getSession(sessionId);
  if (!session || !checkPermission(session.email, "read")) {
    return new Response("Forbidden", { status: 403 });
  }
  const users: UserDto[] = [{ email: session.email }];
  return Response.json(users);
}

export async function POST(request: Request): Promise<Response> {
  const body = (await request.json()) as UserDto;
  return Response.json(body, { status: 201 });
}
