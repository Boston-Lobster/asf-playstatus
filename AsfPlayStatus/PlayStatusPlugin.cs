using System.Collections.Concurrent;
using System.Composition;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Cards;
using SteamKit2;

namespace AsfPlayStatus;

/// <summary>
///     把 ASF 内存中的“正在游玩”列表暴露成自定义 IPC 端点（/Api/PlayStatus）的插件。
///     数据来源：自动挂卡时读取 CardsFarmer.CurrentGamesFarming（ASF 内存），
///     手动 play 模式由外部（如 asf-bridge）通过 POST 端点写入内存并持久化，
///     以便导航页/桥接实时读取当前挂机游戏。
/// </summary>
[Export(typeof(IPlugin))]
internal sealed class PlayStatusPlugin : IPlugin, IBot, IBotConnection {
	internal const string SourceNone = "None";
	internal const string SourceFarming = "Farming";
	internal const string SourceManual = "Manual";

	private static readonly ConcurrentDictionary<string, BotPlayStatus> States = new(StringComparer.OrdinalIgnoreCase);

	private static readonly TimeSpan RefreshPeriod = TimeSpan.FromSeconds(15);

	// 注意：ASF 单文件是裁剪(trimmed)构建，但保留了反射式 JSON API
	// （ASF 自己的 JsonUtilities 就用 DefaultJsonTypeInfoResolver），
	// 因此这里显式配置 TypeInfoResolver 即可与 ASF 运行时兼容。
	private static readonly JsonSerializerOptions PersistenceJsonOptions = new(JsonSerializerDefaults.Strict) {
		WriteIndented = true,
		TypeInfoResolver = new DefaultJsonTypeInfoResolver()
	};

	private Timer? RefreshTimer;

	public string Name => nameof(PlayStatusPlugin);

	public Version Version => typeof(PlayStatusPlugin).Assembly.GetName().Version ?? new Version(1, 0, 0);

	public async Task OnLoaded() {
		await RestoreFromDisk().ConfigureAwait(false);

		RefreshTimer = new Timer(static _ => RefreshAll(), null, TimeSpan.FromSeconds(5), RefreshPeriod);
		ASF.ArchiLogger.LogGenericInfo($"{Name} V{Version} loaded: exposing GET/POST /Api/PlayStatus");
	}

	public Task OnBotInit(Bot bot) {
		RefreshBot(bot);

		return Task.CompletedTask;
	}

	public Task OnBotDestroy(Bot bot) {
		States.TryRemove(bot.BotName, out _);

		return Task.CompletedTask;
	}

	public Task OnBotLoggedOn(Bot bot) {
		RefreshBot(bot);

		return Task.CompletedTask;
	}

	public Task OnBotDisconnected(Bot bot, EResult reason) {
		RefreshBot(bot);

		return Task.CompletedTask;
	}

	internal static IReadOnlyDictionary<string, BotPlayStatus> GetStatesSnapshot() {
		// 隐私处理：对外输出的 key 与 BotName 均使用掩码（bot1/bot2...），不暴露真实账号名
		Dictionary<string, BotPlayStatus> masked = new(StringComparer.OrdinalIgnoreCase);

		foreach (string botName in States.Keys.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)) {
			string maskedName = $"bot{masked.Count + 1}";
			masked[maskedName] = States[botName].CloneWithBotName(maskedName);
		}

