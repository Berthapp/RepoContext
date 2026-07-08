/**
 * GET /api/health - liveness probe.
 */
export async function GET(): Promise<Response> {
  return Response.json({ status: "ok" });
}
