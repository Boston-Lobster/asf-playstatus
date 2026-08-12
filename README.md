# AsfPlayStatus

> 版本：**v1.1.0** ｜ 兼容：ASF 6.3.8.4（.NET 10，linux-x64 单文件裁剪构建）

把 ASF 内存中的“正在游玩”游戏列表暴露成自定义 IPC 端点，供 [asf-nav-bridge](https://github.com/Boston-Lobster/asf-nav-bridge)（导航页状态栏）等客户端实时读取。

**v1.1.0 变更：实时挂机显示。**

- 自动挂卡时逐游戏返回 `CardsRemaining`（剩余卡牌）与 `HoursPlayed`（已挂小时），并在 bot 级返回 `TimeRemaining`（预计剩余时间）与 `CardsRemaining`（队列总剩余卡牌）；
- 新增 `SeenAt`：插件每 15 秒从 ASF 内存轮询一次并刷新，前端可据此展示“状态更新于 xx:xx:xx”；
- 自动挂卡结束后自动清空列表，不再显示过期状态；
- 手动 `play` 列表由外部（play-keep.sh / asf-bridge）POST 同步，建议每次下发 play 后立即 POST，保证显示与 ASF 实际收到的命令一致。

## 为什么需要它

ASF 6.x 的手动 `play` 命令只把游戏列表发送给 Steam，**不会**把它存到任何可读属性里（`CardsFarmer.CurrentGamesFarming` 只在自动挂卡时非空，`CardsFarmer.Paused=true` 在手动模式下是正常现象）。因此本插件负责在 ASF 进程内维护这个列表：

- **自动挂卡时**：直接读取 ASF 内存 `CardsFarmer.CurrentGamesFarming`（Source=`Farming`）；
- **手动 `play` 时**：由外部（如 asf-bridge 重连成功后）通过 `POST /Api/PlayStatus` 把目标列表写入插件内存并持久化（Source=`Manual`）；
- **持久化**：写入 `plugins/AsfPlayStatus/PlayStatus.json`，ASF 重启后自动恢复，不依赖 play-keep.sh 重复下发。

## 端点（均需 `Authentication` 请求头，同 ASF 内置 IPC）

### GET /Api/PlayStatus

返回所有 bot 的实时状态：

```json
{
  "Result": {
    "YOUR_BOT": {
      "BotName": "YOUR_BOT",
      "Connected": true,
      "Paused": true,
      "Farming": false,
      "CurrentGames": [
        { "AppID": 123456, "GameName": "Example Game", "CardsRemaining": 3, "HoursPlayed": 12.5 },
        { "AppID": 654321, "GameName": "Another Game 1896", "CardsRemaining": 0, "HoursPlayed": 8.2 }
      ],
      "Source": "Manual",
      "TimeRemaining": "01:30:00",
      "CardsRemaining": 3,
      "UpdatedAt": "2026-08-12 23:30:00",
      "SeenAt": "2026-08-12 23:30:15"
    }
  },
  "Message": "OK",
  "Success": true
}
```

### POST /Api/PlayStatus

记录手动 `play` 的目标列表（写入内存 + 持久化）：

```json
{
  "Bot": "YOUR_BOT",
  "AppIDs": [123456, 654321, 789012],
  "GameNames": {
    "123456": "Example Game",
    "123456": "Example Game",
    "654321": "Another Game"
  }
}
```

## 构建

需要本机 .NET 10 SDK。`build.ps1` 会自动下载 ASF 6.3.8.4 generic 包中的 `ArchiSteamFarm.dll` / `SteamKit2.dll` 作为编译引用：

```powershell
.\build.ps1
```

产物：`AsfPlayStatus/bin/Release/net10.0/AsfPlayStatus.dll`

## 部署

把 dll 放到 ASF 的 `plugins/AsfPlayStatus/` 子目录并重启 ASF：

```bash
./deploy.sh YOUR_SERVER_IP YOUR_SSH_KEY_PATH
```

或手动：

```bash
scp AsfPlayStatus.dll root@SERVER:/opt/ASF/plugins/AsfPlayStatus/
ssh root@SERVER "chown -R asf:asf /opt/ASF/plugins/AsfPlayStatus && systemctl restart asf"
```

验证：

```bash
curl -H 'Authentication: <ASF_IPC_PASSWORD>' http://127.0.0.1:1242/api/playstatus
```

## 与 asf-nav-bridge 集成

- asf-nav-bridge **v1.2.0** 起，`GET /asf/api/status` 优先读取本插件端点；
- 重连按钮执行 `resume` + `play` 后会主动 `POST /Api/PlayStatus` 同步列表；
- 插件不可达时回退到桥接自身 `config.json` 的 `play_appids`。

## 与 play-keep.sh 集成（推荐）

`play-keep.sh` 每次向 ASF 下发 `play` 命令后，应紧接着 POST 一次 `/Api/PlayStatus`，把同一列表写入插件：

```bash
curl -s -X POST -H "Authentication: <ASF_IPC_PASSWORD>" -H "Content-Type: application/json" \
  -d '{"Bot":"YOUR_BOT","AppIDs":[123456,654321],"GameNames":{"123456":"Example Game","654321":"Another Game 1896"}}' \
  http://127.0.0.1:1242/Api/PlayStatus > /dev/null 2>&1
```

这样即使不经过桥接，插件也能实时反映挂机自动化实际下发的游戏列表。

## 注意事项

- ASF 官方 linux-x64 包是**裁剪(trimmed)**单文件构建：插件内只使用 ASF 自身保留的文件 API（`File.WriteAllTextAsync`/`ReadAllTextAsync`/`Move`）和显式 `TypeInfoResolver` 的反射式 JSON 序列化，避免 `MissingMethodException`；
- 若直接修改 `play-keep.sh` 的挂机列表，请同步更新桥接 `config.json`，并确保脚本同时 POST 到插件（见上文），导航页才会显示新列表；
- 手动模式（Source=`Manual`）下 `CardsRemaining`/`HoursPlayed` 为 `null`，`TimeRemaining` 为空字符串——这些进度数据只在自动挂卡时才有。

## 结构

```
AsfPlayStatus/          # 插件源码（net10.0 类库）
build.ps1               # 下载引用 + 编译
deploy.sh               # 一键部署到服务器并重启 ASF
```
