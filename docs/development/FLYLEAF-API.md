# FlyleafLib 3.10.4 实测 API（反射自 DLL，集成修复以此为准）

## Engine
- `FlyleafLib.Engine.Start(EngineConfig)` / `StartAsync`；`Engine.IsLoaded`
- `EngineConfig`: `FFmpegPath`(string)、`UIRefresh`(bool)、`UIRefreshInterval`(int)、`LogLevel`、`FFmpegLogLevel`、`FFmpegHLSLiveSeek`、`KeepDisplayActive`

## Player（FlyleafLib.MediaPlayer.Player）
- 构造 `new Player(Config)`；`player.Config`
- 打开：`OpenAsync(string url, ...)`（异步，完成走 OpenCompleted 事件）；`Open(string,...)` 同步返回 `OpenCompletedArgs{Success}`
- 控制：`Play() Pause() TogglePlayPause() Stop() Dispose()`
- Seek：`Seek(int ms, bool forward)`、`SeekAccurate(int ms)` —— **参数是毫秒 int**
- 时间：`CurTime`(long, **ticks 100ns**)、`Duration`(long ticks)、`BufferedDuration`(long ticks)
- `Speed`(double)、`Status`(enum: Opening,Failed,Stopped,Paused,Playing,Ended)、`IsPlaying`(bool)、`IsLive`、`LastError`(string)
- 音频：`player.Audio.Volume`(int 0-~150)、`Audio.Mute`(bool)
- 事件：`OpenCompleted(EventHandler<OpenCompletedArgs>{Success})`、`PlaybackStopped(EventHandler<PlaybackStoppedArgs>{Success,Error})`、`BufferingStarted/BufferingCompleted`、`SeekCompleted`、`PropertyChanged`（CurTime 属性变更可用 PropertyChanged 或 UIRefresh 轮询——用 DispatcherQueueTimer 轮询 CurTime 最稳）
- 注意：事件回调在播放器线程，UI 操作需 App.Post

## Config（new Config()）
- `Config.Player.AutoPlay`(bool)、`SeekAccurate`、`MinBufferDuration`、`KeyBindings.Enabled=false`（禁用内建按键，避免与页面快捷键冲突）
- `Config.Demuxer.FormatOpt`(Dictionary<string,string>)：FFmpeg avformat/http 选项
  - `FormatOpt["headers"] = "Key: V\r\nKey2: V2\r\n"`；`FormatOpt["user_agent"]`；`FormatOpt["referer"]`
  - 大超时：`FormatOpt["timeout"]`（微秒）等
  - `Config.Demuxer.BufferDuration`(ticks)
- `Config.Video.AspectRatio`(AspectRatio 结构)：`new AspectRatio(16,9)`；`AspectRatio.FromString("16:9")`；保持原比例/填充用静态字段 `AspectRatio.Keep` / `AspectRatio.Fill`（若编译报错用 new AspectRatio(0,0)/(-1,-1) 验证）
- `Config.Subtitles.Enabled`(bool)、`SearchLocal`
- 外挂字幕加载：`player.OpenAsync(subUrlOrPath)` 会作为字幕流打开？不确定 —— 集成时验证；备选：Config.Subtitles + Playlist ExternalSubtitles

## FlyleafHost（FlyleafLib.Controls.WinUI，xmlns using:FlyleafLib.Controls.WinUI）
- `FlyleafHost : ContentControl`；**属性 `Player`**（可 XAML 绑定或代码赋值）
- `KeyBindings`(bool DP) 设 false
- `FSC`(FullScreenContainer：`FullScreen()/ExitFullScreen()/IsFullScreen`)、`SCP`(SwapChainPanel)
- Content 可放覆盖层（控制条等作为 FlyleafHost.Content 或与其同级 Grid 叠放均可）
- lib 目标 net8.0/net10.0-windows10.0.19041，net9 应用可用

## 时间换算
ticks→ms: `/ 10000`；ms→ticks: `* 10000`
