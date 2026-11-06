"""Local PvZ hybrid remake AI-player interface (no OCR or desktop input)."""

from .client import PvzClient, BridgeError

__all__ = ["PvzClient", "BridgeError"]
