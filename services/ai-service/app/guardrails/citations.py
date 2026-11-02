"""Keeps citations honest (Doc 02 §4.4): only [n] markers that match a retrieved source survive."""

import re

_GROUP = re.compile(r"\[(\d+(?:\s*,\s*\d+)*)\]")
_MAX_BUFFER = 24


class MarkerFilter:
    """Filters a streamed answer. Text passes at once, except a possible marker, which waits until it is complete.

    `[1]` stays; `[7]` (no such source) is dropped; `[1, 2]` becomes `[1][2]`; a bracket that is not a marker, such as
    `[note]`, is kept. `used` lists the sources cited, in order of first use.
    """

    def __init__(self, valid: set[int]) -> None:
        self._valid = valid
        self._held = ""
        self._prev = ""  # the last character let through, to tell `a[0]` (an index) from ` [1]` (a citation)
        self.used: list[int] = []

    def feed(self, piece: str) -> str:
        out: list[str] = []
        for ch in piece:
            if self._held:
                self._held += ch
                if ch == "]":
                    out.append(self._resolve())
                elif not (ch.isdigit() or ch in ", ") or len(self._held) > _MAX_BUFFER:
                    out.append(self._release())  # not a marker after all
            elif ch == "[" and not (self._prev.isalnum() or self._prev == "_"):
                self._held = ch
            else:
                out.append(ch)
                self._prev = ch
        return "".join(out)

    def flush(self) -> str:
        """Whatever was held back when the answer ended (an unfinished bracket is plain text)."""
        return self._release()

    def _release(self) -> str:
        held, self._held = self._held, ""
        if held:
            self._prev = held[-1]
        return held

    def _resolve(self) -> str:
        held, self._held = self._held, ""
        if not _GROUP.fullmatch(held):
            self._prev = held[-1]
            return held
        kept: list[int] = []
        for n in (int(x) for x in re.findall(r"\d+", held)):
            if n in self._valid:
                kept.append(n)
                if n not in self.used:
                    self.used.append(n)
        result = "".join(f"[{n}]" for n in kept)
        self._prev = "]" if result else self._prev
        return result
