"""Pricing rules for PAY-142."""


class PriceCalculator:
    """Applies the refund window rule."""

    def refund_allowed(self, age_days: int) -> bool:
        return age_days <= 30


def load_rules(path: str) -> dict:
    return {}
