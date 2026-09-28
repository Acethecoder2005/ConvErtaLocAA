using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("ConvErtaLocAA")]
[assembly: System.Reflection.AssemblyProduct("ConvErtaLocAA")]
[assembly: System.Reflection.AssemblyDescription("Local image conversion and PDF page assembly")]
[assembly: System.Reflection.AssemblyVersion("0.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.2.0.0")]

namespace ConvErtaLocAA {
    static class Program {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [STAThread] static void Main(string[] args) {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string selfTest = args.Length == 2 && args[0] == "--self-test" ? args[1] : null;
            Application.Run(new ConverterForm(selfTest));
        }
    }

    sealed class PageList : ListView {
        public PageList() { DoubleBuffered = true; }
        public void TestFileDrop(string[] paths) {
            OnDragDrop(new DragEventArgs(new DataObject(DataFormats.FileDrop, paths), 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy));
        }
    }

    sealed class ConverterForm : Form {
        readonly List<Dictionary<string, object>> pages = new List<Dictionary<string, object>>();
        readonly string appRoot = AppDomain.CurrentDomain.BaseDirectory;
        readonly string cache = Path.Combine(Path.GetTempPath(), "ConvErtaLocAA", Guid.NewGuid().ToString("N"));
        readonly string outputFolder;
        readonly string selfTest;
        readonly PageList list = new PageList();
        readonly PictureBox preview = new PictureBox();
        readonly Label empty = new Label();
        readonly Label previewCaption = new Label();
        readonly Label status = new Label();
        readonly Label count = new Label();
        readonly ComboBox target = new ComboBox();
        readonly ComboBox paper = new ComboBox();
        readonly TextBox outputName = new TextBox();
        readonly NumericUpDown quality = new NumericUpDown();
        readonly NumericUpDown dpi = new NumericUpDown();
        readonly FlowLayoutPanel pdfOptions = new FlowLayoutPanel();
        readonly FlowLayoutPanel imageOptions = new FlowLayoutPanel();
        readonly Button convertButton = new Button();
        readonly ProgressBar progress = new ProgressBar();
        readonly ImageList thumbnails = new ImageList();
        readonly List<Control> editingControls = new List<Control>();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        bool busy;
        Process worker;
        int dragInsert = -1;
        readonly Color ink = Color.FromArgb(28, 32, 39);
        readonly Color muted = Color.FromArgb(101, 110, 123);
        readonly Color accent = Color.FromArgb(31, 87, 205);

        public ConverterForm(string testDirectory) {
            selfTest = testDirectory;
            outputFolder = testDirectory == null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ConvErtaLocAA") : Path.Combine(Path.GetFullPath(testDirectory), "ui-output");
            Directory.CreateDirectory(cache);
            Text = "ConvErtaLocAA";
            Icon = new Icon(Path.Combine(appRoot, "assets", "converter.ico"));
            Size = new Size(1100, 800);
            MinimumSize = new Size(990, 710);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10);
            BackColor = Color.White;
            ForeColor = ink;
            AutoScaleMode = AutoScaleMode.Dpi;
            BuildUI();
            Shown += async delegate {
                try {
                    SetBusy(true, "Checking local formats...");
                    var caps = await RunWorker(new Dictionary<string, object> { { "action", "capabilities" } });
                    target.Items.Add("PDF");
                    foreach (object value in ArrayItems(caps["formats"])) target.Items.Add(value.ToString());
                    target.SelectedIndex = 0;
                    SetBusy(false, "Ready. Add images or PDFs to begin.");
                    if (!target.Items.Contains("HEIC")) {
                        var note = Controls.Find("codecNote", true).FirstOrDefault() as Label;
                        if (note != null) note.Text = "HEIC input supported. HEIC output requires an additional local encoder.";
                    }
                    if (selfTest != null) await RunSelfTest();
                } catch (Exception ex) {
                    SetBusy(false, "Could not start the converter.");
                    ShowError(ex.Message);
                    if (selfTest != null) Close();
                }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (busy) { e.Cancel = true; status.Text = "Please let the current operation finish before closing."; }
            };
            FormClosed += delegate {
                if (preview.Image != null) preview.Image.Dispose();
                thumbnails.Dispose();
                try { Directory.Delete(cache, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            };
        }

        static Label TextLabel(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 9, 8, 0) }; }
        static object[] ArrayItems(object value) { return ((IEnumerable)value).Cast<object>().ToArray(); }
        static void DrawComboText(ComboBox combo) {
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                e.DrawBackground();
                string text = e.Index >= 0 ? combo.Items[e.Index].ToString() : combo.Text;
                TextRenderer.DrawText(e.Graphics, text, combo.Font, e.Bounds, combo.Enabled ? e.ForeColor : SystemColors.GrayText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                e.DrawFocusRectangle();
            };
        }
        Button ActionButton(string text, EventHandler handler) {
            var button = new Button { Text = text, AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 0, 7, 0), BackColor = Color.White, Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = Color.FromArgb(212, 217, 225);
            button.Click += handler;
            editingControls.Add(button);
            return button;
        }

