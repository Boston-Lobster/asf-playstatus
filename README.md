# AsfPlayStatus

> 版本：**v1.3.1** ｜ 编译引用：ASF 6.3.8.4 ｜ 目标：.NET 10、linux-x64 ASF 裁剪单文件

本插件通过 ASF IPC 暴露自动挂卡状态和最近一次手动下发的游戏目标列表，供 [asf-nav-bridge](https://github.com/Boston-Lobster/asf-nav-bridge) 等客户端使用。

## v1.3.1

- 新增 `GET /Api/PlayStatus?bot=<BotName>`，可选择单个 bot，响应 key 和 `BotName` 仍为掩码；无查询参数时保留全量响应。
- 新增 `POST /Api/PlayStatus/Play`，调用 ASF 执行手动 play，并在 ASF 接受后记录目标。
- `POST /Api/PlayStatus` 接受空 `AppIDs`，用于清除手动目标。
- 只持久化非空手动目标；bot 删除时同步清理文件；重启后只为实际存在的 bot 恢复目标，不恢复旧连接或挂卡标志。
- 并发持久化写入串行执行；写盘失败时 API 返回 HTTP 500。
- `build.ps1` 按 ASF 版本隔离引用缓存并校验程序集版本，同时检查 `dotnet` 原生命令退出码。

## 状态说明

自动挂卡时，插件从 ASF 内存读取 `CardsFarmer.CurrentGamesFarming` 和挂卡进度（`Source=Farming`）。手动模式下记录调用方最近一次成功同步的目标列表（`Source=Manual`）。ASF 没有可靠的结构化 API 来确认 `play`、`reset` 或 `resume` 后 Steam 当前展示的游戏，因此 `Manual` 和 `SeenAt` 只表示最近一次同步目标及插件轮询时间，不代表已确认 Steam 当前状态。

如果通过 ASF 的 `reset`/`resume` 或其他方式停止或替换手动列表，调用方应向 `POST /Api/PlayStatus` 发送空 `AppIDs` 清除旧目标，或提交替换列表。插件不会根据轮询猜测手动列表已经停止。

所有端点均使用 ASF IPC 身份验证，需要 `Authentication` 请求头。

## GET /Api/PlayStatus

不带查询参数时返回所有已知 bot。可用完整 bot 名筛选单个状态，名称匹配不区分大小写：

```http
GET /Api/PlayStatus?Bot=YOUR_BOT
```

查询参数只用于选择 bot，响应中的 key 和 `BotName` 仍使用 `bot1`、`bot2` 等掩码。不存在的 bot 返回 HTTP 400。

```json
{
  "Result": {
    "bot1": {
      "BotName": "bot1",
      "Connected": true,
      "Paused": true,
      "Farming": false,
      "CurrentGames": [
        { "AppID": 123456, "GameName": "Example Game", "CardsRemaining": null }
      ],
      "Source": "Manual",
      "TimeRemaining": "",
      "CardsRemaining": 0,
      "UpdatedAt": "2026-08-12 23:30:00",
      "SeenAt": "2026-08-12 23:30:15"
    }
  },
  "Message": "OK",
  "Success": true
}
```

## POST /Api/PlayStatus

同步已经下发给 ASF 的列表；此端点本身不发送 Steam play 命令。传入空 `AppIDs` 会清空手动列表并将来源改为 `None`。

```json
{
  "Bot": "YOUR_BOT",
  "AppIDs": [123456, 654321],
  "GameNames": {
    "123456": "Example Game",
    "654321": "Another Game"
  }
}
```

## POST /Api/PlayStatus/Play

请求体与上例一致。插件调用 ASF `Bot.Actions.Play`，只有 ASF 返回成功后才记录并持久化目标。`AppIDs` 需包含 1 至 32 个非零 ID。若 ASF 已接受命令但随后写盘失败，接口返回 HTTP 500；此时 play 命令可能已经生效。

```bash
curl -s -X POST \
  -H "Authentication: <ASF_IPC_PASSWORD>" \
  -H "Content-Type: application/json" \
  -d '{"Bot":"YOUR_BOT","AppIDs":[123456,654321],"GameNames":{"123456":"Example Game","654321":"Another Game"}}' \
  http://127.0.0.1:1242/Api/PlayStatus/Play
```

## 构建

需要 .NET 10 SDK。默认 `build.ps1` 会下载 ASF 6.3.8.4 generic 包中的 `ArchiSteamFarm.dll` 和 `SteamKit2.dll` 到版本专属引用目录，验证 ASF 程序集版本后再 restore/build：

```powershell
.\build.ps1
```

可用 `-AsfVersion` 指定其他四段版本号；项目会使用对应的 `lib/asf-<版本>/` 引用目录。

产物：`AsfPlayStatus/bin/Release/net10.0/AsfPlayStatus.dll`

回归 harness 会链接并编译插件的原始 C# 源文件，ASF API 通过本地 stubs 提供：

```powershell
dotnet run --project tests/AsfPlayStatus.RegressionHarness/AsfPlayStatus.RegressionHarness.csproj
```

该 harness 验证插件逻辑，不代替针对目标 ASF 版本的正式产品构建或服务验证。

## 部署

将 DLL 放入 ASF 的 `plugins/AsfPlayStatus/` 子目录并重启 ASF：

```bash
./deploy.sh YOUR_SERVER_IP YOUR_SSH_KEY_PATH
```

手动安装：

```bash
scp AsfPlayStatus.dll root@SERVER:/opt/ASF/plugins/AsfPlayStatus/
ssh root@SERVER "chown -R asf:asf /opt/ASF/plugins/AsfPlayStatus && systemctl restart asf"
```

验证：

```bash
curl -H 'Authentication: <ASF_IPC_PASSWORD>' http://127.0.0.1:1242/api/playstatus
```

## 集成说明

- 桥接端可调用 `/Api/PlayStatus/Play`，通过 ASF 的结构化成功结果判断 play 是否接受。
- `reset`/`resume` 停止手动列表后，调用空 `POST /Api/PlayStatus` 清除旧目标。
- 若客户端自行通过 ASF 下发 `play`，应随后把相同列表 POST 到 `/Api/PlayStatus` 同步。
- 插件不可达时，客户端应显示状态未知；配置 `play_appids` 仅可标为目标列表。

## 注意事项

- 手动模式下逐游戏 `CardsRemaining` 为 `null`，`TimeRemaining` 为空；挂卡进度仅适用于自动挂卡。
- API 会掩码 bot 名并省略 `HoursPlayed`，避免暴露账号名和挂机时长。
- 持久化使用显式 `TypeInfoResolver` 和 ASF 裁剪构建保留的文件 API。
- 已在 ASF 6.3.10.3 的 linux-x64 裁剪运行时做隔离 IPC、持久化和桥接集成验证。

## 版本和 Release

默认分支的 GitHub Actions 先运行原源码回归 harness、编译 DLL 和校验版本，成功后为新版本创建标签和 Release。Release 包含源码包、DLL 安装包和 SHA256SUMS。历史标签保持原指向，不重新发布到同名标签。

## 目录

```text
AsfPlayStatus/          # .NET 10 插件类库
build.ps1               # 下载引用并构建
deploy.sh               # 部署并重启 ASF
CHANGELOG.md             # 版本记录
tests/                   # 原插件源码回归 harness
```
