// GifRecorder.cs v2
// 轻量 GIF 区域录制小工具（Windows，单文件 exe，无需安装）
// 功能：主界面 / 全局快捷键 / 框选区域 / 倒计时开录 / 录制指示器 / 裁剪时间段 / 导出 GIF
//
// 编译（Linux + Mono）：
//   mcs -target:winexe -codepage:65001 -win32icon:app.ico -r:System.Windows.Forms.dll -r:System.Drawing.dll GifRecorder.cs -out:GifRecorder.exe

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;

namespace GifRecorder
{
    // ---------------- 全局配置（含持久化） ----------------
    static class Config
    {
        public static int Fps = 10;            // 录制帧率
        public static int CountdownSecs = 0;   // 开始前倒计时：0 / 3 / 5
        public const int MaxSeconds = 60;      // 单次最长录制秒数
        public static string OutputDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "GIF 录制");

        private static string IniPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GifRecorder", "settings.ini");

        public static void Load()
        {
            try
            {
                if (!File.Exists(IniPath)) return;
                foreach (string line in File.ReadAllLines(IniPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLower();
                    string v = line.Substring(eq + 1).Trim();
                    if (k == "fps") { int n; if (int.TryParse(v, out n)) Fps = Math.Max(5, Math.Min(20, n)); }
                    else if (k == "countdown") { int n; if (int.TryParse(v, out n)) CountdownSecs = n; }
                    else if (k == "outputdir" && v.Length > 0) OutputDir = v;
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(IniPath));
                File.WriteAllLines(IniPath, new string[] {
                    "fps=" + Fps,
                    "countdown=" + CountdownSecs,
                    "outputdir=" + OutputDir,
                });
            }
            catch { }
        }
    }

    // ---------------- 小工具：从 exe 自带图标取窗口图标 ----------------
    static class Ui
    {
        private static Icon appIcon;

        public static void ApplyIcon(Form f)
        {
            try
            {
                if (appIcon == null)
                    appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (appIcon != null) f.Icon = appIcon;
            }
            catch { }
        }

        public static Icon AppIcon
        {
            get
            {
                try
                {
                    if (appIcon == null)
                        appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                }
                catch { }
                return appIcon ?? SystemIcons.Application;
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Config.Load();
            try { Directory.CreateDirectory(Config.OutputDir); }
            catch { }
            // 清理上次残留的临时帧目录
            try
            {
                string t = Path.Combine(Path.GetTempPath(), "GifRecorder");
                if (Directory.Exists(t)) Directory.Delete(t, true);
            }
            catch { }
            Application.Run(new MainForm());
        }
    }

    // 隐藏窗口，只为接收全局快捷键消息
    class HotkeyWindow : NativeWindow
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint VK_R = 0x52;
        public const int HOTKEY_ID = 1;

