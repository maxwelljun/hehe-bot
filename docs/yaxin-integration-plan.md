# 亚信网页监控与反向下单设计

## 结论

推荐在现有 .NET 程序中增加一个站点适配器，通过 Chrome DevTools Protocol（CDP）连接专用 Chrome 实例，读取网页已经接收并解码的桌台数据。不要依赖屏幕截图判断路单，也不要直接复刻登录、验证码和 Protobuf 协议。

这种方式仍可只使用 .NET 内置的 `HttpClient`、`ClientWebSocket` 和 `System.Text.Json`，不需要 Selenium、Playwright、ChromeDriver、OpenCV 或 Node.js。Chrome 可以最小化，监控不受窗口遮挡、分辨率、缩放和动画影响；登录和验证码仍由用户在浏览器内完成。

第一阶段只记录信号和“模拟下单”，验证至少若干完整牌靴。真实下单必须另行显式启用，并同时满足桌台状态、局号、余额、限额、时间窗口和幂等检查。

## 已确认的网站结构

分析时间：2026-09-20。站点前端版本为 `26.09.14.1201`，基于 Cocos Creator 3.8.6，游戏 Canvas 和模块系统位于顶层 `yaxin868.com` 页面；`dabashou365.com/player/apple.html` iframe 只负责视频播放。普通 DOM 选择器无法稳定读取路单或点击下注区。

### 登录

登录请求为：

```text
POST /client/loginMobile.jsp
```

已观察到的主要参数：

| 参数 | 含义 |
| --- | --- |
| `username` | 大写后的账号 |
| `password` | 明文密码的 MD5，再转大写 |
| `random` | 图片验证码 |
| `clientType` | 登录页面发送 `2` |
| `kaptchaId` | 验证码标识 |
| `authenticatorCode` | 可选的动态验证码 |

登录响应提供 WebSocket 地址和一次性 `Code`。页面随后连接选定的 `wss://host:port`，发送 Protobuf `Message`：

```js
{
  type: 1,
  loginReq: {
    name: platformName,
    code: Code,
    clientType: 0
  }
}
```

因此程序不应保存账号密码或自行绕过验证码。用户在专用 Chrome 配置中正常登录，适配器只附加到已登录页面。会话失效时停止自动操作并提示重新登录。

### WebSocket 消息

负载是网站自定义 Protobuf `Message`，不是 JSON。前端使用其内置的 `protobuf.js` 和 `ProtoMrg.Ins.SerializeMsg("Message", msg)`。

| `type` | 用途 |
| --- | --- |
| `1` / `2` | 登录请求 / 响应 |
| `7` / `8` | 进入桌台请求 / 响应 |
| `11` / `12` | 下注请求 / 响应 |
| `10010` | 结算结果 |
| `10017` | 单桌更新 |
| `10018` | 全部桌台更新 |
| `10019` | 好路更新 |
| `10034` | 最新牌局线路 |

百家乐桌台数据包含：

```text
tableId, tableName, shoeSeq, gameSeq,
history, history2, history3,
tablestate, stopTime, lastlimitKey
```

`history` 的主结果是位掩码：

| 位值 | 含义 |
| --- | --- |
| `1` | 庄 |
| `2` | 闲 |
| `3` | 和 |
| `16` | 闲对附加标记 |
| `32` | 庄对附加标记 |
| `64` | 幸运六附加标记 |

判定庄、闲、和时必须使用 `historyValue & 0x03`，不能直接比较完整数值，否则对子或幸运六会使结果误判。

桌台状态已确认包括：`A`（可下注）、`D`（停止下注）、`S`（洗牌）、`R`（结算）和 `RP`（维护）。

### 下单消息

前端构造的请求如下：

```js
{
  type: 11,
  betReq: {
    tableId,
    sbanker,
    gamblingNum: gameSeq,
    betPointList: [{ option: "PLAYER" | "BANKER", amount }],
    xAxis: randomInteger0To59,
    yAxis: randomInteger0To59,
    enterType,
    quotaKey
  }
}
```

`enterType` 已观察到 `1`（大厅）、`2`（换桌）、`3`（多桌）和 `4`（房间）。下注响应错误码 `0` 表示成功；`-140` 会触发反自动化验证码。还存在超时、低于/高于限额、余额不足、禁止下注和频率限制等失败结果。

