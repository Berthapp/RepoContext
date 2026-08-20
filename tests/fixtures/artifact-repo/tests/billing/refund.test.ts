import { refundPayment } from "../../src/billing/refund";

// Covers REQ-4711 / PAY-142.
test("refunds inside the window", () => {
  refundPayment("p1", new Date(0), new Date(86400000));
});