		return masked;
	}

	/// <summary>隐私处理：返回单个状态对外输出的副本（BotName 替换为掩码）。</summary>
	internal static BotPlayStatus MaskForApi(BotPlayStatus state) {
		string[] names = States.Keys.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray();
		int index = Array.IndexOf(names, state.BotName);
		string maskedName = index >= 0 ? $"bot{index + 1}" : "bot";

		return state.CloneWithBotName(maskedName);
	}

	/// <summary>记录手动 play 的目标游戏列表到内存并持久化，返回更新后的状态。</summary>
	internal static async Task<BotPlayStatus> SetManualGames(Bot bot, IReadOnlyCollection<uint> appIDs, IReadOnlyDictionary<string, string>? gameNames) {
		ArgumentNullException.ThrowIfNull(appIDs);

		if (appIDs.Count == 0) {
			throw new ArgumentException("AppIDs must not be empty", nameof(appIDs));
		}

		BotPlayStatus state = States.GetOrAdd(bot.BotName, static name => new BotPlayStatus { BotName = name });
		RefreshLiveFlags(bot, state);

		state.CurrentGames = appIDs.Select(appID => new PlayStatusGame {
			AppID = appID,
			GameName = (gameNames != null) && gameNames.TryGetValue(appID.ToString(CultureInfo.InvariantCulture), out string? name) && !string.IsNullOrEmpty(name)
				? name
				: appID.ToString(CultureInfo.InvariantCulture)
		}).ToList();

		state.Source = SourceManual;
		state.UpdatedAt = Now();
		state.TimeRemaining = "";
		state.CardsRemaining = 0;
		state.SeenAt = Now();
		await PersistToDisk().ConfigureAwait(false);

		return state;
	}

	private static void RefreshAll() {
		if (Bot.BotsReadOnly is not { Count: > 0 }) {
			return;
		}

		foreach (Bot bot in Bot.BotsReadOnly.Values) {
			RefreshBot(bot);
		}
	}

	private static void RefreshBot(Bot bot) {
		BotPlayStatus state = States.GetOrAdd(bot.BotName, static name => new BotPlayStatus { BotName = name });
		RefreshLiveFlags(bot, state);

		// 自动挂卡时：实时读取 ASF 内存中的正在游玩列表 + 挂卡进度
		if (bot.CardsFarmer.NowFarming) {
			IReadOnlyCollection<Game> farming = bot.CardsFarmer.CurrentGamesFarmingReadOnly;

			if (farming.Count > 0) {
				state.CurrentGames = farming.Select(static game => new PlayStatusGame {
					AppID = game.AppID,
					GameName = game.GameName,
					CardsRemaining = game.CardsRemaining
				}).ToList();

				state.Source = SourceFarming;
				state.TimeRemaining = FormatTimeSpan(bot.CardsFarmer.TimeRemaining);
				state.CardsRemaining = (uint) bot.CardsFarmer.GamesToFarmReadOnly.Sum(static game => game.CardsRemaining);
				state.UpdatedAt = Now();
			}
		} else if (state.Source == SourceFarming) {
			// 自动挂卡已结束：清空列表，避免显示过期的挂机状态
			state.CurrentGames = [];
			state.Source = SourceNone;
			state.TimeRemaining = "";
			state.CardsRemaining = 0;
			state.UpdatedAt = Now();
		} else if (state.Source == SourceManual) {
			// 手动 play：ASF 不保存该列表，由 play-keep/桥接每次下发后 POST 同步；
			// 这里保留最近一次同步结果，只刷新实时在线标志
			state.TimeRemaining = "";
			state.CardsRemaining = 0;
		}

		// 每次轮询都刷新 SeenAt，供前端展示“状态实时更新时间”
		state.SeenAt = Now();
	}

	private static void RefreshLiveFlags(Bot bot, BotPlayStatus state) {
		state.Connected = bot.IsConnectedAndLoggedOn;
		state.Paused = bot.CardsFarmer.Paused;
		state.Farming = bot.CardsFarmer.NowFarming;
	}

	private static string Now() {
		return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
	}

	private static string FormatTimeSpan(TimeSpan timeSpan) {
		return timeSpan.TotalSeconds < 1 ? "" : timeSpan.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
	}

	private static string PersistenceFilePath {
		get {
			string pluginDirectory = Path.GetDirectoryName(typeof(PlayStatusPlugin).Assembly.Location) ?? Directory.GetCurrentDirectory();

			return Path.Combine(pluginDirectory, "PlayStatus.json");
		}
	}

	private static async Task PersistToDisk() {
		try {
			PersistedPlayState persisted = new() {
				Bots = States.Where(static pair => pair.Value.CurrentGames.Count > 0)
					.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase)
			};

			string json = JsonSerializer.Serialize(persisted, PersistenceJsonOptions);
			string tmpPath = PersistenceFilePath + ".tmp";

			// 注意：ASF 单文件是裁剪(trimmed)构建，只用 ASF 自身保留的文件 API
			await File.WriteAllTextAsync(tmpPath, json).ConfigureAwait(false);
			File.Move(tmpPath, PersistenceFilePath, true);
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericWarning($"{nameof(PlayStatusPlugin)}: failed to persist state: {e.Message}");
		}
	}

	private static async Task RestoreFromDisk() {
		try {
			if (!File.Exists(PersistenceFilePath)) {
				return;
			}

			PersistedPlayState? persisted = JsonSerializer.Deserialize<PersistedPlayState>(await File.ReadAllTextAsync(PersistenceFilePath).ConfigureAwait(false), PersistenceJsonOptions);

			if (persisted?.Bots == null) {
				return;
			}

			foreach ((string botName, BotPlayStatus status) in persisted.Bots) {
				if (string.IsNullOrEmpty(botName) || (status.CurrentGames.Count == 0)) {
					continue;
				}

				status.BotName = botName;
				States[botName] = status;
			}

			if (States.Count > 0) {
				ASF.ArchiLogger.LogGenericInfo($"{nameof(PlayStatusPlugin)}: restored manual play list from {PersistenceFilePath}");
			}
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericWarning($"{nameof(PlayStatusPlugin)}: failed to restore state: {e.Message}");
		}
	}

}
