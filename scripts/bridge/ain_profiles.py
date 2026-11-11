"""Source identities and CODE relocations for the supported AIN v12 builds."""
from dataclasses import dataclass
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
VERSIONS = json.loads((ROOT / "src/R10.Core/Memory/GameVersions.json").read_text(encoding="utf-8"))
VERSION_BY_SOURCE = {version["sourceSha256"]: version for version in VERSIONS}

SECTION_BOUNDS = [
    ("VERS", 0, 8), ("CODE", 8, 0xDE5CFA),
    ("FUNC", 0xDE5CFA, 0x1395C28), ("GLOB", 0x1395C28, 0x139B996),
    ("STRT", 0x139B996, 0x13E5A65), ("MSG1", 0x13E5A65, 0x1AE6582),
    ("MAIN", 0x1AE6582, 0x1AE658A), ("HLL0", 0x1AE658A, 0x1AFA1BD),
    ("SWI0", 0x1AFA1BD, 0x1B02B15), ("GVER", 0x1B02B15, 0x1B02B1D),
    ("STR0", 0x1B02B1D, 0x1B419BD), ("DELG", 0x1B419BD, 0x1B62B4B),
    ("OBJG", 0x1B62B4B, 0x1B62BCA), ("ENUM", 0x1B62BCA, 0x1B6369F),
]


def shifted_sections(**length_deltas):
    shift = 0
    bounds = []
    for name, start, end in SECTION_BOUNDS:
        relocated_start = start + shift
        shift += length_deltas.get(name, 0)
        bounds.append((name, relocated_start, end + shift))
    return bounds


def japanese_sections():
    ends = (8, 0xDE5702, 0x13993C4, 0x139F222, 0x13E936B, 0x1CA20B4, 0x1CA20BC,
            0x1CB5CEF, 0x1CBEB9F, 0x1CBEBA7, 0x1D06135, 0x1D2B915, 0x1D2B9CE, 0x1D2C4A3)
    return [(name, start, end) for (name, _, _), start, end in zip(SECTION_BOUNDS, (0, *ends[:-1]), ends, strict=True)]


@dataclass(frozen=True)
class AinLayout:
    sections: list[tuple[str, int, int]]
    # Each entry is a BugFix FUNC header PC and the displacement from that PC onward.
    shifts: tuple[tuple[int, int], ...] = ()
    code_page: int = 936
    patch_managed_rerolls: bool = False

    def relocate(self, pc):
        for start, displacement in reversed(self.shifts):
            if pc >= start:
                return pc + displacement
        return pc


treasure = AinLayout(shifted_sections(CODE=-184), ((0x3E328C, -184),))
LAYOUTS = {
    "bugfix": AinLayout(SECTION_BOUNDS),
    "level40-treasure": treasure,
    "treasure": treasure,
    "extra-card": AinLayout(shifted_sections(CODE=234, STR0=28),
                            ((0x3ACBA8, 98), (0x3E3476, 202), (0x43E2BE, 234))),
    "combined": AinLayout(shifted_sections(CODE=78, MSG1=-4, STR0=19),
                          ((0x3ACBA8, 98), (0x3B0EC6, 138), (0x3B13C6, 196), (0x3E2BAA, 216),
                           (0x3E328C, 32), (0x3E3476, 142), (0x3E4C88, 46), (0x43E2BE, 78)),
                          patch_managed_rerolls=True),
    "auto-food": AinLayout(shifted_sections(CODE=-86), ((0x3B0EC6, 40), (0x3B13C6, 98), (0x3E328C, -86)),
                           patch_managed_rerolls=True),
    "chinese-original": AinLayout(shifted_sections(CODE=-140, MSG1=1, STR0=-22),
                                  ((0x33D51A, -14), (0x39FA3A, -132), (0x44C818, -58), (0xCC3BD6, -104),
                                   (0xD1FF4A, -128), (0xD4A0DE, -154), (0xD5530A, -140))),
    "japanese-original": AinLayout(japanese_sections(),
                                   ((0x33D51A, -28), (0x39FA3A, -146), (0x44C818, -72), (0x548CF6, -86),
                                    (0xAA95EC, -100), (0xCC011A, -1018), (0xCC3BD6, -1368),
                                    (0xD1FF4A, -1446), (0xD4A0DE, -1472), (0xD5530A, -1458), (0xD5E238, -1528)),
                                   code_page=932),
}
