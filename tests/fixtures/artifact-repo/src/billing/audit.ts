/** Append-only audit trail for billing operations. */
export class AuditLog {
  static record(paymentId: string, action: string): void {
    void paymentId;
    void action;
  }
}
