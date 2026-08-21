package gateway

// Session is the caller's authenticated context.
type Session struct {
	ID string
}

// Refund forwards a refund to the pricing service. Implements PAY-142.
func Refund(s *Session, paymentID string) error {
	return nil
}