        public event Action HotkeyPressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_HOTKEY = 0x0312;
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID && HotkeyPressed != null)
                HotkeyPressed();
            base.WndProc(ref m);
        }
    }

    // ---------------- 主界面 ----------------
    class MainForm : Form
    {
        private HotkeyWindow hotkeyWnd;
        private NotifyIcon tray;
        private bool reallyExit = false;

        private bool busy = false;
        private bool recording = false;
        private volatile bool stopFlag = false;

        private NumericUpDown numFps;
        private ComboBox cmbCountdown;
        private TextBox txtDir;
        private Label lblStatus;

        public MainForm()
        {
            Text = "GIF 录制";
            Ui.ApplyIcon(this);
            Size = new Size(400, 330);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            int y = 16;

            Button btnRecord = new Button();
            btnRecord.Text = "框选并录制  (Ctrl+Shift+R)";
            btnRecord.Font = new Font("微软雅黑", 13, FontStyle.Bold);
            btnRecord.SetBounds(24, y, 328, 52);
            btnRecord.Click += delegate { OnHotkey(); };
            Controls.Add(btnRecord);
            y += 68;

            GroupBox grp = new GroupBox();
            grp.Text = "设置";
            grp.SetBounds(24, y, 328, 132);
            Controls.Add(grp);

            Label l1 = new Label();
            l1.Text = "帧率 (FPS)：";
            l1.AutoSize = true;
            l1.SetBounds(16, 28, 90, 20);
            grp.Controls.Add(l1);

            numFps = new NumericUpDown();
            numFps.Minimum = 5;
            numFps.Maximum = 20;
            numFps.Value = Config.Fps;
            numFps.SetBounds(110, 24, 70, 24);
            numFps.ValueChanged += delegate
            {
                Config.Fps = (int)numFps.Value;
                Config.Save();
            };
            grp.Controls.Add(numFps);

            Label l2 = new Label();
            l2.Text = "开始前倒计时：";
            l2.AutoSize = true;
            l2.SetBounds(16, 60, 100, 20);
            grp.Controls.Add(l2);

            cmbCountdown = new ComboBox();
            cmbCountdown.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbCountdown.Items.AddRange(new object[] { "不倒计时", "3 秒", "5 秒" });
            cmbCountdown.SelectedIndex = Config.CountdownSecs == 5 ? 2 : (Config.CountdownSecs == 3 ? 1 : 0);
            cmbCountdown.SetBounds(120, 56, 100, 24);
            cmbCountdown.SelectedIndexChanged += delegate
            {
                Config.CountdownSecs = cmbCountdown.SelectedIndex == 2 ? 5 : (cmbCountdown.SelectedIndex == 1 ? 3 : 0);
                Config.Save();
            };
            grp.Controls.Add(cmbCountdown);

            Label l3 = new Label();
            l3.Text = "保存目录：";
            l3.AutoSize = true;
            l3.SetBounds(16, 92, 70, 20);
            grp.Controls.Add(l3);

            txtDir = new TextBox();
            txtDir.ReadOnly = true;
            txtDir.Text = Config.OutputDir;
            txtDir.SetBounds(90, 88, 160, 24);
            grp.Controls.Add(txtDir);

            Button btnBrowse = new Button();
            btnBrowse.Text = "浏览…";
            btnBrowse.SetBounds(256, 87, 60, 26);
            btnBrowse.Click += delegate
            {
                using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "选择 GIF 保存目录";
                    dlg.SelectedPath = Config.OutputDir;
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        Config.OutputDir = dlg.SelectedPath;
                        txtDir.Text = Config.OutputDir;
                        Config.Save();
                    }
                }
            };
            grp.Controls.Add(btnBrowse);
            y += 144;

            lblStatus = new Label();
            lblStatus.Text = "就绪";
            lblStatus.AutoSize = true;
            lblStatus.SetBounds(24, y, 328, 20);
            Controls.Add(lblStatus);

            // 全局快捷键
            hotkeyWnd = new HotkeyWindow();
            if (HotkeyWindow.RegisterHotKey(hotkeyWnd.Handle, HotkeyWindow.HOTKEY_ID,
                    HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_SHIFT, HotkeyWindow.VK_R))
            {
                hotkeyWnd.HotkeyPressed += OnHotkey;
            }
            else
            {
                lblStatus.Text = "快捷键 Ctrl+Shift+R 注册失败，可能被其他程序占用";
            }

            // 托盘图标
            tray = new NotifyIcon();
            tray.Icon = Ui.AppIcon;
            tray.Text = "GIF 录制";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowMain(); };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, delegate { ShowMain(); });
            menu.Items.Add("退出", null, delegate
            {
                reallyExit = true;
                tray.Visible = false;
                HotkeyWindow.UnregisterHotKey(hotkeyWnd.Handle, HotkeyWindow.HOTKEY_ID);
                Application.Exit();
            });
            tray.ContextMenuStrip = menu;
        }

        private void ShowMain()
        {
            Visible = true;
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                tray.ShowBalloonTip(2000, "GIF 录制",
                    "已最小化到托盘，快捷键仍可使用，右键托盘图标可退出",
                    ToolTipIcon.Info);
            }
            base.OnFormClosing(e);
        }

        // 快捷键 / 按钮 入口
        private void OnHotkey()
        {
            if (recording) { stopFlag = true; return; } // 录制中 → 停止
            if (busy) return;
            busy = true;
            try
            {
                ShowMain();
                RunFlow();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "出错了：" + ex.Message, "GIF 录制",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { busy = false; }
        }

        private void RunFlow()
        {
            while (true)
            {
                // 1. 框选区域
                Rectangle sel;
                using (SelectForm sf = new SelectForm())
                {
                    if (sf.ShowDialog(this) != DialogResult.OK) return;
                    sel = sf.Selection;
                }

                // 2. 确认（可调整开始时机）
                DialogResult r;
                using (ReadyForm rf = new ReadyForm(sel))
                {
                    r = rf.ShowDialog(this);
                }
                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.Retry) continue; // 重新框选

                // 3. 倒计时
                if (Config.CountdownSecs > 0)
                {
                    using (CountdownForm cf = new CountdownForm(Config.CountdownSecs))
                    {
                        if (cf.ShowDialog(this) != DialogResult.OK) return;
                    }
                }

                // 4. 录制
                string tmpDir = Path.Combine(Path.GetTempPath(), "GifRecorder",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmpDir);
                List<string> frames;
                try
                {
                    recording = true;
                    stopFlag = false;
                    using (IndicatorForm ind = new IndicatorForm())
                    {
                        ind.Show(this);
                        frames = RecordLoop(sel, tmpDir, ind);
                    }
                }
                finally { recording = false; }

                if (frames.Count == 0)
                {
                    try { Directory.Delete(tmpDir, true); } catch { }
                    lblStatus.Text = "没有录到任何画面";
                    return;
                }

                // 5. 裁剪时间段 + 导出
                DialogResult tr;
                string exported = null;
                using (TrimForm tf = new TrimForm(frames, Config.Fps, Config.OutputDir))
                {
                    tr = tf.ShowDialog(this);
                    exported = tf.ExportPath;
                }
                try { Directory.Delete(tmpDir, true); } catch { }

                if (tr == DialogResult.Retry) continue; // 重新录制 → 回到框选
                if (exported != null)
                    lblStatus.Text = "已保存：" + exported;
                return;
            }
        }

        private List<string> RecordLoop(Rectangle sel, string tmpDir, IndicatorForm ind)
        {
            List<string> paths = new List<string>();
            int interval = 1000 / Config.Fps;
            long deadline = (long)Config.MaxSeconds * 1000;
            Stopwatch sw = Stopwatch.StartNew();
            int i = 0;
            while (!ind.StopRequested && !stopFlag && sw.ElapsedMilliseconds < deadline)
            {
                long t0 = sw.ElapsedMilliseconds;
                string path = Path.Combine(tmpDir, string.Format("frame_{0:00000}.png", i));
                using (Bitmap bmp = new Bitmap(sel.Width, sel.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(sel.Left, sel.Top, 0, 0, sel.Size,
                            CopyPixelOperation.SourceCopy);
                    }
                    bmp.Save(path, ImageFormat.Png);
                }
                paths.Add(path);
                i++;
                Application.DoEvents(); // 让指示器刷新、响应停止按钮和快捷键
                long spent = sw.ElapsedMilliseconds - t0;
                if (spent < interval) Thread.Sleep((int)(interval - spent));
            }
            return paths;
        }
    }

    // ---------------- 框选区域 ----------------
    class SelectForm : Form
    {
        public Rectangle Selection { get; private set; }

        private bool dragging = false;
        private Point p0;
        private Rectangle cur;

        public SelectForm()
        {
            Ui.ApplyIcon(this);
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            TopMost = true;
            ShowInTaskbar = false;
            Opacity = 0.25;
            BackColor = Color.Black;
            Cursor = Cursors.Cross;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Label hint = new Label();
            hint.Text = "拖拽鼠标框选录制区域，松开进入下一步（Esc 取消）";
            hint.ForeColor = Color.White;
            hint.BackColor = Color.Black;
            hint.Font = new Font("微软雅黑", 14);
            hint.AutoSize = true;
            hint.Location = new Point(
                (ClientSize.Width - hint.PreferredWidth) / 2, 40);
            Controls.Add(hint);
        }

        private static Rectangle Norm(Point a, Point b)
        {
            return new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                                 Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                p0 = e.Location;
                cur = new Rectangle(p0, Size.Empty);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging) { cur = Norm(p0, e.Location); Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging && e.Button == MouseButtons.Left)
            {
                dragging = false;
                Rectangle r = Norm(p0, e.Location);
                if (r.Width > 10 && r.Height > 10)
                {
                    Selection = r;
                    DialogResult = DialogResult.OK;
                }
                Close();
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (dragging && cur.Width > 0 && cur.Height > 0)
            {
                using (Pen pen = new Pen(Color.Red, 2))
                    e.Graphics.DrawRectangle(pen, cur);
            }
            base.OnPaint(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    // ---------------- 录制前确认 ----------------
    class ReadyForm : Form
    {
        public ReadyForm(Rectangle sel)
        {
            Ui.ApplyIcon(this);
            Text = "准备录制";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(340, 210);

            Label l1 = new Label();
            l1.Text = string.Format("已选择区域：{0} × {1} 像素", sel.Width, sel.Height);
            l1.AutoSize = true;
            l1.SetBounds(24, 20, 290, 20);
            Controls.Add(l1);

            Label l2 = new Label();
            l2.Text = Config.CountdownSecs > 0
                ? string.Format("点击「开始录制」后将倒计时 {0} 秒", Config.CountdownSecs)
                : "点击「开始录制」后立即开始（可在主界面设置倒计时）";
            l2.AutoSize = true;
            l2.SetBounds(24, 48, 290, 40);
            Controls.Add(l2);

            Button btnGo = new Button();
            btnGo.Text = "开始录制";
            btnGo.Font = new Font("微软雅黑", 11, FontStyle.Bold);
            btnGo.SetBounds(24, 110, 130, 40);
            btnGo.DialogResult = DialogResult.OK;
            Controls.Add(btnGo);
            AcceptButton = btnGo;

            Button btnRe = new Button();
            btnRe.Text = "重新框选";
            btnRe.SetBounds(164, 110, 70, 40);
            btnRe.DialogResult = DialogResult.Retry;
            Controls.Add(btnRe);

            Button btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.SetBounds(244, 110, 60, 40);
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);
            CancelButton = btnCancel;
        }
    }

    // ---------------- 倒计时 ----------------
    class CountdownForm : Form
    {
        private int n;
        private Label lbl;
        private System.Windows.Forms.Timer timer;

        public CountdownForm(int seconds)
        {
            Ui.ApplyIcon(this);
            n = seconds;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(360, 280);
            BackColor = Color.FromArgb(20, 20, 20);
            Opacity = 0.92;

            lbl = new Label();
            lbl.ForeColor = Color.White;
            lbl.BackColor = Color.FromArgb(20, 20, 20);
            lbl.Font = new Font("微软雅黑", 96, FontStyle.Bold);
            lbl.AutoSize = true;
            lbl.Text = n.ToString();
            lbl.Location = new Point(
                (ClientSize.Width - lbl.PreferredWidth) / 2, 40);
            Controls.Add(lbl);

            Label sub = new Label();
            sub.Text = "准备录制…（Esc 取消）";
            sub.ForeColor = Color.Gray;
            sub.BackColor = Color.FromArgb(20, 20, 20);
            sub.Font = new Font("微软雅黑", 12);
            sub.AutoSize = true;
            sub.Location = new Point(
                (ClientSize.Width - sub.PreferredWidth) / 2, 200);
            Controls.Add(sub);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate
            {
                n--;
                if (n <= 0)
                {
                    timer.Stop();
                    DialogResult = DialogResult.OK;
                    Close();
                }
                else
                {
                    lbl.Text = n.ToString();
                    lbl.Location = new Point(
                        (ClientSize.Width - lbl.PreferredWidth) / 2, 40);
                }
            };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            timer.Start();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                timer.Stop();
                DialogResult = DialogResult.Cancel;
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    // ---------------- 录制指示器 ----------------
    class IndicatorForm : Form
    {
        public volatile bool StopRequested = false;

        private Label lbl;
        private DateTime t0;
        private System.Windows.Forms.Timer timer;

        public IndicatorForm()
        {
            Ui.ApplyIcon(this);
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(34, 34, 34);
            Opacity = 0.92;
            Size = new Size(240, 72);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 24, wa.Top + 24);

            lbl = new Label();
            lbl.ForeColor = Color.FromArgb(255, 68, 68);
            lbl.BackColor = Color.FromArgb(34, 34, 34);
            lbl.Font = new Font("微软雅黑", 13, FontStyle.Bold);
            lbl.AutoSize = true;
            lbl.Location = new Point(14, 8);
            lbl.Text = "● 录制中 00:00";
            Controls.Add(lbl);

            Button btn = new Button();
            btn.Text = "停止";
            btn.AutoSize = true;
            btn.Location = new Point(14, 36);
            btn.Click += delegate { StopRequested = true; };
            Controls.Add(btn);

            t0 = DateTime.Now;
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 200;
            timer.Tick += delegate
            {
                if (StopRequested) { Close(); return; }
                TimeSpan ts = DateTime.Now - t0;
                lbl.Text = string.Format("● 录制中 {0:00}:{1:00}",
                    (int)ts.TotalMinutes, ts.Seconds);
            };
            timer.Start();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { StopRequested = true; return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    // ---------------- 裁剪时间段 + 导出 ----------------
    class TrimForm : Form
    {
        private List<string> frames;
        private int fps;
        private double totalSec;
        private string defaultDir;

        private PictureBox pic;
        private TrackBar track;
        private Label lblTime;
        private NumericUpDown numStart;
        private NumericUpDown numEnd;
        private Button btnPlay;
        private System.Windows.Forms.Timer playTimer;
        private bool playing = false;
        private int playIdx;
        private int playEnd;

        public string ExportPath { get; private set; }

        public TrimForm(List<string> frames, int fps, string defaultDir)
        {
            Ui.ApplyIcon(this);
            this.frames = frames;
            this.fps = fps;
            this.defaultDir = defaultDir;
            totalSec = frames.Count / (double)fps;

            Text = "裁剪并导出";
            Size = new Size(660, 600);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            pic = new PictureBox();
            pic.SetBounds(14, 12, 600, 360);
            pic.SizeMode = PictureBoxSizeMode.Zoom;
            pic.BorderStyle = BorderStyle.FixedSingle;
            pic.BackColor = Color.Black;
            Controls.Add(pic);

            track = new TrackBar();
            track.SetBounds(14, 380, 600, 40);
            track.Minimum = 0;
            track.Maximum = frames.Count - 1;
            track.TickFrequency = Math.Max(1, frames.Count / 20);
            track.Scroll += delegate
            {
                StopPreview();
                ShowFrame(track.Value);
            };
            Controls.Add(track);

            lblTime = new Label();
            lblTime.AutoSize = true;
            lblTime.SetBounds(14, 424, 600, 20);
            Controls.Add(lblTime);

            Label ls = new Label();
            ls.Text = "开始（秒）：";
            ls.AutoSize = true;
            ls.SetBounds(14, 452, 80, 20);
            Controls.Add(ls);

            numStart = new NumericUpDown();
            numStart.DecimalPlaces = 1;
            numStart.Increment = 0.1M;
            numStart.Minimum = 0;
            numStart.Maximum = (decimal)totalSec;
            numStart.Value = 0;
            numStart.SetBounds(96, 448, 80, 24);
            Controls.Add(numStart);

            Label le = new Label();
            le.Text = "结束（秒）：";
            le.AutoSize = true;
            le.SetBounds(190, 452, 80, 20);
            Controls.Add(le);

            numEnd = new NumericUpDown();
            numEnd.DecimalPlaces = 1;
            numEnd.Increment = 0.1M;
            numEnd.Minimum = 0;
            numEnd.Maximum = (decimal)totalSec;
            numEnd.Value = (decimal)totalSec;
            numEnd.SetBounds(272, 448, 80, 24);
            Controls.Add(numEnd);

            btnPlay = new Button();
            btnPlay.Text = "预览裁剪段";
            btnPlay.SetBounds(380, 446, 110, 28);
            btnPlay.Click += delegate { TogglePreview(); };
            Controls.Add(btnPlay);

            Button btnExport = new Button();
            btnExport.Text = "导出 GIF";
            btnExport.Font = new Font("微软雅黑", 11, FontStyle.Bold);
            btnExport.SetBounds(14, 492, 150, 40);
            btnExport.Click += delegate { DoExport(); };
            Controls.Add(btnExport);
            AcceptButton = btnExport;

            Button btnRetry = new Button();
            btnRetry.Text = "重新录制";
            btnRetry.SetBounds(176, 492, 100, 40);
            btnRetry.Click += delegate
            {
                StopPreview();
                DialogResult = DialogResult.Retry;
                Close();
            };
            Controls.Add(btnRetry);

            Button btnCancel = new Button();
            btnCancel.Text = "放弃";
            btnCancel.SetBounds(288, 492, 80, 40);
            btnCancel.Click += delegate
            {
                StopPreview();
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(btnCancel);

            Label hint = new Label();
            hint.Text = "拖动进度条查看每一帧，在下方设置要保留的时间段再导出。";
            hint.AutoSize = true;
            hint.ForeColor = Color.Gray;
            hint.SetBounds(14, 540, 600, 20);
            Controls.Add(hint);

            playTimer = new System.Windows.Forms.Timer();
            playTimer.Interval = 1000 / fps;
            playTimer.Tick += delegate
            {
                playIdx++;
                if (playIdx > playEnd) playIdx = playStartIdx;
                ShowFrame(playIdx);
            };

            ShowFrame(0);
        }

        private int playStartIdx;

        private void ShowFrame(int i)
        {
            i = Math.Max(0, Math.Min(frames.Count - 1, i));
            Bitmap bmp = GifUtil.LoadBitmap(frames[i]);
            if (pic.Image != null) pic.Image.Dispose();
            pic.Image = bmp;
            lblTime.Text = string.Format("{0:0.0} 秒 / 共 {1:0.0} 秒   （第 {2} / {3} 帧）",
                i / (double)fps, totalSec, i + 1, frames.Count);
            if (track.Value != i) track.Value = i;
        }

        private void TogglePreview()
        {
            if (playing) { StopPreview(); return; }
            int s = (int)((double)numStart.Value * fps);
            int e = (int)((double)numEnd.Value * fps);
            s = Math.Max(0, Math.Min(frames.Count - 1, s));
            e = Math.Max(0, Math.Min(frames.Count - 1, e));
            if (s >= e)
            {
                MessageBox.Show(this, "开始时间必须小于结束时间", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            playStartIdx = s;
            playEnd = e;
            playIdx = s;
            playing = true;
            btnPlay.Text = "停止预览";
            playTimer.Start();
        }

        private void StopPreview()
        {
            if (!playing) return;
            playing = false;
            playTimer.Stop();
            btnPlay.Text = "预览裁剪段";
        }

        private void DoExport()
        {
            StopPreview();
            double s = (double)numStart.Value;
            double e = (double)numEnd.Value;
            if (s < 0) s = 0;
            if (e > totalSec) e = totalSec;
            if (s >= e)
            {
                MessageBox.Show(this, "开始时间必须小于结束时间", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int i0 = Math.Max(0, (int)(s * fps));
            int i1 = Math.Min(frames.Count - 1, (int)(e * fps));

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "GIF 图片|*.gif";
                dlg.FileName = "gif_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".gif";
                dlg.InitialDirectory = defaultDir;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    GifUtil.SaveGif(frames.GetRange(i0, i1 - i0 + 1), dlg.FileName, fps);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "导出失败：" + ex.Message, "GIF 录制",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                ExportPath = dlg.FileName;
                try { Process.Start("explorer.exe", "/select,\"" + dlg.FileName + "\""); }
                catch { }
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopPreview();
            if (pic.Image != null) { pic.Image.Dispose(); pic.Image = null; }
            base.OnFormClosing(e);
        }
    }

    // ---------------- GIF 编解码小工具 ----------------
    static class GifUtil
    {
        public static Bitmap LoadBitmap(string path)
        {
            // 用流 + 拷贝的方式加载，避免锁定文件
            using (FileStream fs = new FileStream(path, FileMode.Open,
                       FileAccess.Read, FileShare.Read))
            using (Image img = Image.FromStream(fs))
            {
                return new Bitmap(img);
            }
        }

        public static void SaveGif(List<string> framePaths, string outPath, int fps)
        {
            ImageCodecInfo enc = GetGifEncoder();
            if (enc == null) throw new Exception("找不到 GIF 编码器");
            int delayCs = 100 / fps; // 帧间隔（1/100 秒）
            EncoderParameters ep = new EncoderParameters(1);
            Bitmap first = null;
            try
            {
                foreach (string p in framePaths)
                {
                    using (Bitmap bmp = LoadBitmap(p))
                    {
                        SetFrameDelay(bmp, delayCs);
                        if (first == null)
                        {
                            ep.Param[0] = new EncoderParameter(
                                Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
                            bmp.Save(outPath, enc, ep);
                            first = (Bitmap)bmp.Clone();
                        }
                        else
                        {
                            ep.Param[0] = new EncoderParameter(
                                Encoder.SaveFlag, (long)EncoderValue.FrameDimensionTime);
                            first.SaveAdd(bmp, ep);
                        }
                    }
                }
                if (first != null)
                {
                    ep.Param[0] = new EncoderParameter(
                        Encoder.SaveFlag, (long)EncoderValue.Flush);
                    first.SaveAdd(ep);
                }
            }
            finally { if (first != null) first.Dispose(); }
        }

        private static ImageCodecInfo GetGifEncoder()
        {
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Gif.Guid) return c;
            return null;
        }

        private static void SetFrameDelay(Bitmap bmp, int delayCs)
        {
            // PropertyItem 没有公开构造函数，用反序列化方式创建
            PropertyItem pi = (PropertyItem)FormatterServices.GetUninitializedObject(
                typeof(PropertyItem));
            pi.Id = 0x5100; // FrameDelay
            pi.Len = 4;
            pi.Type = 4;     // LONG
            pi.Value = BitConverter.GetBytes(delayCs);
            bmp.SetPropertyItem(pi);
        }
    }
}
