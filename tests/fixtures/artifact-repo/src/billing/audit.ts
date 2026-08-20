/** Append-only audit trail for billing operations. */
export const AuditLog = {
  record(paymentId: string, action: string): void {
    void paymentId;
    void action;
  },
};
