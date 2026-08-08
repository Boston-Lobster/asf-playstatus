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
