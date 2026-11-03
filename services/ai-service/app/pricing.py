"""What calls cost (Doc 02 §4.6). Prices are configuration, never guessed: the operator sets them."""

import json
from dataclasses import dataclass


class PriceTableError(ValueError):
    """The price table is missing, not valid JSON, or does not cover a model in use."""


@dataclass(frozen=True)
class Price:
    """US dollars per million tokens."""

    input: float
    output: float = 0.0


def parse_price_table(raw: str | None) -> dict[str, Price]:
    """`{"model-id": {"input": 1.0, "output": 5.0}}`. An embedding model only needs `input`."""
    if not raw or not raw.strip():
        return {}
    try:
        data = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise PriceTableError(f"PRICE_TABLE is not valid JSON: {exc.msg}") from exc
    if not isinstance(data, dict):
        raise PriceTableError("PRICE_TABLE must be a JSON object keyed by model id")
    table: dict[str, Price] = {}
    for model, entry in data.items():
        if not isinstance(entry, dict) or "input" not in entry:
            raise PriceTableError(f"PRICE_TABLE['{model}'] needs an 'input' price")
        try:
            price = Price(float(entry["input"]), float(entry.get("output", 0.0)))
        except (TypeError, ValueError) as exc:
            raise PriceTableError(f"PRICE_TABLE['{model}'] prices must be numbers") from exc
        if price.input < 0 or price.output < 0:
            raise PriceTableError(f"PRICE_TABLE['{model}'] prices cannot be negative")
        table[str(model)] = price
    return table


class CostCalculator:
    """Estimated cost of a call. A model with no entry (every local model) costs nothing."""

    def __init__(self, table: dict[str, Price] | None = None) -> None:
        self._table = table or {}

    def has(self, model: str) -> bool:
        return model in self._table

    def cost(self, model: str, tokens_in: int, tokens_out: int = 0) -> float:
        p = self._table.get(model)
        if p is None:
            return 0.0
        return round((tokens_in * p.input + tokens_out * p.output) / 1_000_000, 8)  # the column keeps 8 places
