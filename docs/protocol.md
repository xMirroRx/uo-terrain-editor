# Protocol

Four packets carry the editor. Three go client to server as sub-commands of the generic text command
`0x12`, one goes server to client as an UltimaLive sub-command. The ground block push is plain
UltimaLive and is not repeated here.

Every client-to-server packet is refused server side below `PLEVEL_GM`.

## 0x12 / 0xCD — brush stroke

The one packet behind Paint, Set Z, Raise, Smooth, Revert and Scatter. 23 bytes of payload,
little-endian.

| offset | size | field |
|--------|------|-------|
| 0 | 1 | sub-command `0xCD` |
| 1 | 1 | tool (see below) |
| 2 | 2 | x |
| 4 | 2 | y |
| 6 | 2 | tile id — the ground graphic, or the scatter set's first id |
| 8 | 1 | brush radius (0..16, so 1x1 to 33x33) |
| 9 | 1 | flags, bit 0 = square brush |
| 10 | 1 | signed value — target height, step, smoothing strength, or scatter density |
| 11 | 1 | edge softness, percent |
| 12 | 1 | set size — NxN tiles per texture, or the scatter variant count |
| 13 | 1 | brush shape (0..12) |
| 14 | 2 | variant A / scatter stroke seed |
| 16 | 1 | variant A set size |
| 17 | 2 | variant B |
| 19 | 1 | variant B set size |

Tools:

| value | tool |
|-------|------|
| 0 | paint the ground graphic |
| 1 | set an absolute height |
| 2 | raise or lower by a step |
| 3 | smooth heights, and round off the painted edges |
| 4 | graphic and height at once |
| 5 | revert to the untouched mul data |
| 6 | scatter: draw over the ground, do not replace it |
| 7 | erase the scatter under the brush |

Brush shapes: 0 round, 1 round with a hard edge, 2 square, 3 line N-S, 4 line W-E, 5 and 6 the two
diagonals, 7 ring, 8 four-armed star, 9 eight-armed star, 10 dot, 11 speckle, 12 organic.

The variant fields were used by an earlier edge-blending scheme and are now only read by the
scatter tool, which puts the stroke's random seed in variant A. A fresh seed per stroke is what makes
a brush dragged over the same spot keep filling in new tiles instead of redrawing the same ones.

## 0x12 / 0xCC — editor command

| offset | size | field |
|--------|------|-------|
| 0 | 1 | sub-command `0xCC` |
| 1 | 1 | action: 0 = undo the last stroke, 1 = save the edits now |

## 0x12 / 0xCB — decoration brush

| offset | size | field |
|--------|------|-------|
| 0 | 1 | sub-command `0xCB` |
| 1 | 2 | x |
| 3 | 2 | y |
| 5 | 2 | item id |
| 7 | 1 | radius |
| 8 | 1 | flags, bit 0 = square, bit 1 = erase |
| 9 | 1 | density, percent |
| 10 | 1 | brush shape |

Everything it drops carries `TAG.terrainedit = 1`, and the eraser only removes items carrying that
tag, so it can never delete what a player or a script put there.

## 0x3F / 0xF0 — scatter block (server to client)

143 bytes, big-endian, riding UltimaLive's variable-length `0x3F`. The sub-command byte sits at
offset 13 where UltimaLive's own commands put it, so the stock handler dispatches it.

| offset | size | field |
|--------|------|-------|
| 0 | 1 | `0x3F` |
| 1 | 2 | packet length (143) |
| 3 | 4 | block index — `(x / 8) * blocksHigh + (y / 8)` |
| 7 | 4 | 0 |
| 11 | 2 | 0 |
| 13 | 1 | sub-command `0xF0` |
| 14 | 1 | map index |
| 15 | 128 | 64 scatter graphics, row-major inside the 8x8 block, 0 = nothing here |

The tile's index inside the block is `(y % 8) * 8 + (x % 8)`, the same order `map0.mul` uses for its
own 8x8 blocks.

## save/sphereterrain.bin

`SPHMEDIT`, a `uint32` version, then:

1. `uint32` count, then that many `{ uint8 map, uint32 block, CUOMapBlock(196) }` — the edited ground.
2. version 2 and up: `uint32` count, then the same records — the untouched copy of every block the
   editor first touched, which is what Revert falls back on when `map0.mul` cannot be read.
3. version 3 and up: `uint32` count, then `{ uint8 map, uint32 block, uint16[64] }` — the scatter
   layer.

Older files load into a newer server; the missing sections are simply empty.
