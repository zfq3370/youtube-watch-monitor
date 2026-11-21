using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public static class SecureSmtp {
    static void Reply(StreamReader reader, int code) {
        string line; int count = 0;
        do {
            line = reader.ReadLine();
            if (line == null || line.Length < 3 || ++count > 100) throw new IOException("SMTP response missing");
            if (!line.StartsWith(code.ToString())) throw new IOException("SMTP response " + line.Substring(0, 3));
        } while (line.Length > 3 && line[3] == '-');
    }
    static void SendLine(StreamWriter writer, StreamReader reader, string line, int code) { writer.WriteLine(line); Reply(reader, code); }
    public static void Send(string host, string user, string recipient, string password, string subject, string body) {
        if (!Rules.IsHost(host) || !Rules.IsEmail(user) || !Rules.IsEmail(recipient) || String.IsNullOrEmpty(password)) throw new IOException("SMTP settings invalid");
        using (var tcp = new TcpClient()) {
            IAsyncResult result = tcp.BeginConnect(host, 465, null, null);
            using (result.AsyncWaitHandle) { if (!result.AsyncWaitHandle.WaitOne(10000)) throw new IOException("SMTP connection timeout"); tcp.EndConnect(result); }
            using (var ssl = new SslStream(tcp.GetStream(), false)) {
                ssl.ReadTimeout = 20000; ssl.WriteTimeout = 20000;
                ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, true);
                using (var reader = new StreamReader(ssl, Encoding.ASCII, false, 4096, true))
                using (var writer = new StreamWriter(ssl, new UTF8Encoding(false), 4096, true)) {
                    writer.NewLine = "\r\n"; writer.AutoFlush = true; Reply(reader, 220);
                    SendLine(writer, reader, "EHLO localhost", 250); SendLine(writer, reader, "AUTH LOGIN", 334);
                    SendLine(writer, reader, Convert.ToBase64String(Encoding.UTF8.GetBytes(user)), 334);
                    SendLine(writer, reader, Convert.ToBase64String(Encoding.UTF8.GetBytes(password)), 235);
                    SendLine(writer, reader, "MAIL FROM:<" + user + ">", 250); SendLine(writer, reader, "RCPT TO:<" + recipient + ">", 250);
                    SendLine(writer, reader, "DATA", 354);
                    writer.WriteLine("From: <" + user + ">"); writer.WriteLine("To: <" + recipient + ">");
                    writer.WriteLine("Date: " + DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture));
                    writer.WriteLine("Message-ID: <" + Guid.NewGuid().ToString("N") + "@" + host + ">");
                    for (int i = 0; i < subject.Length; i += 10) writer.WriteLine((i == 0 ? "Subject: " : " ") + "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(subject.Substring(i, Math.Min(10, subject.Length - i)))) + "?=");
                    writer.WriteLine("MIME-Version: 1.0"); writer.WriteLine("Content-Type: text/plain; charset=UTF-8");
                    writer.WriteLine("Content-Transfer-Encoding: base64"); writer.WriteLine();
                    string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                    for (int i = 0; i < encoded.Length; i += 76) writer.WriteLine(encoded.Substring(i, Math.Min(76, encoded.Length - i)));
                    writer.WriteLine("."); Reply(reader, 250); writer.WriteLine("QUIT");
                }
            }
        }
    }
}

static class WindowIcon {
    // 图标随 exe 一起编译进清单资源，按需要的尺寸取最清晰的一档；万一取不到就退回系统图标，不影响任何功能。
    static readonly byte[] data = Read();
    static byte[] Read() {
        try {
            using (var stream = typeof(WindowIcon).Assembly.GetManifestResourceStream("app.ico")) {
                if (stream == null) return null;
                byte[] all = new byte[stream.Length];
                return stream.Read(all, 0, all.Length) == all.Length ? all : null;
            }
        } catch { return null; }
    }
    public static Icon Load(int size) {
        try { if (data != null) return new Icon(new MemoryStream(data), new Size(size, size)); } catch { }
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return SystemIcons.Application; }
    }
}

public class TaskEditor : Form {
    public WatchTask Result;
    TextBox nameBox, urlBox, authorBox, commentBox;
    ComboBox modeBox; NumericUpDown intervalBox; CheckBox enabledBox, popupBox, emailBox, musicBox;
    Label hint;
    public TaskEditor(WatchTask existing) {
        SuspendLayout(); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Text = existing == null ? "添加监控任务" : "编辑监控任务"; ClientSize = new Size(730, 570);
        StartPosition = FormStartPosition.CenterParent; Font = new Font("Microsoft YaHei UI", 10);
        MinimumSize = new Size(650, 540); BackColor = Color.FromArgb(248, 250, 253); Icon = WindowIcon.Load(32);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, RowCount = 9, Padding = new Padding(20) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 9; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(layout);
        nameBox = Field(layout, "任务名称", 0);
        modeBox = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        modeBox.Items.AddRange(new object[] { "博主置顶评论", "指定评论变更", "会员专属新视频" }); LabelCell(layout, "监控类型", 1); layout.Controls.Add(modeBox, 1, 1);
        urlBox = Field(layout, "视频 / 频道链接", 2); authorBox = Field(layout, "评论作者", 3); commentBox = Field(layout, "指定评论编号", 4);
        intervalBox = new NumericUpDown { Minimum = 60, Maximum = 86400, Increment = 30, Value = 120, Width = 160 };
        LabelCell(layout, "刷新间隔（秒）", 5); layout.Controls.Add(intervalBox, 1, 5);
        var checks = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        enabledBox = Check("启用任务", true); popupBox = Check("桌面弹窗", true); emailBox = Check("邮件", true); musicBox = Check("播放音乐", true);
        checks.Controls.AddRange(new Control[] { enabledBox, popupBox, emailBox, musicBox }); layout.Controls.Add(checks, 1, 6);
        hint = new Label { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(3, 12, 3, 18), ForeColor = Color.FromArgb(70, 86, 106) }; layout.Controls.Add(hint, 0, 7); layout.SetColumnSpan(hint, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "保存任务", AutoSize = true, MinimumSize = new Size(145, 38), Padding = new Padding(10, 6, 10, 6) }; var cancel = new Button { Text = "取消", AutoSize = true, MinimumSize = new Size(100, 38), Padding = new Padding(10, 6, 10, 6), DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { save, cancel }); layout.Controls.Add(buttons, 0, 8); layout.SetColumnSpan(buttons, 2); CancelButton = cancel;
        modeBox.SelectedIndexChanged += delegate { bool pinned = modeBox.SelectedIndex == 0, comment = modeBox.SelectedIndex == 1; authorBox.Enabled = pinned; commentBox.Enabled = comment;
            hint.Text = pinned ? "监控指定作者的置顶评论出现、修改、替换或取消。评论作者填 @用户名，留空表示任意作者。\r\n点赞、回复数及相对时间不参与比较。" : comment ? "粘贴带 lc= 的评论链接，或填视频链接及评论编号。点击评论的发布时间可取得独立链接。\r\n找不到评论会显示读取失败，不把未加载或删除当作正文修改。" : "粘贴频道首页或会员专享播放列表链接（列表编号以 UUMO 开头）。\r\n首次读取只建立基准；以后新出现在列表顶部的会员视频才提醒。须登录有权限的账号。"; };
        if (existing != null) { nameBox.Text = existing.name; urlBox.Text = existing.url; authorBox.Text = existing.author; commentBox.Text = existing.commentId; intervalBox.Value = existing.intervalSeconds; enabledBox.Checked = existing.enabled; popupBox.Checked = existing.popup; emailBox.Checked = existing.email; musicBox.Checked = existing.music; }
        modeBox.SelectedIndex = existing == null ? 0 : existing.mode == "comment" ? 1 : existing.mode == "members" ? 2 : 0;
        save.Click += delegate {
            try {
                var task = new WatchTask { name = nameBox.Text, mode = modeBox.SelectedIndex == 1 ? "comment" : modeBox.SelectedIndex == 2 ? "members" : "pinned", url = urlBox.Text, author = authorBox.Text, commentId = commentBox.Text, intervalSeconds = (int)intervalBox.Value, enabled = enabledBox.Checked, popup = popupBox.Checked, email = emailBox.Checked, music = musicBox.Checked };
                Rules.Validate(task); if (existing != null) task.id = existing.id;
                Result = task; DialogResult = DialogResult.OK; Close();
            } catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "请检查任务配置", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        ResumeLayout(true);
    }
    static CheckBox Check(string text, bool value) { return new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(3, 8, 12, 8) }; }
    static void LabelCell(TableLayoutPanel layout, string text, int row) { layout.Controls.Add(new Label { Text = text, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 5, 0, 0), Margin = new Padding(3, 3, 8, 12) }, 0, row); }
    static TextBox Field(TableLayoutPanel layout, string text, int row) { LabelCell(layout, text, row); var box = new TextBox { Dock = DockStyle.Top, Margin = new Padding(3, 3, 3, 12) }; layout.Controls.Add(box, 1, row); return box; }
}

