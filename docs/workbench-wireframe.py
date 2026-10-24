"""Generates workbench-wireframe.svg: the solving state of puzzle 00sJ9.

Usage: python docs/workbench-wireframe.py docs/workbench-wireframe.svg
"""
import sys
from html import escape

W, H = 1280, 840
WIN, SURF, RAISED, INPUT, SEP, BOUND = "#1C1B19", "#24221F", "#2B2824", "#181715", "#3B3732", "#797267"
TEXT, SEC, QUIET, ACCENT, ACCENT_TEXT = "#E8E4DC", "#B8B2A8", "#989187", "#4CC2FF", "#111111"
SUCCESS, INFO = "#9BC6A6", "#A7BDCF"
LIGHT, DARK = "#E9D8B8", "#A9825F"
FONT = "Segoe UI Variable Text, Segoe UI, sans-serif"

out = []


def rect(x, y, w, h, fill, stroke=None, r=0, extra=""):
    s = f' stroke="{stroke}"' if stroke else ""
    out.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{r}" fill="{fill}"{s} {extra}/>')


def text(x, y, s, size=14, fill=TEXT, weight=400, anchor="start", family=FONT):
    out.append(
        f'<text x="{x}" y="{y}" font-family="{family}" font-size="{size}" font-weight="{weight}" '
        f'fill="{fill}" text-anchor="{anchor}">{escape(s)}</text>'
    )


def line(x1, y1, x2, y2, stroke=SEP):
    out.append(f'<line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" stroke="{stroke}"/>')


def field(x, y, w, label, value, placeholder=False):
    text(x, y, label, 12, SEC)
    rect(x, y + 6, w, 30, INPUT, BOUND, 4)
    text(x + 10, y + 26, value, 14, QUIET if placeholder else TEXT)


def button(x, y, w, label, primary=False):
    rect(x, y, w, 32, ACCENT if primary else RAISED, None if primary else BOUND, 4)
    text(x + w / 2, y + 21, label, 14, ACCENT_TEXT if primary else TEXT, 600 if primary else 400, "middle")


def radio(x, y, label, on):
    out.append(f'<circle cx="{x + 8}" cy="{y - 5}" r="8" fill="none" stroke="{ACCENT if on else BOUND}" stroke-width="1.5"/>')
    if on:
        out.append(f'<circle cx="{x + 8}" cy="{y - 5}" r="4" fill="{ACCENT}"/>')
    text(x + 24, y, label)


out.append(f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}">')
rect(0, 0, W, H, WIN)

# Title bar and menu bar
rect(0, 0, W, 32, WIN)
text(16, 21, "Lichess Puzzles", 12, SEC)
for i, g in enumerate(["—", "☐", "✕"]):
    text(W - 115 + i * 46, 21, g, 12, SEC, anchor="middle")
x = 12
for m in ["File", "Edit", "View", "Puzzle", "Players", "Help"]:
    text(x, 53, m)
    x += len(m) * 8 + 28
line(0, 64, W, 64)

# Filter pane
FX, FW, TOP, BOT = 0, 300, 64, 808
rect(FX, TOP, FW, BOT - TOP, SURF)
line(FW, TOP, FW, BOT)
px = 16
y = TOP + 30
text(px, y, "1,026,573 matching puzzles", 14, TEXT, 600)
text(FW - 16, y, "Reset filters", 14, ACCENT, anchor="end")
y += 22
field(px, y, FW - 32 - 40, "Preset", "Current filters")
rect(FW - 16 - 32, y + 6, 32, 30, RAISED, BOUND, 4)
text(FW - 32, y + 26, "…", 14, TEXT, anchor="middle")
y += 54
half = (FW - 32 - 8) / 2
field(px, y, half, "Rating from", "1500")
field(px + half + 8, y, half, "to", "1800")
y += 60
text(px, y, "Choose puzzles", 12, SEC)
radio(px, y + 24, "At random", True)
radio(px + 128, y + 24, "Near my rating", False)
y += 48
text(px, y, "Themes", 14, TEXT, 600)
y += 8
rect(px, y, FW - 32, 30, INPUT, BOUND, 4)
text(px + 10, y + 20, "Find theme", 14, QUIET)
y += 46
text(px, y, "Included themes must:", 12, SEC)
rect(px + 136, y - 16, 112, 24, INPUT, BOUND, 4)
rect(px + 137, y - 15, 55, 22, ACCENT, None, 3)
text(px + 164, y, "Any", 12, ACCENT_TEXT, 600, "middle")
text(px + 220, y, "All", 12, TEXT, anchor="middle")
y += 22


