using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AsfPlayStatus;

/// <summary>持久化到磁盘的状态（手动 play 列表）。</summary>
internal sealed class PersistedPlayState {
	public Dictionary<string, BotPlayStatus> Bots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>单个正在游玩的游戏条目。</summary>
public sealed class PlayStatusGame {
	[JsonInclude]
	public uint AppID { get; init; }

	[JsonInclude]
	public string GameName { get; init; } = "";

	/// <summary>自动挂卡时：该游戏剩余可掉落卡牌数（手动模式为 null）。</summary>
	[JsonInclude]
	public ushort? CardsRemaining { get; init; }
}

/// <summary>单个 bot 的实时游玩状态（插件维护在 ASF 内存中）。</summary>
public sealed class BotPlayStatus {
	[JsonInclude]
	public string BotName { get; set; } = "";

	[JsonInclude]
	public bool Connected { get; set; }

	[JsonInclude]
	public bool Paused { get; set; }

	[JsonInclude]
	public bool Farming { get; set; }

	/// <summary>当前正在游玩的游戏列表。</summary>
	[JsonInclude]
	public IReadOnlyCollection<PlayStatusGame> CurrentGames { get; set; } = [];

	/// <summary>数据来源：Farming（自动挂卡）/ Manual（手动 play 命令）/ None。</summary>
	[JsonInclude]
	public string Source { get; set; } = PlayStatusPlugin.SourceNone;

	[JsonInclude]
	public string UpdatedAt { get; set; } = "";

	/// <summary>自动挂卡时：剩余挂卡时间（HH:mm:ss），手动模式为空字符串。</summary>
	[JsonInclude]
	public string TimeRemaining { get; set; } = "";

	/// <summary>自动挂卡队列中剩余可掉落卡牌总数（手动模式为 0）。</summary>
	[JsonInclude]
	public uint CardsRemaining { get; set; }

	/// <summary>最近一次实时轮询时间（每次定时刷新都会更新，用于前端显示状态新鲜度）。</summary>
	[JsonInclude]
	public string SeenAt { get; set; } = "";

	/// <summary>生成对外输出的副本，仅替换账号名为掩码（隐私处理）。</summary>
	public BotPlayStatus CloneWithBotName(string maskedBotName) {
		return new BotPlayStatus {
			BotName = maskedBotName,
			Connected = Connected,
			Paused = Paused,
			Farming = Farming,
			CurrentGames = CurrentGames,
			Source = Source,
			TimeRemaining = TimeRemaining,
			CardsRemaining = CardsRemaining,
			UpdatedAt = UpdatedAt,
			SeenAt = SeenAt
		};
	}
}

/// <summary>POST /Api/PlayStatus 请求体：记录手动 play 的目标游戏列表。</summary>
public sealed class PlayStatusSetRequest {
	[JsonInclude]
	[Required]
	public string Bot { get; init; } = "";

	[JsonInclude]
	[Required]
	public IReadOnlyCollection<uint> AppIDs { get; init; } = [];

	/// <summary>可选的 AppID(字符串键) -> 游戏名映射。</summary>
	[JsonInclude]
	public Dictionary<string, string>? GameNames { get; init; }
}

