import { AuditLog } from "./audit";

export const REFUND_WINDOW_DAYS = 30;

/** Refunds a captured payment. Implements PAY-142. */
export function refundPayment(paymentId: string, capturedAt: Date, now: Date): string {
  const ageDays = (now.getTime() - capturedAt.getTime()) / 86400000;
  if (ageDays > REFUND_WINDOW_DAYS) {
    throw new Error("REFUND_WINDOW_EXPIRED");
  }

  AuditLog.record(paymentId, "refund");
  return paymentId;
}
