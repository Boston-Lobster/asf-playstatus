using System.Globalization;
using System.Composition;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Cards;
using SteamKit2;

namespace AsfPlayStatus;

/// <summary>
///     Exposes current farming status and the last manually submitted game target list through IPC.
///     ASF does not provide a reliable API for querying the games currently shown by Steam after a
///     manual play/reset/resume command, so manual state is a synchronized target, not confirmation.
/// </summary>
[Export(typeof(IPlugin))]
internal sealed class PlayStatusPlugin : IPlugin, IBot, IBotConnection {
	internal const string SourceNone = "None";
	internal const string SourceFarming = "Farming";
	internal const string SourceManual = "Manual";

	private static readonly object StateLock = new();
	private static readonly Dictionary<string, BotPlayStatus> States = new(StringComparer.OrdinalIgnoreCase);
	private static readonly Dictionary<string, BotPlayStatus> PendingManualRestore = new(StringComparer.OrdinalIgnoreCase);
	private static readonly HashSet<string> DestroyedBots = new(StringComparer.OrdinalIgnoreCase);
	private static readonly SemaphoreSlim PersistenceSemaphore = new(1, 1);

	private static readonly TimeSpan RefreshPeriod = TimeSpan.FromSeconds(15);

	private static readonly JsonSerializerOptions PersistenceJsonOptions = new(JsonSerializerDefaults.Strict) {
		WriteIndented = true,
		TypeInfoResolver = new DefaultJsonTypeInfoResolver()
	};

	private Timer? RefreshTimer;

	public string Name => nameof(PlayStatusPlugin);

	public Version Version => typeof(PlayStatusPlugin).Assembly.GetName().Version ?? new Version(1, 0, 0);

	public async Task OnLoaded() {
		await RestoreFromDisk().ConfigureAwait(false);

		// Depending on ASF startup timing, some bots may already exist before the plugin is loaded.
		if (Bot.BotsReadOnly is { Count: > 0 }) {
			foreach (Bot bot in Bot.BotsReadOnly.Values) {
				RestoreManualState(bot);
			}
		}

		RefreshTimer = new Timer(static _ => RefreshAll(), null, TimeSpan.FromSeconds(5), RefreshPeriod);
		ASF.ArchiLogger.LogGenericInfo($"{Name} V{Version} loaded: exposing GET/POST /Api/PlayStatus");
	}

	public Task OnBotInit(Bot bot) {
		lock (StateLock) {
			DestroyedBots.Remove(bot.BotName);
		}

		RestoreManualState(bot);
		RefreshBot(bot);

		return Task.CompletedTask;
	}

	public async Task OnBotDestroy(Bot bot) {
		lock (StateLock) {
			States.Remove(bot.BotName);
			PendingManualRestore.Remove(bot.BotName);
			DestroyedBots.Add(bot.BotName);
		}

		try {
			await PersistToDisk().ConfigureAwait(false);
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericWarning($"{nameof(PlayStatusPlugin)}: failed to persist state after bot removal: {e.Message}");
		}
	}

	public Task OnBotLoggedOn(Bot bot) {
		RefreshBot(bot);

		return Task.CompletedTask;
	}

	public Task OnBotDisconnected(Bot bot, EResult reason) {
		RefreshBot(bot);

		return Task.CompletedTask;
	}

