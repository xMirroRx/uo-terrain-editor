// SPDX-License-Identifier: BSD-2-Clause
//
// MyShard terrain texture importer.
//
// Turns ordinary square ground textures (PNG) into real UO land tiles, so they can be painted with the
// in-game terrain editor. For every texture it writes three things:
//
//   texmaps.mul / texidx.mul  the 128x128 texture, used when the ground is stretched over uneven heights
//   art.mul / artidx.mul      the 44x44 diamond, used when the ground is flat
//   tiledata.mul              the land entry: name, flags and the texture id of the new tile
//
// The client and the server read the same files (sphere.ini MulFiles points at the client muls folder),
// so one import serves both. Every file it touches is backed up first.
//
// Usage:
//   TerrainTextureImporter --muls <dir> --report
//   TerrainTextureImporter --muls <dir> --input <png dir> [--start 0x3E00] [--preview <dir>]

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MyShard.Tools.TerrainTextureImporter;

internal static class Program
{
    private const int LAND_TILE_QTY = 0x4000;
    private const int LAND_ENTRY_SIZE = 30;         // HS tiledata: 8 flag bytes + 2 texture id + 20 name
    private const int LAND_GROUP_SIZE = 4 + (32 * LAND_ENTRY_SIZE);
    private const int LAND_SECTION_SIZE = (LAND_TILE_QTY / 32) * LAND_GROUP_SIZE;
    private const int TEXMAP_SIZE = 128;            // 128x128 texture
    private const int ART_SIZE = 44;                // 44x44 land diamond

    private static int SampleSize = 48;             // texture density used for the flat land art
    private static float SharpenAmount = 0.6f;      // brings the pattern back after the downscale
    private static int Split = 1;                   // how many tiles across one texture is spread
    private static int BaseTile = -1;               // land tile to mix the texture into (-1 = none)
    private static int[] Opacities = null;          // which mixes to produce, in percent
    private static uint[] _baseArt;                 // the base tile's 44x44 art, read once
    private static ushort[] _baseTexmap;            // the base tile's texture, read once
    private static int _baseTexmapSize;
    private static int CurrentOpacity = 100;        // how much of the texture shows over the base tile
    private static bool Overlay = false;            // the texture has its own alpha (scatter, gravel, sand)
    private static string ThumbDir = null;          // where to write the palette thumbnails

    private static int Main(string[] args)
    {
        string muls = GetArg(args, "--muls");
        string input = GetArg(args, "--input");
        string previewDir = GetArg(args, "--preview");
        string startText = GetArg(args, "--start");
        bool report = HasFlag(args, "--report");

        string sampleText = GetArg(args, "--sample");
        if (!string.IsNullOrEmpty(sampleText)) SampleSize = int.Parse(sampleText);
        string splitText = GetArg(args, "--split");
        if (!string.IsNullOrEmpty(splitText)) Split = Math.Clamp(int.Parse(splitText), 1, 16);
        Overlay = HasFlag(args, "--overlay");
        ThumbDir = GetArg(args, "--thumbs");
        string baseText = GetArg(args, "--basetile");
        if (!string.IsNullOrEmpty(baseText)) BaseTile = ParseId(baseText, -1);
        string opacityText = GetArg(args, "--opacity");
        if (!string.IsNullOrEmpty(opacityText)) Opacities = opacityText.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Math.Clamp(int.Parse(s.Trim()), 1, 100)).ToArray();
        string sharpenText = GetArg(args, "--sharpen");
        if (!string.IsNullOrEmpty(sharpenText)) SharpenAmount = float.Parse(sharpenText, System.Globalization.CultureInfo.InvariantCulture);

        if (string.IsNullOrEmpty(muls) || !Directory.Exists(muls))
        {
            Console.Error.WriteLine("Usage: --muls <mul folder> [--report | --input <png folder>] [--start <first land id>] [--preview <dir>]");

            return 1;
        }

        string tiledataPath = Path.Combine(muls, "tiledata.mul");
        byte[] tiledata = File.ReadAllBytes(tiledataPath);

        if (tiledata.Length < LAND_SECTION_SIZE)
        {
            Console.Error.WriteLine($"tiledata.mul is too small ({tiledata.Length} bytes), this tool expects the HS format.");

            return 1;
        }

        string verifyText = GetArg(args, "--verify");