这些字段是分析当前前端构建得到的实现细节，不是公开稳定 API。网站升级后字段、消息类型和校验规则都可能改变，所以适配器必须包含版本探测并在未知版本下默认禁用下单。

## 运行架构

```text
专用 Chrome 配置目录
    │  localhost CDP
    ▼
ChromeConnector ── 页面版本/登录状态检查
    │
    ▼
SiteRuntimeAdapter ── 订阅全部桌台快照和单桌增量消息
    │
    ▼
RoadNormalizer ── history & 0x03，生成庄/闲/和序列
    │
    ▼
StreakDetector ── 六连判定与每局幂等锁
    │
    ├── ReadOnly / DryRunRecorder（默认只读）
    └── OrderGuard → SiteOrderBridge（需要显式启用）
```

### Chrome 启动方式

程序启动一个独立配置目录的 Chrome，并仅在本机开放调试端口：

```powershell
chrome.exe `
  --remote-debugging-address=127.0.0.1 `
  --remote-debugging-port=9222 `
  --user-data-dir="$env:LOCALAPPDATA\ScreenWatch\chrome-profile" `
  https://www.yaxin868.com/
```

程序通过 `http://127.0.0.1:9222/json` 找到目标标签页，再使用返回的 `webSocketDebuggerUrl` 建立 CDP 连接。调试端口必须绑定 `127.0.0.1`；不得绑定局域网地址。

### 页面数据接入

优先顺序：

1. 在文档创建前注入轻量脚本，挂接站点的消息分发入口，把已解码的 `10017`、`10018` 和 `10010` 数据复制到受控队列。
2. 若站点模块对象无法稳定访问，则挂接页面的 WebSocket 收包入口，并调用网页自身的 Protobuf 解码器。
3. 只有上述方式都不可用时，才在程序中维护独立 `.proto` 模型并解码二进制消息。该方案维护成本最高。

注入脚本只能返回结构化桌台数据和执行受限命令，不能接受任意代码字符串。程序与页面间每条消息应包含递增序号和页面会话 ID，页面刷新后清空旧状态。

真实下单由页面运行时适配器调用网站自身的消息构造和序列化路径。`enterType`、`quotaKey`、`sbanker` 和当前局号必须从目标桌台的实时上下文取得，不能为所有桌台硬编码相同值。若页面运行时不能为未渲染桌台提供完整参数，该桌仍可监控，但必须标记为不可自动下注，直到适配器验证对应的多桌下注路径。

### 全部桌台监控

监控范围是服务器下发的全部百家乐桌台，不是当前页面可见的卡片：

- 使用 `type = 10018` 的全桌快照发现并初始化所有桌台。
- 使用 `type = 10017` 的单桌更新持续更新对应 `tableId`。
- 每个 `tableId` 独立保存 `shoeSeq`、`gameSeq`、完整 `history`、当前连续走势、追注任务和操作锁。
- 页面滚动位置、浏览器尺寸、Canvas 是否渲染该桌卡片，都不影响监控。
- 运行中新增的桌台自动加入；维护、关闭或长时间未更新的桌台标记为不可下注，但不误用其他桌台的数据。
- 页面刷新或 WebSocket 重连后，必须等新的 `10018` 快照完成全量校准，再恢复信号判断和下单。

如果某个前端版本不再向当前会话推送完整 `10018` 快照，程序应报告“全桌数据不完整”并禁用自动下单，不能退回屏幕滚动或坐标点击来补齐。

## 可配置连续走势规则

软件一次运行一条连续走势策略，默认是“六连反打 10/20/40”。用户可以设置触发走势（庄和闲、仅庄、仅闲）、连续次数 `2–20`、顺向或反向，以及 `1–10` 档整数金额。每个桌台独立维护一组追注任务，规则为：