	internal static IReadOnlyDictionary<string, BotPlayStatus> GetStatesSnapshot(string? botName = null) {
		// Keep real names private; a selected bot retains its normal masked position.
		lock (StateLock) {
			Dictionary<string, BotPlayStatus> masked = new(StringComparer.OrdinalIgnoreCase);
			int maskIndex = 0;

			foreach ((string realBotName, BotPlayStatus state) in States.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)) {
				maskIndex++;
				if ((botName != null) && !string.Equals(realBotName, botName, StringComparison.OrdinalIgnoreCase)) {
					continue;
				}

				string maskedName = $"bot{maskIndex}";
				masked[maskedName] = state.CloneWithBotName(maskedName);
			}

			return masked;
		}
	}

	/// <summary>Return an isolated API copy with its bot name masked.</summary>
	internal static BotPlayStatus MaskForApi(BotPlayStatus state) {
		lock (StateLock) {
			int index = 0;
			string maskedName = "bot";

			foreach (string name in States.Keys.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)) {
				index++;
				if (string.Equals(name, state.BotName, StringComparison.OrdinalIgnoreCase)) {
					maskedName = $"bot{index}";
					break;
				}
			}

			return state.CloneWithBotName(maskedName);
		}
	}

	/// <summary>Record a manual target list; an empty list clears the manual status.</summary>
	internal static async Task<BotPlayStatus> SetManualGames(Bot bot, IReadOnlyCollection<uint> appIDs, IReadOnlyDictionary<string, string>? gameNames) {
		ArgumentNullException.ThrowIfNull(appIDs);

		List<PlayStatusGame> games = appIDs.Select(appID => new PlayStatusGame {
			AppID = appID,
			GameName = (gameNames != null) && gameNames.TryGetValue(appID.ToString(CultureInfo.InvariantCulture), out string? name) && !string.IsNullOrEmpty(name)
				? name
				: appID.ToString(CultureInfo.InvariantCulture)
		}).ToList();

		bool connected = bot.IsConnectedAndLoggedOn;
		bool paused = bot.CardsFarmer.Paused;
		bool farming = bot.CardsFarmer.NowFarming;
		BotPlayStatus state;

		lock (StateLock) {
			if (!States.TryGetValue(bot.BotName, out state!)) {
				state = new BotPlayStatus { BotName = bot.BotName };
				States[bot.BotName] = state;
			}

			state.Connected = connected;
			state.Paused = paused;
			state.Farming = farming;
			state.CurrentGames = games;
			state.Source = games.Count > 0 ? SourceManual : SourceNone;
			state.UpdatedAt = Now();
			state.TimeRemaining = "";
			state.CardsRemaining = 0;
			state.SeenAt = Now();
		}

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
		bool connected = bot.IsConnectedAndLoggedOn;
		bool paused = bot.CardsFarmer.Paused;
		bool farmingNow = bot.CardsFarmer.NowFarming;
		IReadOnlyCollection<Game> farming = farmingNow ? bot.CardsFarmer.CurrentGamesFarmingReadOnly : [];
		string timeRemaining = farmingNow ? FormatTimeSpan(bot.CardsFarmer.TimeRemaining) : "";
		uint cardsRemaining = farmingNow ? (uint) bot.CardsFarmer.GamesToFarmReadOnly.Sum(static game => game.CardsRemaining) : 0;

		bool persistAfterManualTargetEnds = false;
		lock (StateLock) {
			if (DestroyedBots.Contains(bot.BotName)) {
				return;
			}

			if (!States.TryGetValue(bot.BotName, out BotPlayStatus? state)) {
				state = new BotPlayStatus { BotName = bot.BotName };
				States[bot.BotName] = state;
			}

			state.Connected = connected;
			state.Paused = paused;
			state.Farming = farmingNow;

			if (farmingNow) {
				persistAfterManualTargetEnds = state.Source == SourceManual;
				state.CurrentGames = farming.Select(static game => new PlayStatusGame {
					AppID = game.AppID,
					GameName = game.GameName,
					CardsRemaining = game.CardsRemaining
				}).ToList();
				state.Source = farming.Count > 0 ? SourceFarming : SourceNone;
				state.TimeRemaining = timeRemaining;
				state.CardsRemaining = cardsRemaining;
				state.UpdatedAt = Now();
			} else if (state.Source == SourceFarming) {
				state.CurrentGames = [];
				state.Source = SourceNone;
				state.TimeRemaining = "";
				state.CardsRemaining = 0;
				state.UpdatedAt = Now();
			} else if (state.Source == SourceManual) {
				// This is the last successfully synchronized target, not confirmation of Steam state.
				state.TimeRemaining = "";
				state.CardsRemaining = 0;
			}

			// This timestamp indicates the latest plugin poll, not a confirmed Steam game list.
			state.SeenAt = Now();
		}

		if (persistAfterManualTargetEnds) {
			_ = PersistAfterRefreshAsync();
		}
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
		await PersistenceSemaphore.WaitAsync().ConfigureAwait(false);
		try {
			PersistedPlayState persisted;
			lock (StateLock) {
				persisted = new PersistedPlayState {
					Bots = States.Where(static pair => (pair.Value.Source == SourceManual) && (pair.Value.CurrentGames.Count > 0))
						.ToDictionary(static pair => pair.Key, static pair => CreatePersistedManualState(pair.Key, pair.Value), StringComparer.OrdinalIgnoreCase)
				};
			}

			string json = JsonSerializer.Serialize(persisted, PersistenceJsonOptions);
			string tmpPath = PersistenceFilePath + ".tmp";

			await File.WriteAllTextAsync(tmpPath, json).ConfigureAwait(false);
			File.Move(tmpPath, PersistenceFilePath, true);
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericWarning($"{nameof(PlayStatusPlugin)}: failed to persist state: {e.Message}");
			throw new IOException("Failed to persist play status", e);
		} finally {
			PersistenceSemaphore.Release();
		}
	}

	private static async Task PersistAfterRefreshAsync() {
		try {
			await PersistToDisk().ConfigureAwait(false);
		} catch (IOException) {
			// PersistToDisk already logs the underlying failure. The refresh callback has no HTTP caller.
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

			lock (StateLock) {
				PendingManualRestore.Clear();
				foreach ((string botName, BotPlayStatus status) in persisted.Bots) {
					if (string.IsNullOrEmpty(botName) || (status.Source != SourceManual) || (status.CurrentGames.Count == 0)) {
						continue;
					}

					PendingManualRestore[botName] = CreatePersistedManualState(botName, status);
				}
			}

			if (Bot.BotsReadOnly is { Count: > 0 }) {
				foreach (Bot bot in Bot.BotsReadOnly.Values) {
					RestoreManualState(bot);
				}
			}
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericWarning($"{nameof(PlayStatusPlugin)}: failed to restore state: {e.Message}");
		}
	}

	private static void RestoreManualState(Bot bot) {
		lock (StateLock) {
			if (!PendingManualRestore.Remove(bot.BotName, out BotPlayStatus? pending)) {
				return;
			}

			States[bot.BotName] = CreatePersistedManualState(bot.BotName, pending);
		}
	}

	private static BotPlayStatus CreatePersistedManualState(string botName, BotPlayStatus source) {
		return new BotPlayStatus {
			BotName = botName,
			Connected = false,
			Paused = false,
			Farming = false,
			CurrentGames = source.CurrentGames.Select(static game => new PlayStatusGame {
				AppID = game.AppID,
				GameName = game.GameName,
				CardsRemaining = null
			}).ToList(),
			Source = SourceManual,
			UpdatedAt = "",
			TimeRemaining = "",
			CardsRemaining = 0,
			SeenAt = ""
		};
	}
}
