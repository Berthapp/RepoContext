import { loginUser, logoutUser, Credentials } from "../login";
import { getSession } from "../session";

describe("loginUser", () => {
  it("creates an active session for valid credentials", async () => {
    const creds: Credentials = { email: "user@example.com", password: "secret" };
    const session = await loginUser(creds);
    expect(session.active).toBe(true);
    expect(getSession(session.id)).toBeDefined();
  });

  it("deactivates the session on logout", async () => {
    const creds: Credentials = { email: "user@example.com", password: "secret" };
    const session = await loginUser(creds);
    logoutUser(session);
    expect(session.active).toBe(false);
  });
});