- 只看百家乐主结果 `historyValue & 0x03`。
- 庄、闲计入连续次数；和局忽略，既不增加也不中断当前连庄/连闲。
- 只有该桌最新结果首次达到配置的连续次数时才创建任务；历史中较早的连续走势不触发。
- 反向模式下连庄买闲、连闲买庄；顺向模式下跟随连续走势下注。
- 第一次反向下注获胜后立即结束任务；失败则下一局继续买同一反方向。
- 任意一次获胜立即结束；金额序列用完后任务结束。
- 同一条连续走势超过配置次数时不创建新任务。必须先发生反转并在之后重新形成新的连续走势，才允许再次触发。
- 只有订单已受理且对应牌局结算为失败时才进入下一档。
- 和局按庄/闲标准下注的退注处理：不算成功或失败、不消耗档位，下一局仍执行当前档位。
- 洗牌或 `shoeSeq` 改变时清空该桌状态。
- 每次下注按 `(tableId, shoeSeq, targetGameSeq, side, attempt)` 建立唯一操作键；任务按 `(tableId, shoeSeq, streakStartGameSeq, streakSide)` 建立唯一键。两种键都要持久化，重启后不能重复操作。

例如 `庄、庄、和、庄、庄、庄、庄` 算六连庄并创建买闲任务。下一局先下注 `10`；若输，下一局下注 `20`；再次输，下一局下注 `40`。任意一档获胜立即结束；第 3 档仍失败则任务结束，这条连庄即使继续也不再触发。

程序应根据服务器推送的完整 `history` 重算当前走势，而不是只依赖增量消息。这样在断线重连或漏包后能够恢复正确状态。

默认策略定义为：

```json
{
  "streakLength": 6,
  "triggerSide": "Both",
  "direction": "Opposite",
  "stakes": [10, 20, 40],
  "triggerTiePolicy": "ignore",
  "settlementTiePolicy": "keep-attempt",
  "missedWindowPolicy": "keep-attempt",
  "oncePerRun": true,
  "stopOnWin": true
}
```

程序启动时校验策略范围，并根据完整策略生成稳定 ID 参与任务键计算。策略只能在停止监控后修改；存在等待确认、等待开奖结果或状态不明的活动追注时拒绝切换，避免用新策略继续旧任务。已挂起等待外部结算的订单不再关联活动追注。

## 下单状态机

```text
观察最新结果 → 达到配置连数 → 等待可下注 → 已受理 → 等待结算
     ↑                                      │
     │                    ┌── 获胜 ─────────┤→ 任务结束
     │                    ├── 和局 ─────────┘→ 同次数继续
     │                    └── 失败且仍有下一档 ─→ 下一局按配置方向下注
     │
     └──────── 金额序列用完：任务结束并锁住当前走势
```

程序必须区分三种结果：

- **下单成功**：`type = 12` 返回错误码 `0`，只代表订单已受理。
- **下注获胜**：对应局的 `type = 10010` 结算结果与下注选项一致，任务结束。
- **下注失败**：订单已受理且结算结果与下注选项相反，进入下一金额档位。

下单未受理不消耗金额档位，但应记录具体错误并按错误类别跳过本局或熔断。响应超时属于结果未知，不能自动补单或进入下一次。

产生信号后，只有全部满足以下条件才进入 `Eligible`：

- 页面仍已登录，页面版本属于经过验收的白名单。
- 同一 `tableId`、`shoeSeq`，且目标 `gameSeq` 未变化。
- `tablestate == "A"`，服务器剩余时间高于可配置安全余量。
- 下注选项是精确的 `PLAYER` 或 `BANKER`。
- 当前档金额来自已校验的策略金额序列，且满足当前桌台与 `quotaKey` 对应的最小/最大限额。
- 页面余额足够。
- 唯一操作键尚未提交，当前局也没有待确认请求。
- 最近没有出现验证码、频率限制、未知错误码或人工暂停。

发送后等待 `type = 12` 的确认。只有错误码 `0` 记录为成功；超时属于“结果未知”，必须停止自动模式并由用户核对，不能自动重试。任何 `-140`、协议变化或连续错误都应熔断。

### 下单成功声音

网站返回 `type = 12` 且错误码为 `0` 后，程序先将订单原子记录为 `Accepted`，再播放一次 Windows 系统提示音。提示音代表“下注订单已被网站受理”，不代表该局获胜。

