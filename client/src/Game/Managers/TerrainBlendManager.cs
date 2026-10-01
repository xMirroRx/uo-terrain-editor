// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// MyShard soft terrain edges.
    ///
    /// A UO tile is either one ground graphic or another, so where two kinds of ground meet you get a hard
    /// staircase edge. This draws a second, faded copy of the neighbouring ground over the tile: opaque at
    /// the shared edge, fading out towards the middle. Two grounds then melt into each other instead of
    /// stopping dead, which is what terrain editors in other engines get from blending textures.
    ///
    /// The faded sprites are built once per (graphic, side) from the land art itself and kept in memory.
    /// </summary>
    internal static class TerrainBlendManager
    {
        /// <summary>
        /// The eight neighbours of a tile, in tile coordinates. The four sides soften a straight border,
        /// the four corners round off the steps a tile grid leaves behind.
        /// </summary>
        public const int SIDE_WEST = 0;     // the (X - 1, Y) neighbour
        public const int SIDE_NORTH = 1;    // the (X, Y - 1) neighbour
        public const int SIDE_EAST = 2;     // the (X + 1, Y) neighbour
        public const int SIDE_SOUTH = 3;    // the (X, Y + 1) neighbour
        public const int SIDE_NW = 4;       // the (X - 1, Y - 1) neighbour
        public const int SIDE_NE = 5;       // the (X + 1, Y - 1) neighbour
        public const int SIDE_SE = 6;       // the (X + 1, Y + 1) neighbour
        public const int SIDE_SW = 7;       // the (X - 1, Y + 1) neighbour
        public const int SIDE_QTY = 8;

        private const int ART_SIZE = 44;

        /// <summary>How far into the tile the neighbour bleeds, as a share of the tile.</summary>
        public static float Reach = 0.5f;

        /// <summary>
        /// How many tiles away from the border the blend still reaches. 1 is the tile right next to the
        /// other ground; 2 and 3 carry a fainter copy further in, which is what makes a wide, gradual
        /// transition instead of a one tile seam.
        /// </summary>
        public static int Spread = 1;   // tek halka: daha derin yayılma doğal durmuyordu, arayüzden kaldırıldı

        /// <summary>How much of the blend is left one ring further out.</summary>
        private const float RING_FALLOFF = 0.3f;

        /// <summary>Turn the soft edges off to compare with plain UO rendering.</summary>
        public static bool Enabled = true;

        private static readonly Dictionary<int, Texture2D> _cache = new Dictionary<int, Texture2D>();

        // Which kind of ground a tile graphic belongs to. UO builds one kind of ground out of several tile
        // variants (grass alone has a handful), and our own textures are cut into sets of pieces - none of
        // those borders may be blended, only the border between two different kinds of ground.
        private static int[] _groups;
        private static bool[] _custom;   // sadece editörle gelen dokularımız

        /// <summary>
        /// The ground kind of a tile graphic. Tiles of the same kind never blend into each other.
        /// Pieces of an imported texture set share the set; everything else is grouped by its tiledata name,
        /// which is what makes all the grass variants one ground.
        /// </summary>
        public static int GetGroup(ushort graphic)
        {
            if (_groups == null)
            {
                BuildGroups();
            }

            return graphic < _groups.Length ? _groups[graphic] : graphic + 0x10000;
        }

        /// <summary>
        /// True for the grounds we brought in ourselves (the imported texture sets). UO's own world is left
        /// exactly as it was: softening every grass/dirt border on the map would change the whole shard,
        /// not just what was built with the editor.
        /// </summary>
        public static bool IsCustomGround(ushort graphic)
        {
            if (_custom == null)
            {
                BuildGroups();
            }

            return (graphic < _custom.Length) && _custom[graphic];
        }

        private static void BuildGroups()
        {
            _groups = new int[0x4000];
            _custom = new bool[0x4000];

            for (int i = 0; i < _groups.Length; i++)
            {
                string name = Client.Game.UO.FileManager.TileData.LandData[i].Name;

                // No name of its own: it can only be its own kind.
                // Kendi adı yoksa yalnızca kendisiyle aynı zemindir. Hash maskeleniyor: işaretsiz ve
                // sınırlı olsun ki başka bir aralığa taşıp kendi dokularımızla karışmasın.
                _groups[i] = string.IsNullOrWhiteSpace(name)
                    ? 0x10000 + i
                    : 0x20000 + (name.Trim().ToLowerInvariant().GetHashCode() & 0xFFFF);
            }

            // The pieces of an imported texture set all belong to that set.
            try
            {
                string path = Client.Game.UO.FileManager.GetUOFilePath("myshard_terrain_sets.txt");

                if (!System.IO.File.Exists(path))
                {
                    return;
                }

                foreach (string line in System.IO.File.ReadAllLines(path))
                {
                    string[] parts = line.Split(' ', 3, System.StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length < 2 || !parts[0].StartsWith("0x", System.StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!int.TryParse(parts[0].Substring(2), System.Globalization.NumberStyles.HexNumber, null, out int baseId) ||
                        !int.TryParse(parts[1], out int size))
                    {
                        continue;
                    }

                    // Bir dokunun tam hali ile zayıf karışımları ("… 65", "… 35") AYNI zemindir. Ayrı
                    // saymak, boyanan alanın içindeki her karonun komşusuyla karışmasına ve her şeyin
                    // gölgeli/soluk görünmesine yol açıyordu.
                    string name = parts.Length > 2 ? parts[2].Trim() : string.Empty;
                    int space = name.LastIndexOf(' ');

                    if (space > 0 && int.TryParse(name.Substring(space + 1), out _))
                    {
                        name = name.Substring(0, space);
                    }

                    int group = 0x30000 + (name.ToLowerInvariant().GetHashCode() & 0xFFFF);

                    for (int i = baseId; i < baseId + (size * size) && i < _groups.Length; i++)
                    {
                        _groups[i] = group;
                        _custom[i] = true;
                    }
                }
            }
            catch (System.Exception e)
            {
                Utility.Logging.Log.Warn($"Terrain blending: could not read the texture set list: {e.Message}");
            }
        }

        public static void Clear()
        {
            foreach (Texture2D texture in _cache.Values)
            {
                texture?.Dispose();
            }

            _cache.Clear();
        }

        /// <summary>
        /// The neighbouring ground, faded towards the middle of the tile. Returns null when the graphic has
        /// no art.
        /// </summary>
        public static Texture2D GetEdge(GraphicsDevice device, ushort graphic, int side, int ring = 1)
        {
            int key = (graphic << 6) | (side << 3) | ring;

            if (_cache.TryGetValue(key, out Texture2D cached))
            {
                return cached;
            }

            Texture2D texture = Build(device, graphic, side, ring);
            _cache[key] = texture;

            return texture;
        }

        private static Texture2D Build(GraphicsDevice device, ushort graphic, int side, int ring)
        {
            ArtInfo art = Client.Game.UO.FileManager.Arts.GetArt(graphic);

            if (art.Pixels.IsEmpty || art.Width != ART_SIZE || art.Height != ART_SIZE)
            {
                return null;
            }

            var data = new Color[ART_SIZE * ART_SIZE];

            for (int y = 0; y < ART_SIZE; y++)
            {
                for (int x = 0; x < ART_SIZE; x++)
                {
                    uint pixel = art.Pixels[(y * ART_SIZE) + x];

                    if ((pixel & 0xFF00_0000) == 0)
                    {
                        continue;   // outside the diamond
                    }

                    // The tile's own axes: u runs along world X, v along world Y. They are what the
                    // isometric diamond is made of, so a fade along them lines up with the tile edges.
                    float sx = (x + 0.5f - 22f) / 22f;
                    float sy = (y + 0.5f) / 22f;
                    float u = (sy + sx) * 0.5f;
                    float v = (sy - sx) * 0.5f;

                    // How close this pixel is to the neighbour: 1 right at the shared edge (or corner).
                    float t = side switch
                    {
                        SIDE_WEST => 1f - u,
                        SIDE_EAST => u,
                        SIDE_NORTH => 1f - v,
                        SIDE_SOUTH => v,
                        SIDE_NW => Math.Min(1f - u, 1f - v),
                        SIDE_NE => Math.Min(u, 1f - v),
                        SIDE_SE => Math.Min(u, v),
                        _ => Math.Min(1f - u, v)
                    };

                    // A corner touches the tile on a much smaller front, so it reaches in less far.
                    float reach = side >= SIDE_NW ? Reach * 0.7f : Reach;

                    // Tiles further from the border get a fainter copy, so the transition keeps going
                    // instead of stopping at the first tile. The mask itself must NOT grow with the ring:
                    // a wide, faint layer over a whole tile reads as fog, not as a transition.
                    float strength = 1f;

                    for (int i = 1; i < ring; i++)
                    {
                        strength *= RING_FALLOFF;
                    }

                    // Opaque at the shared edge, gone by the time it has come that far into the tile.
                    float alpha = (t - (1f - reach)) / reach;

                    if (alpha <= 0f)
                    {
                        continue;
                    }

                    if (alpha > 1f)
                    {
                        alpha = 1f;
                    }

                    alpha = alpha * alpha * (3f - (2f * alpha));   // smooth it out, no banding
                    alpha *= strength;

                    byte a = (byte)(alpha * 255f);

                    if (a == 0)
                    {
                        continue;
                    }

                    // Art pixels are packed red first (0xAABBGGRR), and the batcher expects colours that are
                    // already multiplied by their alpha.
                    byte r = (byte)(((pixel & 0xFF) * a) / 255);
                    byte g = (byte)((((pixel >> 8) & 0xFF) * a) / 255);
                    byte b = (byte)((((pixel >> 16) & 0xFF) * a) / 255);

                    data[(y * ART_SIZE) + x] = new Color(r, g, b, a);
                }
            }

            var result = new Texture2D(device, ART_SIZE, ART_SIZE);
            result.SetData(data);

            return result;
        }
    }
}
