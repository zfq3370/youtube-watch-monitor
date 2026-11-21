using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

public static class CoreTests {
    static int count;
    static void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); count++; }
    static void Reject(Action action, string name) { try { action(); } catch (ArgumentException) { count++; return; } throw new Exception("FAIL: " + name); }
    static Comment C(string text, string id = "UgsyntheticComment00000001", string author = "/@samplechannel") { return new Comment { id = id, text = text, authorPath = author, author = "示例博主" }; }
    static Observation O(WatchTask task, Comment comment, bool ok = true) { return new Observation { taskId = task.id, revision = task.revision, scanId = Guid.NewGuid().ToString(), ok = ok, comment = comment, error = ok ? "" : "offline" }; }
    static Observation V(WatchTask task, params string[] ids) { return new Observation { taskId = task.id, revision = task.revision, scanId = Guid.NewGuid().ToString(), ok = true, sourceUrl = "https://www.youtube.com/playlist?list=UUMOsampleMemberList000000", videos = ids.Select((id, index) => new MemberVideo { id = id, title = "会员视频 " + id, memberOnly = true, position = index + 1 }).ToList() }; }
    public static void Run() {
        var task = new WatchTask { name = "test", url = "https://www.youtube.com/watch?v=TESTVIDEO01", author = "@samplechannel" };
        Rules.Validate(task); var state = new TaskState();
        Assert(Engine.Observe(task, state, O(task, C("old"))).Count == 0 && state.ready, "first baseline");
        Assert(Engine.Observe(task, state, O(task, C(" old \r\n"))).Count == 0, "whitespace ignored");
        var edit = O(task, C("new")); Assert(Engine.Observe(task, state, edit).Single().kind == "评论正文已修改", "edit");
        Assert(Engine.Observe(task, state, edit).Count == 0, "duplicate scan");
        Assert(Engine.Observe(task, state, O(task, C("replacement", "UgcommentReplacement22"))).Single().kind == "置顶评论已替换", "replacement");
        var miss = O(task, null); Assert(Engine.Observe(task, state, miss).Count == 0 && state.missingCount == 1, "first missing");
        Assert(Engine.Observe(task, state, miss).Count == 0 && state.missingCount == 1, "duplicate missing not confirmation");
        Assert(Engine.Observe(task, state, O(task, null, false)).Count == 0 && state.missingCount == 0 && state.comment != null, "failed read preserves baseline and resets missing");
        Assert(Engine.Observe(task, state, O(task, null)).Count == 0, "missing starts again");
        Assert(Engine.Observe(task, state, O(task, null)).Single().kind == "目标置顶评论已取消或移除", "two independent misses");
        Assert(Engine.Observe(task, state, O(task, C("other", "UgotherComment12345", "/@other"))).Count == 0 && state.comment == null, "non-target author excluded");
        Assert(Engine.Observe(task, state, O(task, C("target"))).Single().kind == "出现目标作者的置顶评论", "author appearance");
        var intervalOnly = new JavaScriptSerializer().Deserialize<WatchTask>(new JavaScriptSerializer().Serialize(task)); intervalOnly.intervalSeconds = 300; intervalOnly.name = "renamed"; intervalOnly.music = false;
        Assert(Rules.SameTarget(task, intervalOnly), "interval and notification changes keep baseline");
        intervalOnly.author = "@other"; Assert(!Rules.SameTarget(task, intervalOnly), "target change resets baseline");
        Assert(Rules.MatchesAuthor("@SAMPLECHANNEL", C("body")), "handles case insensitive");
        Assert(Rules.MatchesAuthor("/channel/UCsampleChannelId0000000", C("body", author: "/channel/UCsampleChannelId0000000")), "channel author ID");
        var specific = new WatchTask { mode = "comment", url = "https://www.youtube.com/watch?v=TESTVIDEO01&lc=UgnewComment12345", commentId = "UgoldComment12345" };
        Rules.Validate(specific); Assert(specific.commentId == "UgnewComment12345", "new pasted comment link updates exact ID");
        Assert(!Rules.ValidObservation(O(specific, C("wrong")), specific), "exact comment required");
        Assert(!Rules.ValidObservation(O(specific, null), specific), "missing exact comment is not change");
        Assert(Rules.ValidObservation(O(specific, C("good", specific.commentId)), specific), "valid exact comment");
        var stale = O(task, C("stale")); stale.revision = "old"; Assert(!Rules.ValidObservation(stale, task), "stale revision rejected");
        var memberTask = new WatchTask { mode = "members", url = "https://www.youtube.com/@samplechannel/videos" }; Rules.Validate(memberTask);
        Assert(memberTask.url == "https://www.youtube.com/@samplechannel", "channel canonical URL");
        state = new TaskState();
        Assert(Engine.Observe(memberTask, state, V(memberTask, "AAAAAAAAAAA", "BBBBBBBBBBB")).Count == 0 && state.seenVideos.Count == 2, "member baseline excludes historical alerts");
        Assert(Engine.Observe(memberTask, state, V(memberTask, "AAAAAAAAAAA", "BBBBBBBBBBB", "CCCCCCCCCCC")).Count == 0, "older backfill excluded");
        var memberScan = V(memberTask, "DDDDDDDDDDD", "EEEEEEEEEEE", "AAAAAAAAAAA", "BBBBBBBBBBB");
        Assert(Engine.Observe(memberTask, state, memberScan).Count == 2, "two new member videos");
        Assert(Engine.Observe(memberTask, state, V(memberTask, "DDDDDDDDDDD", "EEEEEEEEEEE", "AAAAAAAAAAA")).Count == 0, "member deduplication");
        Assert(Engine.Observe(memberTask, state, V(memberTask, "FFFFFFFFFFF")).Count == 0 && !state.seenVideos.Contains("FFFFFFFFFFF"), "no-overlap safe warning");
        var publicVideo = V(memberTask, "AAAAAAAAAAA"); publicVideo.videos[0].memberOnly = false; Assert(!Rules.ValidObservation(publicVideo, memberTask), "public video rejected");
        var duplicate = V(memberTask, "AAAAAAAAAAA", "AAAAAAAAAAA"); Assert(!Rules.ValidObservation(duplicate, memberTask), "duplicate video IDs rejected");
        Assert(Rules.ValidObservation(memberScan, memberTask), "valid member observation");
        Reject(delegate { Rules.Validate(new WatchTask { mode = "members", url = "https://www.youtube.com/playlist?list=PL12345" }); }, "ordinary playlist rejected");
        Reject(delegate { Rules.Validate(new WatchTask { url = "https://evil.example/watch?v=TESTVIDEO01" }); }, "foreign host rejected");
        Reject(delegate { Rules.Validate(new WatchTask { url = task.url, intervalSeconds = 30 }); }, "too-fast interval rejected");
        Reject(delegate { Rules.Validate(new WatchTask { url = "http://www.youtube.com/watch?v=TESTVIDEO01" }); }, "insecure URL rejected");
        Reject(delegate { Rules.Validate(new WatchTask { url = "https://user:password@www.youtube.com/watch?v=TESTVIDEO01" }); }, "URL credentials rejected");
        Reject(delegate { Rules.Validate(new WatchTask { url = "https://www.youtube.com:8443/watch?v=TESTVIDEO01" }); }, "nonstandard port rejected");
        var shortLink = new WatchTask { url = "https://youtu.be/TESTVIDEO01" }; Rules.Validate(shortLink); Assert(shortLink.url == task.url, "short link normalized");
        Assert(Store.Unprotect(Store.Protect("synthetic-local-test")) == "synthetic-local-test", "DPAPI roundtrip");
        Assert(new JavaScriptSerializer().Deserialize<Settings>("{\"token\":\"not-read\"}").volume == 70, "legacy settings defaults");
        Assert(new JavaScriptSerializer().Deserialize<Settings>("{}").smtpHost == "smtp.163.com", "mail server default");
        var packedSettings = new Settings { token = Store.Protect("local-pairing-secret"), password = Store.Protect("local-auth-secret"), extensionOrigin = "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", smtpHost = "smtp.example.com", mailUser = "me@example.com", volume = 40, loop = false };
        var packedStates = new Dictionary<string, TaskState> { { task.id, new TaskState { ready = true, comment = C("kept text") } }, { memberTask.id, new TaskState { ready = true, seenVideos = new List<string> { "AAAAAAAAAAA" } } } };
        var packedTasks = new List<WatchTask> { task, memberTask, specific };
        string packed = Migration.Export(packedSettings, packedTasks, packedStates, true);
        Assert(packed.IndexOf("token") < 0 && packed.IndexOf("password") < 0 && packed.IndexOf("extensionOrigin") < 0, "config file carries no machine secrets");
        Assert(packed.IndexOf("local-pairing-secret") < 0 && packed.IndexOf("local-auth-secret") < 0, "config file carries no decoded secrets");
        Assert(packed.Contains("smtp.example.com") && packed.Contains("me@example.com"), "config file carries mail addresses");
        ConfigPackage imported = Migration.Parse(packed);
        Assert(imported.tasks.Count == 3 && imported.baselines.Count == 2, "config roundtrip keeps tasks and baselines");
        Assert(imported.notify.volume == 40 && !imported.notify.loop, "config roundtrip keeps music settings");
        var localSettings = new Settings { token = packedSettings.token, password = packedSettings.password, extensionOrigin = packedSettings.extensionOrigin, musicPath = AppDomain.CurrentDomain.BaseDirectory + "x.mp3" };
        var localTasks = new List<WatchTask> { new WatchTask { name = "旧任务", url = "https://www.youtube.com/watch?v=TESTVIDEO01" } };
        var localStates = new Dictionary<string, TaskState>();
        string note = Migration.Apply(imported, localSettings, localTasks, localStates);
        Assert(localTasks.Count == 3 && localTasks.TrueForAll(t => t.revision != null), "import replaces tasks");
        Assert(localStates[task.id].ready && localStates[task.id].comment.text == "kept text", "import restores comment baseline");
        Assert(localStates[memberTask.id].seenVideos.Count == 1 && localStates[specific.id].ready == false, "import restores member baseline only where provided");
        Assert(localSettings.smtpHost == "smtp.example.com" && localSettings.mailUser == "me@example.com" && localSettings.volume == 40 && !localSettings.loop, "import applies notify profile");
        Assert(localSettings.token == packedSettings.token && localSettings.password == packedSettings.password && localSettings.extensionOrigin == packedSettings.extensionOrigin, "import keeps local pairing and secrets untouched");
        Assert(localSettings.musicPath == AppDomain.CurrentDomain.BaseDirectory + "x.mp3", "missing music path stays local");
        Assert(note.Contains("已导入 3 项") && note.Contains("恢复基准"), "import summary reports baselines");
        ConfigPackage bare = Migration.Parse(Migration.Export(packedSettings, packedTasks, packedStates, false));
        var bareTasks = new List<WatchTask>(); var bareStates = new Dictionary<string, TaskState>();
        string bareNote = Migration.Apply(bare, new Settings(), bareTasks, bareStates);
        Assert(bare.baselines == null && bareStates.Values.All(saved => !saved.ready), "export without baselines re-records everything");
        Assert(bareNote.Contains("首次读取只建立基准"), "summary warns about missing baselines");
        Reject(delegate { Migration.Parse("{\"app\":\"other-tool\",\"schema\":1,\"tasks\":[],\"notify\":{}}"); }, "foreign config rejected");
        Reject(delegate { Migration.Parse("{\"app\":\"youtube-watch-monitor\",\"schema\":9,\"tasks\":[],\"notify\":{}}"); }, "future schema rejected");
        Reject(delegate { Migration.Parse("{\"app\":\"youtube-watch-monitor\",\"schema\":1,\"tasks\":[{\"name\":\"坏的\",\"mode\":\"pinned\",\"url\":\"https://evil.example/watch?v=TESTVIDEO01\",\"intervalSeconds\":120}],\"notify\":{}}"); }, "unsafe task in config rejected");
        Reject(delegate { Migration.Parse("{\"app\":\"youtube-watch-monitor\",\"schema\":1,\"tasks\":[{\"id\":\"same\",\"mode\":\"pinned\",\"url\":\"https://www.youtube.com/watch?v=TESTVIDEO01\",\"intervalSeconds\":120},{\"id\":\"same\",\"mode\":\"pinned\",\"url\":\"https://www.youtube.com/watch?v=TESTVIDEO01\",\"intervalSeconds\":120}],\"notify\":{}}"); }, "duplicate task ids rejected");
        Reject(delegate { Migration.Parse("not json"); }, "non-json file rejected");
        string fakeSong = Path.Combine(Path.GetTempPath(), "qoder-watch-music-test.mp3");
        File.WriteAllText(fakeSong, "placeholder");
        var songLocal = new Settings { musicPath = "C:\\keep.mp3" };
        Migration.Apply(Migration.Parse(Migration.Export(new Settings { musicPath = fakeSong, volume = 25 }, packedTasks, packedStates, false)), songLocal, new List<WatchTask>(), new Dictionary<string, TaskState>());
        Assert(songLocal.musicPath == fakeSong && songLocal.volume == 25, "import adopts music file present on this machine");
        File.Delete(fakeSong);
        string missingNote = Migration.Apply(Migration.Parse(Migration.Export(new Settings { musicPath = @"C:\nope\missing.mp3" }, packedTasks, packedStates, false)), songLocal, new List<WatchTask>(), new Dictionary<string, TaskState>());
        Assert(songLocal.musicPath == fakeSong && missingNote.Contains("音乐文件在本机不存在"), "missing music keeps local path and warns");
        Assert(Rules.IsEmail("me@example.com") && Rules.IsEmail("someone@mail.example.co.uk") && !Rules.IsEmail("me@example") && !Rules.IsEmail("me@example.com>") && !Rules.IsEmail("me @example.com") && !Rules.IsEmail(""), "email validation");
        Assert(Rules.IsHost("smtp.163.com") && !Rules.IsHost("smtp.163.com/evil"), "smtp host validation");
        Assert(new JavaScriptSerializer().Deserialize<TaskState>(new JavaScriptSerializer().Serialize(state)).seenVideos.Count == state.seenVideos.Count, "persistent member baseline roundtrip");
        Assert(AlertMusic.ValidatePath("") == "", "empty music uses system sound");
        Reject(delegate { AlertMusic.ValidatePath("missing.mp3"); }, "missing music rejected");
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-result.txt"), "PASS: " + count + " checks; exact comments, pinned author changes, two-scan removal, failures, deduplication, member baseline/new-prefix/backfill/public exclusion, URL validation, persistence, DPAPI, music paths, config export/import without secrets", new UTF8Encoding(false));
    }
}
