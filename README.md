# Suran.QQMedia

把工作区的 `qq-music-sender` 与 `qq-video-sender` 两个技能合并成一个插件：从 B 站（或带代理的 YouTube）下载音频/视频，以 **QQ 语音条**和**QQ 视频消息**发出去。按需求只做"下载 + 发送"，网易云音乐卡片那套（`search_ncm_song` / `send_music_card`）没有移植——发网易云卡片用 Suran.ShareArk。

## 版本
4.1.0（对应 Alife 客户端 4.2.x）

## 依赖
- Alife.Function.FunctionCaller
- 外部：装了 `yt-dlp` 的 Python（本机是 `D:\Alife\Runtime\Python312\python.exe`，yt-dlp 2026.08.19）；发视频还要 `ffmpeg`（本机 8.1.2，在 PATH 上）

## AI 函数
| 函数 | 说明 |
|---|---|
| SendMusic(keyword, targetId?, type?, site?) | 关键词搜 B 站下载 m4a，以语音条发送。`site=yt` 改走 YouTube（需在配置里填代理）。本地有同名文件直接复用；只是名字前后缀接近时也会复用，那种情况回报里写清用的是哪个文件 |
| SendVideo(source, targetId?, type?, site?) | BV 号 / av 号 / 视频链接 / b23.tv 短链 / 关键词，下载合并成 mp4，以视频消息发送（对方可直接播，不是文件消息） |
| MediaEnv() | 自检：用的是哪个 yt-dlp、ffmpeg 在不在、B 站与 YouTube 的直连/代理连通性、**按插件真用的写法实跑一次 B 站搜索（`--simulate` 只模拟不下载）**、两个目录各有多少可复用文件/残片/非媒体文件与清理上限、协议端是哪个端、当前开关与超时 |

发不出去或下载失败，先跑 `MediaEnv` 再改配置——它会直接把"用的哪一个解释器、缺不缺 ffmpeg、通不通、搜索这一步现在还过不过得去"报回来。

## 配置
| 键 | 默认 | 说明 |
|---|---|---|
| WsUrl | `ws://127.0.0.1:3001` | OneBot 正向 WebSocket，语音条与视频段从这里发出 |
| PythonPath | 空 | 装了 `yt-dlp` 的 `python.exe` 完整路径；留空自动找 Alife 自带运行时的 `Python*`，再找 PATH |
| FfmpegPath | 空 | 发视频要它合并音视频；留空由 yt-dlp 在 PATH 里找 |
| ProxyUrl | 空 | HTTP 代理，如 `http://127.0.0.1:7890`。**只在 `site=yt` 时加上**，B 站不需要 |
| MusicFolder / VideoFolder | `Music` / `Video` | 相对 Alife Storage 的目录，与两个技能的旧缓存共用同一批文件 |
| MusicTimeoutSeconds | 60 | 音频下载超时，到点终止整个进程树（钳 10~1800） |
| VideoTimeoutSeconds | 180 | 视频下载超时（钳 10~1800） |
| MaxCacheFiles | 20 | 每个目录各自的媒体文件数上限，超了先删最旧的；**填 0=不清理** |
| CleanupCount | 5 | 超过上限时一次删几个 |
| EnableSend | 开 | 关掉后 SendMusic/SendVideo 不可用，也不会发生下载 |

## 下载
- 音频 `-f "ba[ext=m4a]/ba"`，视频 `-f "bv*+ba/b" --merge-output-format mp4`（合并必须有 ffmpeg，实测 54MB 用时约 2.8 秒）
- **B 站的目标一律带 `--referer https://www.bilibili.com/`**（BV/av、bilibili 链接、b23.tv 短链、`bilisearch:` 关键词都算；只有明确走 YouTube 才不加）
- 产物落 `Alife Storage\Music` 与 `Storage\Video`
- 缓存复用：文件名由关键词或链接里的 BV/av 号决定，所以先 `SendVideo("BV1us41137Fd")` 再 `SendVideo("https://www.bilibili.com/video/BV1us41137Fd")` 会复用同一个文件
  - **精确同名优先**；只有一方是另一方前缀时也算命中，但回报会写清「文件名最接近的是《X》，与这次要的『Y』不是同一个名字，要准的就给BV号」——避免「典狱司 伴奏」拿人声版顶上还一声不吭
  - 同为前缀命中时取最新改过的一个；**0 字节的空文件与 yt-dlp 中断留下的 `X.f30280.m4a` / `X.mp4.part` 残片都不算命中**（超时打断后不会把一个坏文件发出去），但它们计入条目数、会被清理删掉
  - 非媒体文件（如目录里原有的 `bili_cookie.txt`）既不参与复用也不会被清理，`MediaEnv` 单独列出它们的数量
