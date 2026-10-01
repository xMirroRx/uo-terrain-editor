# UO Terrain Editor

An in-game terrain editor for a SphereServer shard and a ClassicUO/TazUO client: paint the ground,
raise and lower it, smooth it, scatter sand and gravel over it, and have every change land on the
server, persist across restarts and show up for every player — without leaving the game.

This is the complete work, lifted out of the shard it was built in, so it can be dropped into
another one. Nothing here depends on that shard's own content: the two patch files, the handful of
new source files and the importer tool are the whole feature.

## What it does

* **Paint** — lay a ground texture over an area. Round, square, line, ring, star, speckle and
  organic brushes, 1x1 to 33x33, with a soft edge that thins the tiles out towards the rim.
* **Set Z / Raise / Smooth** — absolute height, relative steps, and an averaging smooth that also
  rounds off the staircase edges a tile grid leaves behind.
* **Revert** — put the untouched `map0.mul` data back under the brush.
* **Scatter** — a second layer: sand, gravel and stone chips drawn *over* the ground instead of
  replacing it, with its own eraser. Each tile takes a random variant of the texture, so a brushed
  area reads as real scatter rather than one solid mass.
* **Decorate** — scatters items (trees, rocks, debris) and can erase only what the editor placed.
* **Undo** — the last 40 strokes, ground and scatter alike.
* Big, real texture thumbnails in the palette, split into the shard's own imported textures and
  UO's own land tiles, with a search box and a scrollbar.

Everything is staff only: the server refuses every brush packet below GM level.

## How it is put together

The client sends brush strokes, the server owns the world, and the clients are told what changed.

```
 client                         server                          every client nearby
 ------                         ------                          -------------------
 TerrainEditorGump   --0xCD-->  CWorldMap::EditTerrainArea
 (brush stroke)                   -> CServerMapEdits (edit layer over map0.mul)
                                   -> save/sphereterrain.bin     <--0x40 (UltimaLive block)
                                 CWorldMap::EditOverlayArea
                                   -> scatter layer              <--0x3F/0xF0 (scatter block)
```

* **Ground edits** ride UltimaLive's block push (`0x40`), so the client writes them into its own
  copy of the map files and the terrain is really there — height, walkability, line of sight.
* **Scatter** is ours: it never touches a mul file, so it needs no UltimaLive handshake and goes to
  every nearby client. The client keeps it in memory and draws it over the land tile.
* Both layers live in `save/sphereterrain.bin`, written during the world save and every 30 seconds
  while someone is building.
* Blocks are only pushed to clients within 6 blocks of the change, at most 4 per tick per client, so
  a long building session does not flood the server's out quota.

The packet layouts are in [docs/protocol.md](docs/protocol.md).

## Installing it

### Server (SphereServer-X fork, C++/CMake)

```sh
git apply /path/to/uo-terrain-editor/server/patch/0001-terrain-editor.patch
cp /path/to/uo-terrain-editor/server/src/common/CServerMapEdits.* src/common/
```

The patch already adds `CServerMapEdits.cpp/.h` to `src/CMakeSources.cmake`. Re-run CMake and build.
`server/BASE_COMMIT.txt` records the commit the patch was cut against.

### Client (ClassicUO / TazUO fork, C# .NET)

```sh
git apply /path/to/uo-terrain-editor/client/patch/0001-terrain-editor.patch
cp -r /path/to/uo-terrain-editor/client/src/Game/* src/ClassicUO.Client/Game/
```

Then register the command in `Game/Managers/CommandManager.cs`:

```csharp
Register("terrain", s => UI.Gumps.TerrainEditorGump.Toggle(_world));
```

`UltimaLive.Enable()` must run at startup (it already does in stock ClassicUO) — that is what
registers the `0x3F` handler the scatter layer rides on.

### Textures

The editor paints whatever land tiles exist, so UO's own tiles work out of the box. To paint your
own textures, `tools/TerrainTextureImporter` turns ordinary square PNGs into real UO land tiles:

```sh
# 25 ground textures, each spread over 8x8 tiles so the stones keep their real size
dotnet run --project tools/TerrainTextureImporter -- \
    --muls <client muls folder> --input <png folder> --start 0x1000 --split 8 \
    --thumbs <muls>/myshard_terrain_thumbs

# scatter textures: transparent 44x44 sprites, 32 variants each, no mul file is touched
dotnet run --project tools/TerrainTextureImporter -- \
    --muls <client muls folder> --input <scatter png folder> --start 0x3200 --split 8 \
    --variants 32 --spritesout <muls>/myshard_terrain_overlay
```

It backs up every mul file it writes to, and leaves `myshard_terrain_sets.txt` next to them — the
list both the client and the server read to know which land ids belong to one texture.

`assets/` holds a ready-made set produced this way: the set list, 320 scatter sprites and 85 palette
thumbnails. The ground textures they were cut from are not included.

## Layout

```
server/patch/      one patch against the SphereServer fork (14 files)
server/src/        CServerMapEdits.{h,cpp} - the runtime edit layer
client/patch/      one patch against the ClassicUO fork (7 files)
client/src/        TerrainEditorGump, TerrainOverlayManager, TerrainBlendManager
tools/             the texture importer
assets/            set list, scatter sprites, palette thumbnails
docs/              protocol and notes
```

## Notes, and what it cost to learn

* **Never let the client fabricate map files.** Stock ClassicUO's UltimaLive setup writes empty
  `mapX.mul` copies when the server declares a map it cannot find, which silently destroys the
  player's world view. The patch makes the handshake opt-in and declares only map 0, with its real
  size.
* **Land art carries no alpha.** `art.mul` land tiles are 16-bit with no alpha channel, which is why
  the scatter layer ships as PNG sprites instead of land tiles.
* **Depth matters.** The blend and scatter layers are drawn at `+0.25` and `+0.4` over the land. A
  smaller offset fights the depth buffer and makes the ground flicker while you walk.
* **Art pixels are packed red-first** (`0xAABBGGRR`); getting that backwards gives everything a cyan
  fringe.