def group(name, rows, y):
    text(px, y, "▾  " + name, 12, SEC, 600)
    y += 8
    for label, count, state in rows:
        y += 28
        if state == "include":
            rect(px - 4, y - 19, FW - 24, 26, "#1F3540", None, 4)
            text(px + 4, y, "✓", 14, ACCENT, 600)
        elif state == "exclude":
            text(px + 4, y, "−", 14, "#E99A91", 600)
        text(px + 24, y, label)
        tag = {"include": "  Include", "exclude": "  Exclude"}.get(state, "")
        text(FW - 16, y, count, 12, QUIET, anchor="end")
        if tag:
            text(px + 24 + len(label) * 7.4, y, tag, 12, ACCENT if state == "include" else "#E99A91")
    return y + 20


y = group("Goal", [("Equality", "2,104", None), ("Advantage", "301,522", None), ("Crushing", "402,960", None), ("Mate", "322,091", None)], y + 4)
y = group("Motif", [("Fork", "131,048", "include"), ("Pin", "61,377", "include"), ("Skewer", "22,415", None), ("Sacrifice", "70,216", "exclude")], y)
text(px, y, "▸  Advanced  ·  Mate in  ·  Mating pattern  ·  …", 12, SEC, 600)
y += 24
line(px, y, FW - 16, y)
y += 24
field(px, y, FW - 32, "Opening", "Search families and variations", True)
y += 58
text(px, y, "Side to solve", 12, SEC)
radio(px, y + 24, "Either", True)
radio(px + 88, y + 24, "White", False)
radio(px + 176, y + 24, "Black", False)
y += 52
field(px, y, half, "Solution length from", "1")
field(px + half + 8, y, half, "to", "17")
y += 60
text(px, y, "▸  Quality", 14, TEXT, 600)
# The pane scrolls as a whole; its scroll bar shows there is more below.
rect(FW - 7, TOP + 8, 3, 520, BOUND, None, 1.5)

# Board area
BX0, BX1 = FW, 940
S = 72
board_x = BX0 + (BX1 - BX0 - 8 * S) // 2 + 6
board_y = TOP + 36
fen = "r3r1k1/p4ppp/2p2n2/1p6/3P1qb1/2NQ2R1/PPB2PP1/R1B3K1"  # after the opponent's e3g3
board = {}
for rank_index, row in enumerate(fen.split("/")):
    file = 0
    for ch in row:
        if ch.isdigit():
            file += int(ch)
        else:
            board[(file, 7 - rank_index)] = ch
            file += 1
last = {(4, 2), (6, 2)}  # e3, g3
# Black at the bottom: screen column c is file 7 - c, screen row r is rank r.
for r in range(8):
    for c in range(8):
        f, rk = 7 - c, r
        sx, sy = board_x + c * S, board_y + r * S
        rect(sx, sy, S, S, LIGHT if (f + rk) % 2 == 1 else DARK)
        if (f, rk) in last:
            rect(sx, sy, S, S, ACCENT, None, 0, 'fill-opacity="0.35"')
        p = board.get((f, rk))
        if p:
            # Wireframe pieces: a disc with the piece letter, light for White and dark for Black.
            white = p.isupper()
            body, ink = ("#FAF7F0", "#1A1816") if white else ("#1A1816", "#FAF7F0")
            out.append(f'<circle cx="{sx + S / 2}" cy="{sy + S / 2}" r="25" fill="{body}" stroke="{ink}" stroke-width="2"/>')
            text(sx + S / 2, sy + S / 2 + 9, p.upper(), 26, ink, 600, "middle")
        if r == 7:
            text(sx + S - 6, sy + S - 5, "abcdefgh"[f], 11, DARK if (f + rk) % 2 == 1 else LIGHT, 600, "end")
        if c == 0:
            text(sx + 4, sy + 14, str(rk + 1), 11, DARK if (f + rk) % 2 == 1 else LIGHT, 600)