- 每个 `orderKey` 最多播放一次；重复 ACK、页面重连或界面刷新不会重复播放。
- 只对真实下注播放，模拟模式只写日志。
- 声音可通过 `playAcceptedSound` 开关关闭。
- 播放声音在线程池异步执行；声音设备不可用时只记录警告，不影响监控和结算。
- 托盘日志同时显示桌名、下注方向、金额和当前档位，便于确认声音对应哪一笔订单。

## 配置与记录

建议新增独立配置，不把站点字段混入现有截图配置：

```json
{
  "mode": "read-only",
  "liveTradingEnabled": false,
  "chromeDebugPort": 9222,
  "tableScope": "all-baccarat",
  "strategy": {
    "streakLength": 6,
    "triggerSide": "Both",
    "direction": "Opposite",
    "stakes": [10, 20, 40]
  },
  "minimumSecondsRemaining": 8,
  "dailyStakeLimit": 0,
  "maxReservedStake": 0,
  "playAcceptedSound": true,
  "allowedFrontendVersions": ["26.09.14.1201"]
}
```

`tableScope` 固定为 `all-baccarat`，表示监控服务器下发的全部百家乐桌台，包括页面未显示和需要向下滚动才能看到的桌台。每次下单都要分别检查目标桌台限额。`dailyStakeLimit` 和 `maxReservedStake` 为 `0` 时禁止真实下注，必须由用户设置非零上限。

本地审计日志至少记录：时间、页面版本、桌台、牌靴、来源局号、目标局号、走势、下注选项、当前档位、累计失败次数、金额、模式、策略 ID、任务键、操作键、发送时间、响应码、响应时间和结算结果。日志不得写入账号、密码、登录 `Code`、Cookie、WebSocket 地址中的令牌或完整 CDP 消息。

## 程序模块

建议在现有解决方案内增加两个项目，保留当前截图监控功能但不与站点策略耦合：

```text
src/
  ScreenWatch.Core/                 现有通用代码
  ScreenWatch.Windows/              现有托盘和窗口
  YaxinMonitor.Core/
    Models/                          桌台、牌局、追注任务、订单状态
    RoadNormalizer.cs               位掩码转庄/闲/和
    FixedStrategy.cs                策略配置、默认规则和走势归一化
    StreakDetector.cs               判断最新连续走势
    ChaseStateMachine.cs            多档金额、输赢和停止条件
    OrderGuard.cs                    时间、限额、余额、幂等、版本检查
    StateStore.cs                    原子快照和恢复
  YaxinMonitor.Windows/
    ChromeLauncher.cs               启动专用 Chrome
    CdpClient.cs                     CDP 请求、事件和重连
    SiteRuntimeAdapter.cs            页面注入及结构化事件
    OrderDispatcher.cs              全局下单队列和响应关联
    AcceptedNotifier.cs             订单受理后的去重声音提醒
    DashboardForm.cs                 全桌状态与控制面板
```

`YaxinMonitor.Core` 不引用 WinForms、Chrome 或网络，可以用录制事件做确定性测试。`YaxinMonitor.Windows` 只负责浏览器连接、页面桥接和用户界面。两个项目都只使用 .NET 自带库。

核心状态建议使用以下模型：

```text
TableState
  tableId, tableName, shoeSeq, gameSeq, tableState, stopTime
  history, currentRunSide, currentRunLength, activeChase, lockedStreakKey

ChaseTask
  taskKey, tableId, shoeSeq, streakSide, betSide
  strategyId
  attemptIndex, status, pendingGameSeq, lastOrderKey

OrderRecord
  orderKey, taskKey, gameSeq, side, amount
  status: Preparing | Submitted | Accepted | Rejected | Settled | Unknown
  responseCode, settlementResult, timestamps
```

## 多桌并发与资金预留

所有桌台同时监控，每桌可各自存在一个追注任务，但下单发送经过一个全局调度器：

