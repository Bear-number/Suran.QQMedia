# Suran.QQMedia

把工作区的 `qq-music-sender` 与 `qq-video-sender` 两个技能合并成一个插件：从 B 站（或带代理的 YouTube）下载音频/视频，以 **QQ 语音条**和**QQ 视频消息**发出去。按需求只做"下载 + 发送"，网易云音乐卡片那套（`search_ncm_song` / `send_music_card`）没有移植——发网易云卡片用 Suran.ShareArk。

## 版本
4.0.0（对应 Alife 客户端 4.2.x）

## 依赖
- Alife.Function.FunctionCaller
- 外部：装了 `yt-dlp` 的 Python（本机是 `D:\Alife\Runtime\Python312\python.exe`，yt-dlp 2026.08.19）；发视频还要 `ffmpeg`（本机 8.1.2，在 PATH 上）

## AI 函数
| 函数 | 说明 |
|---|---|
| SendMusic(keyword, targetId?, type?, site?) | 关键词搜 B 站下载 m4a，以语音条发送。`site=yt` 改走 YouTube（需在配置里填代理） |
| SendVideo(source, targetId?, type?, site?) | BV 号 / av 号 / 视频链接 / b23.tv 短链 / 关键词，下载合并成 mp4，以视频消息发送（对方可直接播，不是文件消息） |
| MediaEnv() | 自检：用的是哪个 yt-dlp、ffmpeg 在不在、B 站与 YouTube 的直连/代理连通性、音频视频目录在哪且已缓存几个文件、协议端是哪个端、当前开关与超时 |

发不出去或下载失败，先跑 `MediaEnv` 再改配置——它会直接把"用的哪一个解释器、缺不缺 ffmpeg、通不通"报回来。

## 下载
- 音频 `-f "ba[ext=m4a]/ba"`，视频 `-f "bv*+ba/b" --merge-output-format mp4`（合并必须有 ffmpeg，实测 54MB 用时约 2.8 秒）
- 产物落 `Alife Storage\Music` 与 `Storage\Video`，与两个技能的旧缓存共用同一批文件
- 缓存命中就不再下载：文件名由关键词或链接里的 BV/av 号决定，所以先 `SendVideo("BV1us41137Fd")` 再 `SendVideo("https://www.bilibili.com/video/BV1us41137Fd")` 会复用同一个文件；关键词也能松匹配（`大鱼` 命中 `大鱼_周深.m4a`）
- `--print after_move:filepath` 与 `--print webpage_url` 两行输出的**顺序是先链接后文件路径**，插件按内容识别（能 `File.Exists` 的当路径、`http` 开头的当来源），不按行号
- yt-dlp 定位顺序：配置的 Python → Alife 自带运行时的 `Runtime\Python*\python.exe`（不写死 Python312）→ PATH 上的 `python` → 独立 `yt-dlp`，找不到时把试过哪些和安装命令一起报回来
- 超时：音频默认 60 秒、视频 180 秒（钳在 10~1800），到点直接终止整个进程树

## B 站 412（本机实测）
B 站的搜索接口会**间歇性**回 `HTTP Error 412: Precondition Failed`，实测同一个关键词、同一种起进程方式，约每两次拒一次，与关键词写法、`PYTHONUTF8`、重定向方式都无关——是 B 站侧的限流，不是参数问题。所以下载失败自动重试 3 次（间隔 3 秒、6 秒），仍失败才报错，并在错误里提示"等一两分钟再试或换 `site=yt`"。

另外注意：`--proxy` 只在 `site=yt` 且配置里填了代理时才加；但 Python 自己会读 Windows 注册表的系统代理，所以本机开着代理软件时，yt-dlp 即使没配 `ProxyUrl` 也可能走代理，这不是插件的行为。

## 发送
- 走 OneBot v11 正向 WebSocket（配置里 `ws://127.0.0.1:3001`），消息段是 `{"type":"record"/"video","data":{"file":"绝对路径"}}`
- 判成功必须拿到 `data.message_id`；retcode 1404/1400/1200 会分别说明是"该端没这个动作""段类型不认""整条被拒"
- 复用 Suran.ShareArk 那套已验证的 WS 传输（echo 匹配、断线重连、两次尝试）

## 注意事项
- **语音条与视频段在 NapCat / LLOneBot / SnowLuma 三端未做实机验证**——三端的差异源码核对覆盖的是 `json`/`music` 段与历史消息，`record`/`video` 的本地文件路径写法是按 OneBot v11 标准实现的，第一次用请发给自己或小群确认；`MediaEnv` 会告诉你能连上哪个端
- 下载完的文件不会自动删，目录长大会占盘，需要清理自己删 `Storage\Music`、`Storage\Video`
- YouTube 在本机是"直连不通、必须代理"，不填代理的 `site=yt` 会失败
- B 站音频/视频都不需要登录、不需要 Cookie
- 关掉「开关·发送媒体」后 `SendMusic`/`SendVideo` 直接不可用，也不会发生下载
