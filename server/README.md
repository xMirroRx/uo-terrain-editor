# Server side

A SphereServer-X fork. `patch/0001-terrain-editor.patch` touches 14 files; `src/common/CServerMapEdits.*`
are new and are already added to `src/CMakeSources.cmake` by the patch.

    git apply patch/0001-terrain-editor.patch
    cp src/common/CServerMapEdits.* <sphere>/src/common/

`BASE_COMMIT.txt` records the commit the patch was cut against.

## What lives where

| file | what it gained |
|------|----------------|
| `common/CServerMapEdits.{h,cpp}` | the runtime edit layer: patched map blocks, their untouched originals, the scatter layer, and `save/sphereterrain.bin` |
| `common/CServerMap.cpp` | applies the edit layer in `CServerMapBlock::Load`; `ReadTerrainBlockFromMul` for Revert |
| `common/sphereproto.h` | names `0x40` as `XCMD_UpdateTerrain` |
| `game/CWorldMap.{h,cpp}` | the brushes: `EditTerrainArea`, `EditOverlayArea`, `EditStaticArea`, brush shapes, undo, the block pushes and the per-tick catch-up |
| `game/CWorld.cpp` | loads the edits before the world, saves them in the save stage, ticks the pushes |
| `game/clients/CClient.{h,cpp}` | the `TERRAIN` console command, the UltimaLive flag, the per-client sets of already-sent blocks |
| `game/clients/CClientMsg.cpp` | `addUltimaLive()` — the opt-in handshake |
| `network/receive.cpp` | the `0xCD` / `0xCC` / `0xCB` sub-commands, all GM-gated |
| `network/send.{h,cpp}` | `PacketUpdateTerrain` (0x40) and `PacketUpdateTerrainOverlay` (0x3F/0xF0); a rewritten `PacketQueryClient` that declares only map 0, at its real size |
| `tables/CClient_functions.tbl` | the `TERRAIN` command |

## Why the map definitions packet was rewritten

Stock UltimaLive declares every map the server knows. A client that is handed a map whose declared
size does not match the `mapX.mul` it has will build an empty replacement and the player's world
turns to blank tiles. The patched `PacketQueryClient` declares map 0 only, with the size from
`sphere.ini`, and `addUltimaLive()` is never called at login — only when someone actually opens the
editor.
