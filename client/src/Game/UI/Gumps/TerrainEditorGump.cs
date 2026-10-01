// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Input;
using ClassicUO.Network;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.UI.Gumps
{
    /// <summary>
    /// MyShard zemin editörü. Fırça vuruşları sunucuya gider (0x12 alt komut 0xCD), sunucu değişikliği
    /// saklar ve değişen harita bloklarını canlı zemin modundaki istemcilere yollar; yani dünya herkes
    /// için değişir ve yeniden başlatmadan sonra da kalır. Yetki sunucu tarafında kontrol edilir (GM).
    /// </summary>
    public class TerrainEditorGump : Gump
    {
        public enum TerrainTool
        {
            Paint = 0,      // zemin dokusunu değiştirir
            SetZ = 1,       // mutlak yüksekliğe çeker
            Raise = 2,      // yüksekliğe adım ekler
            Smooth = 3,     // yükseklikleri ve kenarları eritir
            PaintZ = 4,     // doku + yükseklik (arayüzde yok)
            Revert = 5,     // özgün haritaya döndürür
            Scatter = 6,    // serpme katmanı: zeminin ÜSTÜNE kum/çakıl/taş
            ScatterErase = 7, // serpme katmanını silme
            Decor = 90,     // nesne serpme (ağaç, kaya, moloz)
            DecorErase = 91 // serpilen nesneleri silme
        }

        public enum BrushShape
        {
            Round = 0, Hard = 1, Square = 2,
            LineNS = 3, LineWE = 4, LineNE = 5, LineNW = 6,
            Ring = 7, Star4 = 8, Star8 = 9, Dot = 10, Speckle = 11, Organic = 12
        }

        private sealed class ToolSettings
        {
            public int Radius = 1;
            public int Softness = 50;
            public bool Square;
            public int Flow = 6;
            public int Value;
            public BrushShape Shape = BrushShape.Round;
        }

        private sealed class TileSet
        {
            public ushort BaseId;
            public int Size;
            public string Name;
        }

        // ---- ölçüler ----
        private const int W = 376;
        private const int H_FULL = 790;      // palet açıkken (BOYA / SERPME)
        // not: dünya geneli yumuşatma ayarları kaldırıldığı için pencere kısaldı
        private const int H_COMPACT = 470;   // diğer araçlarda (palet yok)
        private const int HEADER_H = 44;
        private const int PAD = 12;
        private const int SHAPE_QTY = 13;
        private const int SHAPE_COLUMNS = 7;
        private const int SHAPE_CELL = 34;
        private const int PALETTE_COLUMNS = 4;   // kaydırma çubuğu için bir sütunluk yer kalsın
        private const int PALETTE_ROWS = 3;
        private const int PALETTE_CELL = 84;

        // ---- renkler (MyShard panel dili) ----
        private static readonly Color PanelBg = new Color(16, 18, 24);
        private static readonly Color HeaderBg = new Color(20, 23, 30);
        private static readonly Color CardBg = new Color(24, 28, 37);
        private static readonly Color CellBg = new Color(12, 13, 18);
        private static readonly Color Divider = new Color(38, 40, 49);
        private static readonly Color Gold = new Color(228, 198, 120);
        private static readonly Color BtnFill = new Color(38, 41, 51);
        private static readonly Color BtnBorder = new Color(74, 78, 92);
        private static readonly Color BtnHover = new Color(52, 57, 70);
        private static readonly Color SelFill = new Color(54, 48, 26);

        // ---- paylaşılan durum (oyun sahnesi okur) ----
        public static bool IsEditing { get; private set; }
        public static TerrainTool CurrentTool { get; private set; } = TerrainTool.Paint;
        public static ushort TileId { get; private set; } = 0x0003;
        public static int SetSize { get; private set; } = 1;

        // Serpme katmanının seçili dokusu zeminden ayrı tutulur: kum seçmek döşediğin taşı unutturmasın.
        public static ushort ScatterId { get; private set; } = 0x3300;
        public static int ScatterVariants { get; private set; }

        public static int BrushRadius => Current.Radius;
        public static int EdgeSoftness => Current.Softness;
        public static bool SquareBrush => Current.Square;
        public static int ZValue => Current.Value;
        public static BrushShape Shape => Current.Shape;

        private static readonly Dictionary<TerrainTool, ToolSettings> _settings = new Dictionary<TerrainTool, ToolSettings>
        {
            { TerrainTool.Paint,  new ToolSettings { Radius = 1, Softness = 50, Flow = 6 } },
            { TerrainTool.SetZ,   new ToolSettings { Radius = 1, Softness = 0,  Flow = 4, Value = 0 } },
            { TerrainTool.Raise,  new ToolSettings { Radius = 2, Softness = 70, Flow = 6, Value = 2 } },
            { TerrainTool.Smooth, new ToolSettings { Radius = 2, Softness = 40, Flow = 8, Value = 100 } },
            { TerrainTool.Revert, new ToolSettings { Radius = 2, Softness = 0,  Flow = 6 } },
            { TerrainTool.Scatter,new ToolSettings { Radius = 2, Softness = 0,  Flow = 6, Value = 45 } },
            { TerrainTool.Decor,  new ToolSettings { Radius = 3, Softness = 0,  Flow = 3, Value = 25 } }
        };

        private static ushort[] CurrentSource =>
            CurrentTool == TerrainTool.Scatter ? _scatterTiles : (_showLegacy ? _legacyTiles : _myTiles);

        private static ToolSettings Current => _settings[_settings.ContainsKey(CurrentTool) ? CurrentTool : TerrainTool.Paint];

        private static uint _lastApplyTime;
        private static readonly Random _strokeSeed = new Random();
        private static TerrainEditorGump _instance;
        private static ushort[] _myTiles;       // kendi içe aktardığımız setler
        private static ushort[] _legacyTiles;   // UO'nun kendi zemin karoları
        private static ushort[] _scatterTiles;  // serpme katmanı dokuları (kum, çakıl, taş)
        private static bool _showLegacy;        // palette hangi kaynak açık
        private static Dictionary<ushort, TileSet> _tileSets;
        private static readonly Dictionary<ushort, Texture2D> _thumbs = new Dictionary<ushort, Texture2D>();

        private ushort[] _filteredTiles;
        private int _paletteRow;
        private ModernScrollBar _paletteScroll;

        private readonly Dictionary<TerrainTool, UiButton> _toolButtons = new Dictionary<TerrainTool, UiButton>();
        private readonly List<BrushShapeButton> _shapeButtons = new List<BrushShapeButton>();
        private readonly LandTileButton[] _paletteButtons = new LandTileButton[PALETTE_COLUMNS * PALETTE_ROWS];
        private readonly List<Control> _paletteOnly = new List<Control>();

        private Stepper _brushStep, _softStep, _flowStep, _valueStep;
        private Checkbox _squareBox, _decorEraseBox, _scatterEraseBox;
        private StbTextBox _decorInput;
        public static ushort DecorItemId { get; private set; } = 0x0CE0;   // varsayılan: küçük ağaç
        private StbTextBox _tileInput, _searchInput;
        private Label _statusLabel, _shapeLabel;
        private UiButton _saveButton, _undoButton, _resetButton, _closeButton, _mineTab, _legacyTab;
        private Control _footerAnchor;

        public TerrainEditorGump(World world) : base(world, 0, 0)
        {
            CanMove = true;
            AcceptMouseInput = true;
            CanCloseWithRightClick = true;
            WantUpdateSize = false;
            Width = W;
            Height = H_FULL;
            X = 60;
            Y = 60;

            BuildTileList();
            _filteredTiles = CurrentSource;

            Build();

            IsEditing = true;
            _instance = this;

            ApplyBlendFromPaint();

            SelectTool(CurrentTool);
        }

        private void Build()
        {
            // --- başlık ---
            Add(new Label("ZEMİN EDİTÖRÜ", true, 0x0481, font: 1) { X = PAD, Y = 14 });

            var headerClose = new UiButton(W - PAD - 26, 10, 26, 24, "X");
            headerClose.MouseUp += (s, e) => Dispose();
            Add(headerClose);

            // --- araç sekmeleri ---
            int y = HEADER_H + 10;
            AddToolButton(PAD, y, 66, "BOYA", TerrainTool.Paint);
            AddToolButton(PAD + 70, y, 82, "YÜKSEKLİK", TerrainTool.SetZ);
            AddToolButton(PAD + 156, y, 64, "KALDIR", TerrainTool.Raise);
            AddToolButton(PAD + 224, y, 74, "YUMUŞAT", TerrainTool.Smooth);
            AddToolButton(PAD + 300, y, 46, "ESKİ", TerrainTool.Revert);

            y += 30;
            AddToolButton(PAD, y, 70, "NESNE", TerrainTool.Decor);
            _decorEraseBox = new Checkbox(0x00D2, 0x00D3, "Silgi", 1, 0x0481, true) { X = PAD + 78, Y = y + 2 };
            _decorEraseBox.SetTooltip("Açıkken NESNE fırçası, daha önce editörle serpilmiş nesneleri siler.");
            Add(_decorEraseBox);

            _decorInput = new StbTextBox(0xFF, maxWidth: 70, hue: 0x0481) { X = PAD + 150, Y = y + 3, Width = 70, Height = 18 };
            _decorInput.SetText($"0x{DecorItemId:X4}");
            _decorInput.SetTooltip("Serpilecek nesnenin grafik id'si (ör. 0x0CE0 ağaç, 0x1363 taş).");
            _decorInput.TextChanged += (s, e) =>
            {
                if (TryParseNumber(_decorInput.Text, out int value) && value > 0 && value <= 0xFFFF)
                {
                    DecorItemId = (ushort)value;
                }
            };
            Add(_decorInput);

            Add(new Label("nesne id", true, 0x0481) { X = PAD + 226, Y = y + 4 });

            y += 30;
            AddToolButton(PAD, y, 76, "SERPME", TerrainTool.Scatter);
            _scatterEraseBox = new Checkbox(0x00D2, 0x00D3, "Silgi", 1, 0x0481, true) { X = PAD + 84, Y = y + 2 };
            _scatterEraseBox.SetTooltip("Açıkken SERPME fırçası, serpilmiş kum/çakıl/taşı siler. Altındaki zemine dokunmaz.");
            Add(_scatterEraseBox);

            Add(new Label("zeminin üstüne serper", true, 0x0481) { X = PAD + 156, Y = y + 6 });

            // --- fırça kartı ---
            y += 40;
            _brushStep = AddStepper(y, "Fırça", 0, 16, () => Current.Radius, v => Current.Radius = v,
                v => $"{(v * 2) + 1}x{(v * 2) + 1}");
            _brushStep.SetTooltip("Fırçanın kapladığı alan. 1x1 tek kare, 33x33 geniş alan. Her sekmenin kendi fırça boyutu vardır.");
            y += 30;
            _softStep = AddStepper(y, "Kenar yumuşatma", 0, 100, () => Current.Softness, v => { Current.Softness = v; ApplyBlendFromPaint(); }, v => $"%{v}", 5);
            _softStep.SetTooltip("Kenarın ne kadar yumuşayacağı. %0 keskin kenar. Hem fırçanın kenarındaki kareleri seyreltir, hem de döşenen alanın kenarında komşu zeminin yumuşak geçişini ayarlar.");
            y += 30;
            _flowStep = AddStepper(y, "Akış", 1, 12, () => Current.Flow, v => Current.Flow = v, v => $"{v}/sn");
            _flowStep.SetTooltip("Fare basılı tutulurken saniyede kaç kez uygulanacağı. YÜKSEKLİK, KALDIR ve YUMUŞAT için geçerli; BOYA aynı kareyi tekrar boyamaz.");
            y += 30;
            _valueStep = AddStepper(y, "Değer", -128, 127, () => Current.Value, v => Current.Value = v, v => v.ToString());
            _valueStep.SetTooltip("YÜKSEKLİK: zeminin çekileceği hedef yükseklik. KALDIR: her vuruşta eklenecek adım (eksi değer kazar). YUMUŞAT: yumuşatmanın gücü.");

            y += 34;
            _squareBox = new Checkbox(0x00D2, 0x00D3, "Kare fırça", 1, 0x0481, true) { X = PAD + 4, Y = y, IsChecked = Current.Square };
            _squareBox.SetTooltip("Fırçayı daire yerine kare yapar. Düz kenarlı alanlar için.");
            _squareBox.ValueChanged += (s, e) => Current.Square = _squareBox.IsChecked;
            Add(_squareBox);

            // --- şekil kartı ---
            y += 32;
            _shapeLabel = new Label("Fırça şekli", true, 0x0481) { X = PAD + 4, Y = y };
            Add(_shapeLabel);

            y += 22;

            for (int i = 0; i < SHAPE_QTY; i++)
            {
                var shapeButton = new BrushShapeButton(
                    PAD + 4 + ((i % SHAPE_COLUMNS) * SHAPE_CELL),
                    y + ((i / SHAPE_COLUMNS) * SHAPE_CELL),
                    (BrushShape)i
                );

                shapeButton.SetTooltip(ShapeName((BrushShape)i));
                shapeButton.MouseUp += (s, e) =>
                {
                    Current.Shape = ((BrushShapeButton)s).Shape;
                    UpdateTexts();
                };

                _shapeButtons.Add(shapeButton);
                Add(shapeButton);
            }


            y += (SHAPE_CELL * 2) + 14;
            // --- palet kaynağı: bizim dokularımız / UO'nun kendi karoları ---
            y += 36;
            _mineTab = new UiButton(PAD + 4, y, 96, 24, "MYSHARD") { IsSelected = !_showLegacy };
            _mineTab.MouseUp += (s, e) => SetPaletteSource(false);
            AddPaletteOnly(_mineTab);

            _legacyTab = new UiButton(PAD + 104, y, 96, 24, "LEGACY") { IsSelected = _showLegacy };
            _legacyTab.MouseUp += (s, e) => SetPaletteSource(true);
            AddPaletteOnly(_legacyTab);

            // --- palet ---
            y += 32;
            AddPaletteOnly(new Label("Zemin", true, 0x0481) { X = PAD + 4, Y = y });

            _tileInput = new StbTextBox(0xFF, maxWidth: 70, hue: 0x0481) { X = PAD + 52, Y = y, Width = 70, Height = 18 };
            _tileInput.SetText($"0x{TileId:X4}");
            _tileInput.TextChanged += (s, e) =>
            {
                if (TryParseNumber(_tileInput.Text, out int value) && value >= 0 && value <= 0x3FFF)
                {
                    TileId = (ushort)value;
                    SetSize = (_tileSets != null && _tileSets.TryGetValue((ushort)value, out TileSet typed)) ? typed.Size : 1;
                }
            };
            AddPaletteOnly(_tileInput);

            AddPaletteOnly(new Label("Ara", true, 0x0481) { X = PAD + 132, Y = y });

            _searchInput = new StbTextBox(0xFF, maxWidth: 110, hue: 0x0481) { X = PAD + 164, Y = y, Width = 110, Height = 18 };
            _searchInput.TextChanged += (s, e) => ApplyFilter(_searchInput.Text);
            AddPaletteOnly(_searchInput);


            y += 28;

            // Sayfa düğmeleri yerine kaydırma çubuğu: binlerce karo arasında ileri geri tıklamak yorucuydu.
            _paletteScroll = new ModernScrollBar(PAD + 4 + (PALETTE_COLUMNS * PALETTE_CELL) + 4, y, PALETTE_ROWS * PALETTE_CELL)
            {
                MinValue = 0,
                MaxValue = 0
            };
            _paletteScroll.ValueChanged += (s, e) =>
            {
                if (_paletteScroll.Value != _paletteRow)
                {
                    _paletteRow = _paletteScroll.Value;
                    RefreshPalette(false);
                }
            };
            AddPaletteOnly(_paletteScroll);

            for (int row = 0; row < PALETTE_ROWS; row++)
            {
                for (int col = 0; col < PALETTE_COLUMNS; col++)
                {
                    var button = new LandTileButton(PAD + 2 + (col * PALETTE_CELL), y + (row * PALETTE_CELL), 0xFFFF);

                    button.MouseUp += (s, e) =>
                    {
                        var clicked = (LandTileButton)s;

                        if (clicked.Graphic != 0xFFFF)
                        {
                            SelectTile(clicked.Graphic);
                        }
                    };

                    _paletteButtons[(row * PALETTE_COLUMNS) + col] = button;
                    AddPaletteOnly(button);
                }
            }

            // --- alt bar ---
            _statusLabel = new Label(string.Empty, true, 0x0481, maxwidth: W - (PAD * 2)) { X = PAD, Y = H_FULL - 68 };
            Add(_statusLabel);

            _saveButton = new UiButton(PAD, H_FULL - 34, 78, 24, "KAYDET");
            _saveButton.MouseUp += (s, e) =>
            {
                AsyncNetClient.Socket.Send_TerrainCommand(1);
                SetStatus("Zemin kaydediliyor...");
            };
            Add(_saveButton);

            _undoButton = new UiButton(PAD + 84, H_FULL - 34, 78, 24, "GERİ");
            _undoButton.MouseUp += (s, e) =>
            {
                AsyncNetClient.Socket.Send_TerrainCommand(0);
                SetStatus("Son fırça geri alınıyor...");
            };
            Add(_undoButton);

            _resetButton = new UiButton(PAD + 168, H_FULL - 34, 86, 24, "SIFIRLA");
            _resetButton.MouseUp += (s, e) => ResetSettings();
            Add(_resetButton);

            _closeButton = new UiButton(W - PAD - 78, H_FULL - 34, 78, 24, "KAPAT");
            _closeButton.MouseUp += (s, e) => Dispose();
            Add(_closeButton);

            RefreshPalette();
        }

        private void AddPaletteOnly(Control c)
        {
            _paletteOnly.Add(c);
            Add(c);
        }

        private void AddToolButton(int x, int y, int width, string text, TerrainTool tool)
        {
            var button = new UiButton(x, y, width, 26, text) { IsSelected = CurrentTool == tool };
            button.MouseUp += (s, e) => SelectTool(tool);
            Add(button);
            _toolButtons[tool] = button;
        }

        /// <summary>
        /// Bir ayar satırı: etiket, eksi/artı okları ve kaydırıcı. Oklar tam sayı adımı verir, kaydırıcı
        /// kabaca sürüklemek içindir - tek başına kaydırıcıyla istenen rakamı tutturmak zordu.
        /// </summary>
        private Stepper AddStepper(int y, string label, int min, int max, Func<int> get, Action<int> set, Func<int, string> format, int step = 1)
        {
            var stepper = new Stepper(PAD + 4, y, W - (PAD * 2) - 8, label, min, max, get, set, format, step, UpdateTexts);
            Add(stepper);

            return stepper;
        }

        public override void Dispose()
        {
            if (_instance == this)
            {
                _instance = null;
                IsEditing = false;
            }

            base.Dispose();
        }

        public static void Toggle(World world)
        {
            if (_instance != null && !_instance.IsDisposed)
            {
                _instance.Dispose();

                return;
            }

            UIManager.Add(new TerrainEditorGump(world));
        }

        // ---------- fırça ----------

        public static void ApplyAt(World world, ushort x, ushort y)
        {
            if (!IsEditing)
            {
                return;
            }

            // Kenar yumuşatma için aynı dokunun zayıf karışımları (… 65 / … 35). Varsa fırçanın kenarı
            // bunlarla kademelenir; yoksa sunucu karoları serpiştirerek kenarı kırar.
            (ushort midId, int midSet) = FindVariant(TileId, 65);
            (ushort lowId, int lowSet) = FindVariant(TileId, 35);

            if (CurrentTool == TerrainTool.Scatter)
            {
                // Serpme katmanı: zemin paketiyle aynı yoldan gider ama sunucu onu ayrı katmana yazar.
                // z alanı yoğunluğu, setSize çeşitleme sayısını, variantMid de bu vuruşun tohumunu taşır;
                // tohum her vuruşta değiştiği için fırçayı gezdirdikçe alan hep başka karelerden dolar.
                if (ScatterVariants <= 0)
                {
                    ScatterVariants = Managers.TerrainOverlayManager.VariantQty(ScatterId);
                }

                if (ScatterVariants <= 0)
                {
                    _instance?.SetStatus("Serpme: bu doku için saydam karo bulunamadı.");

                    return;
                }

                bool erasing = _instance != null && _instance._scatterEraseBox.IsChecked;

                AsyncNetClient.Socket.Send_TerrainEdit(
                    (byte)(erasing ? TerrainTool.ScatterErase : TerrainTool.Scatter),
                    x,
                    y,
                    ScatterId,
                    (byte)Current.Radius,
                    (byte)(Current.Square ? 0x01 : 0x00),
                    (sbyte)(byte)Math.Clamp(Current.Value, 1, 100),
                    0,
                    (byte)Math.Clamp(ScatterVariants, 1, 64),
                    (byte)Current.Shape,
                    (ushort)_strokeSeed.Next(1, ushort.MaxValue),
                    1,
                    0,
                    1
                );

                _lastApplyTime = Time.Ticks;
                _instance?.SetStatus($"SERPME {(erasing ? "sil" : "serp")} 0x{ScatterId:X4} -> {x},{y}");

                return;
            }

            if (CurrentTool == TerrainTool.Decor)
            {
                // Dekorasyon fırçası ayrı bir paket: zemin değil, nesne serpiyor.
                AsyncNetClient.Socket.Send_TerrainDecor(
                    x,
                    y,
                    DecorItemId,
                    (byte)Current.Radius,
                    (byte)((Current.Square ? 0x01 : 0x00) | (_instance != null && _instance._decorEraseBox.IsChecked ? 0x02 : 0x00)),
                    (byte)Math.Clamp(Current.Value, 1, 100),
                    (byte)Current.Shape
                );

                _lastApplyTime = Time.Ticks;
                _instance?.SetStatus($"NESNE {(_instance._decorEraseBox.IsChecked ? "sil" : "serp")} 0x{DecorItemId:X4} → {x},{y}");

                return;
            }

            AsyncNetClient.Socket.Send_TerrainEdit(
                (byte)CurrentTool,
                x,
                y,
                TileId,
                (byte)Current.Radius,
                (byte)(Current.Square ? 0x01 : 0x00),
                (sbyte)Math.Max(-128, Math.Min(127, Current.Value)),
                (byte)Current.Softness,
                (byte)SetSize,
                (byte)Current.Shape,
                midId,
                (byte)midSet,
                lowId,
                (byte)lowSet
            );

            _lastApplyTime = Time.Ticks;

            int side = (Current.Radius * 2) + 1;
            _instance?.SetStatus($"{ToolName(CurrentTool)} {side}x{side} {ShapeShortName(Current.Shape)} → {x},{y}");
        }

        /// <summary>
        /// Düğme basılı tutulurken fırçanın akmaya devam etmesi. Boyama ve eski haline döndürme aynı kareye
        /// tekrar uygulandığında hiçbir şey değiştirmez, o yüzden onlar tekrarlanmaz: boşuna paket gitmesin.
        /// </summary>
        public static void ApplyFlow(World world, ushort x, ushort y)
        {
            if (!IsEditing || CurrentTool == TerrainTool.Paint || CurrentTool == TerrainTool.Revert)
            {
                return;
            }

            uint interval = (uint)(1000 / Math.Max(1, Current.Flow));

            if (Time.Ticks - _lastApplyTime < interval)
            {
                return;
            }

            ApplyAt(world, x, y);
        }

        // ---------- araç / ayar ----------

        private void SelectTool(TerrainTool tool)
        {
            CurrentTool = tool;

            foreach (KeyValuePair<TerrainTool, UiButton> pair in _toolButtons)
            {
                pair.Value.IsSelected = pair.Key == tool;
            }

            _squareBox.IsChecked = Current.Square;

            bool isPaint = tool == TerrainTool.Paint;
            bool isScatter = tool == TerrainTool.Scatter;
            bool isDecor = tool == TerrainTool.Decor;
            bool hasValue = tool == TerrainTool.SetZ || tool == TerrainTool.Raise || tool == TerrainTool.Smooth || isDecor || isScatter;

            _valueStep.IsVisible = hasValue;
            _scatterEraseBox.IsVisible = isScatter;
            _softStep.IsVisible = isScatter == false;   // serpmenin kenar yumuşatması yok, yoğunluğu var

            foreach (Control c in _paletteOnly)
            {
                c.IsVisible = isPaint || isScatter;
            }

            // Zemin kaynağı sekmeleri yalnızca zemin boyarken anlamlı.
            _mineTab.IsVisible = isPaint;
            _legacyTab.IsVisible = isPaint;

            if (isScatter)
            {
                _tileInput.SetText($"0x{ScatterId:X4}");
            }

            ApplyFilter(_searchInput.Text);

            int height = (isPaint || isScatter) ? H_FULL : H_COMPACT;
            Height = height;
            _statusLabel.Y = height - 68;
            _saveButton.Y = height - 34;
            _undoButton.Y = height - 34;
            _resetButton.Y = height - 34;
            _closeButton.Y = height - 34;

            UpdateTexts();

            SetStatus(tool switch
            {
                TerrainTool.Paint => "BOYA: seçili zemini döşer.",
                TerrainTool.SetZ => "YÜKSEKLİK: zemini hedef yüksekliğe çeker.",
                TerrainTool.Raise => "KALDIR: yüksekliğe adım ekler (eksi değer kazar).",
                TerrainTool.Smooth => "YUMUŞAT: yükseklikleri ve zemin kenarlarını eritir.",
                TerrainTool.Revert => "ESKİ: fırçanın altını özgün haritaya döndürür.",
                TerrainTool.Scatter => "SERPME: kum/çakıl/taşı zeminin ÜSTÜNE serper, altındaki döşemeyi silmez.",
                TerrainTool.Decor => "NESNE: seçili grafiği alana serper (Değer = yoğunluk). Silgi açıkken editörle serpilenleri siler.",
                _ => string.Empty
            });
        }

        private void UpdateTexts()
        {
            _brushStep?.Refresh();
            _softStep?.Refresh();
            _flowStep?.Refresh();

            if (_valueStep != null)
            {
                _valueStep.Label = CurrentTool switch
                {
                    TerrainTool.SetZ => "Hedef yükseklik",
                    TerrainTool.Raise => "Adım",
                    TerrainTool.Smooth => "Güç",
                    TerrainTool.Scatter => "Yoğunluk",
                    TerrainTool.Decor => "Yoğunluk",
                    _ => "Değer"
                };

                _valueStep.Refresh();
            }
        }

        public override void OnMouseWheel(MouseEventType delta)
        {
            // Palet tekerlekle de kaysın: çubuğu sürüklemeden gezmek daha rahat.
            if (CurrentTool == TerrainTool.Paint && _paletteScroll != null && _paletteScroll.IsVisible)
            {
                _paletteRow += delta == MouseEventType.WheelScrollUp ? -1 : 1;
                RefreshPalette(false);

                return;
            }

            base.OnMouseWheel(delta);
        }

        /// <summary>
        /// Döşenen zeminin kenarındaki yumuşak geçiş, BOYA sekmesinin kenar yumuşatma ayarından sürülür:
        /// ayrı bir açma/kapama yok, sayıyı sıfırlarsan geçiş de kalkar.
        /// </summary>
        private static void ApplyBlendFromPaint()
        {
            int softness = _settings[TerrainTool.Paint].Softness;

            Managers.TerrainBlendManager.Enabled = softness > 0;
            Managers.TerrainBlendManager.Reach = Math.Clamp(softness / 100f, 0.05f, 1f);
            Managers.TerrainBlendManager.Clear();
        }

        private void SetStatus(string text)
        {
            _statusLabel.Text = text;
        }

        private void SelectTile(ushort graphic)
        {
            if (CurrentTool == TerrainTool.Scatter)
            {
                ScatterId = graphic;
                ScatterVariants = Managers.TerrainOverlayManager.VariantQty(graphic);
                _tileInput.SetText($"0x{graphic:X4}");
                SetStatus($"Serpme: {TileName(graphic)} (0x{graphic:X4}, {ScatterVariants} çeşitleme)");

                return;
            }

            TileId = graphic;
            SetSize = (_tileSets != null && _tileSets.TryGetValue(graphic, out TileSet set)) ? set.Size : 1;
            _tileInput.SetText($"0x{graphic:X4}");
            SetStatus($"Seçilen zemin: {TileName(graphic)} (0x{graphic:X4})");
        }

        private void ResetSettings()
        {
            _settings[TerrainTool.Paint] = new ToolSettings { Radius = 1, Softness = 50, Flow = 6 };
            _settings[TerrainTool.SetZ] = new ToolSettings { Radius = 1, Softness = 0, Flow = 4, Value = 0 };
            _settings[TerrainTool.Raise] = new ToolSettings { Radius = 2, Softness = 70, Flow = 6, Value = 2 };
            _settings[TerrainTool.Smooth] = new ToolSettings { Radius = 2, Softness = 40, Flow = 8, Value = 100 };
            _settings[TerrainTool.Revert] = new ToolSettings { Radius = 2, Softness = 0, Flow = 6 };
            _settings[TerrainTool.Scatter] = new ToolSettings { Radius = 2, Softness = 0, Flow = 6, Value = 45 };



            ApplyBlendFromPaint();
            SelectTool(CurrentTool);
            SetStatus("Ayarlar varsayılana döndü.");
        }

        // ---------- palet ----------

        /// <summary>Palette hangi kaynağın gösterileceğini değiştirir.</summary>
        private void SetPaletteSource(bool legacy)
        {
            _showLegacy = legacy;
            _mineTab.IsSelected = !legacy;
            _legacyTab.IsSelected = legacy;
            _paletteRow = 0;
            ApplyFilter(_searchInput.Text);
            SetStatus(legacy ? "UO'nun kendi zemin karoları" : "MyShard dokuları");
        }

        private void ApplyFilter(string text)
        {
            _paletteRow = 0;

            if (string.IsNullOrWhiteSpace(text))
            {
                _filteredTiles = CurrentSource;
                RefreshPalette();

                return;
            }

            text = text.Trim();
            var list = new List<ushort>();

            if (TryParseNumber(text, out int asNumber) && asNumber >= 0 && asNumber <= 0x3FFF)
            {
                list.Add((ushort)asNumber);
            }

            foreach (ushort graphic in CurrentSource)
            {
                if (TileName(graphic).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    list.Add(graphic);
                }
            }

            _filteredTiles = list.ToArray();
            RefreshPalette();
        }

        private void RefreshPalette(bool resetScroll = true)
        {
            int totalRows = (_filteredTiles.Length + PALETTE_COLUMNS - 1) / PALETTE_COLUMNS;
            int maxRow = Math.Max(0, totalRows - PALETTE_ROWS);

            if (resetScroll)
            {
                _paletteRow = 0;
            }

            _paletteRow = Math.Clamp(_paletteRow, 0, maxRow);

            if (_paletteScroll != null)
            {
                _paletteScroll.MaxValue = maxRow;
                _paletteScroll.Value = _paletteRow;
                _paletteScroll.IsVisible = CurrentTool == TerrainTool.Paint && maxRow > 0;
            }

            int perPage = _paletteButtons.Length;
            int first = _paletteRow * PALETTE_COLUMNS;

            for (int i = 0; i < perPage; i++)
            {
                int index = first + i;
                ushort graphic = index < _filteredTiles.Length ? _filteredTiles[index] : (ushort)0xFFFF;
                _paletteButtons[i].Graphic = graphic;
                _paletteButtons[i].SetTooltip(graphic == 0xFFFF ? string.Empty : $"{TileName(graphic)}\n0x{graphic:X4}");
            }
        }

        private static void BuildTileSets()
        {
            if (_tileSets != null)
            {
                return;
            }

            _tileSets = new Dictionary<ushort, TileSet>();

            try
            {
                string path = Client.Game.UO.FileManager.GetUOFilePath("myshard_terrain_sets.txt");

                if (!System.IO.File.Exists(path))
                {
                    return;
                }

                foreach (string line in System.IO.File.ReadAllLines(path))
                {
                    string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length < 3 || !TryParseNumber(parts[0], out int baseId) || !int.TryParse(parts[1], out int size))
                    {
                        continue;
                    }

                    _tileSets[(ushort)baseId] = new TileSet { BaseId = (ushort)baseId, Size = Math.Clamp(size, 1, 16), Name = parts[2].Trim() };
                }
            }
            catch (Exception e)
            {
                Utility.Logging.Log.Warn($"Zemin editörü: doku seti listesi okunamadı: {e.Message}");
            }
        }

        /// <summary>
        /// İki ayrı kaynak: kendi içe aktardığımız doku setleri ve UO'nun kendi zemin karoları. Hepsini tek
        /// listede göstermek binlerce karoyu birbirine karıştırıyordu, bu yüzden palette sekmeli.
        /// </summary>
        private static void BuildTileList()
        {
            if (_myTiles != null)
            {
                return;
            }

            BuildTileSets();

            var mine = new List<ushort>();
            var scatter = new List<ushort>();

            foreach (TileSet set in _tileSets.Values)
            {
                // Serpme dokularının saydam karoları var: onlar zemin değil, zeminin üstüne serpilen bir
                // katman. Zemin paletinde durmasınlar, kendi sekmeleri var.
                if (Managers.TerrainOverlayManager.VariantQty(set.BaseId) > 0)
                {
                    scatter.Add(set.BaseId);

                    continue;
                }

                mine.Add(set.BaseId);
            }

            mine.Sort();
            _myTiles = mine.ToArray();

            scatter.Sort();
            _scatterTiles = scatter.ToArray();

            var legacy = new List<ushort>();

            for (int i = 0; i <= 0x3FFF; i++)
            {
                // Kendi setlerimizin parçaları burada tekrar listelenmesin.
                if (_tileSets.ContainsKey((ushort)i) || IsSetPiece((ushort)i))
                {
                    continue;
                }

                string name = Client.Game.UO.FileManager.TileData.LandData[i].Name ?? string.Empty;

                if (name.IndexOf("unused", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("no draw", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("nodraw", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                ref readonly SpriteInfo art = ref Client.Game.UO.Arts.GetLand((ushort)i);

                if (art.Texture == null)
                {
                    continue;
                }

                legacy.Add((ushort)i);
            }

            _legacyTiles = legacy.ToArray();
        }

        /// <summary>Bir karo, içe aktarılmış bir setin (ilk parçası dışındaki) parçası mı?</summary>
        private static bool IsSetPiece(ushort graphic)
        {
            foreach (TileSet set in _tileSets.Values)
            {
                if (graphic > set.BaseId && graphic < set.BaseId + (set.Size * set.Size))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The picture the texture was made from, written next to the mul files by the importer. Showing it
        /// in the palette says much more at a glance than the 44x44 tile does - a scatter overlay in
        /// particular is almost invisible as a single tile.
        /// </summary>
        private static Texture2D GetThumb(ushort graphic)
        {
            if (_thumbs.TryGetValue(graphic, out Texture2D cached))
            {
                return cached;
            }

            Texture2D texture = null;

            try
            {
                string path = Client.Game.UO.FileManager.GetUOFilePath($"myshard_terrain_thumbs/{graphic:X4}.png");

                if (System.IO.File.Exists(path))
                {
                    texture = PNGLoader.Instance.GetImageTexture(path);
                }
            }
            catch (Exception e)
            {
                Utility.Logging.Log.Warn($"Zemin editörü: {graphic:X4} önizlemesi yüklenemedi: {e.Message}");
            }

            _thumbs[graphic] = texture;

            return texture;
        }

        /// <summary>
        /// Bir setin daha zayıf karışımı: "black irregular cobblestone" için "black irregular cobblestone 65".
        /// İçe aktarıcı bu varyantları çimle harmanlayarak üretiyor; fırçanın kenarını onlarla kademelendiriyoruz.
        /// </summary>
        private static (ushort, int) FindVariant(ushort baseGraphic, int percent)
        {
            if (_tileSets == null || !_tileSets.TryGetValue(baseGraphic, out TileSet set))
            {
                return (0, 1);
            }

            string wanted = $"{set.Name} {percent}";

            foreach (TileSet candidate in _tileSets.Values)
            {
                if (string.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return (candidate.BaseId, candidate.Size);
                }
            }

            return (0, 1);
        }

        private static string TileName(ushort graphic)
        {
            if (_tileSets != null && _tileSets.TryGetValue(graphic, out TileSet set))
            {
                return set.Name;
            }

            if (graphic > 0x3FFF)
            {
                return string.Empty;
            }

            string name = Client.Game.UO.FileManager.TileData.LandData[graphic].Name;

            return string.IsNullOrEmpty(name) ? string.Empty : name;
        }

        // ---------- metinler ----------

        private static string ToolName(TerrainTool tool)
        {
            return tool switch
            {
                TerrainTool.Paint => "BOYA",
                TerrainTool.Scatter => "SERPME",
                TerrainTool.SetZ => "YÜKSEKLİK",
                TerrainTool.Raise => "KALDIR",
                TerrainTool.Smooth => "YUMUŞAT",
                TerrainTool.Revert => "ESKİ",
                _ => "?"
            };
        }

        private static string ShapeShortName(BrushShape shape)
        {
            return shape switch
            {
                BrushShape.Round => "yuvarlak",
                BrushShape.Hard => "sert",
                BrushShape.Square => "kare",
                BrushShape.LineNS => "çizgi K-G",
                BrushShape.LineWE => "çizgi B-D",
                BrushShape.LineNE => "çizgi çapraz",
                BrushShape.LineNW => "çizgi çapraz",
                BrushShape.Ring => "halka",
                BrushShape.Star4 => "yıldız 4",
                BrushShape.Star8 => "yıldız 8",
                BrushShape.Dot => "nokta",
                BrushShape.Speckle => "serpme",
                BrushShape.Organic => "organik",
                _ => string.Empty
            };
        }

        private static string ShapeName(BrushShape shape)
        {
            return shape switch
            {
                BrushShape.Round => "Yuvarlak (yumuşak)\nKenarı, kenar yumuşatma ayarına göre söner.",
                BrushShape.Hard => "Yuvarlak (sert)\nKeskin kenar, yumuşatmayı yok sayar.",
                BrushShape.Square => "Kare (sert)\nKeskin köşeli alan.",
                BrushShape.LineNS => "İnce çizgi: kuzey-güney\nYol ve patika için.",
                BrushShape.LineWE => "İnce çizgi: batı-doğu",
                BrushShape.LineNE => "İnce çizgi: çapraz (sağa yatık)",
                BrushShape.LineNW => "İnce çizgi: çapraz (sola yatık)",
                BrushShape.Ring => "Halka\nOrtası boş çember; sınır çizmek için.",
                BrushShape.Star4 => "Dört kollu yıldız",
                BrushShape.Star8 => "Sekiz kollu yıldız",
                BrushShape.Dot => "Nokta\nFırça boyutundan küçük sıkı çekirdek.",
                BrushShape.Speckle => "Serpme\nDağınık kareler; doğal kirlilik için.",
                BrushShape.Organic => "Organik\nDüzensiz kenarlı leke.",
                _ => string.Empty
            };
        }

        private static bool TryParseNumber(string text, out int value)
        {
            value = 0;

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Trim();

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out value);
            }

            return int.TryParse(text, out value);
        }

        // ---------- fırça ağırlığı (sunucudaki formülün aynısı) ----------

        public static float BrushWeight(BrushShape shape, float dx, float dy, float radius, float core, bool square, int worldX, int worldY)
        {
            if (radius <= 0f)
            {
                return dx == 0f && dy == 0f ? 1f : 0f;
            }

            float dist;

            switch (shape)
            {
                default:
                case BrushShape.Round:
                case BrushShape.Hard:
                case BrushShape.Speckle:
                case BrushShape.Organic:
                    dist = square ? Math.Max(Math.Abs(dx), Math.Abs(dy)) : MathF.Sqrt((dx * dx) + (dy * dy));

                    break;

                case BrushShape.Square:
                    dist = Math.Max(Math.Abs(dx), Math.Abs(dy));

                    break;

                case BrushShape.LineNS:
                    dist = MathF.Sqrt((dx * dx * 16f) + (dy * dy));

                    break;

                case BrushShape.LineWE:
                    dist = MathF.Sqrt((dx * dx) + (dy * dy * 16f));

                    break;

                case BrushShape.LineNE:
                {
                    float u = (dx + dy) * 0.70710678f;
                    float v = (dx - dy) * 0.70710678f;
                    dist = MathF.Sqrt((v * v * 16f) + (u * u));

                    break;
                }

                case BrushShape.LineNW:
                {
                    float u = (dx - dy) * 0.70710678f;
                    float v = (dx + dy) * 0.70710678f;
                    dist = MathF.Sqrt((v * v * 16f) + (u * u));

                    break;
                }

                case BrushShape.Ring:
                {
                    float baseDist = MathF.Sqrt((dx * dx) + (dy * dy));
                    float band = Math.Max(1f, radius * 0.3f);
                    float off = Math.Abs(baseDist - (radius * 0.7f));

                    return off >= band ? 0f : 1f - (off / band);
                }

                case BrushShape.Star4:
                {
                    float baseDist = MathF.Sqrt((dx * dx) + (dy * dy));
                    dist = baseDist + (Math.Min(Math.Abs(dx), Math.Abs(dy)) * 2f);

                    break;
                }

                case BrushShape.Star8:
                {
                    float baseDist = MathF.Sqrt((dx * dx) + (dy * dy));
                    float smallAxis = Math.Min(Math.Abs(dx), Math.Abs(dy));
                    float u = Math.Abs((dx + dy) * 0.70710678f);
                    float v = Math.Abs((dx - dy) * 0.70710678f);
                    dist = baseDist + (Math.Min(smallAxis, Math.Min(u, v)) * 2.5f);

                    break;
                }

                case BrushShape.Dot:
                    dist = MathF.Sqrt((dx * dx) + (dy * dy)) * 2.5f;

                    break;
            }

            if (dist > radius)
            {
                return 0f;
            }

            float weight = shape == BrushShape.Hard || shape == BrushShape.Square
                ? 1f
                : (dist <= core ? 1f : (radius > core ? (radius - dist) / (radius - core) : 0f));

            if (shape == BrushShape.Speckle)
            {
                weight *= Noise(worldX * 3, worldY * 3) > 0.55f ? 1f : 0f;
            }
            else if (shape == BrushShape.Organic)
            {
                weight *= 0.35f + (0.65f * Noise(worldX, worldY));
            }

            return weight;
        }

        private static float Noise(int x, int y)
        {
            uint hash = (uint)((x * 73856093) ^ (y * 19349663));
            hash ^= hash >> 13;
            hash *= 0x85EBCA6Bu;
            hash ^= hash >> 16;

            return (hash & 0xFFFFu) / 65535f;
        }

        // ---------- çizim ----------

        public override bool Draw(UltimaBatcher2D batcher, int x, int y)
        {
            StoreGump.DrawRounded(batcher, x, y, Width, Height, 12, PanelBg, Color.Transparent, 0, true, true, true, true);
            StoreGump.DrawRounded(batcher, x, y, Width, HEADER_H, 12, HeaderBg, Color.Transparent, 0, true, true, false, false);
            batcher.Draw(SolidColorTextureCache.GetTexture(Divider), new Rectangle(x + 1, y + HEADER_H, Width - 2, 1), ShaderHueTranslator.GetHueVector(0, false, 1f));

            // ayar kartı (fırça ayarları satırlarının arkası)
            StoreGump.DrawRounded(batcher, x + PAD - 4, y + HEADER_H + 48, Width - (PAD * 2) + 8, 156, 8, CardBg, Color.Transparent, 0, true, true, true, true);

            // alt bar ayracı
            batcher.Draw(SolidColorTextureCache.GetTexture(Divider), new Rectangle(x + 1, y + Height - 44, Width - 2, 1), ShaderHueTranslator.GetHueVector(0, false, 1f));

            return base.Draw(batcher, x, y);
        }

        /// <summary>Yuvarlatılmış panel düğmesi.</summary>
        private sealed class UiButton : Control
        {
            private readonly string _text;
            private readonly RenderedText _label;

            public UiButton(int x, int y, int width, int height, string text)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
                _text = text;
                AcceptMouseInput = true;
                CanMove = false;

                _label = RenderedText.Create(text, 0x0481, 1, true, align: TEXT_ALIGN_TYPE.TS_CENTER, maxWidth: width);
            }

            public bool IsSelected { get; set; }

            public override bool Draw(UltimaBatcher2D batcher, int x, int y)
            {
                Color fill = IsSelected ? SelFill : (MouseIsOver ? BtnHover : BtnFill);
                Color border = IsSelected ? Gold : BtnBorder;

                StoreGump.DrawRounded(batcher, x, y, Width, Height, 0, fill, border, IsSelected ? 1 : 0, true, true, true, true);

                _label.Draw(batcher, x, y + ((Height - _label.Height) / 2));

                return base.Draw(batcher, x, y);
            }

            public override void Dispose()
            {
                _label?.Destroy();
                base.Dispose();
            }
        }

        /// <summary>
        /// Etiket + eksi/artı + kaydırıcı. Oklar tek tek arttırır (hassas), kaydırıcı kabaca atlamak için.
        /// </summary>
        private sealed class Stepper : Control
        {
            private readonly Func<int> _get;
            private readonly Action<int> _set;
            private readonly Func<int, string> _format;
            private readonly int _min, _max, _step;
            private readonly Action _onChanged;
            private readonly Label _label;
            private readonly Label _value;
            private readonly HSliderBar _slider;

            public Stepper(int x, int y, int width, string label, int min, int max, Func<int> get, Action<int> set, Func<int, string> format, int step, Action onChanged)
            {
                X = x;
                Y = y;
                Width = width;
                Height = 22;
                _min = min;
                _max = max;
                _get = get;
                _set = set;
                _format = format;
                _step = step;
                _onChanged = onChanged;
                AcceptMouseInput = true;   // satırın üstüne gelince açıklama çıksın

                _label = new Label(label, true, 0x0481) { X = 0, Y = 2 };
                Add(_label);

                var minus = new UiButton(132, 0, 22, 20, "-");
                minus.MouseUp += (s, e) => Change(-_step);
                Add(minus);

                _value = new Label(string.Empty, true, 0x0481) { X = 158, Y = 2 };
                Add(_value);

                var plus = new UiButton(212, 0, 22, 20, "+");
                plus.MouseUp += (s, e) => Change(_step);
                Add(plus);

                _slider = new HSliderBar(242, 2, width - 242, min, max, get(), HSliderBarStyle.MetalWidgetRecessedBar);
                _slider.ValueChanged += (s, e) =>
                {
                    if (_slider.Value != _get())
                    {
                        _set(_slider.Value);
                        Refresh();
                        _onChanged?.Invoke();
                    }
                };
                Add(_slider);

                Refresh();
            }

            public string Label
            {
                get => _label.Text;
                set => _label.Text = value;
            }

            private void Change(int delta)
            {
                int next = Math.Clamp(_get() + delta, _min, _max);
                _set(next);
                Refresh();
                _onChanged?.Invoke();
            }

            public void Refresh()
            {
                int v = _get();
                _value.Text = _format(v);

                if (_slider.Value != v)
                {
                    _slider.Value = v;
                }
            }
        }

        /// <summary>Fırça şekli düğmesi: ikonu şeklin kendi ağırlık haritasından üretilir.</summary>
        private sealed class BrushShapeButton : Control
        {
            private static readonly Dictionary<BrushShape, Texture2D> _icons = new Dictionary<BrushShape, Texture2D>();

            public BrushShapeButton(int x, int y, BrushShape shape)
            {
                X = x;
                Y = y;
                Width = SHAPE_CELL - 4;
                Height = SHAPE_CELL - 4;
                Shape = shape;
                AcceptMouseInput = true;
                CanMove = false;
            }

            public BrushShape Shape { get; }

            private static Texture2D GetIcon(GraphicsDevice device, BrushShape shape)
            {
                if (_icons.TryGetValue(shape, out Texture2D cached) && cached != null && !cached.IsDisposed)
                {
                    return cached;
                }

                const int SIZE = 26;
                const float RADIUS = SIZE / 2f;

                var data = new Color[SIZE * SIZE];

                for (int y = 0; y < SIZE; y++)
                {
                    for (int x = 0; x < SIZE; x++)
                    {
                        float dx = x - RADIUS + 0.5f;
                        float dy = y - RADIUS + 0.5f;
                        float weight = BrushWeight(shape, dx, dy, RADIUS, RADIUS * 0.35f, false, x, y);
                        byte a = (byte)Math.Clamp(weight * 255f, 0, 255);

                        data[(y * SIZE) + x] = new Color((byte)(228 * weight), (byte)(198 * weight), (byte)(120 * weight), a);
                    }
                }

                var texture = new Texture2D(device, SIZE, SIZE);
                texture.SetData(data);
                _icons[shape] = texture;

                return texture;
            }

            public override bool Draw(UltimaBatcher2D batcher, int x, int y)
            {
                bool selected = Shape == TerrainEditorGump.Shape;

                StoreGump.DrawRounded(batcher, x, y, Width, Height, 0, selected ? SelFill : CellBg, selected ? Gold : Color.Transparent, selected ? 1 : 0, true, true, true, true);

                batcher.Draw(
                    GetIcon(batcher.GraphicsDevice, Shape),
                    new Rectangle(x + 2, y + 2, Width - 4, Height - 4),
                    null,
                    ShaderHueTranslator.GetHueVector(0, false, 1f)
                );

                return base.Draw(batcher, x, y);
            }
        }

        /// <summary>Palet hücresi: bir zemin karosunu gösterir.</summary>
        private sealed class LandTileButton : Control
        {
            public LandTileButton(int x, int y, ushort graphic)
            {
                X = x;
                Y = y;
                Width = PALETTE_CELL - 6;
                Height = PALETTE_CELL - 6;
                Graphic = graphic;
                AcceptMouseInput = true;
                CanMove = false;
            }

            public ushort Graphic { get; set; }

            public override bool Draw(UltimaBatcher2D batcher, int x, int y)
            {
                bool selected = Graphic != 0xFFFF && Graphic == TileId;

                StoreGump.DrawRounded(batcher, x, y, Width, Height, 0, CellBg, selected ? Gold : Color.Transparent, selected ? 1 : 0, true, true, true, true);

                if (Graphic != 0xFFFF)
                {
                    // The texture's own picture if we have it, otherwise the plain UO tile.
                    Texture2D thumb = GetThumb(Graphic);

                    if (thumb != null)
                    {
                        batcher.Draw(
                            thumb,
                            new Rectangle(x + 2, y + 2, Width - 4, Height - 4),
                            null,
                            ShaderHueTranslator.GetHueVector(0, false, 1f)
                        );
                    }
                    else
                    {
                        ref readonly SpriteInfo artInfo = ref Client.Game.UO.Arts.GetLand(Graphic);

                        if (artInfo.Texture != null)
                        {
                            batcher.Draw(
                                artInfo.Texture,
                                new Vector2(x + ((Width - 44) / 2), y + ((Height - 44) / 2)),
                                artInfo.UV,
                                ShaderHueTranslator.GetHueVector(0, false, 1f),
                                0f,
                                Vector2.Zero,
                                Vector2.One,
                                SpriteEffects.None,
                                0f
                            );
                        }
                    }
                }

                return base.Draw(batcher, x, y);
            }
        }
    }
}