- `--print after_move:filepath` 与 `--print webpage_url` 两行输出的**顺序是先链接后文件路径**，插件按内容识别（能 `File.Exists` 的当路径、`http` 开头的当来源），不按行号
- yt-dlp 定位顺序：配置的 Python → Alife 自带运行时的 `Runtime\Python*\python.exe`（不写死 Python312）→ PATH 上的 `python` → 独立 `yt-dlp`，找不到时把试过哪些和安装命令一起报回来
- 超时：音频默认 60 秒、视频 180 秒（钳在 10~1800），到点直接终止整个进程树
- 重试：只有看起来是限流/网络抖动那一类（412/429/503、`HTTP Error`、`Unable to download`、超时、连接断）才退避重试，最多 3 次；「Unsupported URL」「视频不存在」这类一次就报错，不再白等三倍超时。报错里如实说明这是第几次

## B 站的 412：原因是少 Referer（4.1.0 纠正）
4.0.0 的 README 与代码注释写的是「B 站搜索接口间歇性回 412，与关键词写法、`PYTHONUTF8`、起进程方式都无关，是 B 站侧限流」。**这个结论下早了**——当时只比了参数写法与启动方式，没比请求头。2026-10-22 交替实测：裸跑 `bilisearch:` 5 次中 2 次 `HTTP Error 412: Precondition Failed`，带 `--referer https://www.bilibili.com/` 5 次全过；加上前几轮，Referer 组 9/9、裸跑组 3/9。只加 `--user-agent` 不够（实测仍 412）。所以 4.1.0 起对 B 站目标固定带 Referer，重试退成兜底；真还回 412，那就是 B 站对这个出口 IP 限流，等一两分钟或换 `site=yt`。取 BV 链接本身（不走搜索）不需要 Referer。

另外注意：`--proxy` 只在 `site=yt` 且配置里填了代理时才加；但 Python 自己会读 Windows 注册表的系统代理，所以本机开着代理软件时，yt-dlp 即使没配 `ProxyUrl` 也可能走代理，这不是插件的行为。

## 发送
- 走 OneBot v11 正向 WebSocket（配置里 `ws://127.0.0.1:3001`），消息段是 `{"type":"record"/"video","data":{"file":"绝对路径"}}`
- 判成功必须拿到 `data.message_id`；retcode 1404/1400/1200 会分别说明是"该端没这个动作""段类型不认""整条被拒"
- **失败原因先读 `message`、没有再读 `wording`**：SnowLuma 的失败包只有 `wording` 键，只读 `message` 在它下面所有失败都显示空原因（4.1.0 补上，与另外四个插件同一改法）
- 复用 Suran.ShareArk 那套已验证的 WS 传输（echo 匹配、断线重连、两次尝试）

## 注意事项
- `image` / `record` / `video` 段在 **LLOneBot 7.12.14 本机实弹通过**（2026-10-15 发进指定群并用 `delete_msg` 撤回，4 秒后翻历史确认段真落成了媒体元素）。**NapCat 与 SnowLuma 本机没有，仍未实机验证**——三端差异是源码核对的：成功一律要认 `data.message_id`，不认识的接口回 1404，NapCat 1200 / SnowLuma 1400 会整条退回。换端第一次用请先发给自己或小群
- 目录里的文件不再无限攒：默认每个目录留 20 个媒体条目、超了删最旧的 5 个（`MaxCacheFiles=0` 可以关掉）；正在被协议端读的文件删不掉就留着，只记一条日志，不影响这次发送
- YouTube 在本机是"直连不通、必须代理"，不填代理的 `site=yt` 会失败
- B 站音频/视频都不需要登录、不需要 Cookie
- 关掉「开关·发送媒体」后 `SendMusic`/`SendVideo` 直接不可用，也不会发生下载

## 验证口径
本机反射探针 105 项全过（`scratch/QQMediaProbe`）：Referer 判定与目标拼接、缓存精确/前缀/残片/空文件各种命中、目录清理与上限、失败原因 `wording` 兜底、重试分类、参数守卫；实跑部分只做了 yt-dlp 定位、带 Referer 的 `--simulate` 试搜（真出 BV 号）与缓存命中路径，**没有下载新文件、没有发过任何消息**。写探针这一轮抓出并修掉一个真 bug：候选打分漏了「完全不匹配」那一档，任何关键词都会命中目录里最新的那个文件。
