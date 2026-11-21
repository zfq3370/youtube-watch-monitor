using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Script.Serialization;

public class Comment { public string id; public string text; public string author; public string authorPath; }
public class MemberVideo { public string id; public string title; public bool memberOnly; public int position; }
public class WatchTask {
    public string id = Guid.NewGuid().ToString("N");
    public string revision = Guid.NewGuid().ToString("N");
    public string name; public string mode = "pinned"; public string url; public string author = ""; public string commentId = "";
    public int intervalSeconds = 120; public bool enabled = true; public bool popup = true; public bool email = true; public bool music = true;
}
public class Observation {
    public string taskId; public string revision; public string scanId; public bool ok; public string error;
    public Comment comment; public List<MemberVideo> videos; public string sourceUrl;
}
public class Settings {
    public string token; public string password; public string extensionOrigin;
    public string musicPath = ""; public int volume = 70; public bool loop = true; public bool mailEnabled = true;
    public string smtpHost = "smtp.163.com"; public string mailUser = ""; public string mailTo = "";
}
public class TaskState {
    public bool ready; public Comment comment; public List<string> seenVideos = new List<string>();
    public string checkedAt = ""; public string status = "等待首次检查"; public string lastScan;
    public int missingCount; public DateTime lastReport = DateTime.Now; public bool staleWarned;
}
public static class TaskStateLookup {
    // 删除任务的同一瞬间表格还会用被删的那一行刷新详情，此时按 id 取状态必须安全返回，不能抛异常。
    public static TaskState Find(this Dictionary<string, TaskState> states, string id) {
        TaskState state; return states != null && !String.IsNullOrEmpty(id) && states.TryGetValue(id, out state) && state != null ? state : new TaskState();
    }
    public static TaskState Ensure(this Dictionary<string, TaskState> states, string id) {
        TaskState state; if (states == null || String.IsNullOrEmpty(id)) return new TaskState();
        if (!states.TryGetValue(id, out state) || state == null) { state = new TaskState(); states[id] = state; }
        return state;
    }
}
public class Notice {
    public string id = Guid.NewGuid().ToString("N"); public string taskId; public string taskName;
    public string kind; public string at; public string body; public string url;
    public bool wantsEmail; public bool sent; public bool popup; public bool music;
}
public class MailResult { public string id; public bool ok; }
public static class NoticeLog {
    public const int Keep = 500;
    public const int HardCap = 1000;
    static bool Pending(Notice notice) { return notice.wantsEmail && !notice.sent; }
    // 记录只增不减迟早把 notices.json 撑大；先删最旧的“已发完/不发邮件”的，待发邮件尽量保住。
    public static int Trim(List<Notice> notices) {
        if (notices == null || notices.Count <= Keep) return 0;
        int removed = 0;
        List<Notice> settled = notices.FindAll(delegate(Notice n) { return !Pending(n); });
        for (int i = 0; i < settled.Count && notices.Count > Keep; i++) { notices.Remove(settled[i]); removed++; }
        while (notices.Count > HardCap) { notices.RemoveAt(0); removed++; }
        return removed;
    }
    public static List<Notice> KeepPending(List<Notice> notices) { return notices == null ? new List<Notice>() : notices.FindAll(Pending); }
}
public class NotifyProfile {
    public string musicPath = ""; public int volume = 70; public bool loop = true; public bool mailEnabled = true;
    public string smtpHost = "smtp.163.com"; public string mailUser = ""; public string mailTo = "";
}
public class TaskBaseline { public string taskId; public bool ready; public Comment comment; public List<string> seenVideos = new List<string>(); }
public class ConfigPackage {
    public string app = App.ConfigApp; public int schema = Migration.Schema;
    public string exportedAt; public List<WatchTask> tasks = new List<WatchTask>(); public NotifyProfile notify;
    public List<TaskBaseline> baselines;
}

