// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using ClassicUO.Assets;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// MyShard serpme katmanı.
    ///
    /// Bir UO karesinde tek bir zemin grafiği olabilir, o yüzden kumu taşın üstüne "boyamak" aslında taşı
    /// siliyordu. Serpme katmanı bunu ayırır: sunucu her kare için ayrı bir grafik tutar, istemci de onu
    /// zeminin ÜSTÜNE çizer. Altındaki döşeme olduğu gibi kalır, serpinti de saydam olduğu için arasından
    /// görünür.
    ///
    /// Karolar mul dosyalarında değil, içe aktarıcının yazdığı saydam PNG'lerde durur (land art 16 bit ve
    /// saydamlık taşıyamaz). Her serpinti dokusunun birbirine benzemeyen çeşitlemeleri vardır; hangi karede
    /// hangisinin duracağına sunucu karar verir, böylece aynı dünyayı herkes aynı görür.
    /// </summary>
    internal static class TerrainOverlayManager
    {
        private const int BLOCK_QTY = 8 * 8;
        private const int MAX_VARIANTS = 64;

        private static readonly Dictionary<ulong, ushort[]> _blocks = new Dictionary<ulong, ushort[]>();
        private static readonly Dictionary<ushort, Texture2D> _sprites = new Dictionary<ushort, Texture2D>();
        private static readonly Dictionary<ushort, int> _variantQty = new Dictionary<ushort, int>();

        /// <summary>Sunucudan gelen bir serpme bloğu.</summary>
        public static void SetBlock(int map, int blockIndex, ushort[] tiles)
        {
            if (tiles == null || tiles.Length != BLOCK_QTY)
            {
                return;
            }

            _blocks[Key(map, blockIndex)] = tiles;
        }

        public static void Clear()
        {
            _blocks.Clear();
        }

        /// <summary>Bu karede duran serpinti grafiği, yoksa 0.</summary>
        public static ushort Get(int map, int x, int y)
        {
            if (_blocks.Count == 0)
            {
                return 0;   // sık yürünen yol: hiç serpinti yoksa hesaba girme
            }

            int blocksHeight = BlocksHeight(map);

            if (blocksHeight <= 0)
            {
                return 0;
            }

            int blockIndex = ((x >> 3) * blocksHeight) + (y >> 3);

            if (!_blocks.TryGetValue(Key(map, blockIndex), out ushort[] tiles))
            {
                return 0;
            }

            return tiles[((y & 7) << 3) + (x & 7)];
        }

        /// <summary>
        /// Bir serpinti dokusunun kaç çeşitlemesi var (0 = bu karo serpme katmanına ait değil). Çeşitlemeler
        /// setin ilk kimliğinden başlayarak ardışık durur: 0x3300, 0x3301, ...
        /// </summary>
        public static int VariantQty(ushort baseGraphic)
        {
            if (_variantQty.TryGetValue(baseGraphic, out int cached))
            {
                return cached;
            }

            int qty = 0;

            try
            {
                for (int i = 0; i < MAX_VARIANTS; i++)
                {
                    if (!File.Exists(SpritePath((ushort)(baseGraphic + i))))
                    {
                        break;
                    }

                    ++qty;
                }
            }
            catch (Exception e)
            {
                Utility.Logging.Log.Warn($"Serpme katmanı: {baseGraphic:X4} çeşitlemeleri sayılamadı: {e.Message}");
            }

            _variantQty[baseGraphic] = qty;

            return qty;
        }

        /// <summary>Bir serpinti karosunun 44x44 saydam görüntüsü, yoksa null.</summary>
        public static Texture2D GetSprite(ushort graphic)
        {
            if (_sprites.TryGetValue(graphic, out Texture2D cached))
            {
                return cached;
            }

            Texture2D texture = null;

            try
            {
                string path = SpritePath(graphic);

                if (File.Exists(path))
                {
                    texture = PNGLoader.Instance.GetImageTexture(path);
                }
            }
            catch (Exception e)
            {
                Utility.Logging.Log.Warn($"Serpme katmanı: {graphic:X4} karosu yüklenemedi: {e.Message}");
            }

            _sprites[graphic] = texture;

            return texture;
        }

        private static string SpritePath(ushort graphic)
        {
            return Client.Game.UO.FileManager.GetUOFilePath($"myshard_terrain_overlay/{graphic:X4}.png");
        }

        private static int BlocksHeight(int map)
        {
            int[,] sizes = Client.Game.UO.FileManager.Maps.MapBlocksSize;

            if (sizes == null || map < 0 || map >= sizes.GetLength(0))
            {
                return 0;
            }

            return sizes[map, 1];
        }

        private static ulong Key(int map, int blockIndex)
        {
            return ((ulong)(uint)map << 32) | (uint)blockIndex;
        }
    }
}
