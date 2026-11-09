# windows_perf_mon_demo

监视 Windows ETW 的 **UI 延迟（UI delay）** 事件，把延迟 **超过 200 ms** 的卡顿记录到本地 JSONL 文件。

可执行文件：`bin\WindowsPerfMonDemo.exe`。

数据源与 xperf / WPA 的 `-a uidelay` 分析完全相同：`Microsoft-Windows-Win32k`
提供程序的 `InputProcessDelay` / `MessageCheckDelay` 事件族。

## 监控的事件

| EventId | Task | 说明 |
|---|---|---|
| 37 | InputProcessDelay | 输入已入队，线程迟迟没有取走 |
| 38 | MessageCheckDelay | 线程检查消息队列时测得的延迟（`DelayTimeMs`） |
| 172 | ImmersiveInputProcessDelay | 37 的 UWP 变体 |
| 173 | ImmersiveMessageCheckDelay | 38 的 UWP 变体 |

- 阈值判定：`delay > 200`（严格大于，可用 `--min-ms` 调整）。
- 38/173 使用 `DelayTimeMs`；37/172 没有该字段，使用 `TimeSinceOldestInputMs`。
- 通过关键字 `win:ResponseTime (0x0001000000000000) | UIUnresponsiveness (0x00040000)`
  启用 Win32k，只收这一族事件，不会把 Win32k verbose 日志全部拉进来。

## 构建

不需要 .NET SDK，使用 Windows 自带的 .NET Framework C# 编译器：

```bat
build.bat
```

或：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

产物：`bin\WindowsPerfMonDemo.exe`（AnyCPU，x64/ARM64 原生运行）。

## 运行

> **实时采集需要管理员权限**：Win32k 是内核态 ETW 提供程序。
> 直接运行会自动弹出 UAC 提权重启；加 `--no-elevate` 则直接报错退出。

```bat
:: 默认阈值 200ms，输出到 exe 同目录的 uidelay-yyyyMMdd.jsonl
WindowsPerfMonDemo.exe

:: 只跑 60 秒，自定义输出文件
WindowsPerfMonDemo.exe --duration 60 --out D:\logs\uidelay.jsonl

:: 阈值改成 500ms，安静模式
WindowsPerfMonDemo.exe --min-ms 500 --quiet

:: 离线回放已有 ETL（不需要管理员）
WindowsPerfMonDemo.exe --etl traces\catch_20261028_105441.etl --out .\replay.jsonl

:: 诊断：打印每个 Win32k 事件 ID 的解码结果
WindowsPerfMonDemo.exe --etl traces\catch_20261028_105441.etl --probe-schema
```

### 参数

| 参数 | 说明 |
|---|---|
| `--min-ms <n>` | 延迟阈值（毫秒，严格大于），默认 `200` |
| `--out <path>` | 输出 `.jsonl` 文件；缺省为 exe 同目录 `uidelay-yyyyMMdd.jsonl`，跨天自动换文件 |
| `--etl <path>` | 回放现有 ETL，而不是启动实时会话 |
| `--duration <sec>` | 运行 N 秒后停止；`0`（默认）表示一直跑到 Ctrl+C |
| `--session <name>` | ETW 会话名，默认 `WindowsPerfMonDemo-<pid>` |
| `--no-elevate` | 不提权；无管理员权限时直接报错 |
| `--quiet` | 不逐条打印（文件照写） |
| `--probe` | 打印所有 Win32k delay 事件，包括低于阈值的 |
| `--probe-schema` | 每个事件 ID 首次出现时打印解码后的字段，用于诊断 |
| `--keywords <mask>` | 十六进制关键字掩码或 `all`，默认 `0x0001000000040000` |
| `--level <1..5>` | ETW 级别，默认 `4`（informational） |
| `--count-all` | 诊断：统计所有提供程序的事件，退出时打印 top 提供程序和 Win32k 事件 ID |
| `-h`, `--help` | 帮助 |

### 退出码

| 代码 | 含义 |
|---|---|
| 0 | 正常 |
| 1 | 运行时错误（如 ProcessTrace 失败） |
| 2 | 参数错误 |
| 3 | ETL 不存在 |
| 5 | 需要管理员权限 |
| 7 | ETW 会话启动失败 |

## 输出格式

UTF-8 无 BOM，**JSON Lines**：每行一个独立的 JSON 对象，追加写入，进程被强杀也不会留下残缺文件。
之所以不用单个 JSON 数组，是为了实时追加、崩溃安全和方便 `grep` / `jq`。

示例（数值为示意）：