        if (!string.IsNullOrEmpty(verifyText))
        {
            // Reads a land tile back out of art.mul and writes it as a PNG, to check what really landed
            // in the file (as opposed to what we think we wrote).
            int id = ParseId(verifyText, 0);
            byte[] artidxV = File.ReadAllBytes(Path.Combine(muls, "artidx.mul"));
            int lookup = BitConverter.ToInt32(artidxV, id * 12);
            int length = BitConverter.ToInt32(artidxV, (id * 12) + 4);
            int extra = BitConverter.ToInt32(artidxV, (id * 12) + 8);

            Console.WriteLine($"art 0x{id:X4}: lookup {lookup}, length {length}, extra {extra}");
            Console.WriteLine($"tiledata name '{ReadLandName(tiledata, id)}' texId {BitConverter.ToUInt16(tiledata, LandEntryOffset(id) + 8)} flags 0x{ReadLandFlags(tiledata, id):X}");

            if (lookup >= 0 && length > 0)
            {
                using var fs = new FileStream(Path.Combine(muls, "art.mul"), FileMode.Open, FileAccess.Read);
                fs.Seek(lookup, SeekOrigin.Begin);
                var raw = new byte[length];
                fs.ReadExactly(raw, 0, length);

                var pixels = new ushort[ART_SIZE * ART_SIZE];
                int pos = 0;

                for (int i = 0; i < 22; i++)
                {
                    int start = 22 - (i + 1);
                    int end = start + ((i + 1) << 1);

                    for (int j = start; j < end; j++)
                    {
                        pixels[(i * ART_SIZE) + j] = BitConverter.ToUInt16(raw, pos);
                        pos += 2;
                    }
                }

                for (int i = 0; i < 22; i++)
                {
                    int end = i + ((22 - i) << 1);

                    for (int j = i; j < end; j++)
                    {
                        pixels[((i + 22) * ART_SIZE) + j] = BitConverter.ToUInt16(raw, pos);
                        pos += 2;
                    }
                }

                WritePreview(previewDir ?? ".", $"verify_{id:X4}", pixels);
                Console.WriteLine($"Wrote verify_{id:X4}.png");
            }

            return 0;
        }

        if (report || string.IsNullOrEmpty(input))
        {
            ReportFreeLandTiles(tiledata);

            return 0;
        }

