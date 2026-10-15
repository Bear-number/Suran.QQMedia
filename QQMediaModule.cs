using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.FunctionCaller;
using Alife.Framework;
using Microsoft.Extensions.Logging;

namespace Suran.QQMedia;

public class QQMediaConfig
{
    [DisplayName("协议端地址")]
    [Description("OneBot正向WebSocket地址，语音条与视频段都从这里发出去")]
    public string WsUrl { get; set; } = "ws://127.0.0.1:3001";

    [DisplayName("下载·Python解释器")]
    [Description("装了 yt-dlp 的 python.exe 完整路径；留空则自动找 Alife 自带运行时里的 Python*，再找 PATH 上的 python 与 yt-dlp")]
    public string PythonPath { get; set; } = "";

    [DisplayName("下载·ffmpeg路径")]
    [Description("发视频要靠它把音视频合并成mp4。留空由 yt-dlp 自己在 PATH 里找")]
    public string FfmpegPath { get; set; } = "";

    [DisplayName("下载·代理地址")]
    [Description("只在 site=yt 时生效，如 http://127.0.0.1:7890；B站走国内直连，不需要代理")]
    public string ProxyUrl { get; set; } = "";

    [DisplayName("下载·音频目录")]
    [Description("相对 Alife Storage 的音频目录，与点歌技能共用同一份缓存")]
    public string MusicFolder { get; set; } = "Music";

    [DisplayName("下载·视频目录")]
    [Description("相对 Alife Storage 的视频目录")]
    public string VideoFolder { get; set; } = "Video";

    [DisplayName("下载·音频超时(秒)")]
    [Description("超时直接终止下载进程，音频默认60")]
    public int MusicTimeoutSeconds { get; set; } = 60;

    [DisplayName("下载·视频超时(秒)")]
    [Description("视频体积大，超时比音频宽，默认180")]
    public int VideoTimeoutSeconds { get; set; } = 180;

    [DisplayName("开关·发送媒体")]
    [Description("关闭后 SendMusic/SendVideo 都不可用，也不会发生下载")]
    public bool EnableSend { get; set; } = true;
}

[Module("QQ音视频发送",
    "把B站/YouTube的音频与视频下载成文件，以QQ语音条和视频消息发到当前群或私聊",
    defaultCategory: "苏染的工具")]
