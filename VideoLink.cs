using System;
using System.Text.RegularExpressions;

namespace Suran.QQMedia;

/// <summary>链接来源的类型。</summary>
public enum LinkKind
{
    /// <summary>纯 BV 号。</summary>
    Bvid,
    /// <summary>纯 av 号。</summary>
    Aid,
    /// <summary>b23.tv 短链（要跟一次重定向才知道是哪个视频）。</summary>
    ShortUrl,
    /// <summary>其它网页链接。</summary>
    WebUrl,
    /// <summary>不是链接，是搜索关键词。</summary>
    Keyword
}

/// <summary>解析后的视频来源。</summary>
public readonly record struct VideoSource(LinkKind Kind, string Id, string Url, string Keyword)
{
    /// <summary>是不是能下载的视频链接：BV/av/短链，或者 B 站、YouTube 的网页链接。</summary>
    public bool IsVideo => Kind is LinkKind.Bvid or LinkKind.Aid or LinkKind.ShortUrl
        || VideoLink.IsVideoHost(Url);

    public bool IsYouTube => Url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
        || Url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
}

/// <summary>把「BV 号 / av 号 / 链接 / 短链」归一成一条能下载的链接。纯函数，不联网。</summary>
public static class VideoLink
{
    static readonly Regex BvRx = new(@"BV[0-9A-Za-z]{10}", RegexOptions.Compiled);
    static readonly Regex AidInUrlRx = new(@"/video/av(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PureAidRx = new(@"^av(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ShortRx = new(@"https?://b23\.tv/[0-9A-Za-z]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex WebRx = new(@"https?://[^\s<>""'）】」，。；！？]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // 连紧跟的冒号一起吃掉：[2165860675(涼)]:正文
    static readonly Regex MarkerRx = new(@"\[[^\[\]]{0,120}\]:?", RegexOptions.Compiled);

    static readonly string[] VideoHosts =
    [
        "bilibili.com", "b23.tv", "youtube.com", "youtu.be",
    ];

    /// <summary>去掉宿主给消息加的方括号前缀。</summary>
    public static string StripMarkers(string? text) => MarkerRx.Replace(text ?? "", " ");

    /// <summary>解析来源；认不出来就当关键词。</summary>
    public static VideoSource Parse(string? source, string? site)
    {
        string text = (source ?? "").Trim();
        if (text.Length == 0)
            return new VideoSource(LinkKind.Keyword, "", "", "");

        Match shortMatch = ShortRx.Match(text);
        if (shortMatch.Success)
            return new VideoSource(LinkKind.ShortUrl, "", TrimUrl(shortMatch.Value), "");

        Match webMatch = WebRx.Match(text);
        if (webMatch.Success)
        {
            string url = TrimUrl(webMatch.Value);
            Match bvInUrl = BvRx.Match(url);
            if (bvInUrl.Success)
                return new VideoSource(LinkKind.Bvid, bvInUrl.Value, BiliUrl(bvInUrl.Value), "");
            Match aidInUrl = AidInUrlRx.Match(url);
            if (aidInUrl.Success)
                return new VideoSource(LinkKind.Aid, aidInUrl.Groups[1].Value, AidUrl(aidInUrl.Groups[1].Value), "");
            return new VideoSource(LinkKind.WebUrl, "", url, "");
        }

        Match bv = BvRx.Match(text);
        if (bv.Success)
            return new VideoSource(LinkKind.Bvid, bv.Value, BiliUrl(bv.Value), "");

        Match aid = PureAidRx.Match(text);
        if (aid.Success)
            return new VideoSource(LinkKind.Aid, aid.Groups[1].Value, AidUrl(aid.Groups[1].Value), "");

        return new VideoSource(LinkKind.Keyword, "", "", text);
    }

    static readonly string[] MusicHosts =
    [
        "music.163.com", "y.qq.com", "music.youtube.com", "open.spotify.com",
        "music.apple.com", "kuwo.cn", "kugou.com", "migu.cn",
    ];

    // 消息里出现这些词，按「要听歌」处理
    static readonly string[] AudioWords =
    [
        "歌", "音乐", "来首", "点歌", "伴奏", "翻唱", "纯音乐", "听歌", "唱首", "唱一",
        "bgm", "ost", "music", "song", "audio", "音频", "语音条",
    ];

    // 这些词明确要画面
    static readonly string[] VideoWords =
    [
        "视频", "画面", "片段", "回放", "视频版", "mv", "movie", "video",
    ];

    /// <summary>是不是音乐平台链接。</summary>
    public static bool IsMusicHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (url.Contains("/audio/au", StringComparison.OrdinalIgnoreCase))
            return true;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) == false)
            return false;
        string host = uri.Host.ToLowerInvariant();
        foreach (string music in MusicHosts)
        {
            if (host == music || host.EndsWith("." + music, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 这条链接该按音频还是视频处理。
    /// 音乐平台一律音频；否则看消息里说的是听歌还是看视频；都没说就按视频。
    /// </summary>
    public static bool LooksLikeAudio(string? message, VideoSource source)
    {
        if (IsMusicHost(source.Url))
            return true;
        string text = StripMarkers(message).ToLowerInvariant();
        // 明确要画面的优先：「这首歌的视频」不该当成点歌
        foreach (string word in VideoWords)
        {
            if (text.Contains(word, StringComparison.Ordinal))
                return false;
        }
        foreach (string word in AudioWords)
        {
            if (text.Contains(word, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static bool IsVideoHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) == false)
            return false;
        string host = uri.Host.ToLowerInvariant();
        foreach (string allowed in VideoHosts)
        {
            if (host == allowed || host.EndsWith("." + allowed, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static string BiliUrl(string bvid) => "https://www.bilibili.com/video/" + bvid;

    public static string AidUrl(string aid) => "https://www.bilibili.com/video/av" + aid;

    /// <summary>从消息前缀认出发图目标：群聊取群号，私聊取 QQ 号。</summary>
    public static (long Id, bool IsGroup)? ParseTarget(string? message)
    {
        string text = message ?? "";
        Match group = Regex.Match(text, @"\[群聊消息\((\d+)(?:,[^)\]]*)?\)\]");
        if (group.Success && long.TryParse(group.Groups[1].Value, out long groupId))
            return (groupId, true);
        Match priv = Regex.Match(text, @"\[私聊消息\((\d+)(?:,[^)\]]*)?\)\]");
        if (priv.Success && long.TryParse(priv.Groups[1].Value, out long userId))
            return (userId, false);
        return null;
    }

    /// <summary>去掉链接尾部粘上的标点。</summary>
    public static string TrimUrl(string url)
        => (url ?? "").TrimEnd('.', ',', ';', ':', '!', '?', '、', '。', '，', '；', '：', '！', '？', ')', '）', ']', '】', '"', '\'');
}
