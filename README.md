# AsfPlayStatus

> 版本：**v1.0.0** ｜ 兼容：ASF 6.3.8.4（.NET 10，linux-x64 单文件裁剪构建）

把 ASF 内存中的“正在游玩”游戏列表暴露成自定义 IPC 端点，供 [asf-nav-bridge](https://github.com/Boston-Lobster/asf-nav-bridge)（导航页状态栏）等客户端实时读取。

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
        { "AppID": 789012, "GameName": "Example Game" },
        { "AppID": 123456, "GameName": "Example Game" }
      ],
      "Source": "Manual",
      "UpdatedAt": "2026-08-08 21:30:00"
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

## 注意事项

- ASF 官方 linux-x64 包是**裁剪(trimmed)**单文件构建：插件内只使用 ASF 自身保留的文件 API（`File.WriteAllTextAsync`/`ReadAllTextAsync`/`Move`）和显式 `TypeInfoResolver` 的反射式 JSON 序列化，避免 `MissingMethodException`；
- 若直接修改 `play-keep.sh` 的挂机列表，请同步更新桥接 `config.json`（或手动 POST 一次），导航页才会显示新列表。

## 结构

```
AsfPlayStatus/          # 插件源码（net10.0 类库）
build.ps1               # 下载引用 + 编译
deploy.sh               # 一键部署到服务器并重启 ASF
```