```json
{"time":"2026-11-09T11:34:37.1234567+08:00","timeUtc":"2026-11-09T03:34:37.1234567Z","delayMs":345,"delaySource":"DelayTimeMs","delayType":"MessageCheckDelay","eventId":38,"version":0,"processId":1234,"threadId":5678,"processName":"notepad.exe","imagePath":"C:\\Windows\\System32\\notepad.exe","className":"Notepad","topLevelClassName":"Notepad","delayTimeMs":345,"timeSinceInputRemoveMs":340,"timeSinceOldestInputMs":345,"messageId":512,"flags":"0x40","wParam":"0x0"}
```

| 字段 | 说明 |
|---|---|
| `time` / `timeUtc` | 事件时间（本地 ISO 8601 / UTC） |
| `delayMs` | 用于判定的延迟值 |
| `delaySource` | `DelayTimeMs` 或 `TimeSinceOldestInputMs` |
| `delayType` | 事件名（`MessageCheckDelay` 等） |
| `eventId` / `version` | ETW 事件 ID / 版本 |
| `processId` / `threadId` | 事件关联的进程/线程 |
| `processName` | 写记录时解析的进程名，已退出的进程可能缺省 |
| `imagePath` | 应用可执行文件路径（Immersive 事件没有，改用 `packageMoniker` / `appUserModelId`） |
| `className` / `topLevelClassName` | 窗口类名 / 顶层窗口类名 |
| `delayTimeMs`、`timeSinceInputRemoveMs`、`timeSinceOldestInputMs` | 原始 payload 字段（有哪些就写哪些） |
| `messageId` / `wParam` / `flags` | 原始 payload 字段 |
| `packageMoniker` / `appUserModelId` | UWP 应用标识（仅 Immersive 事件） |

## 工作原理

1. `StartTrace` 创建实时会话（`EVENT_TRACE_REAL_TIME_MODE`）。
2. `EnableTraceEx2` 以 `ResponseTime | UIUnresponsiveness` 关键字启用 Win32k 提供程序。
3. `OpenTrace` + `ProcessTrace` 以 `EVENT_RECORD` 模式接收事件。
4. 过滤出 Win32k 的 delay 事件后，用 **TDH**（`TdhGetPropertySize` / `TdhGetProperty`）
   按**属性名**读取 `DelayTimeMs`、`ClassName`、`ImagePath` 等字段。
   程序没有写死二进制偏移，payload 布局随 Windows 版本变化也不会解析错位。
5. 超过阈值的记录追加到 JSONL；Ctrl+C 或 `--duration` 到期后关闭会话。

程序退出时会 `CloseTrace` 并停止 ETW 会话；若进程被强杀留下同名会话，
下次启动会自动检测并清理，也可以手动：

```bat
logman stop WindowsPerfMonDemo-<pid> -ets
```

## 已验证

- **离线回放**：本地采集的 `traces\catch_20261028_105441.etl`（402 MB，未随仓库提供）
  共识别 **125,765** 条
  Win32k 事件，ETW 结构体布局与回调均正常；该 ETL 采集时未启用 delay 关键字，
  `xperf -a uidelay` 同样为空，程序输出 0 条，二者一致。
- **实时采集**（管理员）：30 秒内收到 10 条 `MessageCheckDelay`，
  `DelayTimeMs` 均为 ~1500 ms，`imagePath` / `className` / `flags` / `messageId`
  等字段全部正确写入 JSONL。
- **宽口径压测**：`--keywords all` 下 12 秒收到 **289,281** 条 Win32k 事件，
  说明实时会话和关键字过滤链路正常（默认掩码下只会收到 delay 事件）。

## 自测

`tests\UiHang\UiHang.exe` 是一个每 3 秒阻塞 UI 线程 1.5 秒的小窗口，
用来在任意机器上制造可复现的 >200 ms 卡顿：

```powershell
# 1) 管理员终端里启动监控（默认 200ms 阈值）
.\bin\WindowsPerfMonDemo.exe --out .\selftest.jsonl

# 2) 普通终端里启动测试窗口
tests\UiHang\bin\UiHang.exe

# 3) 把鼠标在蓝色窗口里来回移动，每 3 秒会记录一条 ~1500ms 的卡顿
```

测试窗口源码在 `tests\UiHang\UiHang.cs`，需要重新编译时运行
`tests\UiHang\build.bat`（同样使用 Windows 自带的 csc）。

## 常见问题

- **一直是 0 条**：delay 事件只在「输入确实等待过」时才会由 Win32k 产生，空闲系统上
  本来就没有；可用上面的 `tests\UiHang` 制造卡顿自测。先用 `--min-ms 0 --probe` 看
  是否收到事件，再用 `--count-all` 确认实时链路是否通。
- **与 WPA 数值对不上**：WPA 的 UI Delay 图按进程/窗口聚合且默认有过滤条件，
  本项目记录的是每条原始事件；单条数值应与 `DelayTimeMs` 一致。
- **文件很大**：正常桌面操作下只有几十条/分钟量级的 >200ms 事件；
  默认按天分文件，也可用 `--out` 固定单文件自行轮转。