        string[] files = Directory.GetFiles(input, "*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();

        if (files.Length == 0)
        {
            Console.Error.WriteLine($"No PNG files in {input}");

            return 1;
        }

        bool previewOnly = HasFlag(args, "--previewonly");

        int neededTiles = files.Length * Split * Split * (Opacities?.Length ?? 1);
        int startId = ParseId(startText, FindFreeRun(tiledata, neededTiles));

        if (startId < 0)
        {
            Console.Error.WriteLine($"Could not find {neededTiles} free land tiles in a row. Pass --start with an id yourself.");

            return 1;
        }

        Console.WriteLine($"{files.Length} textures ({Split}x{Split} tiles each) -> land tiles 0x{startId:X4} .. 0x{startId + neededTiles - 1:X4}");
        // ---- serpme katmanı karoları ----
        // --spritesout hiçbir mul dosyasına dokunmaz: her doku için saydam 44x44 PNG çeşitlemeleri yazar.
        // Bunlar zeminin YERİNE geçmez, istemcide zeminin ÜSTÜNE çizilir; yani kum/çakıl/taş gerçek bir
        // katman olur ve altındaki döşeme görünmeye devam eder.
        //
        // Serpme dokuları zemin dokuları gibi kusursuz tekrarlanan desenler değil, boşluklu birer serpinti
        // tarlası: 8x8'e bölünce parçaların çoğu bomboş çıkıyor. Onun yerine aynı dokudan, taşların gerçek
        // ölçüsünü koruyan (1254 / 8 = 157 piksellik) rastgele kırpmalar alınır. Her kare bunlardan birini
        // çeker, böylece fırça aynı yerde dönerken tek parça bir kütle değil gerçek bir serpinti oluşur.
        string spritesOut = GetArg(args, "--spritesout");

        if (!string.IsNullOrEmpty(spritesOut))
        {
            string variantsText = GetArg(args, "--variants");
            int variants = string.IsNullOrEmpty(variantsText) ? 32 : Math.Clamp(int.Parse(variantsText), 1, 64);

            string tilePixelsText = GetArg(args, "--tilepixels");
            int tilePixels = string.IsNullOrEmpty(tilePixelsText) ? 0 : int.Parse(tilePixelsText);

            Directory.CreateDirectory(spritesOut);

            int written = 0;
            int spriteId = startId;
            var rng = new Random(20261001);     // aynı girdi hep aynı karoları versin

            foreach (string file in files)
            {
                using Image<Rgba32> image = Image.Load<Rgba32>(file);

                int crop = tilePixels > 0 ? tilePixels : Math.Max(32, image.Width / 8);
                crop = Math.Min(crop, Math.Min(image.Width, image.Height));

                for (int v = 0; v < variants; v++)
                {
                    using Image<Rgba32> piece = PickScatterCrop(image, crop, rng);
                    using Image<Rgba32> sprite = BuildOverlaySprite(piece);

                    sprite.Save(Path.Combine(spritesOut, $"{spriteId + v:X4}.png"));
                    ++written;
                }

                Console.WriteLine($"  0x{spriteId:X4}  {variants} çeşitleme ({crop}px)  {MakeSetName(Path.GetFileNameWithoutExtension(file))}");
                spriteId += Split * Split;      // setler arası aralık bozulmasın
            }

            Console.WriteLine($"{written} serpme karosu yazıldı -> {spritesOut}");

            return 0;
        }


        if (previewOnly)
        {
            // Only render the 44x44 diamonds so the sampling settings can be judged before touching
            // anything: nothing is written to the mul files.
            foreach (string file in files)
            {
                using Image<Rgba32> preview = Image.Load<Rgba32>(file);
                string previewName = MakeSetName(Path.GetFileNameWithoutExtension(file));

                // Render the same pieces the real import would, so the preview shows a tile as it will be.
                for (int px = 0; px < Split; px++)
                {
                    for (int py = 0; py < Split; py++)
                    {
                        using Image<Rgba32> piece = CropPiece(preview, px, py, Split);
                        BuildLandArt(piece, previewDir ?? ".", Split > 1 ? MakePieceName(previewName, (px * Split) + py) : MakeTileName(previewName));
                    }
                }
            }

            Console.WriteLine($"Previews only (sample {SampleSize}, sharpen {SharpenAmount}) -> {previewDir}");

            return 0;
        }

        if (BaseTile >= 0)
        {
            LoadBaseTile(muls, tiledata, BaseTile);
        }

        Backup(muls, "tiledata.mul");
        Backup(muls, "texmaps.mul");
        Backup(muls, "texidx.mul");
        Backup(muls, "art.mul");
        Backup(muls, "artidx.mul");

        byte[] texidx = File.ReadAllBytes(Path.Combine(muls, "texidx.mul"));
        byte[] artidx = File.ReadAllBytes(Path.Combine(muls, "artidx.mul"));

        int texEntryQty = texidx.Length / 12;
        var newTexIdx = new List<byte>();
        var report2 = new List<string>();

        using FileStream texmaps = new FileStream(Path.Combine(muls, "texmaps.mul"), FileMode.Open, FileAccess.ReadWrite);
        using FileStream art = new FileStream(Path.Combine(muls, "art.mul"), FileMode.Open, FileAccess.ReadWrite);

        texmaps.Seek(0, SeekOrigin.End);
        art.Seek(0, SeekOrigin.End);

        var sets = new List<string>();
        int nextLandId = startId;
        int nextTexId = texEntryQty;

        // Without --opacity there is one set per texture, at full strength.
        int[] opacities = Opacities ?? new[] { 100 };

        for (int i = 0; i < files.Length * opacities.Length; i++)
        {
            string file = files[i / opacities.Length];
            CurrentOpacity = opacities[i % opacities.Length];

            string fullName = MakeSetName(Path.GetFileNameWithoutExtension(file));

            if (CurrentOpacity < 100)
            {
                fullName = $"{fullName} {CurrentOpacity}";
            }

            int baseId = nextLandId;

            using Image<Rgba32> image = Image.Load<Rgba32>(file);

            // A single texture on a single tile repeats every 44 pixels, which is why the stones come out
            // small and obviously tiled. With --split N the texture is cut into N x N pieces, each piece
            // becomes its own land tile, and the editor lays them down so they line up: one texture then
            // covers N x N tiles and the stones keep their real size.
            for (int px = 0; px < Split; px++)
            {
                for (int py = 0; py < Split; py++)
                {
                    // px follows the world X axis, py the world Y axis - the same order the editor uses.
                    int landId = baseId + (px * Split) + py;
                    string pieceName = Split > 1 ? MakePieceName(fullName, (px * Split) + py) : MakeTileName(fullName);

                    using Image<Rgba32> piece = CropPiece(image, px, py, Split);

                    // ---- texmap: used when the ground is stretched over uneven heights ----
                    using Image<Rgba32> square = piece.Clone(c => c.Resize(TEXMAP_SIZE, TEXMAP_SIZE));
                    byte[] texData = To16Bit(square);
                    if (Overlay)
                        MixOverlayTexmap(square, texData);
                    else
                        MixTexmapWithBase(texData);

                    long texOffset = texmaps.Position;
                    texmaps.Write(texData, 0, texData.Length);
                    int texId = nextTexId++;

                    newTexIdx.AddRange(BitConverter.GetBytes((int)texOffset));
                    newTexIdx.AddRange(BitConverter.GetBytes(texData.Length));
                    newTexIdx.AddRange(BitConverter.GetBytes(1));   // extra: 1 = 128x128

                    // ---- art: the 44x44 diamond, used on flat ground ----
                    byte[] artData = BuildLandArt(piece, previewDir, pieceName);

                    long artOffset = art.Position;
                    art.Write(artData, 0, artData.Length);

                    WriteIdxEntry(artidx, landId, (int)artOffset, artData.Length, 0);

                    // ---- tiledata: name the tile and point it at the new texture ----
                    WriteLandEntry(tiledata, landId, pieceName, (ushort)texId);

                    report2.Add($"0x{landId:X4}  tex {texId,5}  {pieceName}");
                }
            }

            nextLandId += Split * Split;
            sets.Add($"0x{baseId:X4} {Split} {fullName}");

            // A thumbnail of the texture itself: in the editor the picture says more at a glance than a
            // 44x44 tile does, especially for the scatter overlays.
            if (!string.IsNullOrEmpty(ThumbDir))
            {
                Directory.CreateDirectory(ThumbDir);

                using Image<Rgba32> thumb = image.Clone(c => c.Resize(new ResizeOptions
                {
                    Size = new Size(128, 128),
                    Sampler = KnownResamplers.Lanczos3,
                    Mode = ResizeMode.Stretch
                }));

                thumb.Save(Path.Combine(ThumbDir, $"{baseId:X4}.png"));
            }
            Console.WriteLine($"  0x{baseId:X4} ({Split}x{Split})  {fullName}");
        }

        File.WriteAllBytes(Path.Combine(muls, "artidx.mul"), artidx);
        File.WriteAllBytes(Path.Combine(muls, "tiledata.mul"), tiledata);

        using (var texIdxStream = new FileStream(Path.Combine(muls, "texidx.mul"), FileMode.Open, FileAccess.ReadWrite))
        {
            texIdxStream.Seek(0, SeekOrigin.End);
            texIdxStream.Write(newTexIdx.ToArray(), 0, newTexIdx.Count);
        }

        string reportPath = Path.Combine(muls, "myshard_terrain_tiles.txt");
        File.WriteAllLines(reportPath, report2);

        // The editor reads this one: which land id starts a set and how many tiles across it is.
        File.WriteAllLines(Path.Combine(muls, "myshard_terrain_sets.txt"), sets);

        Console.WriteLine($"Done. The list of new tiles is in {reportPath}");

        return 0;
    }