1. 桌台产生可下注请求后进入按截止时间排序的队列。
2. 调度器重新读取该桌最新 `gameSeq`、`tablestate` 和剩余时间，过期请求直接跳过。
3. 发送前按金额预留余额，计算 `服务器余额 - 已接受但未反映的订单 - 正在发送的订单`。
4. 同一桌同一局只允许一个在途订单；全局发送串行化，正常收到响应或将超时订单隔离后再发送下一笔，避免响应串单和瞬时频率过高。已受理订单跨靴仍无结果时挂起等待外部结算，结束旧追注并释放桌台，不阻塞新牌靴的独立策略任务。
5. 多桌同时接近截止时间时，优先处理截止时间最早的请求。来不及满足安全余量的请求跳过该局，不消耗追注次数。
6. 当日累计下注加新订单金额不得超过 `dailyStakeLimit`，所有预留金额不得超过 `maxReservedStake`。

监控不会因为某桌下注而暂停其他桌。全桌数据更新继续处理，只把符合条件的订单请求交给调度器。

## 持久化与恢复

运行状态保存在 `%LOCALAPPDATA%\ScreenWatch\yaxin\`：

```text
settings.json                    非敏感配置，原子替换
state.json                       每桌任务、锁和订单状态，原子替换
events/YYYY-MM-DD.ndjson         匿名桌台事件，供回放测试
orders/YYYY-MM-DD.ndjson         下单与结算审计记录
logs/YYYY-MM-DD.log              运行错误和状态变化
chrome-profile/                  专用 Chrome 用户目录
```

每次发送订单前，先把订单持久化为 `Preparing` 并刷新到磁盘；发送后立即记录 `Submitted`，收到 `type = 12` 后记录 `Accepted` 或 `Rejected`，结算后记录 `Settled`。如果程序崩溃、Chrome 退出或网络中断导致状态停在 `Preparing`、`Submitted` 或 `Accepted`，重启后一律进入 `Unknown` 并暂停真实模式。只有从网站订单记录完成对账或用户明确处置后才能继续，不能推测未下注并自动补单。

重启恢复时先加载 `state.json`，连接页面并取得完整 `10018` 快照，然后按 `tableId + shoeSeq + gameSeq` 校准。已结束牌靴的任务归档；当前牌靴的锁继续有效，防止重启后把同一条六连当作新信号。

## Windows 后台运行与界面

程序发布为 Windows 10/11 x64 自包含单文件，使用 WinForms 系统托盘运行，不需要安装 .NET、Python、Node.js 或浏览器驱动。推荐通过“任务计划程序”设置为用户登录时启动；不设计成 Windows 服务，因为 Chrome 登录会话属于交互式用户。

程序负责查找 Chrome、使用独立 `chrome-profile` 启动并附加 CDP。Chrome 可以最小化，页面不需要滚到任何桌台；Windows 用户会话需保持登录，系统休眠时程序暂停，恢复后必须重新完成全桌快照校准。退出程序时不删除 Chrome 登录数据。

主界面至少包含：

- 连接状态、登录状态、前端版本、最后消息时间和全桌数量。
- 全部桌台列表：桌名、状态、牌靴、局号、最新走势、当前追注档位和待结算订单。
- `只读`、`模拟`、`真实` 三种模式；启动默认 `只读`，真实模式另有总开关。
- 策略设置：触发走势、连续次数、顺/反方向和金额序列；和局保持当前档。
- 当日已下注金额、在途预留金额、当日上限和余额。
- 立即暂停按钮。暂停只停止新订单，仍继续接收结算并保存状态。
- 错误和熔断原因，以及打开数据目录的入口。

以下任一情况自动暂停真实模式：登录失效、运行时接口兼容检查失败、全桌快照整体不可用、消息长时间未更新、CDP 重连、验证码 `-140` 或配置在运行中改变。单桌订单响应超时、订单状态未知、跨靴或历史异常只隔离对应桌台；余额、限额或当日上限不符合时只跳过对应订单。

## 页面桥接协议

页面注入脚本和 .NET 只交换白名单事件：

```text
sessionStarted(version, sessionId)
allTables(sequence, tables[])
tableUpdated(sequence, table)
settled(sequence, result)
balanceUpdated(sequence, balance)
betAccepted(orderKey, responseCode)
fatalError(code, message)
```

.NET 发给页面的唯一业务命令是 `submitBet`，参数必须由 `OrderGuard` 生成，脚本端再次校验字段类型、桌台、局号、选项和金额。每次页面刷新生成新的 `sessionId`；旧会话事件全部丢弃。事件 `sequence` 必须严格递增，发现跳号就暂停下单并请求新的全桌快照。

## 测试矩阵

核心回放测试至少覆盖：

| 场景 | 期望结果 |
| --- | --- |
| 最新结果刚好达到配置连数 | 创建一次任务并使用第一档金额 |
| 历史中达到过连数但最新不是 | 不触发 |
| 连续走势超过配置连数 | 不创建第二个任务 |
| 任意档位获胜 | 立即结束，不再下注 |
| 所有金额档位失败 | 结束并锁住该走势 |
| 追注中出现和局 | 不升级金额，下一局继续当前档 |
| 新牌靴 | 清空旧走势，不跨靴触发 |
| 重复、乱序或漏失消息 | 不重复下注；漏失时暂停并重同步 |
| 多桌同一秒触发 | 每桌任务独立，余额预留正确，订单键不重复 |
| 下单 ACK 超时或程序崩溃 | 标记 Unknown，禁止自动补单 |
| 页面刷新或 Chrome 重启 | 完整快照后恢复，原有任务键不重复 |
| 运行时接口不兼容或验证码 | 立即暂停真实模式 |

除单元测试外，需要用模拟模式连续运行至少 3 个完整牌靴，并对页面可见、页面下方和动态新增桌台累计核对至少 100 局。真实模式前还需在 Windows 真机验证休眠恢复、Chrome 最小化、网络断开、页面刷新和程序强制结束后的恢复行为。

## 实施阶段与验收

### 阶段一：只读适配器

- 启动或附加专用 Chrome，检测目标页面和登录状态。
- 从 `10018` 建立全部百家乐桌台清单，展示状态、`shoeSeq`、`gameSeq` 和标准化后的最新结果。
- 断线、页面刷新后自动恢复订阅。
- 前端文件名变化只记录版本信息；全桌读取、桌台状态、下注、限额或回执接口检查失败时暂停真实下注。

验收：对当前可见、需要滚动才能看到以及运行中新出现的桌台逐桌抽查，累计至少 100 局；主结果、桌台、牌靴和局号全部一致。滚动页面和最小化 Chrome 不得改变监控桌台数量。

### 阶段二：模拟下单

- 实现可配置连续走势检测、每桌独立追注状态机、顺/反方向映射、持久化去重和完整前置检查。
- 只写 `DryRunRecorder`，不调用下单函数。
- 用保存的匿名桌台事件做离线回放测试，包括不同连数、单边触发、顺向和反向、不同金额档数、和局、断线重连、洗牌和重复消息。

验收：连续观察至少 3 个完整牌靴，无漏触发、重复触发或跨牌靴触发；模拟信号的目标局仍处于可下注时间窗。

### 阶段三：受控真实模式

- UI 中单独启用，启动后默认仍为暂停状态。
- 允许全部百家乐桌台使用当前策略的金额档位，同时执行单笔和当日总额上限。
- 增加当日金额上限、连续失败熔断和一键暂停。
- 首次只用允许的最小金额人工旁站验证。

验收前不能把真实模式设为默认，也不能用点击坐标作为下单成功依据。最终成功状态只能来自网站的下注响应和页面订单记录交叉确认。

## 上线前必须填写

程序逻辑已经确定。切换真实模式前还必须在配置中填写：

1. 每日总下注金额上限。
2. 同时在途的最大预留金额。

这两个值保持 `0` 时真实模式无法开启。监控范围始终是全部百家乐桌台；和局不消耗次数，错过下注窗口直接跳过该局但不消耗一次机会。触发走势、连续次数、方向和金额序列由界面配置。

## 风险边界

- 这是对内部网页接口的适配，不是官方 API；站点升级可导致失效或错误操作。
- 自动化可能触发站点的反自动化验证码或违反站点规则。出现验证码后应立即停止，不能尝试绕过。
- 网络超时后无法从“未收到响应”推断“未下注”，自动重试可能导致重复订单。
- 浏览器调试端口能控制登录会话，必须只监听本机，并使用独立 Chrome 配置目录。
- 当前账号余额为零，尚未也不应在分析阶段提交真实订单。