// 配置文件只搬“看什么、怎么提醒”，配对码与邮箱授权码按当前 Windows 用户加密，换机后无法解密，因此永不写入。
public static class Migration {
    public const int Schema = 1;
    public static string FileName() { return "YouTube监控配置-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".json"; }

    static WatchTask Copy(WatchTask source) { return new WatchTask { id = source.id, revision = source.revision, name = source.name, mode = source.mode, url = source.url, author = source.author, commentId = source.commentId, intervalSeconds = source.intervalSeconds, enabled = source.enabled, popup = source.popup, email = source.email, music = source.music }; }
    static Comment Copy(Comment source) { return source == null ? null : new Comment { id = source.id, text = source.text, author = source.author, authorPath = source.authorPath }; }

    public static string Export(Settings settings, List<WatchTask> tasks, Dictionary<string, TaskState> states, bool withBaselines) {
        var package = new ConfigPackage {
            exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            tasks = (tasks ?? new List<WatchTask>()).ConvertAll(Copy),
            notify = new NotifyProfile {
                musicPath = settings.musicPath ?? "", volume = settings.volume, loop = settings.loop, mailEnabled = settings.mailEnabled,
                smtpHost = String.IsNullOrEmpty(settings.smtpHost) ? "smtp.163.com" : settings.smtpHost, mailUser = settings.mailUser ?? "", mailTo = settings.mailTo ?? ""
            }
        };
        if (withBaselines && states != null)
            package.baselines = package.tasks.FindAll(task => states.ContainsKey(task.id) && states[task.id].ready).ConvertAll(task => {
                TaskState state = states[task.id];
                return new TaskBaseline { taskId = task.id, ready = true, comment = Copy(state.comment), seenVideos = new List<string>(state.seenVideos) };
            });
        return new JavaScriptSerializer().Serialize(package);
    }

    public static ConfigPackage Parse(string json) {
        if (String.IsNullOrEmpty(json) || json.Length > 2 * 1024 * 1024) throw new ArgumentException("配置文件为空或过大。");
        ConfigPackage package = null;
        try { package = new JavaScriptSerializer().Deserialize<ConfigPackage>(json); } catch (ArgumentException) { }
        if (package == null || package.app != App.ConfigApp || package.tasks == null || package.notify == null) throw new ArgumentException("这不是本程序导出的配置文件，请确认选择的是“导出配置”生成的文件。");
        if (package.schema != Schema) throw new ArgumentException("配置文件版本为 " + package.schema + "，本程序支持版本 " + Schema + "，请先升级两端程序。");
        if (package.tasks.Count > 100) throw new ArgumentException("配置文件中的任务过多（上限 100 项）。");
        if (package.tasks.Any(task => task == null)) throw new ArgumentException("配置文件中存在不完整的任务条目。");
        if (package.tasks.ConvertAll(task => task.id).Distinct().Count() != package.tasks.Count) throw new ArgumentException("配置文件中存在重复的任务编号。");
        for (int i = 0; i < package.tasks.Count; i++) {
            WatchTask task = package.tasks[i];
            try { Rules.Validate(task); }
            catch (ArgumentException ex) { throw new ArgumentException("第 " + (i + 1) + " 项任务（" + task.name + "）：" + ex.Message); }
        }
        if (package.notify.volume < 0 || package.notify.volume > 100) package.notify.volume = 70;
        if (String.IsNullOrEmpty(package.notify.smtpHost)) package.notify.smtpHost = "smtp.163.com";
        return package;
    }

    public static string Apply(ConfigPackage package, Settings settings, List<WatchTask> tasks, Dictionary<string, TaskState> states) {
        tasks.Clear(); states.Clear();
        foreach (WatchTask task in package.tasks) {
            tasks.Add(task);
            TaskBaseline saved = package.baselines == null ? null : package.baselines.Find(item => item.taskId == task.id);
            states[task.id] = saved == null ? new TaskState() : new TaskState { ready = true, comment = Copy(saved.comment), seenVideos = saved.seenVideos ?? new List<string>(), status = "已恢复导出时的基准", lastReport = DateTime.Now };
        }
        NotifyProfile notify = package.notify;
        settings.smtpHost = notify.smtpHost;
        if (!String.IsNullOrEmpty(notify.mailUser)) settings.mailUser = notify.mailUser;
        if (!String.IsNullOrEmpty(notify.mailTo)) settings.mailTo = notify.mailTo;
        settings.mailEnabled = notify.mailEnabled;
        settings.volume = Math.Max(0, Math.Min(100, notify.volume));
        settings.loop = notify.loop;
        bool musicMissing = !String.IsNullOrEmpty(notify.musicPath) && !File.Exists(notify.musicPath);
        settings.musicPath = musicMissing || String.IsNullOrEmpty(notify.musicPath) ? settings.musicPath : notify.musicPath;
        var notes = new List<string> { "已导入 " + tasks.Count + " 项任务" + (package.baselines == null ? "（首次读取只建立基准）" : "，其中 " + package.baselines.Count + " 项已恢复基准") };
        if (musicMissing) notes.Add("音乐文件在本机不存在，请到“音乐与提醒”重新选择");
        if (String.IsNullOrEmpty(settings.password)) notes.Add("邮箱授权码不随文件搬家，请到“邮箱设置”重新输入");
        return String.Join("；", notes.ToArray()) + "。";
    }
}

public static class App {
    // 发布时三处版本号需一致：此处、VERSION 文件、extension/manifest.json（测试会检查）。
    public const string Version = "2.2.2";
    public const string ConfigApp = "youtube-watch-monitor";
}

public class MailPreset { public string label; public string host; public string serverHelp; public string codeHelp; }
public static class MailPresets {
    // 授权码获取路径按 2026-11 各服务商官方帮助页核实；界面只提示入口，不代替用户操作网页。
    public static readonly MailPreset[] All = {
        new MailPreset { label = "163 免费邮", host = "smtp.163.com", serverHelp = "已按服务商填好，一般不用改。",
            codeHelp = "获取授权码：登录 mail.163.com → 顶部“设置” → “POP3/SMTP/IMAP” → 开启 IMAP/SMTP 服务 → 点“新增授权码”（要手机验证），把生成的那串填到上面。" },
        new MailPreset { label = "126 邮箱", host = "smtp.126.com", serverHelp = "已按服务商填好，一般不用改。",
            codeHelp = "获取授权码：登录 mail.126.com → 顶部“设置” → “POP3/SMTP/IMAP” → 开启 IMAP/SMTP 服务 → 点“新增授权码”（要手机验证）。" },
        new MailPreset { label = "QQ 邮箱", host = "smtp.qq.com", serverHelp = "已按服务商填好，一般不用改。",
            codeHelp = "获取授权码：登录 mail.qq.com → 顶部“设置” → “账号” → 找到“POP3/IMAP/SMTP…”服务 → 开启 IMAP/SMTP → 点“生成授权码”（要 QQ 安全验证）。" },
        new MailPreset { label = "其他邮箱（自己填服务器）", host = "", serverHelp = "填你邮箱服务商的 SMTP 服务器地址，须支持 465 端口 SSL；授权码在它自己的网页设置里获取。",
            codeHelp = "授权码一般在该邮箱网页设置的“POP3/IMAP/SMTP”或“账户与安全”里生成；不是邮箱登录密码。Gmail、Outlook 等在国内无法直连，不建议用。" }
    };
    public static int IndexOf(string host) {
        string value = (host ?? "").Trim();
        for (int i = 0; i < All.Length; i++) if (All[i].host.Length > 0 && All[i].host.Equals(value, StringComparison.OrdinalIgnoreCase)) return i;
        return All.Length - 1;
    }
    public static MailPreset At(int index) { return index >= 0 && index < All.Length ? All[index] : All[All.Length - 1]; }
}

public static class Store {
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouTubePinnedWatch");
    public static T Load<T>(string name) where T : class {
        string path = Path.Combine(Root, name);
        return File.Exists(path) ? new JavaScriptSerializer().Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)) : null;
    }
    public static void Save(string name, object value) {
        Directory.CreateDirectory(Root);
        string path = Path.Combine(Root, name), temp = path + ".tmp";
        File.WriteAllText(temp, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
    }
    public static string Protect(string value) { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)); }
    public static string Unprotect(string value) { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
    public static string Normalize(string value) { return Regex.Replace(value ?? "", @"\s+", " ").Trim(); }
    public static string Key(Comment c) { return c == null ? "(none)" : c.id + "\n" + Normalize(c.text); }
}