public class MonitorForm : Form {
    Settings settings; string token; List<WatchTask> tasks; Dictionary<string, TaskState> states; List<Notice> notices;
    volatile string taskResponse; int forceCheck;
    HttpListener listener; readonly ConcurrentQueue<Observation> observations = new ConcurrentQueue<Observation>();
    readonly ConcurrentQueue<MailResult> mailResults = new ConcurrentQueue<MailResult>();
    DataGridView grid; TextBox detail, history, musicPath; Label connection, mailStatus, musicStatus, migrationStatus, historyInfo;
    RadioButton systemSoundChoice, customMusicChoice; Button musicBrowse;
    NumericUpDown volume; CheckBox loop; NotifyIcon tray; Form alert; TextBox alertBody; Label alertTitle; int alertCount;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer(); readonly System.Windows.Forms.Timer previewTimer = new System.Windows.Forms.Timer();
    bool exiting, mailBusy; DateTime nextMail = DateTime.MinValue, nextCheckAllowed = DateTime.MinValue;
    Button checkNow;

    public MonitorForm() {
        SuspendLayout(); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        settings = Store.Load<Settings>("settings.json") ?? new Settings();
        if (String.IsNullOrEmpty(settings.token)) { byte[] bytes = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes); settings.token = Store.Protect(BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant()); Store.Save("settings.json", settings); }
        token = Store.Unprotect(settings.token);
        tasks = Store.Load<List<WatchTask>>("tasks.json"); states = Store.Load<Dictionary<string, TaskState>>("task-states.json") ?? new Dictionary<string, TaskState>();
        notices = Store.Load<List<Notice>>("notices.json") ?? new List<Notice>();
        if (tasks == null) { tasks = new List<WatchTask>(); Store.Save("tasks.json", tasks); }
        foreach (WatchTask task in tasks) { Rules.Validate(task); if (!states.ContainsKey(task.id)) states[task.id] = new TaskState(); states[task.id].lastReport = DateTime.Now; states[task.id].staleWarned = false; }
        Text = "YouTube 多任务监控面板 v" + App.Version; ClientSize = new Size(1120, 760); MinimumSize = new Size(900, 660); StartPosition = FormStartPosition.CenterScreen; Icon = WindowIcon.Load(32);
        Font = new Font("Microsoft YaHei UI", 10); BackColor = Color.FromArgb(246, 248, 252);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(18) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(root);
        var title = new Label { Text = "YouTube 多任务监控", Font = new Font("Microsoft YaHei UI", 20, FontStyle.Bold), AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 14) }; root.Controls.Add(title, 0, 0);
        var tabs = new TabControl { Dock = DockStyle.Fill }; root.Controls.Add(tabs, 0, 1);
        BuildTasks(tabs); BuildMusic(tabs); BuildHistory(tabs); BuildConnection(tabs); BuildSetup(tabs);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 8, 0, 0) };
        footer.Controls.Add(Button("退出程序", delegate { exiting = true; Close(); })); footer.Controls.Add(Button("最小化到托盘", delegate { Hide(); }));
        footer.Controls.Add(Button("停止音乐", delegate { StopMusic(); })); root.Controls.Add(footer, 0, 2);
        tray = new NotifyIcon { Icon = WindowIcon.Load(16), Text = "YouTube 多任务监控", Visible = true };
        tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
        var menu = new ContextMenuStrip(); menu.Items.Add("打开面板", null, delegate { Show(); Activate(); }); menu.Items.Add("停止音乐", null, delegate { StopMusic(); }); menu.Items.Add("退出", null, delegate { exiting = true; Close(); }); tray.ContextMenuStrip = menu;
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!exiting) { e.Cancel = true; Hide(); } };
        FormClosed += delegate { timer.Stop(); previewTimer.Stop(); AlertMusic.Stop(); if (listener != null) listener.Close(); tray.Dispose(); if (alert != null) alert.Close(); };
        previewTimer.Interval = 10000; previewTimer.Tick += delegate { StopMusic(); musicStatus.Text = "试听已结束（10 秒）。正式提醒会按循环设置播放，直到关闭弹窗或点“停止音乐”。"; };
        listener = new HttpListener(); listener.Prefixes.Add("http://127.0.0.1:18964/"); listener.Start(); Task.Run((Action)Listen);
        PublishTasks(); RefreshGrid(); RefreshHistory(); UpdateMailLabel();
        timer.Interval = 1000; timer.Tick += delegate { Tick(); }; timer.Start();
        ResumeLayout(true);
        Shown += delegate { var work = Screen.FromControl(this).WorkingArea; Size = new Size(Math.Min(Width, work.Width - 24), Math.Min(Height, work.Height - 24)); };
    }
    static Button Button(string text, EventHandler action) {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(110, 38), Padding = new Padding(10, 6, 10, 6), BackColor = Color.White, Margin = new Padding(4) };
        long lastClick = 0; bool handling = false;
        b.Click += delegate(object sender, EventArgs e) {
            long now = Stopwatch.GetTimestamp();
            if (handling || (lastClick != 0 && now - lastClick < Stopwatch.Frequency)) return;
            lastClick = now; handling = true;
            try { action(sender, e); } finally { handling = false; }
        };
        return b;
    }
    static TabPage Page(TabControl tabs, string name) { var page = new TabPage(name) { Padding = new Padding(12), BackColor = Color.FromArgb(248, 250, 253) }; tabs.TabPages.Add(page); return page; }
    static Label Label(string text) { return new Label { Text = text, AutoSize = true, MaximumSize = new Size(880, 0), Margin = new Padding(4, 8, 4, 8) }; }
    static FlowLayoutPanel Stack(TabPage page) {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true }; page.Controls.Add(panel);
        panel.SizeChanged += delegate {
            int width = Math.Max(200, panel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 16);
            foreach (Control child in panel.Controls) {
                if (child is Label || child is CheckBox) child.MaximumSize = new Size(width, 0);
                else if (child is TextBox || child is FlowLayoutPanel) child.Width = width;
            }
        };
        return panel;
    }
    void BuildTasks(TabControl tabs) {
        var page = Page(tabs, "监控任务");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 52)); page.Controls.Add(layout);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 10) }; bar.Controls.Add(Button("添加任务", delegate { EditTask(null); })); bar.Controls.Add(Button("编辑任务", delegate { if (Selected() != null) EditTask(Selected()); }));
        bar.Controls.Add(Button("启用 / 暂停", delegate { var t = Selected(); if (t != null) { t.enabled = !t.enabled; t.revision = Guid.NewGuid().ToString("N"); SaveTasks(); } }));
        bar.Controls.Add(Button("重新建立基准", delegate { var t = Selected(); if (t != null && MessageBox.Show(this, "重新建立基准后，下次读取的现有内容不会提醒。原有提醒记录保留。", "确认重建基准", MessageBoxButtons.OKCancel) == DialogResult.OK) { states[t.id] = new TaskState(); t.revision = Guid.NewGuid().ToString("N"); SaveTasks(); } }));
        bar.Controls.Add(Button("删除任务", delegate { var t = Selected(); if (t != null && MessageBox.Show(this, "删除这项监控任务及其基准？已有提醒记录保留。", "确认删除任务", MessageBoxButtons.OKCancel) == DialogResult.OK) { tasks.Remove(t); states.Remove(t.id); SaveTasks(); } }));
        checkNow = Button("全部立即检查", delegate {
            if (DateTime.Now < nextCheckAllowed) return;
            nextCheckAllowed = DateTime.Now.AddSeconds(30); checkNow.Enabled = false; checkNow.Text = "已请求检查…";
            forceCheck++; PublishTasks(); connection.Text = "已请求检查；助手通常会在 30 秒内接收，正在加载的任务不会重复刷新。";
        }); bar.Controls.Add(checkNow);
        layout.Controls.Add(bar, 0, 0);
        grid = new DataGridView { Dock = DockStyle.Fill, Font = Font, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle, EnableHeadersVisualStyles = false };
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True; grid.DefaultCellStyle.Padding = new Padding(8, 6, 8, 6);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 235, 252); grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(24, 43, 65);
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True; grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 7, 8, 7);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 239, 248); grid.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 253); grid.GridColor = Color.FromArgb(219, 226, 236);
        grid.Columns.Add("enabled", "启用"); grid.Columns[0].FillWeight = 30; grid.Columns.Add("name", "任务名称"); grid.Columns[1].FillWeight = 100;
        grid.Columns.Add("mode", "类型"); grid.Columns[2].FillWeight = 80; grid.Columns.Add("interval", "间隔"); grid.Columns[3].FillWeight = 45;
        grid.Columns.Add("checked", "最后检查"); grid.Columns[4].FillWeight = 100; grid.Columns.Add("status", "状态"); grid.Columns[5].FillWeight = 185;
        int[] minimums = { 60, 140, 120, 70, 160, 220 };
        for (int i = 0; i < grid.Columns.Count; i++) { grid.Columns[i].MinimumWidth = minimums[i]; grid.Columns[i].SortMode = DataGridViewColumnSortMode.NotSortable; }
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.SelectionChanged += delegate { ShowDetail(); }; grid.CellDoubleClick += delegate { if (Selected() != null) EditTask(Selected()); }; layout.Controls.Add(grid, 0, 1);
        layout.Controls.Add(new Label { Text = "每项任务固定使用一个后台标签页，按各自间隔刷新（最短 60 秒），不需要额外窗口，也不会打断你正在浏览的页面。\r\nChrome 和本程序须保持运行；Chrome 窗口可以最小化，页面在后台静默加载。\r\n会员视频首次只建基准；置顶取消需两次成功检查确认。更改监控目标会重新建立基准。", AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(75, 91, 111), Margin = new Padding(3, 10, 3, 10) }, 0, 2);
        layout.Controls.Add(new Label { Text = "任务详情 / 最近读取内容", AutoSize = true, Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 4, 3, 6) }, 0, 3);
        detail = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White }; layout.Controls.Add(detail, 0, 4);
    }
    void BuildMusic(TabControl tabs) {
        var panel = Stack(Page(tabs, "音乐与提醒"));
        panel.Controls.Add(Label("提醒声音："));
        systemSoundChoice = new RadioButton { Text = "Windows 系统提示音（推荐：任何电脑都有，不用选文件）", AutoSize = true, Margin = new Padding(4, 4, 4, 4) };
        customMusicChoice = new RadioButton { Text = "播放我自己的音乐文件（MP3 / WAV，只在本机播放，不上传）", AutoSize = true, Margin = new Padding(4, 4, 4, 4) };
        panel.Controls.Add(systemSoundChoice); panel.Controls.Add(customMusicChoice);
        musicPath = new TextBox { Width = 810, Text = settings.musicPath ?? "" }; panel.Controls.Add(musicPath);
        musicBrowse = Button("浏览音乐文件", delegate { using (var picker = new OpenFileDialog { Filter = "音乐文件 (*.mp3;*.wav)|*.mp3;*.wav", CheckFileExists = true, Multiselect = false }) if (picker.ShowDialog(this) == DialogResult.OK) musicPath.Text = picker.FileName; });
        panel.Controls.Add(musicBrowse);
        SyncMusicControls();
        systemSoundChoice.CheckedChanged += delegate { if (systemSoundChoice.Checked) ApplyMusicMode(false); };
        customMusicChoice.CheckedChanged += delegate { if (customMusicChoice.Checked) ApplyMusicMode(true); };
        panel.Controls.Add(Label("音量（0～100，仅调整本程序的音乐，不改变系统音量）："));
        volume = new NumericUpDown { Minimum = 0, Maximum = 100, Value = Math.Max(0, Math.Min(100, settings.volume)), Width = 140 }; panel.Controls.Add(volume);
        loop = new CheckBox { Text = "循环播放，直到点击“知道了”或关闭提醒弹窗", Checked = settings.loop, AutoSize = true, Margin = new Padding(4, 15, 4, 15) }; panel.Controls.Add(loop);
        var buttons = new FlowLayoutPanel { Width = 820, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
        buttons.Controls.Add(Button("保存音乐设置", delegate { try { settings.musicPath = customMusicChoice.Checked ? AlertMusic.ValidatePath(musicPath.Text) : ""; settings.volume = (int)volume.Value; settings.loop = loop.Checked; Store.Save("settings.json", settings); SyncMusicControls(); musicStatus.Text = customMusicChoice.Checked ? "已保存：提醒时播放这个文件。" : "已保存：提醒时播放 Windows 系统提示音。"; } catch (ArgumentException ex) { musicStatus.Text = ex.Message; } }));
        buttons.Controls.Add(Button("试听 10 秒", delegate { try { previewTimer.Stop(); AlertMusic.Play(customMusicChoice.Checked ? AlertMusic.ValidatePath(musicPath.Text) : "", (int)volume.Value, loop.Checked); previewTimer.Start(); musicStatus.Text = "正在试听，可随时点“停止音乐”。"; } catch (Exception ex) { musicStatus.Text = "音乐未能播放：" + ex.Message; } }));
        buttons.Controls.Add(Button("测试弹窗与音乐", delegate { ShowAlert("测试：桌面弹窗与音乐", "这是一条测试提醒，不是实际评论或新视频变化。\r\n正式提醒会显示任务名称、变化前后正文或新视频标题。", null, true); })); panel.Controls.Add(buttons);
        musicStatus = Label("系统提示音不需要任何文件；选自己的音乐时路径只保存在本机。测试弹窗使用已保存的设置。"); panel.Controls.Add(musicStatus);
        panel.Controls.Add(Label("每个任务可独立开启 / 关闭邮件、弹窗和音乐。建议需要循环音乐的任务同时启用弹窗。\r\n只播放本机文件或系统提示音，不上传音乐；点“知道了”、关闭弹窗或退出程序都会停止音乐。\r\nChrome 和程序必须保持运行；电脑休眠时无法检查，锁屏时弹窗要解锁后才能看见。"));
    }
    void ApplyMusicMode(bool useCustom) { musicPath.Enabled = useCustom; musicBrowse.Enabled = useCustom; }
    void SyncMusicControls() {
        bool useCustom = !String.IsNullOrEmpty(settings.musicPath);
        systemSoundChoice.Checked = !useCustom; customMusicChoice.Checked = useCustom;
        musicPath.Text = settings.musicPath ?? ""; ApplyMusicMode(useCustom);
    }
    void BuildHistory(TabControl tabs) {
        var page = Page(tabs, "提醒记录");
        history = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
        historyInfo = new Label { AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = Color.FromArgb(75, 91, 111), Margin = new Padding(4, 9, 12, 6) };
        bar.Controls.Add(historyInfo); bar.Controls.Add(Button("清空提醒记录", delegate { ClearNotices(); }));
        page.Controls.Add(history); page.Controls.Add(bar);
    }
    void ClearNotices() {
        List<Notice> kept = NoticeLog.KeepPending(notices);
        int dropped = notices.Count - kept.Count;
        if (dropped == 0) { historyInfo.Text = "提醒记录里没有可清空的条目（" + kept.Count + " 条都还在等邮件发出）。"; return; }
        string warning = kept.Count > 0
            ? "有 " + kept.Count + " 条提醒的邮件还没发出去，这些记录会保留以便重发；其余 " + dropped + " 条将被清空。确定继续吗？"
            : "确定清空全部 " + dropped + " 条提醒记录吗？只删记录，不影响监控基准，已经提醒过的内容不会重新提醒。";
        if (MessageBox.Show(this, warning, "确认清空提醒记录", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        notices = kept; Store.Save("notices.json", notices); RefreshHistory();
        historyInfo.Text = kept.Count > 0 ? "已清空 " + dropped + " 条，保留 " + kept.Count + " 条邮件待发送的记录，发出后可以再清一次。" : "已清空 " + dropped + " 条提醒记录。";
    }
    void BuildConnection(TabControl tabs) {
        var panel = Stack(Page(tabs, "连接与邮箱"));
        connection = Label("等待新版浏览器助手报告。旧助手升级后需在 Chrome 扩展管理页面点一次“重新加载”。"); panel.Controls.Add(connection);
        var buttons = new FlowLayoutPanel { Width = 860, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true }; buttons.Controls.Add(Button("复制配对码", delegate { Clipboard.SetText(token); connection.Text = "配对码已复制，请仅粘贴到 Chrome 助手；不要发送到聊天。已配对通常无需重新粘贴。"; }));
        buttons.Controls.Add(Button("打开扩展文件夹", delegate { Process.Start("explorer.exe", "\"" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extension") + "\""); }));
        buttons.Controls.Add(Button("邮箱设置…", delegate { ConfigureMail(); })); buttons.Controls.Add(Button("发送测试邮件", delegate { QueueTestMail(); })); panel.Controls.Add(buttons);
        mailStatus = Label(""); panel.Controls.Add(mailStatus);
        var enabled = new CheckBox { Text = "启用邮件发送（关闭时保留待发记录，不发送）", AutoSize = true, Checked = settings.mailEnabled, Margin = new Padding(4, 15, 4, 15) };
        enabled.CheckedChanged += delegate { settings.mailEnabled = enabled.Checked; Store.Save("settings.json", settings); nextMail = DateTime.MinValue; UpdateMailLabel(); }; panel.Controls.Add(enabled);
        panel.Controls.Add(Label("首次配对：点击“复制配对码” → Chrome 拼图图标 → YouTube 多任务监控助手 → 粘贴后配对 / 恢复。\r\n邮箱地址与授权码只在本机设置窗口输入，授权码按当前 Windows 用户加密保存，不写进配置文件、不发送到任何网站。\r\n断网或读取失败不会当作内容变化；邮件失败会保留并在 15 分钟后重试。"));
    }
    void BuildSetup(TabControl tabs) {
        var panel = Stack(Page(tabs, "安装与迁移"));
        panel.Controls.Add(Label("配置文件：把“监控哪些目标 + 提醒方式”装进一个 .json 文件，换电脑时拷过去点“导入配置”即可，不必重新填写链接。\r\n文件里有你监控的视频与频道链接，请自己保管；配对码和邮箱授权码按当前 Windows 用户加密，不会写进文件，到新电脑需各填一次。"));
        var withBaseline = new CheckBox { Text = "附带监控基准（推荐：新电脑接着上次的结果比，不会漏掉刚发布的内容）", AutoSize = true, Checked = true, Margin = new Padding(4, 10, 4, 10) }; panel.Controls.Add(withBaseline);
        var buttons = new FlowLayoutPanel { Width = 820, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
        buttons.Controls.Add(Button("导出配置…", delegate { ExportConfig(withBaseline.Checked); }));
        buttons.Controls.Add(Button("导入配置…", delegate { ImportConfig(); }));
        buttons.Controls.Add(Button("创建桌面快捷方式", delegate { CreateShortcut(); }));
        buttons.Controls.Add(Button("打开程序文件夹", delegate { OpenFolder(AppDomain.CurrentDomain.BaseDirectory); }));
        buttons.Controls.Add(Button("打开数据文件夹", delegate { OpenFolder(Store.Root); })); panel.Controls.Add(buttons);
        var startup = new CheckBox { Text = "开机后自动启动本程序（Chrome 仍要开着，可在 Chrome 设置里勾选“开机时打开”）", AutoSize = true, Checked = StartupLink.Enabled(), Margin = new Padding(4, 12, 4, 12) };
        startup.CheckedChanged += delegate {
            try { if (startup.Checked) StartupLink.Enable(); else StartupLink.Disable(); migrationStatus.Text = startup.Checked ? "已设置为开机自动启动。" : "已取消开机自动启动。"; }
            catch (Exception ex) { startup.Checked = StartupLink.Enabled(); migrationStatus.Text = "未能修改开机启动：" + ex.Message; }
        }; panel.Controls.Add(startup);
        migrationStatus = Label("这里显示导出、导入、快捷方式与开机启动的执行结果。"); panel.Controls.Add(migrationStatus);
        panel.Controls.Add(Label("新电脑只需三件事：\r\n1. 解压程序文件夹，双击 PinnedWatch.exe；\r\n2. Chrome 扩展管理页选择“加载已解压的扩展程序”，指向文件夹里的 extension；\r\n3. 在“连接与邮箱”点“复制配对码”，粘贴到扩展弹窗里配对，然后导入配置文件。\r\n\r\n扩展弹窗里的配对码只需在同一个 Windows 用户下输入一次，之后自动沿用。"));
    }
    void ExportConfig(bool withBaseline) {
        using (var dialog = new SaveFileDialog { Filter = "监控配置 (*.json)|*.json", FileName = Migration.FileName(), Title = "导出配置文件" }) {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try {
                File.WriteAllText(dialog.FileName, Migration.Export(settings, tasks, states, withBaseline), new UTF8Encoding(false));
                migrationStatus.Text = "已导出 " + tasks.Count + " 项任务到\r\n" + dialog.FileName + "\r\n把这个文件拷到新电脑，在新程序里点“导入配置…”。";
            } catch (Exception ex) { migrationStatus.Text = "导出失败：" + ex.Message; }
        }
    }
    void ImportConfig() {
        using (var dialog = new OpenFileDialog { Filter = "监控配置 (*.json)|*.json", Title = "选择导出的配置文件" }) {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try {
                ConfigPackage package = Migration.Parse(File.ReadAllText(dialog.FileName, Encoding.UTF8));
                string warning = "即将导入：" + package.tasks.Count + " 项任务" + (package.baselines == null ? "（不含基准，新电脑首次读取只建立基准）" : "，其中 " + package.baselines.Count + " 项已含基准") +
                    "\r\n导出时间：" + (package.exportedAt ?? "未记录") + "\r\n\r\n导入会替换本机的全部任务和提醒设置（已有的提醒记录保留）。\r\n邮箱授权码与扩展配对码需要重新填写一次。\r\n\r\n确认导入？";
                if (MessageBox.Show(this, warning, "确认导入配置", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                migrationStatus.Text = Migration.Apply(package, settings, tasks, states);
                Store.Save("settings.json", settings);
                SyncMusicControls(); volume.Value = settings.volume; loop.Checked = settings.loop;
                SaveTasks(); UpdateMailLabel();
                migrationStatus.Text += "\r\nChrome 助手通常会在 30 秒内收到新任务，可到“监控任务”查看状态。";
            } catch (Exception ex) { migrationStatus.Text = "未能导入：" + ex.Message + "\r\n本机现有任务没有被改动。"; }
        }
    }
    static void OpenFolder(string path) { try { Process.Start("explorer.exe", "\"" + path + "\""); } catch (Exception ex) { MessageBox.Show("无法打开文件夹：" + ex.Message); } }
    void CreateShortcut() {
        try {
            string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "YouTube 监控面板.lnk");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new IOException("未找到 Windows 脚本宿主");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
            Type item = shortcut.GetType();
            item.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath });
            item.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { AppDomain.CurrentDomain.BaseDirectory });
            item.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath + ",0" });
            item.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "YouTube 多任务监控面板" });
            item.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            migrationStatus.Text = "桌面快捷方式已创建：YouTube 监控面板\r\n以后双击它即可打开面板（程序已在运行时只会提醒一次）。";
        } catch (Exception ex) { migrationStatus.Text = "未能创建快捷方式：" + ex.Message; }
    }
    WatchTask Selected() { return grid.CurrentRow == null ? null : grid.CurrentRow.Tag as WatchTask; }
    void EditTask(WatchTask task) {
        using (var editor = new TaskEditor(task)) {
            if (editor.ShowDialog(this) != DialogResult.OK) return;
            WatchTask next = editor.Result;
            if (task == null) { tasks.Add(next); states[next.id] = new TaskState(); }
            else { if (!Rules.SameTarget(task, next)) states[next.id] = new TaskState(); tasks[tasks.IndexOf(task)] = next; }
            SaveTasks();
        }
    }
    void SaveTasks() { Store.Save("tasks.json", tasks); Store.Save("task-states.json", states); forceCheck++; PublishTasks(); RefreshGrid(); }
    void PublishTasks() { taskResponse = new JavaScriptSerializer().Serialize(new { version = 2, forceCheck = forceCheck, tasks = tasks }); }
    void RefreshGrid() {
        string id = Selected() == null ? null : Selected().id; grid.Rows.Clear();
        foreach (var task in tasks) { TaskState state = states.Find(task.id); int row = grid.Rows.Add(task.enabled ? "是" : "暂停", task.name, Rules.ModeName(task.mode), task.intervalSeconds + "秒", state.checkedAt, task.enabled ? state.status : "已暂停"); grid.Rows[row].Tag = task; if (task.id == id) grid.CurrentCell = grid.Rows[row].Cells[1]; }
        ShowDetail();
    }
    void ShowDetail() {
        if (detail == null) return; WatchTask task = Selected(); if (task == null) { detail.Text = "选择任务查看详情，或点击“添加任务”。"; return; }
        TaskState state = states.Find(task.id);
        detail.Text = "任务：" + task.name + "\r\n链接：" + task.url + "\r\n通知：" + (task.popup ? "弹窗 " : "") + (task.email ? "邮件 " : "") + (task.music ? "音乐" : "") +
            (task.mode == "pinned" ? "\r\n评论作者：" + (task.author == "" ? "任意作者" : task.author) : task.mode == "comment" ? "\r\n评论编号：" + task.commentId : "\r\n累计已记录会员视频：" + state.seenVideos.Count) + "\r\n最近检查：" + state.checkedAt + "\r\n状态：" + state.status + (state.comment == null ? "" : "\r\n\r\n最近成功读取的评论：\r\n" + state.comment.text);
    }
    void RefreshHistory() {
        historyInfo.Text = "共 " + notices.Count + " 条记录，界面显示最近 " + Math.Min(100, notices.Count) + " 条；超过 " + NoticeLog.Keep + " 条会自动删掉最旧的已发出记录，还没发出邮件的条目优先保留。";
        history.Text = notices.Count == 0 ? "尚未发现变化。历史视频、点赞数及重复报告不会重复提醒。" : String.Join("\r\n\r\n────────────\r\n\r\n", notices.AsEnumerable().Reverse().Take(100).Select(n => n.at + "  " + n.taskName + "  " + n.kind + (n.wantsEmail ? n.sent ? " [邮件已发送]" : " [邮件待发送]" : " [不发邮件]") + "\r\n" + n.body));
    }
    string Recipient() { return String.IsNullOrEmpty(settings.mailTo) ? settings.mailUser : settings.mailTo; }
    bool MailReady() { return settings.mailEnabled && !String.IsNullOrEmpty(settings.password) && Rules.IsEmail(settings.mailUser) && Rules.IsEmail(Recipient()); }
    void UpdateMailLabel() {
        mailStatus.Text = "发件：" + (String.IsNullOrEmpty(settings.mailUser) ? "未填写" : settings.mailUser) + "｜收件：" + (String.IsNullOrEmpty(Recipient()) ? "未填写" : Recipient()) +
            "｜" + (!settings.mailEnabled ? "邮件已暂停" : !Rules.IsEmail(settings.mailUser) ? "请先填写邮箱地址" : String.IsNullOrEmpty(settings.password) ? "尚未填写 SMTP 授权码，待发邮件会保留" : "授权码已按当前 Windows 用户加密保存");
    }
    void Listen() {
        while (listener.IsListening) { HttpListenerContext context; try { context = listener.GetContext(); } catch (HttpListenerException) { return; } catch (ObjectDisposedException) { return; } Task.Run(delegate { HandleRequest(context); }); }
    }
    void HandleRequest(HttpListenerContext context) {
        try {
            var req = context.Request; var res = context.Response; string origin = req.Headers["Origin"] ?? "";
            if ((!String.IsNullOrEmpty(origin) && !Regex.IsMatch(origin, @"\Achrome-extension://[a-p]{32}\z")) || (!String.IsNullOrEmpty(origin) && !String.IsNullOrEmpty(settings.extensionOrigin) && origin != settings.extensionOrigin)) { res.StatusCode = 403; res.Close(); return; }
            if (!String.IsNullOrEmpty(origin)) res.Headers["Access-Control-Allow-Origin"] = origin; res.Headers["Vary"] = "Origin";
            if (req.HttpMethod == "OPTIONS") { res.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Monitor-Token"; res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS"; res.StatusCode = 204; res.Close(); return; }
            if (req.Headers["X-Monitor-Token"] != token) { res.StatusCode = 401; res.Close(); return; }
            if (req.HttpMethod == "GET" && req.Url.AbsolutePath == "/health") {
                BeginInvoke((Action)delegate { if (!String.IsNullOrEmpty(origin) && String.IsNullOrEmpty(settings.extensionOrigin)) { settings.extensionOrigin = origin; Store.Save("settings.json", settings); } connection.Text = "浏览器助手已配对，等待各任务报告。"; });
                Respond(res, "{\"version\":2,\"status\":\"配对成功\"}"); return;
            }
            if (req.HttpMethod == "GET" && req.Url.AbsolutePath == "/tasks") { Respond(res, taskResponse); return; }
            if (req.HttpMethod != "POST" || req.Url.AbsolutePath != "/observe") { res.StatusCode = 404; res.Close(); return; }
            if (req.ContentLength64 < 0 || req.ContentLength64 > 524288) { res.StatusCode = 413; res.Close(); return; }
            string json; using (var reader = new StreamReader(req.InputStream, Encoding.UTF8)) json = reader.ReadToEnd();
            var o = new JavaScriptSerializer().Deserialize<Observation>(json);
            var configured = new JavaScriptSerializer().Deserialize<TaskEnvelope>(taskResponse);
            WatchTask task = configured.tasks.Find(t => t.id == (o == null ? null : o.taskId) && t.enabled);
            if (task == null || !Rules.ValidObservation(o, task)) { res.StatusCode = 400; res.Close(); return; }
            observations.Enqueue(o); Respond(res, "{\"status\":\"报告已接收，桌面程序正在比较\"}");
        } catch { try { context.Response.StatusCode = 400; context.Response.Close(); } catch { } }
    }
    class TaskEnvelope { public List<WatchTask> tasks { get; set; } }
    static void Respond(HttpListenerResponse response, string json) { byte[] bytes = Encoding.UTF8.GetBytes(json); response.ContentType = "application/json; charset=utf-8"; response.ContentLength64 = bytes.Length; response.OutputStream.Write(bytes, 0, bytes.Length); response.Close(); }
    void Tick() {
        if (!checkNow.Enabled && DateTime.Now >= nextCheckAllowed) { checkNow.Enabled = true; checkNow.Text = "全部立即检查"; }
        bool changed = false; Observation o;
        while (observations.TryDequeue(out o)) {
            WatchTask task = tasks.Find(t => t.id == o.taskId && t.enabled && t.revision == o.revision); if (task == null) continue;
            TaskState state = states.Ensure(task.id);
            var fresh = Engine.Observe(task, state, o); notices.AddRange(fresh); NoticeLog.Trim(notices); changed = true;
            connection.Text = "助手已连接。最近收到任务报告：" + task.name + "，" + state.checkedAt;
            foreach (Notice notice in fresh) {
                if (notice.popup) ShowAlert(notice.kind, notice.body, notice.url, notice.music);
                else if (notice.music) PlayMusic();
            }
            if (fresh.Count > 0) { Store.Save("notices.json", notices); RefreshHistory(); nextMail = DateTime.MinValue; }
        }
        if (changed) { Store.Save("task-states.json", states); RefreshGrid(); }
        foreach (WatchTask task in tasks.Where(t => t.enabled)) {
            TaskState state = states.Ensure(task.id);
            if (!state.staleWarned && (DateTime.Now - state.lastReport).TotalSeconds > Math.Max(600, task.intervalSeconds * 2 + 120)) { state.staleWarned = true; state.status = "长时间没有收到报告，请检查助手、Chrome 和网络"; RefreshGrid(); connection.Text = task.name + "：" + state.status; }
        }
        MailResult result;
        while (mailResults.TryDequeue(out result)) {
            mailBusy = false;
            if (result.id != "test") { Notice n = notices.Find(x => x.id == result.id); if (n != null && result.ok) { n.sent = true; Store.Save("notices.json", notices); RefreshHistory(); } nextMail = result.ok ? DateTime.MinValue : DateTime.Now.AddMinutes(15); }
            mailStatus.Text = result.ok ? "邮件已被 163 接收，请检查收件箱或垃圾箱。" : "邮件发送失败；实际变化的待发邮件保留，15 分钟后重试。请检查 SMTP 设置和网络。";
        }
        if (!mailBusy && MailReady() && DateTime.Now >= nextMail) { Notice pending = notices.Find(n => n.wantsEmail && !n.sent); if (pending != null) StartMail(pending.id, pending.kind, pending.body); }
    }
    void StartMail(string id, string title, string body) {
        mailBusy = true; string password = Store.Unprotect(settings.password);
        string host = settings.smtpHost, user = settings.mailUser, recipient = Recipient();
        Task.Run(delegate { bool ok = false; try { SecureSmtp.Send(host, user, recipient, password, "[YouTube监控] " + title, body); ok = true; } catch { } mailResults.Enqueue(new MailResult { id = id, ok = ok }); });
    }
    void QueueTestMail() { if (!MailReady()) { mailStatus.Text = "请先在“邮箱设置”中填写服务器、发件邮箱和授权码，并启用邮件发送。"; return; } if (mailBusy) { mailStatus.Text = "正在发送其他邮件，请稍后再试。"; return; } StartMail("test", "邮件通道测试", "这是 YouTube 多任务监控程序的测试邮件。\r\n收到此邮件表示邮件通道可用。"); mailStatus.Text = "正在发送测试邮件…"; }
    void ConfigureMail() {
        using (var form = new Form { Text = "邮箱设置（信息只保存在本机）", ClientSize = new Size(700, 640), MinimumSize = new Size(620, 520), StartPosition = FormStartPosition.CenterParent, Font = Font, AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi, BackColor = Color.FromArgb(248, 250, 253), Icon = WindowIcon.Load(32) }) {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, RowCount = 7, Padding = new Padding(20) };
            for (int i = 0; i < 7; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            form.Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "程序用 SMTP（465 端口、SSL）直接发信，不经过任何第三方服务。\r\n只需三样：选服务商、填你的邮箱、填客户端授权码（不是邮箱登录密码）。", AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(620, 0), Margin = new Padding(3, 0, 3, 18) }, 0, 0);
            var providerCell = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, Margin = new Padding(3, 3, 3, 12) };
            providerCell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); providerCell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            providerCell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            providerCell.Controls.Add(new Label { Text = "邮箱服务商（选完下面两栏自动填好）", AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(620, 0), Margin = new Padding(0, 0, 0, 5) }, 0, 0);
            var provider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
            foreach (MailPreset preset in MailPresets.All) provider.Items.Add(preset.label);
            providerCell.Controls.Add(provider, 0, 1); layout.Controls.Add(providerCell, 0, 1);
            Label hostHelp, codeHelp;
            TextBox host = MailField(layout, 2, "SMTP 服务器", settings.smtpHost, "", out hostHelp);
            TextBox user = MailField(layout, 3, "你的邮箱地址（发件用的登录名）", settings.mailUser, "填完整地址，例如 xxx@163.com；程序就用这个账号登录发信。");
            TextBox password = MailField(layout, 4, "SMTP 客户端授权码", "", "按当前 Windows 用户加密保存在本机，不写进配置文件；之前填过可以留空。", out codeHelp);
            password.UseSystemPasswordChar = true;
            Action apply = delegate {
                MailPreset preset = MailPresets.At(provider.SelectedIndex);
                if (preset.host.Length > 0) host.Text = preset.host;
                host.ReadOnly = preset.host.Length > 0;
                hostHelp.Text = preset.serverHelp; codeHelp.Text = preset.codeHelp;
            };
            provider.SelectedIndexChanged += delegate { apply(); };
            provider.SelectedIndex = MailPresets.IndexOf(settings.smtpHost);
            var toCell = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3, Margin = new Padding(3, 3, 3, 12) };
            for (int i = 0; i < 3; i++) toCell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            toCell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var otherTo = new CheckBox { Text = "提醒发到另一个邮箱（默认发给自己，一般不用勾）", AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 5) };
            TextBox to = new TextBox { Dock = DockStyle.Top, Text = settings.mailTo ?? "" };
            bool sendElsewhere = !String.IsNullOrEmpty(settings.mailTo);
            otherTo.Checked = sendElsewhere; to.ReadOnly = !sendElsewhere;
            otherTo.CheckedChanged += delegate { to.ReadOnly = !otherTo.Checked; };
            toCell.Controls.Add(otherTo, 0, 0); toCell.Controls.Add(to, 0, 1);
            toCell.Controls.Add(new Label { Text = "不勾就发到你上面填的那个邮箱。", AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(620, 0), ForeColor = Color.FromArgb(75, 91, 111), Margin = new Padding(0, 5, 0, 0) }, 0, 2);
            layout.Controls.Add(toCell, 0, 5);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(3, 14, 3, 6) };
            var save = new Button { Text = "保存", AutoSize = true, MinimumSize = new Size(150, 40), Padding = new Padding(10, 6, 10, 6), Margin = new Padding(4) };
            var test = new Button { Text = "保存并发测试邮件", AutoSize = true, MinimumSize = new Size(190, 40), Padding = new Padding(10, 6, 10, 6), Margin = new Padding(4) };
            var cancel = new Button { Text = "取消", AutoSize = true, MinimumSize = new Size(120, 40), Padding = new Padding(10, 6, 10, 6), Margin = new Padding(4), DialogResult = DialogResult.Cancel };
            buttons.Controls.AddRange(new Control[] { save, test, cancel }); layout.Controls.Add(buttons, 0, 6);
            form.CancelButton = cancel;
            save.Click += delegate { if (SaveMailFields(host, user, to, password, form)) form.Close(); };
            test.Click += delegate { if (SaveMailFields(host, user, to, password, form)) { form.Close(); QueueTestMail(); } };
            form.ShowDialog(this);
        }
    }
    static TextBox MailField(TableLayoutPanel layout, int row, string caption, string value, string help) {
        Label ignored; return MailField(layout, row, caption, value, help, out ignored);
    }
    static TextBox MailField(TableLayoutPanel layout, int row, string caption, string value, string help, out Label helpLabel) {
        var cell = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3, Margin = new Padding(3, 3, 3, 12) };
        for (int i = 0; i < 3; i++) cell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cell.Controls.Add(new Label { Text = caption, AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(620, 0), Margin = new Padding(0, 0, 0, 5) }, 0, 0);
        var box = new TextBox { Dock = DockStyle.Top, Text = value ?? "" };
        cell.Controls.Add(box, 0, 1);
        helpLabel = new Label { Text = help ?? "", AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(620, 0), ForeColor = Color.FromArgb(75, 91, 111), Margin = new Padding(0, 5, 0, 0) };
        cell.Controls.Add(helpLabel, 0, 2);
        layout.Controls.Add(cell, 0, row);
        return box;
    }
    bool SaveMailFields(TextBox host, TextBox user, TextBox to, TextBox password, Form form) {
        string hostText = Store.Normalize(host.Text), userText = Store.Normalize(user.Text);
        string toText = to.ReadOnly ? "" : Store.Normalize(to.Text);
        string error = !Rules.IsHost(hostText) ? "SMTP 服务器填写不正确，例如 smtp.163.com。" :
            !Rules.IsEmail(userText) ? "邮箱地址填写不正确，需要包含 @ 的完整地址。" :
            toText.Length > 0 && !Rules.IsEmail(toText) ? "收件邮箱填写不正确；不勾上面那行就发给自己。" : null;
        if (error != null) { MessageBox.Show(form, error, "请检查邮箱设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        settings.smtpHost = hostText; settings.mailUser = userText; settings.mailTo = toText;
        if (!String.IsNullOrWhiteSpace(password.Text)) settings.password = Store.Protect(password.Text.Trim());
        Store.Save("settings.json", settings);
        password.Clear(); UpdateMailLabel(); nextMail = DateTime.MinValue;
        return true;
    }
    void StopMusic() { previewTimer.Stop(); AlertMusic.Stop(); }
    void PlayMusic() { previewTimer.Stop(); try { AlertMusic.Play(settings.musicPath, settings.volume, settings.loop); } catch (Exception ex) { musicStatus.Text = "音乐未能播放：" + ex.Message; System.Media.SystemSounds.Exclamation.Play(); } }
    void ShowAlert(string title, string body, string url, bool music) {
        if (alert == null || alert.IsDisposed) {
            alertCount = 0; alert = new Form { Text = "YouTube 监控提醒", AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(920, 690), MinimumSize = new Size(760, 550), TopMost = true, StartPosition = FormStartPosition.CenterScreen, BackColor = Color.FromArgb(255, 248, 235), Font = new Font("Microsoft YaHei UI", 12), Icon = WindowIcon.Load(32) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(20) }; layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); alert.Controls.Add(layout);
            alertTitle = new Label { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(3, 0, 3, 14), ForeColor = Color.FromArgb(185, 25, 35), Font = new Font("Microsoft YaHei UI", 23, FontStyle.Bold) }; layout.Controls.Add(alertTitle, 0, 0);
            alertBody = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Font = new Font("Microsoft YaHei UI", 13) }; layout.Controls.Add(alertBody, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            buttons.Controls.Add(Button("知道了（停止音乐）", delegate { alert.Close(); })); buttons.Controls.Add(Button("停止音乐", delegate { StopMusic(); }));
            var open = Button("打开最新目标", delegate { string target = alert.Tag as string; if (!String.IsNullOrEmpty(target)) Process.Start(target); }); buttons.Controls.Add(open); layout.Controls.Add(buttons, 0, 2);
            alert.FormClosed += delegate { StopMusic(); alertCount = 0; }; alert.Show();
        }
        alertCount++; alertTitle.Text = alertCount == 1 ? title : alertCount + " 条未确认提醒";
        alertBody.AppendText((alertCount == 1 ? "" : "\r\n\r\n────────────\r\n\r\n") + body); alert.Tag = url;
        alert.WindowState = FormWindowState.Normal; alert.Activate(); if (music) PlayMusic();
    }
}