    /// <summary>
    /// Reads the land tile the new textures are mixed into (normally grass), both as the 44x44 diamond and
    /// as its texture, so the mixed tiles sit on the real ground instead of an invented green.
    /// </summary>
    private static void LoadBaseTile(string muls, byte[] tiledata, int landId)
    {
        byte[] artidx = File.ReadAllBytes(Path.Combine(muls, "artidx.mul"));
        int lookup = BitConverter.ToInt32(artidx, landId * 12);
        int length = BitConverter.ToInt32(artidx, (landId * 12) + 4);

        if (lookup >= 0 && length > 0)
        {
            using var fs = new FileStream(Path.Combine(muls, "art.mul"), FileMode.Open, FileAccess.Read);
            fs.Seek(lookup, SeekOrigin.Begin);
            var raw = new byte[length];
            fs.ReadExactly(raw, 0, length);

            _baseArt = new uint[ART_SIZE * ART_SIZE];
            int pos = 0;

            for (int i = 0; i < 22; i++)
            {
                int start = 22 - (i + 1);
                int end = start + ((i + 1) << 1);

                for (int j = start; j < end; j++)
                {
                    _baseArt[(i * ART_SIZE) + j] = Color16ToRgb(BitConverter.ToUInt16(raw, pos));
                    pos += 2;
                }
            }

            for (int i = 0; i < 22; i++)
            {
                int end = i + ((22 - i) << 1);

                for (int j = i; j < end; j++)
                {
                    _baseArt[((i + 22) * ART_SIZE) + j] = Color16ToRgb(BitConverter.ToUInt16(raw, pos));
                    pos += 2;
                }
            }
        }

        // Its texture, for the stretched (sloped) rendering.
        ushort texId = BitConverter.ToUInt16(tiledata, LandEntryOffset(landId) + 8);
        byte[] texidx = File.ReadAllBytes(Path.Combine(muls, "texidx.mul"));

        if (texId > 0 && (texId * 12) + 8 <= texidx.Length)
        {
            int texLookup = BitConverter.ToInt32(texidx, texId * 12);
            int texLength = BitConverter.ToInt32(texidx, (texId * 12) + 4);

            if (texLookup >= 0 && texLength > 0)
            {
                using var fs = new FileStream(Path.Combine(muls, "texmaps.mul"), FileMode.Open, FileAccess.Read);
                fs.Seek(texLookup, SeekOrigin.Begin);
                var raw = new byte[texLength];
                fs.ReadExactly(raw, 0, texLength);

                _baseTexmapSize = texLength == 0x2000 ? 64 : 128;
                _baseTexmap = new ushort[_baseTexmapSize * _baseTexmapSize];

                for (int i = 0; i < _baseTexmap.Length && (i * 2) + 1 < raw.Length; i++)
                {
                    _baseTexmap[i] = BitConverter.ToUInt16(raw, i * 2);
                }
            }
        }

        Console.WriteLine($"Mixing into land tile 0x{landId:X4} '{ReadLandName(tiledata, landId)}' (art {(_baseArt != null ? "ok" : "missing")}, texture {(_baseTexmap != null ? _baseTexmapSize + "px" : "missing")})");
    }

    private static uint Color16ToRgb(ushort c)
    {
        uint r = (uint)(((c >> 10) & 0x1F) << 3);
        uint g = (uint)(((c >> 5) & 0x1F) << 3);
        uint b = (uint)((c & 0x1F) << 3);

        return (r << 16) | (g << 8) | b;
    }