        void BuildUI() {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 14), ColumnCount = 1, RowCount = 7 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            Controls.Add(layout);

            var titlePanel = new Panel { Dock = DockStyle.Fill };
            var brandLogo = new PictureBox { Location = new Point(0, 1), Size = new Size(54, 54), SizeMode = PictureBoxSizeMode.Zoom };
            using (var logoFile = Image.FromFile(Path.Combine(appRoot, "assets", "converter-logo.png"))) brandLogo.Image = new Bitmap(logoFile);
            FormClosed += delegate { brandLogo.Image.Dispose(); };
            titlePanel.Controls.Add(brandLogo);
            titlePanel.Controls.Add(new Label { Text = "ConvErtaLocAA", Font = new Font("Segoe UI", 22, FontStyle.Bold), AutoSize = true, Location = new Point(65, 0) });
            titlePanel.Controls.Add(new Label { Text = "Images and PDFs. Converted on this computer.", ForeColor = muted, AutoSize = true, Location = new Point(67, 41) });
            layout.Controls.Add(titlePanel, 0, 0);

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
            toolbar.Controls.Add(ActionButton("Add files", async delegate { using (var dlg = new OpenFileDialog { Multiselect = true, Title = "Add images or PDFs", Filter = "Images and PDFs|*.heic;*.heif;*.jpg;*.jpeg;*.png;*.webp;*.avif;*.bmp;*.tif;*.tiff;*.gif;*.pdf|All files|*.*" }) { if (dlg.ShowDialog(this) == DialogResult.OK) await AddPaths(dlg.FileNames); } }));
            toolbar.Controls.Add(ActionButton("Move up", delegate { MoveSelected(-1); }));
            toolbar.Controls.Add(ActionButton("Move down", delegate { MoveSelected(1); }));
            toolbar.Controls.Add(ActionButton("Rotate", delegate { RotateSelected(); }));
            toolbar.Controls.Add(ActionButton("Duplicate", delegate { DuplicateSelected(); }));
            toolbar.Controls.Add(ActionButton("Remove", delegate { RemoveSelected(); }));
            toolbar.Controls.Add(ActionButton("Clear", delegate { pages.Clear(); RebuildList(null); status.Text = "Ready. Add images or PDFs to begin."; }));
            layout.Controls.Add(toolbar, 0, 1);

