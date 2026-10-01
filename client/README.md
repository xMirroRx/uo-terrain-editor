# Client side

A ClassicUO / TazUO fork, C# .NET.

    git apply patch/0001-terrain-editor.patch
    cp -r src/Game/* <client>/src/ClassicUO.Client/Game/

Then register the command in `Game/Managers/CommandManager.cs`:

```csharp
Register("terrain", s => UI.Gumps.TerrainEditorGump.Toggle(_world));
```

## What lives where

| file | what it does |
|------|--------------|
| `Game/UI/Gumps/TerrainEditorGump.cs` | the whole window: tools, brush settings, shapes, palette, footer. Sends every stroke. |
| `Game/Managers/TerrainOverlayManager.cs` | the scatter layer: the blocks the server pushed, and the 44x44 PNG sprites |
| `Game/Managers/TerrainBlendManager.cs` | soft edges where two of *our* grounds meet, built from the land art itself |
| `GameObjects/Views/LandView.cs` | draws the soft edges and the scatter over the land tile |
| `Game/Managers/SpellEffectsManager.cs` | the brush cursor on the ground |
| `Game/Scenes/GameScene*.cs` | world clicks and click-and-drag feed the brush |
| `Game/UltimaLive.cs` | the `0xF0` scatter block, real mul copies instead of fabricated ones, and guards against a push arriving before the map files are ready |
| `Network/OutgoingPackets.cs` | `Send_TerrainEdit`, `Send_TerrainCommand`, `Send_TerrainDecor` |
| `Game/UI/Controls/ModernScrollBar.cs` | recalculates the thumb every frame, so scrolling by wheel moves it too |

The gump's own text is Turkish, as the shard it was built for is. The strings are all in
`TerrainEditorGump.cs`.

## Where the palette art comes from

Thumbnails and scatter sprites are read from the client's UO files folder, not from the embedded
resources — any path containing `ExternalImages` is redirected to an embedded resource by this fork's
`PNGLoader`, so they must live elsewhere:

    <muls>/myshard_terrain_sets.txt        which land ids make up one texture
    <muls>/myshard_terrain_thumbs/<ID>.png 128x128 palette thumbnails
    <muls>/myshard_terrain_overlay/<ID>.png 44x44 transparent scatter sprites