    /// <summary>Mixes two 16 bit colours: 0 = all base, 100 = all texture.</summary>
    private static ushort Mix16(ushort texture, uint baseRgb, int opacity)
    {
        float t = opacity / 100f;

        float r = ((((texture >> 10) & 0x1F) << 3) * t) + (((baseRgb >> 16) & 0xFF) * (1f - t));
        float g = ((((texture >> 5) & 0x1F) << 3) * t) + (((baseRgb >> 8) & 0xFF) * (1f - t));
        float b = (((texture & 0x1F) << 3) * t) + ((baseRgb & 0xFF) * (1f - t));

        return (ushort)(((int)(r) >> 3 << 10) | ((int)(g) >> 3 << 5) | ((int)(b) >> 3));
    }


    /// <summary>
    /// A texture with its own alpha (an overlay) laid onto the base tile's texture, for the stretched
    /// rendering. Fully transparent pixels become the plain ground.
    /// </summary>
    private static void MixOverlayTexmap(Image<Rgba32> overlay, byte[] texData)
    {
        if (!Overlay || _baseTexmap == null)
        {
            return;
        }

        for (int y = 0; y < TEXMAP_SIZE; y++)
        {
            for (int x = 0; x < TEXMAP_SIZE; x++)
            {
                int index = ((y * TEXMAP_SIZE) + x) * 2;
                ushort texColor = (ushort)(texData[index] | (texData[index + 1] << 8));

                int bx = x * _baseTexmapSize / TEXMAP_SIZE;
                int by = y * _baseTexmapSize / TEXMAP_SIZE;

                int strength = CurrentOpacity * overlay[x, y].A / 255;
                ushort mixed = Mix16(texColor, Color16ToRgb(_baseTexmap[(by * _baseTexmapSize) + bx]), strength);

                texData[index] = (byte)(mixed & 0xFF);
                texData[index + 1] = (byte)(mixed >> 8);
            }
        }
    }

    /// <summary>Mixes a freshly built texture with the base tile's texture, when an opacity was asked for.</summary>
    private static void MixTexmapWithBase(byte[] texData)
    {
        if (CurrentOpacity >= 100 || _baseTexmap == null)
        {
            return;
        }

        for (int y = 0; y < TEXMAP_SIZE; y++)
        {
            for (int x = 0; x < TEXMAP_SIZE; x++)
            {
                int index = ((y * TEXMAP_SIZE) + x) * 2;
                ushort texColor = (ushort)(texData[index] | (texData[index + 1] << 8));

                int bx = x * _baseTexmapSize / TEXMAP_SIZE;
                int by = y * _baseTexmapSize / TEXMAP_SIZE;
                ushort mixed = Mix16(texColor, Color16ToRgb(_baseTexmap[(by * _baseTexmapSize) + bx]), CurrentOpacity);

                texData[index] = (byte)(mixed & 0xFF);
                texData[index + 1] = (byte)(mixed >> 8);
            }
        }
    }

    // ---------- tiledata ----------

    private static int LandEntryOffset(int landId)
    {
        int group = landId / 32;
        int indexInGroup = landId % 32;

        return (group * LAND_GROUP_SIZE) + 4 + (indexInGroup * LAND_ENTRY_SIZE);
    }

    private static string ReadLandName(byte[] tiledata, int landId)
    {
        int offset = LandEntryOffset(landId) + 10;
        int length = 0;

        while (length < 20 && tiledata[offset + length] != 0)
        {
            length++;
        }

        return System.Text.Encoding.ASCII.GetString(tiledata, offset, length).Trim();
    }

    private static ulong ReadLandFlags(byte[] tiledata, int landId)
    {
        return BitConverter.ToUInt64(tiledata, LandEntryOffset(landId));
    }

