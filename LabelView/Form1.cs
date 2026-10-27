using System.Drawing.Imaging;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace LabelView
{
    public partial class Form1 : Form
    {
        // ── State ────────────────────────────────────────────────────
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

        private string?            _currentFile;
        private List<string>       _labels      = [];
        private Dictionary<int, Bitmap> _cache  = [];
        private int                _currentIndex;
        private int                _rotation;          // 0 / 90 / 180 / 270
        private CancellationTokenSource? _renderCts;

        private static readonly string SettingsDir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LabelView");
        private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.txt");

        // ── Controls ─────────────────────────────────────────────────
        private ListBox    _fileList = null!;
        private PictureBox _picture  = null!;
        private Label      _counter  = null!;
        private Label      _status   = null!;
        private Button     _btnPrev  = null!;
        private Button     _btnNext  = null!;

        // ── Construction ─────────────────────────────────────────────
        public Form1(string? initialFile = null)
        {
            InitializeComponent();
            _rotation = LoadRotation();
            BuildUi();
            if (initialFile is not null)
                Load += (_, _) => OpenFileAsync(initialFile);
        }

        private void BuildUi()
        {
            Text        = "LabelView";
            Size        = new Size(1150, 780);
            MinimumSize = new Size(700, 500);
            BackColor   = Color.FromArgb(43, 43, 43);
            KeyPreview  = true;
            KeyDown    += OnKeyDown;
            AllowDrop   = true;
            DragEnter  += OnDragEnter;
            DragDrop   += OnDragDrop;

            // ── Split (left = file list, right = viewer) ─────────────
            var split = new SplitContainer
            {
                Dock             = DockStyle.Fill,
                SplitterDistance = 210,
                FixedPanel       = FixedPanel.Panel1,
                SplitterWidth    = 3,
                BackColor        = Color.FromArgb(43, 43, 43),
            };
            Controls.Add(split);

            // ── Left panel ───────────────────────────────────────────
            split.Panel1.BackColor = Color.FromArgb(28, 28, 28);

            var leftTitle = new Label
            {
                Text      = "Files",
                Dock      = DockStyle.Top,
                Height    = 30,
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.FromArgb(160, 160, 160),
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding   = new Padding(10, 0, 0, 0),
            };

            _fileList = new ListBox
            {
                Dock          = DockStyle.Fill,
                BackColor     = Color.FromArgb(28, 28, 28),
                ForeColor     = Color.FromArgb(210, 210, 210),
                SelectionMode = SelectionMode.One,
                BorderStyle   = BorderStyle.None,
                Font          = new Font("Segoe UI", 9f),
                ItemHeight    = 20,
            };
            _fileList.SelectedIndexChanged += OnFileListSelect;

            // Add in reverse (Fill first so it doesn't crowd the header)
            split.Panel1.Controls.Add(_fileList);
            split.Panel1.Controls.Add(leftTitle);

            // ── Right panel ──────────────────────────────────────────
            var rightPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(43, 43, 43) };
            split.Panel2.Controls.Add(rightPanel);

            // Toolbar uses TableLayoutPanel: buttons left, status fills right
            var toolbar = new TableLayoutPanel
            {
                Dock        = DockStyle.Top,
                Height      = 46,
                BackColor   = Color.FromArgb(51, 51, 51),
                ColumnCount = 2,
                RowCount    = 1,
                Padding     = new Padding(6, 7, 6, 7),
            };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rightPanel.Controls.Add(toolbar);

            var btnFlow = new FlowLayoutPanel
            {
                AutoSize          = true,
                FlowDirection     = FlowDirection.LeftToRight,
                WrapContents      = false,
                BackColor         = Color.Transparent,
                Margin            = new Padding(0),
            };
            toolbar.Controls.Add(btnFlow, 0, 0);

            _status = new Label
            {
                Dock      = DockStyle.Fill,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(120, 120, 120),
                Font      = new Font("Segoe UI", 9f, FontStyle.Italic),
                TextAlign = ContentAlignment.MiddleRight,
                Padding   = new Padding(0, 0, 6, 0),
                Text      = "Open a ZPL file…",
            };
            toolbar.Controls.Add(_status, 1, 0);

            // Helper: create a toolbar button
            Button MakeBtn(string text, EventHandler click, int width = 78)
            {
                var b = new Button
                {
                    Text      = text,
                    Width     = width,
                    Height    = 30,
                    Margin    = new Padding(2, 0, 2, 0),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(68, 68, 68),
                    ForeColor = Color.White,
                    Font      = new Font("Segoe UI", 9f),
                    Cursor    = Cursors.Hand,
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(88, 88, 88);
                b.FlatAppearance.BorderSize  = 1;
                b.Click += click;
                return b;
            }

            // Helper: thin vertical separator for FlowLayoutPanel
            Panel MakeSep() => new Panel
            {
                Width     = 1,
                Height    = 28,
                Margin    = new Padding(8, 1, 8, 1),
                BackColor = Color.FromArgb(88, 88, 88),
            };

            // Helper: label between buttons
            Label MakeLbl(string text, int width = 52) => new Label
            {
                Text      = text,
                Width     = width,
                Height    = 30,
                Margin    = new Padding(2, 0, 2, 0),
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(190, 190, 190),
                Font      = new Font("Segoe UI", 9f),
                TextAlign = ContentAlignment.MiddleCenter,
            };

            _btnPrev = MakeBtn("◀  Prev", (_, _) => Navigate(-1));
            _counter = MakeLbl("— / —", 52);
            _btnNext = MakeBtn("Next  ▶", (_, _) => Navigate(+1));

            btnFlow.Controls.Add(_btnPrev);
            btnFlow.Controls.Add(_counter);
            btnFlow.Controls.Add(_btnNext);
            btnFlow.Controls.Add(MakeSep());
            btnFlow.Controls.Add(MakeBtn("↺  CCW",   (_, _) => Rotate(-90), 72));
            btnFlow.Controls.Add(MakeBtn("↻  CW",    (_, _) => Rotate(+90), 68));
            btnFlow.Controls.Add(MakeBtn("Reset ↑",  (_, _) => SetRotation(0), 72));

            // Image display panel
            var imgPanel = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 20),
                Padding   = new Padding(16),
            };
            rightPanel.Controls.Add(imgPanel);

            _picture = new PictureBox
            {
                Dock     = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(20, 20, 20),
            };
            imgPanel.Controls.Add(_picture);
        }

        // ── File opening ─────────────────────────────────────────────

        private async void OpenFileAsync(string path)
        {
            try
            {
                path = Path.GetFullPath(path);
                if (!File.Exists(path))
                {
                    MessageBox.Show($"File not found:\n{path}", "LabelView", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                _currentFile = path;
                Text = $"LabelView — {Path.GetFileName(path)}";
                PopulateFileList(Path.GetDirectoryName(path)!);

                string content;
                try   { content = File.ReadAllText(path); }
                catch (Exception ex)
                {
                    MessageBox.Show($"Cannot read file:\n{ex.Message}", "LabelView", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                ClearCache();
                _labels       = ExtractLabels(content);
                _currentIndex = 0;
                UpdateCounter();

                if (_labels.Count == 0)
                {
                    _picture.Image = null;
                    SetStatus("No ZPL labels found in this file.");
                    return;
                }

                await RenderAndShowAsync(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unexpected error:\n{ex.Message}", "LabelView", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnDragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        }

        private void OnDragDrop(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                OpenFileAsync(files[0]);
        }

        private void PopulateFileList(string dir)
        {
            _fileList.SelectedIndexChanged -= OnFileListSelect;
            _fileList.BeginUpdate();
            _fileList.Items.Clear();
            try
            {
                foreach (var name in Directory.GetFiles(dir)
                                              .Select(Path.GetFileName)
                                              .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                    _fileList.Items.Add(name!);

                if (_currentFile is not null)
                {
                    int idx = _fileList.Items.IndexOf(Path.GetFileName(_currentFile));
                    if (idx >= 0) { _fileList.SelectedIndex = idx; _fileList.TopIndex = Math.Max(0, idx - 3); }
                }
            }
            catch { }
            _fileList.EndUpdate();
            _fileList.SelectedIndexChanged += OnFileListSelect;
        }

        private void OnFileListSelect(object? sender, EventArgs e)
        {
            if (_fileList.SelectedItem is string fname && _currentFile is not null)
            {
                var full = Path.Combine(Path.GetDirectoryName(_currentFile)!, fname);
                if (full != _currentFile)
                    OpenFileAsync(full);
            }
        }

        // ── Navigation & rotation ────────────────────────────────────

        private void Navigate(int delta)
        {
            int next = _currentIndex + delta;
            if (next < 0 || next >= _labels.Count) return;
            _currentIndex = next;
            UpdateCounter();
            _ = RenderAndShowAsync(_currentIndex);
        }

        private void Rotate(int delta)
        {
            _rotation = ((_rotation + delta) % 360 + 360) % 360;
            SaveRotation();
            if (_cache.TryGetValue(_currentIndex, out var bmp)) ShowBitmap(bmp);
        }

        private void SetRotation(int angle)
        {
            _rotation = ((angle % 360) + 360) % 360;
            SaveRotation();
            if (_cache.TryGetValue(_currentIndex, out var bmp)) ShowBitmap(bmp);
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Left  or Keys.PageUp:   Navigate(-1);   break;
                case Keys.Right or Keys.PageDown: Navigate(+1);   break;
                case Keys.R when e.Shift:         Rotate(-90);    break;
                case Keys.R:                      Rotate(+90);    break;
                case Keys.D0 or Keys.NumPad0:     SetRotation(0); break;
            }
        }

        // ── Rendering ────────────────────────────────────────────────

        private async Task RenderAndShowAsync(int index)
        {
            if (_cache.TryGetValue(index, out var cached))
            {
                ShowBitmap(cached);
                return;
            }

            _renderCts?.Cancel();
            _renderCts = new CancellationTokenSource();
            var ct = _renderCts.Token;

            SetStatus("Rendering via Labelary…");
            _picture.Image = null;

            var zpl = _labels[index];
            var (w, h) = DetectLabelSize(zpl);

            try
            {
                var bmp = await FetchBitmapAsync(zpl, w, h, ct);
                if (!ct.IsCancellationRequested && index == _currentIndex)
                {
                    _cache[index] = bmp;
                    ShowBitmap(bmp);
                    SetStatus(Path.GetFileName(_currentFile) ?? "");
                }
            }
            catch (OperationCanceledException) { /* navigated away */ }
            catch (HttpRequestException ex)    { SetStatus($"HTTP error {(int?)ex.StatusCode}: {ex.Message}"); }
            catch (Exception ex)               { SetStatus($"Error: {ex.Message}"); }
        }

        private void ShowBitmap(Bitmap source)
        {
            Bitmap display;
            if (_rotation == 0)
            {
                display = source;
            }
            else
            {
                display = new Bitmap(source);
                display.RotateFlip(_rotation switch
                {
                    90  => RotateFlipType.Rotate90FlipNone,
                    180 => RotateFlipType.Rotate180FlipNone,
                    270 => RotateFlipType.Rotate270FlipNone,
                    _   => RotateFlipType.RotateNoneFlipNone,
                });
            }

            var old = _picture.Image;
            _picture.Image = display;
            // Dispose the previous rotated copy, but never the cached original
            if (old is not null && old != source && !_cache.ContainsValue((Bitmap)old))
                old.Dispose();
        }

        // ── ZPL helpers ──────────────────────────────────────────────

        private static List<string> ExtractLabels(string content) =>
            Regex.Matches(content, @"\^XA.*?\^XZ", RegexOptions.Singleline | RegexOptions.IgnoreCase)
                 .Select(m => m.Value)
                 .ToList();

        // Detect print width from ^PW. Use a generous height (12") so nothing
        // gets clipped; we trim bottom whitespace after rendering.
        private static (double w, double h) DetectLabelSize(string zpl)
        {
            const double dpi = 8 * 25.4;  // 8 dpmm * 25.4 mm/inch ≈ 203.2 dots/inch
            double w = 4.0;
            var pw = Regex.Match(zpl, @"\^PW(\d+)", RegexOptions.IgnoreCase);
            if (pw.Success) w = Math.Round(int.Parse(pw.Groups[1].Value) / dpi, 2);
            w = Math.Clamp(w, 0.5, 20.0);
            return (w, 12.0);
        }

        // ── Labelary API ─────────────────────────────────────────────

        private static async Task<Bitmap> FetchBitmapAsync(string zpl, double w, double h, CancellationToken ct)
        {
            var ws  = w.ToString("G", CultureInfo.InvariantCulture);
            var hs  = h.ToString("G", CultureInfo.InvariantCulture);
            var url = $"http://api.labelary.com/v1/printers/8dpmm/labels/{ws}x{hs}/0/";

            using var body = new StringContent(zpl, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var req  = new HttpRequestMessage(HttpMethod.Post, url) { Content = body };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));

            using var resp  = await Http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            using var ms = new MemoryStream(bytes);
            var raw = new Bitmap(ms);
            return TrimBottomWhitespace(raw);
        }

        // Scan from the bottom up and crop away trailing white rows.
        private static Bitmap TrimBottomWhitespace(Bitmap bmp)
        {
            int lastRow = bmp.Height - 1;
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                     ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    byte* scan0 = (byte*)data.Scan0;
                    int stride = data.Stride;
                    // Walk rows from bottom to top
                    for (int y = bmp.Height - 1; y >= 0; y--)
                    {
                        byte* row = scan0 + y * stride;
                        for (int x = 0; x < bmp.Width; x++)
                        {
                            // BGRA layout: B=row[0], G=row[1], R=row[2], A=row[3]
                            int off = x * 4;
                            if (row[off] != 255 || row[off + 1] != 255 || row[off + 2] != 255)
                            {
                                lastRow = y;
                                goto done;
                            }
                        }
                    }
                    done:;
                }
            }
            finally { bmp.UnlockBits(data); }

            int cropHeight = Math.Min(lastRow + 20, bmp.Height); // small bottom margin
            if (cropHeight >= bmp.Height)
                return bmp;

            var cropped = bmp.Clone(new Rectangle(0, 0, bmp.Width, cropHeight), bmp.PixelFormat);
            bmp.Dispose();
            return cropped;
        }

        // ── Cleanup ──────────────────────────────────────────────────

        private void ClearCache()
        {
            _renderCts?.Cancel();
            var old = _picture.Image;
            _picture.Image = null;
            if (old is Bitmap ob && !_cache.ContainsValue(ob)) ob.Dispose();
            foreach (var bmp in _cache.Values) bmp.Dispose();
            _cache.Clear();
        }

        private void UpdateCounter()
        {
            int total = _labels.Count;
            _counter.Text    = total == 0 ? "— / —" : $"{_currentIndex + 1} / {total}";
            _btnPrev.Enabled = _currentIndex > 0;
            _btnNext.Enabled = _currentIndex < total - 1;
        }

        private void SetStatus(string msg) => _status.Text = msg;

        // ── Settings persistence ─────────────────────────────────────

        private static int LoadRotation()
        {
            try
            {
                if (File.Exists(SettingsFile) && int.TryParse(File.ReadAllText(SettingsFile).Trim(), out int r))
                    return ((r % 360) + 360) % 360;
            }
            catch { }
            return 0;
        }

        private static void SaveRotation(int rotation)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(SettingsFile, rotation.ToString());
            }
            catch { }
        }

        private void SaveRotation() => SaveRotation(_rotation);

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            ClearCache();
        }
    }
}