            var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 8, 0, 8) };
            middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
            middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            var queuePanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 14, 0) };
            thumbnails.ImageSize = new Size(56, 56);
            thumbnails.ColorDepth = ColorDepth.Depth32Bit;
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.SmallImageList = thumbnails;
            list.ShowItemToolTips = true;
            list.Columns.Add("Page", 92);
            list.Columns.Add("File", 310);
            list.Columns.Add("Source", 112);
            list.BorderStyle = BorderStyle.FixedSingle;
            list.SelectedIndexChanged += delegate { UpdatePreview(); };
            list.KeyDown += delegate(object sender, KeyEventArgs e) {
                if (busy) return;
                if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
                if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem row in list.Items) row.Selected = true; e.Handled = true; }
                if (e.Alt && e.KeyCode == Keys.Up) { MoveSelected(-1); e.Handled = true; }
                if (e.Alt && e.KeyCode == Keys.Down) { MoveSelected(1); e.Handled = true; }
            };
            list.ItemDrag += delegate { if (!busy) list.DoDragDrop("ConvErtaLocAA-pages", DragDropEffects.Move); };
            queuePanel.Controls.Add(list);
            empty.Text = "Drop images or PDFs here\n\nEach image or PDF page becomes one row.\nDrag rows to choose the output page order.";
            empty.TextAlign = ContentAlignment.MiddleCenter;
            empty.ForeColor = muted;
            empty.BackColor = Color.FromArgb(248, 250, 253);
            empty.Dock = DockStyle.Fill;
            empty.Font = new Font("Segoe UI", 11);
            queuePanel.Controls.Add(empty);
            empty.BringToFront();
            EnableDrop(list);
            EnableDrop(empty);
            middle.Controls.Add(queuePanel, 0, 0);

            var previewPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Color.FromArgb(247, 249, 252), Padding = new Padding(12) };
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
            previewPanel.Controls.Add(new Label { Text = "PREVIEW", Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = muted, Dock = DockStyle.Fill }, 0, 0);
            preview.Dock = DockStyle.Fill;
            preview.SizeMode = PictureBoxSizeMode.Zoom;
            previewPanel.Controls.Add(preview, 0, 1);
            previewCaption.Dock = DockStyle.Fill;
            previewCaption.TextAlign = ContentAlignment.MiddleCenter;
            previewCaption.ForeColor = muted;
            previewCaption.AutoEllipsis = true;
            previewCaption.Text = "Select a page to preview it.";
            previewPanel.Controls.Add(previewCaption, 0, 2);
            middle.Controls.Add(previewPanel, 1, 0);
            layout.Controls.Add(middle, 0, 2);

            var options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            options.Controls.Add(TextLabel("Convert to"));
            target.DropDownStyle = ComboBoxStyle.DropDownList;
            DrawComboText(target);
            target.Width = 100;
            target.Margin = new Padding(0, 5, 18, 0);
            target.SelectedIndexChanged += delegate { UpdateOptions(); };
            options.Controls.Add(target);
            editingControls.Add(target);
            pdfOptions.AutoSize = true;
            pdfOptions.WrapContents = false;
            pdfOptions.Margin = new Padding(0);
            pdfOptions.Controls.Add(TextLabel("PDF name"));
            outputName.Text = "Combined";
            outputName.Width = 175;
            outputName.Margin = new Padding(0, 5, 18, 0);
            pdfOptions.Controls.Add(outputName);
            pdfOptions.Controls.Add(TextLabel("Photo pages"));
            paper.DropDownStyle = ComboBoxStyle.DropDownList;
            DrawComboText(paper);
            paper.Items.AddRange(new object[] { "Fit image", "Letter", "A4" });
            paper.SelectedIndex = 0;
            paper.Width = 115;
            paper.Margin = new Padding(0, 5, 0, 0);
            pdfOptions.Controls.Add(paper);
            options.Controls.Add(pdfOptions);
            imageOptions.AutoSize = true;
            imageOptions.WrapContents = false;
            imageOptions.Margin = new Padding(0);
            imageOptions.Controls.Add(TextLabel("Quality"));
            quality.Minimum = 1; quality.Maximum = 100; quality.Value = 92; quality.Width = 66; quality.Margin = new Padding(0, 5, 18, 0);
            imageOptions.Controls.Add(quality);
            imageOptions.Controls.Add(TextLabel("PDF render DPI"));
            dpi.Minimum = 72; dpi.Maximum = 600; dpi.Value = 150; dpi.Width = 76; dpi.Margin = new Padding(0, 5, 0, 0);
            imageOptions.Controls.Add(dpi);
            options.Controls.Add(imageOptions);
            editingControls.Add(pdfOptions); editingControls.Add(imageOptions);
            layout.Controls.Add(options, 0, 3);
            var codecNote = new Label { Name = "codecNote", Dock = DockStyle.Fill, ForeColor = muted, Text = "", Font = new Font("Segoe UI", 9), TextAlign = ContentAlignment.TopLeft };
            layout.Controls.Add(codecNote, 0, 4);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            var folderLabel = new Label { Text = "Saves to\n" + outputFolder, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = muted, Font = new Font("Segoe UI", 9) };
            bottom.Controls.Add(folderLabel, 0, 0);
            var openButton = new Button { Text = "Open folder", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 10, 10), Cursor = Cursors.Hand };
            openButton.FlatAppearance.BorderColor = Color.FromArgb(212, 217, 225);
            openButton.Click += delegate { try { Directory.CreateDirectory(outputFolder); Process.Start(new ProcessStartInfo(outputFolder) { UseShellExecute = true }); } catch (Exception ex) { ShowError(ex.Message); } };
            bottom.Controls.Add(openButton, 1, 0);
            convertButton.Text = "Convert";
            convertButton.Dock = DockStyle.Fill;
            convertButton.FlatStyle = FlatStyle.Flat;
            convertButton.BackColor = accent;
            convertButton.ForeColor = Color.White;
            convertButton.FlatAppearance.BorderSize = 0;
            convertButton.Font = new Font("Segoe UI", 11, FontStyle.Bold);
            convertButton.Margin = new Padding(0, 0, 0, 10);
            convertButton.Cursor = Cursors.Hand;
            convertButton.Click += async delegate { await ConvertQueue(); };
            bottom.Controls.Add(convertButton, 2, 0);
            layout.Controls.Add(bottom, 0, 5);
            var statusPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0) };
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            status.Text = "Ready. Add images or PDFs to begin.";
            status.Dock = DockStyle.Fill;
            status.AutoEllipsis = true;
            status.Font = new Font("Segoe UI", 9);
            count.Dock = DockStyle.Fill;
            count.TextAlign = ContentAlignment.TopRight;
            count.ForeColor = muted;
            count.Font = new Font("Segoe UI", 9);
            progress.Dock = DockStyle.Fill;
            progress.Style = ProgressBarStyle.Marquee;
            progress.Visible = false;
            progress.Margin = new Padding(0, 0, 10, 7);
            statusPanel.Controls.Add(status, 0, 0);
            statusPanel.Controls.Add(progress, 1, 0);
            statusPanel.Controls.Add(count, 2, 0);
            layout.Controls.Add(statusPanel, 0, 6);
            convertButton.Enabled = false;
        }

        void EnableDrop(Control control) {
            control.AllowDrop = true;
            control.DragEnter += delegate(object sender, DragEventArgs e) {
                e.Effect = busy ? DragDropEffects.None : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : e.Data.GetDataPresent(DataFormats.Text) && (string)e.Data.GetData(DataFormats.Text) == "ConvErtaLocAA-pages" ? DragDropEffects.Move : DragDropEffects.None;
            };
            control.DragOver += delegate(object sender, DragEventArgs e) {
                if (busy || !e.Data.GetDataPresent(DataFormats.Text)) return;
                Point point = list.PointToClient(new Point(e.X, e.Y));
                var row = list.GetItemAt(Math.Max(1, point.X), point.Y);
                dragInsert = row == null ? pages.Count : row.Index + (point.Y > row.Bounds.Top + row.Bounds.Height / 2 ? 1 : 0);
                list.InsertionMark.Index = Math.Min(dragInsert, pages.Count - 1);
                list.InsertionMark.AppearsAfterItem = dragInsert == pages.Count;
                if (row != null && point.Y < 65 && row.Index > 0) list.EnsureVisible(row.Index - 1);
                if (row != null && point.Y > list.Height - 45 && row.Index < pages.Count - 1) list.EnsureVisible(row.Index + 1);
            };
            control.DragLeave += delegate { list.InsertionMark.Index = -1; };
            control.DragDrop += async delegate(object sender, DragEventArgs e) {
                list.InsertionMark.Index = -1;
                if (busy) return;
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) await AddPaths((string[])e.Data.GetData(DataFormats.FileDrop));
                else if (e.Data.GetDataPresent(DataFormats.Text) && (string)e.Data.GetData(DataFormats.Text) == "ConvErtaLocAA-pages") ReorderSelected(dragInsert < 0 ? pages.Count : dragInsert);
                dragInsert = -1;
            };
        }

        HashSet<string> SelectedIds() { return new HashSet<string>(list.SelectedItems.Cast<ListViewItem>().Select(row => (string)row.Tag)); }
        void RebuildList(HashSet<string> selected) {
            list.BeginUpdate();
            list.Items.Clear();
            thumbnails.Images.Clear();
            for (int index = 0; index < pages.Count; index++) {
                var page = pages[index];
                string id = (string)page["id"];
                using (var image = LoadPreview(page)) using (var tile = new Bitmap(56, 56)) {
                    using (var graphics = Graphics.FromImage(tile)) {
                        graphics.Clear(Color.White);
                        float scale = Math.Min(52f / image.Width, 52f / image.Height);
                        float width = image.Width * scale, height = image.Height * scale;
                        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(image, (56 - width) / 2, (56 - height) / 2, width, height);
                    }
                    thumbnails.Images.Add(id, tile);
                }
                var row = new ListViewItem((index + 1).ToString(), id) { Tag = id, ToolTipText = (string)page["path"] };
                row.SubItems.Add(Path.GetFileName((string)page["path"]));
                row.SubItems.Add((string)page["kind"] == "pdf" ? "PDF page " + (Convert.ToInt32(page["page"]) + 1) : "Image" + (Convert.ToInt32(page["frame"]) > 0 ? " " + (Convert.ToInt32(page["frame"]) + 1) : ""));
                list.Items.Add(row);
                row.Selected = selected != null && selected.Contains(id);
            }
            list.EndUpdate();
            empty.Visible = pages.Count == 0;
            if (empty.Visible) empty.BringToFront();
            count.Text = pages.Count + (pages.Count == 1 ? " page" : " pages");
            convertButton.Enabled = !busy && pages.Count > 0;
            if (list.SelectedItems.Count > 0) list.SelectedItems[0].EnsureVisible();
            UpdatePreview();
        }

        Bitmap LoadPreview(Dictionary<string, object> page) {
            using (var file = Image.FromFile((string)page["preview"])) {
                var result = new Bitmap(file);
                int rotation = Convert.ToInt32(page["rotation"]) % 360;
                if (rotation == 90) result.RotateFlip(RotateFlipType.Rotate90FlipNone);
                else if (rotation == 180) result.RotateFlip(RotateFlipType.Rotate180FlipNone);
                else if (rotation == 270) result.RotateFlip(RotateFlipType.Rotate270FlipNone);
                return result;
            }
        }
        void UpdatePreview() {
            var old = preview.Image;
            preview.Image = null;
            if (old != null) old.Dispose();
            if (list.SelectedItems.Count == 0) { previewCaption.Text = "Select a page to preview it."; return; }
            int index = list.SelectedItems[0].Index;
            if (index >= pages.Count) return;
            var page = pages[index];
            preview.Image = LoadPreview(page);
            previewCaption.Text = "Output page " + (index + 1) + "\n" + Path.GetFileName((string)page["path"]);
        }
        void MoveSelected(int direction) {
            if (busy) return;
            var selected = SelectedIds();
            if (direction < 0) {
                for (int i = 1; i < pages.Count; i++) if (selected.Contains((string)pages[i]["id"]) && !selected.Contains((string)pages[i - 1]["id"])) { var p = pages[i]; pages[i] = pages[i - 1]; pages[i - 1] = p; }
            } else {
                for (int i = pages.Count - 2; i >= 0; i--) if (selected.Contains((string)pages[i]["id"]) && !selected.Contains((string)pages[i + 1]["id"])) { var p = pages[i]; pages[i] = pages[i + 1]; pages[i + 1] = p; }
            }
            RebuildList(selected);
        }
        void ReorderSelected(int insertion) {
            var selected = SelectedIds();
            var moved = pages.Where(p => selected.Contains((string)p["id"])).ToList();
            int before = pages.Take(insertion).Count(p => selected.Contains((string)p["id"]));
            pages.RemoveAll(p => selected.Contains((string)p["id"]));
            pages.InsertRange(Math.Max(0, Math.Min(pages.Count, insertion - before)), moved);
            RebuildList(selected);
        }
        void RotateSelected() {
            if (busy) return;
            var selected = SelectedIds();
            foreach (var p in pages.Where(p => selected.Contains((string)p["id"]))) p["rotation"] = (Convert.ToInt32(p["rotation"]) + 90) % 360;
            RebuildList(selected);
        }
        void DuplicateSelected() {
            if (busy) return;
            var selected = SelectedIds();
            var copies = new HashSet<string>();
            if (pages.Count + selected.Count > 2000) { ShowError("Keep the queue at 2,000 pages or fewer."); return; }
            for (int i = pages.Count - 1; i >= 0; i--) if (selected.Contains((string)pages[i]["id"])) {
                var copy = new Dictionary<string, object>(pages[i]); copy["id"] = Guid.NewGuid().ToString("N"); copies.Add((string)copy["id"]); pages.Insert(i + 1, copy);
            }
            RebuildList(copies);
        }
        void RemoveSelected() { if (busy) return; var selected = SelectedIds(); pages.RemoveAll(p => selected.Contains((string)p["id"])); RebuildList(null); }
        void UpdateOptions() {
            bool pdf = (string)target.SelectedItem == "PDF";
            pdfOptions.Visible = pdf;
            imageOptions.Visible = !pdf;
            quality.Enabled = !pdf && new[] { "JPG", "WebP", "AVIF", "HEIC" }.Contains((string)target.SelectedItem);
        }
        void SetBusy(bool value, string message) {
            busy = value;
            foreach (Control control in editingControls) control.Enabled = !value;
            list.Enabled = !value;
            convertButton.Enabled = !value && pages.Count > 0;
            progress.Visible = value;
            status.Text = message;
            if (!value) UpdateOptions();
        }
        void ShowError(string message) {
            if (selfTest != null) { File.AppendAllText(Path.Combine(selfTest, "ui-errors.txt"), message + Environment.NewLine); return; }
            MessageBox.Show(this, message, "ConvErtaLocAA", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        async Task AddPaths(string[] paths) {
            if (busy || paths.Length == 0) return;
            SetBusy(true, "Reading files...");
            try {
                var result = await RunWorker(new Dictionary<string, object> { { "action", "inspect" }, { "paths", paths }, { "cache", cache } });
                var added = ArrayItems(result["items"]).Cast<Dictionary<string, object>>().ToList();
                if (pages.Count + added.Count > 2000) throw new Exception("Keep the queue at 2,000 pages or fewer.");
                pages.AddRange(added);
                RebuildList(added.Count == 0 ? null : new HashSet<string> { (string)added[0]["id"] });
                SetBusy(false, "Added " + added.Count + " page(s). Drag rows to set their order.");
                var errors = ArrayItems(result["errors"]);
                if (errors.Length > 0) ShowError(string.Join("\n\n", errors.Select(e => e.ToString())));
            } catch (Exception ex) { SetBusy(false, "Could not add those files."); ShowError(ex.Message); }
        }
        async Task ConvertQueue() {
            if (busy || pages.Count == 0) return;
            var request = new Dictionary<string, object> { { "action", "convert" }, { "items", pages }, { "format", target.SelectedItem }, { "output", outputFolder }, { "name", outputName.Text }, { "page_size", paper.SelectedItem }, { "quality", (int)quality.Value }, { "dpi", (int)dpi.Value }, { "strip_metadata", true } };
            SetBusy(true, "Converting...");
            try {
                var result = await RunWorker(request);
                var outputs = ArrayItems(result["outputs"]);
                var errors = ArrayItems(result["errors"]);
                SetBusy(false, "Saved " + outputs.Length + " file(s) to your output folder." + (errors.Length > 0 ? " Some files failed." : ""));
                if (selfTest != null) File.WriteAllText(Path.Combine(selfTest, "ui-conversion.json"), json.Serialize(result));
                if (errors.Length > 0) ShowError(string.Join("\n\n", errors.Select(e => e.ToString())));
            } catch (Exception ex) { SetBusy(false, "Conversion failed. Originals were not changed."); ShowError(ex.Message); }
        }

        async Task<Dictionary<string, object>> RunWorker(Dictionary<string, object> request) {
            string payload = json.Serialize(request);
            return await Task.Run(delegate {
                var start = new ProcessStartInfo {
                    FileName = Path.Combine(appRoot, "runtime", "python.exe"),
                    Arguments = "-I -B \"" + Path.Combine(appRoot, "source", "engine.py") + "\"",
                    WorkingDirectory = appRoot,
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                // Isolated Python ignores PYTHONPATH and user-installed packages.
                using (var process = new Process { StartInfo = start }) {
                    worker = process;
                    process.Start();
                    var stderr = process.StandardError.ReadToEndAsync();
                    // Explicit UTF-8 handles international file names independently of the console locale.
                    using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false))) {
                        input.Write(payload);
                    }
                    Dictionary<string, object> result = null;
                    string failure = null;
                    string line;
                    while ((line = process.StandardOutput.ReadLine()) != null) {
                        var message = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(line);
                        string type = (string)message["type"];
                        if (type == "progress") {
                            string text = (string)message["message"];
                            if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(delegate { status.Text = text; }));
                        } else if (type == "result") result = (Dictionary<string, object>)message["result"];
                        else if (type == "error") failure = (string)message["message"];
                    }
                    process.WaitForExit();
                    string diagnostics = stderr.GetAwaiter().GetResult();
                    worker = null;
                    if (failure != null) throw new Exception(failure);
                    if (process.ExitCode != 0 || result == null) throw new Exception("The local converter stopped unexpectedly.\n" + diagnostics.Substring(0, Math.Min(diagnostics.Length, 1600)));
                    return result;
                }
            });
        }

        async Task RunSelfTest() {
            try {
                list.TestFileDrop(new[] { Path.Combine(selfTest, "red.png"), Path.Combine(selfTest, "sample.pdf"), Path.Combine(selfTest, "blue.png") });
                while (busy) await Task.Delay(50);
                if (pages.Count != 4) throw new Exception("UI import expected 4 pages, got " + pages.Count);
                RebuildList(new HashSet<string> { (string)pages[3]["id"] });
                MoveSelected(-1);
                if (!((string)pages[2]["path"]).EndsWith("blue.png")) throw new Exception("Move up failed.");
                ReorderSelected(0);
                if (!((string)pages[0]["path"]).EndsWith("blue.png")) throw new Exception("Drag reorder failed.");
                RotateSelected();
                if (Convert.ToInt32(pages[0]["rotation"]) != 90) throw new Exception("Rotate failed.");
                DuplicateSelected();
                if (pages.Count != 5) throw new Exception("Duplicate failed.");
                RemoveSelected();
                if (pages.Count != 4) throw new Exception("Remove failed.");
                await AddPaths(new[] { Path.Combine(selfTest, "Résumé 测试.png") });
                if (pages.Count != 5) throw new Exception("Unicode file import failed.");
                RemoveSelected();
                if (pages.Count != 4) throw new Exception("Unicode file removal failed.");
                RebuildList(new HashSet<string> { (string)pages[0]["id"] });
                target.SelectedItem = "PDF";
                outputName.Text = "UI test-" + DateTime.Now.ToString("HHmmss");
                await ConvertQueue();
                if (!File.Exists(Path.Combine(outputFolder, outputName.Text + ".pdf"))) throw new Exception("UI conversion failed.");
                await Task.Delay(250);
                using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(Path.Combine(selfTest, "app-preview.png")); }
                File.WriteAllText(Path.Combine(selfTest, "ui-test-result.txt"), "PASS: file-drop event, PDF expansion, move, drag reorder, rotate, duplicate, remove, Unicode paths, convert, preview.");
            } catch (Exception ex) { File.WriteAllText(Path.Combine(selfTest, "ui-test-result.txt"), "FAIL: " + ex.ToString()); }
            Close();
        }
    }
}