    private static void WriteLandEntry(byte[] tiledata, int landId, string name, ushort texId)
    {
        int offset = LandEntryOffset(landId);

        // Flags 0 = plain walkable ground, which is what a paved or grassy surface should be.
        BitConverter.GetBytes((ulong)0).CopyTo(tiledata, offset);
        BitConverter.GetBytes(texId).CopyTo(tiledata, offset + 8);

        for (int i = 0; i < 20; i++)
        {
            tiledata[offset + 10 + i] = 0;
        }

        byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name.Length > 19 ? name.Substring(0, 19) : name);
        nameBytes.CopyTo(tiledata, offset + 10);
    }

    private static bool IsFreeLandTile(byte[] tiledata, int landId)
    {
        string name = ReadLandName(tiledata, landId);

        if (name.Length == 0)
        {
            return true;
        }

        return name.Contains("unused", StringComparison.OrdinalIgnoreCase)
               || name.Contains("no draw", StringComparison.OrdinalIgnoreCase)
               || name.Contains("nodraw", StringComparison.OrdinalIgnoreCase)
               || name.Equals("noname", StringComparison.OrdinalIgnoreCase);
    }

    private static void ReportFreeLandTiles(byte[] tiledata)
    {
        int runStart = -1;
        int best = 0, bestStart = -1;

        for (int i = 0; i < LAND_TILE_QTY; i++)
        {
            if (IsFreeLandTile(tiledata, i))
            {
                if (runStart < 0)
                {
                    runStart = i;
                }

                int length = i - runStart + 1;

                if (length > best)
                {
                    best = length;
                    bestStart = runStart;
                }
            }
            else
            {
                runStart = -1;
            }
        }

        Console.WriteLine($"Longest free land tile run: {best} tiles from 0x{bestStart:X4} to 0x{bestStart + best - 1:X4}");

        Console.WriteLine("All free runs of 128 tiles or more:");
        int runFrom = -1;
        for (int i = 0; i <= LAND_TILE_QTY; i++)
        {
            bool free = i < LAND_TILE_QTY && IsFreeLandTile(tiledata, i);
            if (free)
            {
                if (runFrom < 0) runFrom = i;
            }
            else if (runFrom >= 0)
            {
                if (i - runFrom >= 128) Console.WriteLine($"  0x{runFrom:X4} - 0x{i - 1:X4}  ({i - runFrom} tiles)");
                runFrom = -1;
            }
        }

        Console.WriteLine("First used tiles, for a sanity check:");

        int shown = 0;

        for (int i = 0; i < LAND_TILE_QTY && shown < 8; i++)
        {
            if (!IsFreeLandTile(tiledata, i))
            {
                Console.WriteLine($"  0x{i:X4}  flags 0x{ReadLandFlags(tiledata, i):X}  {ReadLandName(tiledata, i)}");
                shown++;
            }
        }
    }

    private static int FindFreeRun(byte[] tiledata, int needed)
    {
        int runStart = -1;

        // Search from the back: the high ids are the ones nothing uses.
        for (int i = LAND_TILE_QTY - 1; i >= 0; i--)
        {
            if (IsFreeLandTile(tiledata, i))
            {
                if (runStart < 0)
                {
                    runStart = i;
                }

                if (runStart - i + 1 >= needed)
                {
                    return i;
                }
            }
            else
            {
                runStart = -1;
            }
        }

        return -1;
    }

    // ---------- art / texmaps ----------

    private static byte[] To16Bit(Image<Rgba32> image)
    {
        var data = new byte[image.Width * image.Height * 2];
        int pos = 0;

        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Rgba32 pixel = image[x, y];
                ushort color = (ushort)(((pixel.R >> 3) << 10) | ((pixel.G >> 3) << 5) | (pixel.B >> 3));
                data[pos++] = (byte)(color & 0xFF);
                data[pos++] = (byte)(color >> 8);
            }
        }

        return data;
    }

    /// <summary>
    /// Builds the 44x44 land diamond: the square texture seen from the isometric camera. The diamond is
    /// stored row by row, 2 pixels on the first row growing to 44 in the middle and back down.
    /// </summary>
    private static byte[] BuildLandArt(Image<Rgba32> texture, string previewDir, string name)
    {
        // A tile's own axes are only about 31 screen pixels long. Sampling the full 128x128 turns the
        // pattern into noise, averaging it down too far turns it into mush, so the density is tunable
        // and a little sharpening brings the stone edges back at tile size.
        int sampleSize = SampleSize;

        var pixels = new ushort[ART_SIZE * ART_SIZE];
        var alphas = new byte[ART_SIZE * ART_SIZE];
        using Image<Rgba32> source = texture.Clone(c =>
        {
            c.Resize(new ResizeOptions
            {
                Size = new Size(sampleSize, sampleSize),
                Sampler = KnownResamplers.Box,
                Mode = ResizeMode.Stretch
            });

            if (SharpenAmount > 0f)
            {
                c.GaussianSharpen(SharpenAmount);
            }
        });

        for (int y = 0; y < ART_SIZE; y++)
        {
            for (int x = 0; x < ART_SIZE; x++)
            {
                // screen = ((u - v) * 22 + 22, (u + v) * 22)  ->  invert it
                float sx = (x + 0.5f - 22f) / 22f;
                float sy = (y + 0.5f) / 22f;
                float u = (sy + sx) * 0.5f;
                float v = (sy - sx) * 0.5f;

                u -= MathF.Floor(u);
                v -= MathF.Floor(v);

                (byte r, byte g, byte b, byte a) = SampleBilinear(source, u * sampleSize, v * sampleSize);
                pixels[(y * ART_SIZE) + x] = (ushort)(((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3));
                alphas[(y * ART_SIZE) + x] = a;
            }
        }

        // Mix the diamond into the base ground. Two things feed the mix: the opacity asked for, and the
        // texture's own alpha - an overlay (scattered gravel, sand) is mostly transparent, and what shows
        // through has to be the ground it was laid on.
        if (_baseArt != null && (CurrentOpacity < 100 || Overlay))
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                if (!IsInDiamond(i % ART_SIZE, i / ART_SIZE))
                {
                    continue;
                }

                int strength = CurrentOpacity;

                if (Overlay)
                {
                    strength = strength * alphas[i] / 255;
                }

                pixels[i] = Mix16(pixels[i], _baseArt[i], strength);
            }
        }

        if (!string.IsNullOrEmpty(previewDir))
        {
            WritePreview(previewDir, name, pixels);
        }

        // Now write it out in the diamond order the client reads.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        for (int i = 0; i < 22; i++)
        {
            int start = 22 - (i + 1);
            int end = start + ((i + 1) << 1);

            for (int j = start; j < end; j++)
            {
                writer.Write(pixels[(i * ART_SIZE) + j]);
            }
        }

        for (int i = 0; i < 22; i++)
        {
            int end = i + ((22 - i) << 1);

            for (int j = i; j < end; j++)
            {
                writer.Write(pixels[((i + 22) * ART_SIZE) + j]);
            }
        }

        writer.Flush();

        return stream.ToArray();
    }

    /// <summary>Wrapping bilinear sample, so the diamond keeps the seamless texture seamless.</summary>
    private static (byte, byte, byte, byte) SampleBilinear(Image<Rgba32> image, float x, float y)
    {
        int w = image.Width, h = image.Height;

        float fx = x - 0.5f;
        float fy = y - 0.5f;

        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        float tx = fx - x0;
        float ty = fy - y0;

        int x1 = ((x0 + 1) % w + w) % w;
        int y1 = ((y0 + 1) % h + h) % h;
        x0 = (x0 % w + w) % w;
        y0 = (y0 % h + h) % h;

        Rgba32 p00 = image[x0, y0], p10 = image[x1, y0], p01 = image[x0, y1], p11 = image[x1, y1];

        float Mix(float a, float b, float c, float d)
        {
            return ((a * (1 - tx)) + (b * tx)) * (1 - ty) + (((c * (1 - tx)) + (d * tx)) * ty);
        }

        return (
            (byte)Math.Clamp(Mix(p00.R, p10.R, p01.R, p11.R), 0, 255),
            (byte)Math.Clamp(Mix(p00.G, p10.G, p01.G, p11.G), 0, 255),
            (byte)Math.Clamp(Mix(p00.B, p10.B, p01.B, p11.B), 0, 255),
            (byte)Math.Clamp(Mix(p00.A, p10.A, p01.A, p11.A), 0, 255)
        );
    }

    private static void WritePreview(string previewDir, string name, ushort[] pixels)
    {
        Directory.CreateDirectory(previewDir);

        using var preview = new Image<Rgba32>(ART_SIZE, ART_SIZE);

        for (int y = 0; y < ART_SIZE; y++)
        {
            for (int x = 0; x < ART_SIZE; x++)
            {
                ushort color = pixels[(y * ART_SIZE) + x];
                bool inDiamond = IsInDiamond(x, y);

                preview[x, y] = inDiamond
                    ? new Rgba32((byte)(((color >> 10) & 0x1F) << 3), (byte)(((color >> 5) & 0x1F) << 3), (byte)((color & 0x1F) << 3), 255)
                    : new Rgba32(0, 0, 0, 0);
            }
        }

        preview.Save(Path.Combine(previewDir, name.Replace(' ', '_') + ".png"));
    }

    /// <summary>
    /// Saydam serpme karosu: dokunun kendi alfasını koruyan 44x44 elmas. Mul dosyalarındaki land art
    /// 16 bit ve saydamlık taşıyamaz, bu yüzden serpme katmanı PNG olarak taşınır ve istemci onu
    /// zeminin üstüne çizer.
    /// </summary>
    /// <summary>
    /// Serpinti dokusundan dolu bir kare kırpar. Doku boşluklu olduğu için rastgele seçilen yerin gerçekten
    /// taş taşıdığı denetlenir; taşımıyorsa başka bir yer denenir. Ayna ve çeyrek dönüşler aynı dokudan
    /// birbirine benzemeyen karolar çıkmasını sağlar.
    /// </summary>
    private static Image<Rgba32> PickScatterCrop(Image<Rgba32> image, int size, Random rng)
    {
        int maxX = Math.Max(0, image.Width - size);
        int maxY = Math.Max(0, image.Height - size);
        int bestX = 0, bestY = 0;
        double bestCover = -1.0;

        for (int attempt = 0; attempt < 240; attempt++)
        {
            int ox = maxX > 0 ? rng.Next(maxX + 1) : 0;
            int oy = maxY > 0 ? rng.Next(maxY + 1) : 0;
            double cover = Coverage(image, ox, oy, size);

            if (cover > bestCover)
            {
                bestCover = cover;
                bestX = ox;
                bestY = oy;
            }

            if (cover >= 0.12)
            {
                break;
            }
        }

        int flip = rng.Next(4);
        int ox2 = bestX, oy2 = bestY;

        return image.Clone(c =>
        {
            c.Crop(new Rectangle(ox2, oy2, size, size));

            if ((flip & 1) != 0)
            {
                c.Flip(FlipMode.Horizontal);
            }

            if ((flip & 2) != 0)
            {
                c.Flip(FlipMode.Vertical);
            }
        });
    }

    /// <summary>Bir karenin ne kadarının gerçekten görünür olduğu (0..1).</summary>
    private static double Coverage(Image<Rgba32> image, int ox, int oy, int size)
    {
        int step = Math.Max(1, size / 24);
        int seen = 0, total = 0;

        for (int y = oy; y < oy + size; y += step)
        {
            for (int x = ox; x < ox + size; x += step)
            {
                ++total;

                if (image[x, y].A > 16)
                {
                    ++seen;
                }
            }
        }

        return total == 0 ? 0.0 : (double)seen / total;
    }

    private static Image<Rgba32> BuildOverlaySprite(Image<Rgba32> texture)
    {
        int sampleSize = Math.Clamp(SampleSize, 8, 256);

        using Image<Rgba32> source = texture.Clone(c =>
        {
            c.Resize(new ResizeOptions
            {
                Size = new Size(sampleSize, sampleSize),
                Sampler = KnownResamplers.Box,
                Mode = ResizeMode.Stretch
            });

            if (SharpenAmount > 0f)
            {
                c.GaussianSharpen(SharpenAmount);
            }
        });

        var result = new Image<Rgba32>(ART_SIZE, ART_SIZE);

        for (int y = 0; y < ART_SIZE; y++)
        {
            for (int x = 0; x < ART_SIZE; x++)
            {
                if (!IsInDiamond(x, y))
                {
                    result[x, y] = new Rgba32(0, 0, 0, 0);

                    continue;
                }

                float sx = (x + 0.5f - 22f) / 22f;
                float sy = (y + 0.5f) / 22f;
                float u = (sy + sx) * 0.5f;
                float v = (sy - sx) * 0.5f;

                u -= MathF.Floor(u);
                v -= MathF.Floor(v);

                (byte r, byte g, byte b, byte a) = SampleBilinear(source, u * sampleSize, v * sampleSize);

                result[x, y] = new Rgba32(r, g, b, a);
            }
        }

        return result;
    }

    private static bool IsInDiamond(int x, int y)
    {
        if (y < 22)
        {
            int start = 22 - (y + 1);

            return x >= start && x < start + ((y + 1) << 1);
        }

        int i = y - 22;

        return x >= i && x < i + ((22 - i) << 1);
    }

    private static void WriteIdxEntry(byte[] idx, int index, int lookup, int length, int extra)
    {
        int offset = index * 12;
        BitConverter.GetBytes(lookup).CopyTo(idx, offset);
        BitConverter.GetBytes(length).CopyTo(idx, offset + 4);
        BitConverter.GetBytes(extra).CopyTo(idx, offset + 8);
    }

    // ---------- helpers ----------

    private static void Backup(string dir, string file)
    {
        string source = Path.Combine(dir, file);
        string target = source + ".bak_terrain";

        if (!File.Exists(target))
        {
            File.Copy(source, target);
            Console.WriteLine($"  backup: {Path.GetFileName(target)}");
        }
    }


    /// <summary>Cuts the piece (px, py) out of an N x N grid over the texture.</summary>
    private static Image<Rgba32> CropPiece(Image<Rgba32> image, int px, int py, int split)
    {
        if (split <= 1)
        {
            return image.Clone();
        }

        int w = image.Width / split;
        int h = image.Height / split;

        return image.Clone(c => c.Crop(new Rectangle(px * w, py * h, w, h)));
    }

    private static string MakeSetName(string fileName)
    {
        string name = fileName;
        int dash = name.IndexOf('-');

        if (dash > 0 && int.TryParse(name.Substring(0, dash), out _))
        {
            name = name.Substring(dash + 1);
        }

        if (name.EndsWith("-raw", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(0, name.Length - 4);
        }

        return name.Replace('-', ' ').Replace('_', ' ').Trim();
    }

    private static string MakePieceName(string setName, int index)
    {
        // tiledata names are 20 characters, so the set name is shortened to leave room for the piece number.
        string shortName = setName.Length > 16 ? setName.Substring(0, 16) : setName;

        return $"{shortName} {index:X2}";
    }

    private static string MakeTileName(string fileName)
    {
        // "01-black-irregular-cobblestone" -> "black irregular cobb" (tiledata names are 20 characters)
        string name = fileName;
        int dash = name.IndexOf('-');

        if (dash > 0 && int.TryParse(name.Substring(0, dash), out _))
        {
            name = name.Substring(dash + 1);
        }

        name = name.Replace('-', ' ').Replace('_', ' ').Trim();

        return name.Length > 19 ? name.Substring(0, 19) : name;
    }

    private static string GetArg(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasFlag(string[] args, string key)
    {
        return args.Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
    }

    private static int ParseId(string text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        text = text.Trim();

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.Parse(text.Substring(2), System.Globalization.NumberStyles.HexNumber);
        }

        return int.Parse(text);
    }
}