public static class StartupLink {
    const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    const string Value = "YouTubeWatchMonitor";
    public static bool Enabled() { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && key.GetValue(Value) != null; }
    public static void Enable() { using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey)) key.SetValue(Value, "\"" + Application.ExecutablePath + "\""); }
    public static void Disable() { using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey)) if (key.GetValue(Value) != null) key.DeleteValue(Value); }
}

public static class Program {
    [STAThread] public static void Main(string[] args) {
        if (args.Length > 0) {
            try {
                if (args[0] == "--self-test") CoreTests.Run();
                else if (args[0] == "--audio-test" && args.Length == 2) { AlertMusic.Play(args[1], 15, true); Thread.Sleep(700); if (!AlertMusic.IsPlaying()) throw new Exception("Audio did not start"); AlertMusic.Stop(); if (AlertMusic.IsPlaying()) throw new Exception("Audio did not stop"); File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio-test-result.txt"), "PASS: local WAV playback, volume command, repeat command, stop", Encoding.UTF8); }
                else throw new ArgumentException("Unknown test mode");
            } catch (Exception ex) { AlertMusic.Stop(); File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-failure.txt"), ex.ToString(), Encoding.UTF8); Environment.ExitCode = 1; }
            return;
        }
        bool created; using (var mutex = new Mutex(true, "Local\\YouTubePinnedWatchMonitor", out created)) {
            if (!created) { MessageBox.Show("程序已经运行，请双击托盘图标打开面板。", "YouTube 监控"); return; }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new MonitorForm()); } catch (Exception ex) { MessageBox.Show("程序无法启动：" + ex.Message + "\r\n请保留现有配置，不要删除。", "YouTube 监控"); }
        }
    }
}