public class QQMediaModule(
    XmlFunctionCaller functionCaller,
    ILogger<QQMediaModule> logger,
    Interactor<QQMediaModule> interactor
) : ChatBehaviour, IConfigurable<QQMediaConfig>
{
    public QQMediaConfig Configuration { get; set; } = null!;

    const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    const string ProbeBiliUrl = "https://www.bilibili.com";
    const string ProbeYouTubeUrl = "https://www.youtube.com";
    const int ProbeTimeoutSeconds = 8;
    const int DownloadAttempts = 3;

    static readonly SemaphoreSlim downloadLock = new(1, 1);
    static string ytDlpFileName = "";
    static string ytDlpPrefix = "";
    static string ytDlpVersion = "";
    static ClientWebSocket? sharedWs;
    static readonly SemaphoreSlim wsLock = new(1, 1);
    static string providerName = "";
    static bool providerResolved;

    protected override Task OnAwake()
    {
        XmlHandler handler = new(this)
        {
            Description = "点歌与发视频：从B站（或带代理的YouTube）下载音频/视频，以QQ语音条或视频消息发出。",
            Explanation = "SendMusic 发语音条（m4a），SendVideo 发视频（mp4），两者都是先下载再发送，B站不需要登录也不需要代理。"
                + "同名的音频/视频已在本地缓存时直接复用，不再重新下载。"
                + "发不出去或下载失败先跑 MediaEnv，它会说明用的是哪个 yt-dlp、ffmpeg 在不在、B站与YouTube通不通、协议端是哪个端。"
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);
        return Task.CompletedTask;
    }

    // ============================================================
    // AI 函数
    // ============================================================

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按歌名搜索并下载音频，以QQ语音条发送。site留空走B站（国内直连），填yt走YouTube（需在配置里填代理）；本地已有同名缓存时直接复用")]
    public async Task SendMusic(
        [Description("歌名关键词，可带歌手名，如 极乐净土 GARNiDELiA")] string keyword,
        [Description("目标群号或QQ号")] long? targetId = null,
        [Description("group或private，默认group")] string? type = null,
        [Description("音源：留空=bilibili，yt=YouTube")] string? site = null)
    {
        if (Gate(Configuration.EnableSend, "发送媒体")) return;
        if (targetId == null) throw new Exception("没给目标，发群里还是私聊得说一个");
        if (keyword.Trim().Length == 0) throw new Exception("没给歌名");
        bool isGroup = !string.Equals(type, "private", StringComparison.OrdinalIgnoreCase);
        MediaFile media = await FetchMediaAsync(keyword, false, site);
        await SendAndReportAsync(media, targetId.Value, isGroup, "record", "语音");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("把视频下载成mp4以QQ视频消息发送（不是文件消息，对方可直接播）。source可以是BV号/av号/视频链接/b23.tv短链，也可以是搜索关键词；关键词搜索时site留空走B站、填yt走YouTube")]
    public async Task SendVideo(
        [Description("BV号、av号、视频链接或搜索关键词")] string source,
        [Description("目标群号或QQ号")] long? targetId = null,
        [Description("group或private，默认group")] string? type = null,
        [Description("音源：留空=bilibili，yt=YouTube，仅关键词搜索时生效")] string? site = null)
    {
        if (Gate(Configuration.EnableSend, "发送媒体")) return;
        if (targetId == null) throw new Exception("没给目标，发群里还是私聊得说一个");
        if (source.Trim().Length == 0) throw new Exception("没给视频来源");
        bool isGroup = !string.Equals(type, "private", StringComparison.OrdinalIgnoreCase);
        MediaFile media = await FetchMediaAsync(source, true, site);
        await SendAndReportAsync(media, targetId.Value, isGroup, "video", "视频");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("自检：用的是哪个 yt-dlp 与 ffmpeg、B站/YouTube直连与代理的连通性、音频视频目录在哪已缓存几个文件、协议端是哪个端")]
    public async Task MediaEnv()
    {
        List<string> lines = new();
        try
        {
            await EnsureYtDlpAsync();
            lines.Add("yt-dlp：" + ytDlpFileName + " " + ytDlpPrefix.Trim() + "（版本 " + ytDlpVersion + "）");
        }
        catch (Exception ytDlpError)
        {
            lines.Add("❌ yt-dlp：" + ytDlpError.Message);
        }

        string ffmpeg = Configuration.FfmpegPath.Trim();
        if (ffmpeg.Length > 0)
        {
            lines.Add("ffmpeg：配置为 " + ffmpeg + (File.Exists(ffmpeg) ? "（存在）" : "（不存在，发视频会失败）"));
        }
        else
        {
            lines.Add("ffmpeg：未配置，交给 yt-dlp 在 PATH 里找" + (FindOnPath("ffmpeg.exe") == null ? "（本机PATH里没找到，发视频大概率失败，建议在配置里填完整路径）" : "（本机PATH里有：" + FindOnPath("ffmpeg.exe") + "）"));
        }

        lines.Add("B站直连：" + await ProbeAsync(ProbeBiliUrl, null));
        lines.Add("YouTube直连：" + await ProbeAsync(ProbeYouTubeUrl, null));
        string proxy = Configuration.ProxyUrl.Trim();
        if (proxy.Length > 0)
        {
            lines.Add("YouTube经代理：" + await ProbeAsync(ProbeYouTubeUrl, proxy));
        }

        lines.Add("音频目录：" + DescribeFolder(false));
        lines.Add("视频目录：" + DescribeFolder(true));

        try
        {
            await ResolveProviderAsync();
            lines.Add("协议端：" + ProviderLabel() + "，地址 " + Configuration.WsUrl);
        }
        catch (Exception wsError)
        {
            lines.Add("❌ 协议端连不上（" + Configuration.WsUrl + "）：" + wsError.Message);
        }
        lines.Add("发送开关：" + (Configuration.EnableSend ? "开" : "关") + "，超时 音频" + TimeoutSeconds(false) + "秒/视频" + TimeoutSeconds(true) + "秒");
        interactor.Poke(string.Join("\n", lines));
    }

    // ============================================================
    // 下载
    // ============================================================

    sealed record MediaFile(string Path, string SourceUrl, bool FromCache);

    async Task<MediaFile> FetchMediaAsync(string query, bool isVideo, string? site)
    {
        await EnsureYtDlpAsync();
        string folder = Path.Combine(AlifePath.StorageFolderPath, (isVideo ? Configuration.VideoFolder : Configuration.MusicFolder).Trim());
        Directory.CreateDirectory(folder);
        string name = SafeFileName(CacheKey(query));
        string outPath = Path.Combine(folder, name + (isVideo ? ".mp4" : ".m4a"));

        string? cached = FindCachedFile(folder, name, isVideo);
        if (cached != null)
        {
            return new MediaFile(cached, "", true);
        }

        List<string> arguments = new();
        if (ytDlpPrefix.Length > 0)
        {
            arguments.Add(ytDlpPrefix.Trim());
        }
        arguments.Add("--no-simulate");
        arguments.Add("--quiet");
        arguments.Add("--no-progress");
        arguments.Add("--no-warnings");
        arguments.Add("--no-playlist");
        arguments.Add("--print after_move:filepath");
        arguments.Add("--print webpage_url");
        arguments.Add("-f " + (isVideo ? "\"bv*+ba/b\"" : "\"ba[ext=m4a]/ba\""));
        if (isVideo)
        {
            arguments.Add("--merge-output-format mp4");
            if (Configuration.FfmpegPath.Trim().Length > 0)
            {
                arguments.Add("--ffmpeg-location " + Quote(Configuration.FfmpegPath.Trim()));
            }
        }
        if (UseProxy(site))
        {
            arguments.Add("--proxy " + Quote(Configuration.ProxyUrl.Trim()));
        }
        arguments.Add("-o " + Quote(outPath));
        arguments.Add(Quote(BuildTarget(query, site)));

        string command = string.Join(" ", arguments);
        logger.LogInformation("[QQMedia] 下载 {Query}：{Command}", query, command);
        (int exitCode, string output, string error) = await RunDownloadAsync(command, TimeoutSeconds(isVideo));
        if (exitCode != 0)
        {
            string detail = error.Length > 0 ? error : output;
            throw new Exception("yt-dlp 退出码 " + exitCode + "（共试了 " + DownloadAttempts + " 次）：" + Shorten(detail) + LimitHint(detail));
        }

        string? filePath = null;
        string sourceUrl = "";
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string value = line.Trim();
            if (filePath == null && value.Length > 0 && File.Exists(value))
            {
                filePath = value;
            }
            else if (value.StartsWith("http"))
            {
                sourceUrl = value;
            }
        }
        if (filePath == null && File.Exists(outPath))
        {
            filePath = outPath;
        }
        if (filePath == null || new FileInfo(filePath).Length == 0)
        {
            throw new Exception("yt-dlp 跑完了但没产出文件：" + Shorten(output));
        }
        return new MediaFile(filePath, sourceUrl, false);
    }

    // 优先级：配置指定 → Alife 自带运行时的 Python*（版本号会变，不写死）→ PATH 上的 python → 独立 yt-dlp 可执行文件
    async Task EnsureYtDlpAsync()
    {
        if (ytDlpFileName.Length > 0)
        {
            return;
        }
        await downloadLock.WaitAsync();
        try
        {
            if (ytDlpFileName.Length > 0)
            {
                return;
            }
            List<(string FileName, string Prefix)> candidates = new();
            string configured = Configuration.PythonPath.Trim();
            if (configured.Length > 0)
            {
                candidates.Add((configured, "-m yt_dlp "));
            }
            string runtimeRoot = AlifePath.RuntimeFolderPath;
            List<string> runtimePythons = new();
            if (Directory.Exists(runtimeRoot))
            {
                foreach (string directory in Directory.GetDirectories(runtimeRoot, "Python*"))
                {
                    string candidate = Path.Combine(directory, "python.exe");
                    runtimePythons.Add(candidate);
                    candidates.Add((candidate, "-m yt_dlp "));
                }
            }
            candidates.Add(("python", "-m yt_dlp "));
            candidates.Add(("yt-dlp", ""));

            List<string> failures = new();
            foreach ((string fileName, string prefix) in candidates)
            {
                bool launchable = fileName == "python" || fileName == "yt-dlp" || File.Exists(fileName);
                if (launchable == false)
                {
                    failures.Add(fileName + " 不存在");
                    continue;
                }
                try
                {
                    (int exitCode, string output, string error) = await RunProcessAsync(fileName, prefix.Trim() + " --version", 30);
                    if (exitCode == 0 && Regex.IsMatch(output, @"\d{4}\.\d{2}"))
                    {
                        ytDlpFileName = fileName;
                        ytDlpPrefix = prefix;
                        ytDlpVersion = output.Trim();
                        logger.LogInformation("[QQMedia] yt-dlp 走 {FileName} {Prefix}", fileName, prefix);
                        return;
                    }
                    failures.Add(fileName + " " + Shorten(exitCode == 0 ? "版本输出认不出来：" + output : error));
                }
                catch (Exception probeError)
                {
                    failures.Add(fileName + " 起不来（" + probeError.Message + "）");
                }
            }
            throw new Exception("没找到可用的 yt-dlp。试过的：" + string.Join("；", failures)
                + "。解决办法：给 Alife 自带 Python 装（"
                + (runtimePythons.Count > 0 ? runtimePythons[0] + " -m pip install -U yt-dlp" : "Runtime 目录下没有 Python*，得自己准备一个装了 yt-dlp 的 python.exe")
                + "），或在插件配置「下载·Python解释器」里填路径");
        }
        finally
        {
            downloadLock.Release();
        }
    }

    // B站的搜索接口会间歇性回 412（本机实测同一个关键词、同一种起进程方式，约每两次就拒一次），
    // 与参数写法无关，所以这里带退避重试，而不是把一次失败当成结论
    async Task<(int ExitCode, string Output, string Error)> RunDownloadAsync(string command, int timeoutSeconds)
    {
        (int exitCode, string output, string error) = await RunProcessAsync(ytDlpFileName, command, timeoutSeconds);
        for (int attempt = 2; attempt <= DownloadAttempts && exitCode != 0; attempt++)
        {
            logger.LogInformation("[QQMedia] 下载失败，{Delay}秒后第{Attempt}次重试：{Error}", (attempt - 1) * 3, attempt, Shorten(error));
            await Task.Delay(TimeSpan.FromSeconds((attempt - 1) * 3), DestroyCancellationToken);
            (exitCode, output, error) = await RunProcessAsync(ytDlpFileName, command, timeoutSeconds);
        }
        return (exitCode, output, error);
    }

    async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(string fileName, string arguments, int timeoutSeconds)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        using Process process = new();
        process.StartInfo = startInfo;
        try
        {
            process.Start();
        }
        catch (Exception startError)
        {
            throw new Exception("起不动 " + fileName + "：" + startError.Message);
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await outputTask, await errorTask);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new Exception("下载超过 " + timeoutSeconds + " 秒，已终止 yt-dlp");
        }
    }

    int TimeoutSeconds(bool isVideo)
    {
        int raw = isVideo ? Configuration.VideoTimeoutSeconds : Configuration.MusicTimeoutSeconds;
        return Math.Clamp(raw, 10, 1800);
    }

    bool UseProxy(string? site) => NormalizeSite(site) == "yt" && Configuration.ProxyUrl.Trim().Length > 0;

    static string NormalizeSite(string? site)
    {
        string want = (site ?? "").Trim().ToLowerInvariant();
        return want == "youtube" ? "yt" : want;
    }

    static bool IsDirectLink(string query)
    {
        string trimmed = query.Trim();
        return trimmed.StartsWith("http://") || trimmed.StartsWith("https://");
    }

    // BV号/av号/链接原样交给 yt-dlp，关键词按 site 选 bilisearch / ytsearch
    static string BuildTarget(string query, string? site)
    {
        string trimmed = query.Trim();
        if (IsDirectLink(trimmed))
        {
            return trimmed;
        }
        if (Regex.IsMatch(trimmed, @"^(BV[0-9A-Za-z]{10}|av\d+)$", RegexOptions.IgnoreCase))
        {
            return "https://www.bilibili.com/video/" + trimmed;
        }
        return (NormalizeSite(site) == "yt" ? "ytsearch:" : "bilisearch:") + trimmed;
    }

    // BV号/av号做缓存名，避免把整条URL塞进文件名
    static string CacheKey(string query)
    {
        Match match = Regex.Match(query, @"(BV[0-9A-Za-z]{10}|av\d+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : query.Trim();
    }

    static readonly char[] invalidNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    static string SafeFileName(string raw)
    {
        StringBuilder builder = new();
        foreach (char c in raw)
        {
            builder.Append(char.IsWhiteSpace(c) || c < 32 || Array.IndexOf(invalidNameChars, c) >= 0 ? '_' : c);
        }
        string name = builder.ToString().Trim('_');
        return name.Length == 0 ? "media" : (name.Length > 60 ? name[..60] : name);
    }

    static string NormalizeName(string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        foreach (char c in " _-·、.")
        {
            baseName = baseName.Replace(c.ToString(), "");
        }
        return baseName.ToLowerInvariant();
    }

    static string? FindCachedFile(string folder, string key, bool isVideo)
    {
        string want = NormalizeName(key);
        if (want.Length == 0)
        {
            return null;
        }
        foreach (string file in Directory.GetFiles(folder))
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            bool matchedExtension = isVideo ? extension is ".mp4" or ".mkv" or ".webm" : extension is ".m4a" or ".mp3";
            if (matchedExtension == false)
            {
                continue;
            }
            string have = NormalizeName(Path.GetFileName(file));
            if ((have == want || have.StartsWith(want) || want.StartsWith(have)) && new FileInfo(file).Length > 0)
            {
                return file;
            }
        }
        return null;
    }

    string DescribeFolder(bool isVideo)
    {
        string folder = Path.Combine(AlifePath.StorageFolderPath, (isVideo ? Configuration.VideoFolder : Configuration.MusicFolder).Trim());
        if (Directory.Exists(folder) == false)
        {
            return folder + "（还没建，第一次下载时自动创建）";
        }
        long bytes = 0;
        int count = 0;
        foreach (string file in Directory.GetFiles(folder))
        {
            bytes += new FileInfo(file).Length;
            count++;
        }
        return folder + "（" + count + " 个文件，" + SizeText(bytes) + "）";
    }

    // ============================================================
    // 发送
    // ============================================================

    async Task SendAndReportAsync(MediaFile media, long target, bool isGroup, string segmentType, string noun)
    {
        bool success;
        string failure = "";
        try
        {
            (success, failure) = await SendSegmentAsync(target, isGroup, segmentType, media.Path);
        }
        catch (Exception sendError)
        {
            success = false;
            failure = sendError.Message;
        }
        if (success == false)
        {
            interactor.Poke("❌ " + noun + "已下载到本地但没发出去：" + failure + "\n文件：" + media.Path);
            return;
        }
        FileInfo info = new(media.Path);
        string source = media.FromCache ? "本地缓存，没重新下载" : media.SourceUrl;
        interactor.Poke("✅ " + noun + "已发送：" + Path.GetFileNameWithoutExtension(media.Path)
            + "（" + SizeText(info.Length) + "，" + source + "）");
    }

    async Task<(bool Success, string Message)> SendSegmentAsync(long target, bool isGroup, string segmentType, string filePath)
    {
        await ResolveProviderAsync();
        JsonArray segments = new();
        segments.Add(new JsonObject
        {
            ["type"] = segmentType,
            ["data"] = new JsonObject { ["file"] = filePath }
        });
        string action = isGroup ? "send_group_msg" : "send_private_msg";
        JsonObject parameters = new()
        {
            [isGroup ? "group_id" : "user_id"] = target,
            ["message"] = segments
        };
        (int retcode, string message, JsonElement data) = await SendActionAsync(action, parameters);
        if (retcode != 0)
        {
            return (false, ProviderLabel() + " 返回 " + retcode + "：" + message + FailureHint(retcode));
        }
        // 三端成功都带 message_id；LLOneBot 对不认识的段不报错直接丢，所以只认 retcode 不够
        if (data.ValueKind != JsonValueKind.Object || data.TryGetProperty("message_id", out _) == false)
        {
            return (false, ProviderLabel() + " 回成功但没给 message_id，" + segmentType + " 段可能被静默丢弃");
        }
        return (true, "");
    }

    string FailureHint(int retcode) => retcode switch
    {
        1404 => "（" + ProviderLabel() + " 未实现该接口）",
        1400 or 1200 => "（" + ProviderLabel() + " 不认识这个消息段，整条已退回；record/video 是 OneBot 标准段，多数是本地文件路径读不到）",
        _ => ""
    };

    string ProviderLabel() => providerName.Length > 0 ? providerName : "协议端";

    async Task ResolveProviderAsync()
    {
        if (providerResolved)
        {
            return;
        }
        providerResolved = true;
        (int retcode, string message, JsonElement data) = await SendActionAsync("get_version_info", new JsonObject());
        if (retcode != 0)
        {
            throw new Exception("get_version_info 返回 " + retcode + "：" + message);
        }
        providerName = (GetText(data, "app_name") + " " + GetText(data, "name") + " " + GetText(data, "vendor")).Trim();
        logger.LogInformation("[QQMedia] 协议端：{Provider} {Version}", providerName, GetText(data, "app_version"));
    }

    async Task<(int Retcode, string Message, JsonElement Data)> SendActionAsync(string action, JsonObject parameters)
    {
        await wsLock.WaitAsync();
        try
        {
            Exception? lastError = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return await SendActionOnceAsync(action, parameters);
                }
                catch (Exception attemptError)
                {
                    lastError = attemptError;
                    sharedWs?.Dispose();
                    sharedWs = null;
                }
            }
            throw new Exception(action + " 失败：" + lastError!.Message);
        }
        finally
        {
            wsLock.Release();
        }
    }

    async Task<(int Retcode, string Message, JsonElement Data)> SendActionOnceAsync(string action, JsonObject parameters)
    {
        if (sharedWs == null || sharedWs.State != WebSocketState.Open)
        {
            sharedWs?.Dispose();
            ClientWebSocket connection = new();
            using CancellationTokenSource connectTimeout = new(TimeSpan.FromSeconds(10));
            await connection.ConnectAsync(new Uri(Configuration.WsUrl), connectTimeout.Token);
            sharedWs = connection;
        }
        string echo = Guid.NewGuid().ToString();
        JsonObject payload = new()
        {
            ["action"] = action,
            ["params"] = parameters,
            ["echo"] = echo
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        byte[] receiveBuffer = new byte[64 * 1024];
        await sharedWs.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload.ToJsonString())),
            WebSocketMessageType.Text, true, timeout.Token);
        while (timeout.IsCancellationRequested == false)
        {
            MemoryStream buffer = new();
            WebSocketReceiveResult received;
            do
            {
                received = await sharedWs.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), timeout.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    throw new Exception("协议端关闭了连接");
                }
                buffer.Write(receiveBuffer, 0, received.Count);
            } while (received.EndOfMessage == false);

            using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
            // 事件推送与别的响应都会路过，只认自己 echo 的那一条
            if (document.RootElement.TryGetProperty("echo", out JsonElement echoElement) == false
                || echoElement.GetString() != echo)
            {
                continue;
            }
            int retcode = document.RootElement.TryGetProperty("retcode", out JsonElement retcodeElement)
                && retcodeElement.ValueKind == JsonValueKind.Number ? retcodeElement.GetInt32() : -1;
            JsonElement data = document.RootElement.TryGetProperty("data", out JsonElement dataElement)
                ? dataElement.Clone() : JsonDocument.Parse("null").RootElement.Clone();
            return (retcode, GetText(document.RootElement, "message"), data);
        }
        throw new Exception("等协议端响应超时");
    }

    // ============================================================
    // 杂项
    // ============================================================

    async Task<string> ProbeAsync(string url, string? proxy)
    {
        HttpClientHandler handler = new();
        if (proxy != null)
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(proxy);
        }
        using HttpClient client = new(handler);
        client.Timeout = TimeSpan.FromSeconds(ProbeTimeoutSeconds);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        try
        {
            using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, DestroyCancellationToken);
            return (int)response.StatusCode + "（通）";
        }
        catch (Exception probeError)
        {
            return "不通（" + probeError.GetType().Name + "）";
        }
    }

    static string? FindOnPath(string executable)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
            {
                continue;
            }
            string candidate = Path.Combine(directory.Trim(), executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    static string SizeText(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }
        return (bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB";
    }

    static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    // 本机实测：B站搜索接口会间歇性回 412，同一关键词、同一个调用方式大约每两次拒一次，与查询内容无关
    static string LimitHint(string text) => text.Contains("412")
        ? "\n（B站那边间歇性拒绝搜索，跟关键词写法无关，已自动重试过；等一两分钟再试，或换 site=yt 走YouTube）"
        : "";

    static string Shorten(string text)
    {
        string oneLine = Regex.Replace(text, "\\s+", " ").Trim();
        return oneLine.Length > 300 ? oneLine[..300] + "…" : oneLine;
    }

    static string GetText(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || element.TryGetProperty(name, out JsonElement value) == false)
        {
            return "";
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        };
    }

    bool Gate(bool enabled, string name)
    {
        if (enabled)
        {
            return false;
        }
        interactor.Poke(name + "功能已在插件配置中关闭");
        return true;
    }
}