nav_y = board_y + 8 * S + 20
labels = ["⏮", "◀", "▶", "⏭"]
nx = (BX0 + BX1) / 2 - (4 * 40 + 3 * 8 + 24 + 40) / 2
for g in labels:
    rect(nx, nav_y, 40, 32, RAISED, BOUND, 4)
    text(nx + 20, nav_y + 21, g, 13, TEXT, anchor="middle")
    nx += 48
nx += 16
rect(nx, nav_y, 40, 32, RAISED, BOUND, 4)
text(nx + 20, nav_y + 22, "⇅", 15, TEXT, anchor="middle")
text((BX0 + BX1) / 2, nav_y + 56, "Evaluation bar appears at the board's left edge when enabled", 12, QUIET, anchor="middle")

# Puzzle pane
PX = 940
rect(PX, TOP, W - PX, BOT - TOP, SURF)
line(PX, TOP, PX, BOT)
qx = PX + 16
qw = W - PX - 32
y = TOP + 28
text(qx, y, "Puzzle", 14, TEXT, 600)
text(qx + 80, y, "History", 14, SEC)
rect(qx, y + 8, 46, 3, ACCENT, None, 1.5)
line(PX, y + 14, W, y + 14)
y += 46
text(qx, y, "Puzzle 00sJ9", 20, TEXT, 600)
text(W - 16, y, "Open on lichess.org ↗", 12, ACCENT, anchor="end")
y += 24
text(qx, y, "Rating 2671 ± 105 · popularity 87 · 325 plays", 12, SEC)
y += 20
text(qx, y, "French Defense / Exchange Variation", 12, SEC)
y += 20
text(qx, y, "Themes are shown when the puzzle ends", 12, QUIET)
y += 18
rect(qx, y, qw, 58, "#22303A", "#3D5566", 6)
text(qx + 14, y + 23, "ⓘ  Your turn", 14, TEXT, 600)
text(qx + 14, y + 45, "Find the best move for Black.", 14, SEC)
y += 74
rect(qx, y, qw, 168, INPUT, SEP, 6)
text(qx + 12, y + 26, "18.", 14, QUIET)
text(qx + 50, y + 26, "Rg3", 14, TEXT)
text(qx + 12, y + 50, "Puzzle starts", 12, QUIET)
rect(qx + 140, y + 34, 70, 24, "#1F3540", None, 4)
text(qx + 150, y + 51, "…", 14, TEXT)
text(qx + 12, y + 80, "Solution moves appear here as you play them.", 12, QUIET)
y += 184
bw = (qw - 8) / 2
button(qx, y, bw, "Hint")
button(qx + bw + 8, y, bw, "Show solution")
button(qx, y + 40, bw, "Retry")
button(qx + bw + 8, y + 40, bw, "Next puzzle", True)
y += 96
line(qx, y, W - 16, y)
y += 28
text(qx, y, "Ian ▾", 14, TEXT, 600)
text(W - 16, y, "1,642?   +12", 14, TEXT, 600, "end")
y += 30
rect(qx, y - 15, 40, 20, ACCENT, None, 10)
out.append(f'<circle cx="{qx + 30}" cy="{y - 5}" r="6" fill="{ACCENT_TEXT}"/>')
text(qx + 50, y, "Rated")
y += 26
text(qx, y, "Today: 14 solved of 18 · streak 3", 12, SEC)

# Status bar
rect(0, BOT, W, H - BOT, WIN)
line(0, BOT, W, BOT)
text(16, BOT + 21, "6,157,341 puzzles · Lichess database of 2 October 2026", 12, SEC)
text(500, BOT + 21, "Update available (2 November 2026, 293 MB)", 12, INFO)
text(790, BOT + 21, "Download update", 12, ACCENT)
text(W - 16, BOT + 21, "Wireframe · illustrative", 12, QUIET, anchor="end")

out.append("</svg>")
with open(sys.argv[1], "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(out) + "\n")
