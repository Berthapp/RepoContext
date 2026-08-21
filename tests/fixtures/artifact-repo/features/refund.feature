Feature: Refunds

  Scenario: Refund inside the window
    Given a payment captured 5 days ago
    When a full refund is requested
    Then the refund succeeds

  Scenario: Refund outside the window
    Given a payment captured 40 days ago
    When a full refund is requested
    Then the refund is rejected with REFUND_WINDOW_EXPIRED
