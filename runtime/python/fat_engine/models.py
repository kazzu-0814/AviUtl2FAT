from __future__ import annotations

from dataclasses import dataclass, asdict


@dataclass(frozen=True)
class Caption:
    id: str
    start_time: float
    end_time: float
    original_transcript: str
    text: str
    provider: str = "passthrough"
    model: str | None = None
    confidence: float | None = None
    detected_language: str | None = None
    output_language: str | None = None

    def validate(self) -> "Caption":
        if not self.id or self.start_time < 0 or self.end_time < self.start_time:
            raise ValueError("invalid caption")
        return self

    def to_dict(self) -> dict[str, object]:
        return asdict(self)