public static class Rules {
    public static string ModeName(string mode) { return mode == "comment" ? "指定评论变更" : mode == "members" ? "会员专属新视频" : "博主置顶评论"; }
    public static bool VideoId(string id) { return Regex.IsMatch(id ?? "", @"\A[A-Za-z0-9_-]{11}\z"); }
    public static bool CommentId(string id) { return Regex.IsMatch(id ?? "", @"\A[A-Za-z0-9_.-]{10,200}\z"); }
    public static bool IsEmail(string value) { return Regex.IsMatch((value ?? "").Trim(), @"\A[^@\s""<>]{1,64}@[a-zA-Z0-9](?:[a-zA-Z0-9.-]{0,119}[a-zA-Z0-9])?\.[a-zA-Z]{2,24}\z"); }
    public static bool IsHost(string value) { return Regex.IsMatch((value ?? "").Trim(), @"\A[a-zA-Z0-9.-]{1,100}\z"); }
    public static string Author(string value) {
        value = (value ?? "").Trim();
        if (value == "") return "";
        Uri uri;
        if (Uri.TryCreate(value, UriKind.Absolute, out uri) && (uri.Host == "www.youtube.com" || uri.Host == "youtube.com")) value = Uri.UnescapeDataString(uri.AbsolutePath);
        if (value.StartsWith("/@")) value = value.Substring(1);
        if (value.StartsWith("UC") && Regex.IsMatch(value, @"\AUC[A-Za-z0-9_-]{22}\z")) value = "/channel/" + value;
        if (!value.StartsWith("/channel/") && !value.StartsWith("@")) value = "@" + value;
        if (!Regex.IsMatch(value, @"\A(@[^/?#\s]{1,100}|/channel/UC[A-Za-z0-9_-]{22})\z")) throw new ArgumentException("评论作者请填写 @用户名或 YouTube 频道链接。");
        return value;
    }
    public static bool MatchesAuthor(string wanted, Comment comment) {
        if (String.IsNullOrWhiteSpace(wanted)) return true;
        string actual = String.IsNullOrEmpty(comment.authorPath) ? comment.author : comment.authorPath;
        try { return String.Equals(Author(wanted), Author(actual), wanted.StartsWith("/channel/") ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }
    public static void Validate(WatchTask task) {
        if (task.mode != "comment" && task.mode != "pinned" && task.mode != "members") throw new ArgumentException("请选择监控类型。");
        if (task.intervalSeconds < 60 || task.intervalSeconds > 86400) throw new ArgumentException("刷新间隔应为 60～86400 秒。");
        string input = (task.url ?? "").Trim();
        if (!input.Contains("://")) input = "https://" + input;
        Uri uri;
        if (!Uri.TryCreate(input, UriKind.Absolute, out uri) || uri.Scheme != "https" || !String.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) throw new ArgumentException("请输入 HTTPS YouTube 链接。");
        var query = HttpUtility.ParseQueryString(uri.Query);
        string path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
        if (task.mode == "members") {
            if (uri.Host != "www.youtube.com" && uri.Host != "youtube.com") throw new ArgumentException("会员视频任务需要 YouTube 频道或会员专享列表链接。");
            string list = query["list"];
            if (path == "/playlist" && Regex.IsMatch(list ?? "", @"\AUUMO[A-Za-z0-9_-]{20,40}\z")) task.url = "https://www.youtube.com/playlist?list=" + Uri.EscapeDataString(list);
            else {
                var match = Regex.Match(path, @"\A(/@[^/?#\s]+|/channel/UC[A-Za-z0-9_-]{22})(/.*)?\z");
                if (!match.Success) throw new ArgumentException("请填频道首页链接或 UUMO 开头的会员专享播放列表链接；普通播放列表不能用于此规则。");
                task.url = "https://www.youtube.com" + match.Groups[1].Value;
            }
            task.author = ""; task.commentId = "";
        } else {
            string video = query["v"];
            if (uri.Host == "youtu.be") video = path.Trim('/');
            else if (uri.Host == "www.youtube.com" || uri.Host == "youtube.com") {
                if (path.StartsWith("/shorts/")) video = path.Substring(8);
                else if (path != "/watch") throw new ArgumentException("此任务需要视频链接，而不是频道链接。");
            } else throw new ArgumentException("仅支持 YouTube 视频链接。");
            if (!VideoId(video)) throw new ArgumentException("未找到有效的视频编号，请粘贴完整视频链接。");
            task.url = "https://www.youtube.com/watch?v=" + video;
            if (task.mode == "comment") {
                if (!String.IsNullOrWhiteSpace(query["lc"])) task.commentId = query["lc"];
                task.commentId = (task.commentId ?? "").Trim();
                if (!CommentId(task.commentId)) throw new ArgumentException("指定评论需要带 lc= 的评论链接，或手动填写评论编号。点击评论发布时间可取得该链接。");
                task.author = "";
            } else { task.author = Author(task.author); task.commentId = ""; }
        }
        task.name = Store.Normalize(task.name);
        if (task.name.Length == 0) task.name = ModeName(task.mode);
        if (task.name.Length > 100) throw new ArgumentException("任务名称最多 100 个字符。");
    }
    public static bool SameTarget(WatchTask a, WatchTask b) { return a.mode == b.mode && a.url == b.url && a.author == b.author && a.commentId == b.commentId; }
    public static bool ValidObservation(Observation o, WatchTask task) {
        if (o == null || o.revision != task.revision || !Regex.IsMatch(o.scanId ?? "", @"\A[A-Za-z0-9_-]{10,100}\z")) return false;
        if (!o.ok) return (o.error ?? "").Length <= 2000;
        if (task.mode == "members") {
            Uri uri;
            if (o.videos == null || o.videos.Count > 100 || !Uri.TryCreate(o.sourceUrl ?? "", UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Host != "www.youtube.com" || uri.AbsolutePath != "/playlist" || !Regex.IsMatch(HttpUtility.ParseQueryString(uri.Query)["list"] ?? "", @"\AUUMO[A-Za-z0-9_-]{20,40}\z")) return false;
            return o.videos.All(v => v != null && v.memberOnly && VideoId(v.id) && !String.IsNullOrWhiteSpace(v.title) && v.title.Length <= 1000) && o.videos.Select(v => v.id).Distinct().Count() == o.videos.Count;
        }
        if (o.comment == null) return task.mode == "pinned";
        return CommentId(o.comment.id) && !String.IsNullOrWhiteSpace(o.comment.text) && o.comment.text.Length <= 15000 &&
            (o.comment.author ?? "").Length <= 200 && (o.comment.authorPath ?? "").Length <= 300 && (task.mode != "comment" || o.comment.id == task.commentId);
    }
}

public static class Engine {
    public static List<Notice> Observe(WatchTask task, TaskState state, Observation o) {
        var notices = new List<Notice>();
        if (state.lastScan == o.scanId) return notices;
        state.lastScan = o.scanId; state.lastReport = DateTime.Now; state.staleWarned = false;
        state.checkedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        if (!o.ok) { state.missingCount = 0; state.status = "读取失败：" + Store.Normalize(o.error) + "（保留基准）"; return notices; }
        if (task.mode == "members") {
            var known = new HashSet<string>(state.seenVideos);
            if (!state.ready) {
                state.seenVideos = o.videos.Select(v => v.id).ToList(); state.ready = true;
                state.status = "已建立 " + state.seenVideos.Count + " 个会员视频基准，历史视频不提醒"; return notices;
            }
            int anchor = o.videos.FindIndex(v => known.Contains(v.id));
            if (anchor < 0 && known.Count > 0 && o.videos.Count > 0) { state.status = "列表与历史无重叠，暂不判定新视频；请检查列表或重新建立基准"; return notices; }
            int limit = known.Count == 0 ? o.videos.Count : Math.Max(0, anchor);
            foreach (MemberVideo video in o.videos.Take(limit).Where(v => !known.Contains(v.id))) {
                string url = "https://www.youtube.com/watch?v=" + video.id;
                notices.Add(Create(task, "发现新的会员专属视频", "会员专享列表中出现新视频：\r\n" + video.title + "\r\n\r\n" + url, url));
            }
            foreach (MemberVideo video in o.videos) if (known.Add(video.id)) state.seenVideos.Add(video.id);
            state.status = notices.Count > 0 ? "发现 " + notices.Count + " 个新的会员视频" : "会员列表正常，未发现新视频";
            return notices;
        }
        Comment next = o.comment;
        if (next != null) next.text = Store.Normalize(next.text);
        if (task.mode == "pinned" && next != null && !Rules.MatchesAuthor(task.author, next)) next = null;
        if (!state.ready) {
            state.comment = next; state.ready = true;
            state.status = next == null ? "基准已建立：当前没有匹配的置顶评论" : "基准已建立：目标评论已找到";
            return notices;
        }
        if (Store.Key(state.comment) == Store.Key(next)) { state.missingCount = 0; state.status = next == null ? "当前没有匹配的置顶评论" : "监控正常，正文未变"; return notices; }
        if (next == null && ++state.missingCount < 2) { state.status = "暂未发现目标置顶，等待另一次刷新确认"; return notices; }
        state.missingCount = 0;
        string kind = next == null ? "目标置顶评论已取消或移除" : state.comment == null ? "出现目标作者的置顶评论" : state.comment.id == next.id ? "评论正文已修改" : "置顶评论已替换";
        string link = task.url + (next == null ? "" : "&lc=" + Uri.EscapeDataString(next.id));
        string body = "变化前：\r\n" + (state.comment == null ? "没有匹配的置顶评论" : state.comment.text) + "\r\n\r\n变化后：\r\n" + (next == null ? "未发现目标置顶评论（两次独立刷新确认）" : next.text) + "\r\n\r\n" + link;
        notices.Add(Create(task, kind, body, link)); state.comment = next; state.status = kind;
        return notices;
    }
    public static Notice Create(WatchTask task, string kind, string body, string url) {
        string at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return new Notice { taskId = task.id, taskName = task.name, kind = kind, at = at, url = url,
            body = "任务：" + task.name + "\r\n类型：" + Rules.ModeName(task.mode) + "\r\n发现时间（电脑本地时间）：" + at + "\r\n\r\n" + body,
            wantsEmail = task.email, popup = task.popup, music = task.music };
    }
}

public static class AlertMusic {
    const string Alias = "qoderWatchMusic";
    // 提醒音要一直响到弹窗关闭：系统提示音本身只响一下，音乐文件也可能没循环成功，两种都由界面定时器按这个间隔补播。
    public const int RepeatIntervalMs = 3000;
    public static bool IsSystemSound(string path) { return String.IsNullOrEmpty(path); }
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int mciSendString(string command, StringBuilder result, int resultLength, IntPtr callback);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern bool mciGetErrorString(int code, StringBuilder text, int length);
    static void Command(string command) {
        int result = mciSendString(command, null, 0, IntPtr.Zero);
        if (result != 0) { var text = new StringBuilder(256); mciGetErrorString(result, text, text.Capacity); throw new IOException(text.ToString()); }
    }
    public static string ValidatePath(string path) {
        path = (path ?? "").Trim().Trim('"');
        if (path.Length == 0) return "";
        if (!Path.IsPathRooted(path) || path.StartsWith(@"\\") || path.IndexOf('"') >= 0 || !File.Exists(path)) throw new ArgumentException("音乐请填写本机已有文件的完整路径，或点击“浏览”。");
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".mp3" && ext != ".wav") throw new ArgumentException("请选择 MP3 或 WAV 音乐文件。");
        return Path.GetFullPath(path);
    }
    public static void Play(string path, int volume, bool loop) {
        Stop(); path = ValidatePath(path);
        if (path == "") { System.Media.SystemSounds.Exclamation.Play(); return; }
        try {
            Command("open \"" + path + "\" type mpegvideo alias " + Alias);
            Command("setaudio " + Alias + " volume to " + Math.Max(0, Math.Min(100, volume)) * 10);
            Command("play " + Alias + (loop ? " repeat" : ""));
        } catch { Stop(); throw; }
    }
    public static void Stop() { mciSendString("close " + Alias, null, 0, IntPtr.Zero); }
    public static bool IsPlaying() {
        var text = new StringBuilder(100);
        return mciSendString("status " + Alias + " mode", text, text.Capacity, IntPtr.Zero) == 0 && text.ToString() == "playing";
    }
}
