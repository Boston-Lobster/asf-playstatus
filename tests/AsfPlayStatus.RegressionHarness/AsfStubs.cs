using System.Collections.Concurrent;

namespace System.Composition {
	[AttributeUsage(AttributeTargets.Class)]
	public sealed class ExportAttribute(Type contractType) : Attribute {
		public Type ContractType { get; } = contractType;
	}
}

namespace ArchiSteamFarm.Core {
	public static class ASF {
		public static readonly TestLogger ArchiLogger = new();
	}

	public sealed class TestLogger {
		public void LogGenericInfo(string message) { }
		public void LogGenericWarning(string message) { }
	}
}

namespace ArchiSteamFarm.Plugins.Interfaces {
	public interface IPlugin {
		string Name { get; }
		Version Version { get; }
		Task OnLoaded();
	}

	public interface IBot {
		Task OnBotInit(ArchiSteamFarm.Steam.Bot bot);
		Task OnBotDestroy(ArchiSteamFarm.Steam.Bot bot);
	}

	public interface IBotConnection {
		Task OnBotLoggedOn(ArchiSteamFarm.Steam.Bot bot);
		Task OnBotDisconnected(ArchiSteamFarm.Steam.Bot bot, SteamKit2.EResult reason);
	}
}

namespace ArchiSteamFarm.Steam {
	public sealed class Bot(string botName) {
		private static readonly ConcurrentDictionary<string, Bot> MutableBots = new(StringComparer.OrdinalIgnoreCase);

		public static IReadOnlyDictionary<string, Bot> BotsReadOnly => MutableBots;
		public string BotName { get; } = botName;
		public bool IsConnectedAndLoggedOn { get; set; }
		public Cards.CardsFarmer CardsFarmer { get; } = new();
		public TestActions Actions { get; } = new();

		public static Bot? GetBot(string name) => MutableBots.TryGetValue(name, out Bot? bot) ? bot : null;
		public static void Register(Bot bot) => MutableBots[bot.BotName] = bot;
		public static void Unregister(string name) => MutableBots.TryRemove(name, out _);
		public static void Clear() => MutableBots.Clear();
	}

	public sealed class TestActions {
		public bool Success { get; set; } = true;
		public string Message { get; set; } = "accepted";
		public IReadOnlyCollection<uint> LastPlayedIDs { get; private set; } = [];
		public int PlayCalls { get; private set; }

		public Task<(bool Success, string Message)> Play(IReadOnlyCollection<uint> gameIDs, string? gameName = null) {
			PlayCalls++;
			LastPlayedIDs = gameIDs.ToArray();
			return Task.FromResult((Success, Message));
		}
	}
}

namespace ArchiSteamFarm.Steam.Cards {
	public sealed class Game(uint appID, string gameName, ushort cardsRemaining) {
		public uint AppID { get; } = appID;
		public string GameName { get; } = gameName;
		public ushort CardsRemaining { get; } = cardsRemaining;
	}

	public sealed class CardsFarmer {
		public bool NowFarming { get; set; }
		public bool Paused { get; set; }
		public IReadOnlyCollection<Game> CurrentGamesFarmingReadOnly { get; set; } = [];
		public IReadOnlyCollection<Game> GamesToFarmReadOnly { get; set; } = [];
		public TimeSpan TimeRemaining { get; set; }
	}
}

namespace ArchiSteamFarm.IPC.Controllers.Api {
	public class ArchiController : Microsoft.AspNetCore.Mvc.ControllerBase { }
}

namespace ArchiSteamFarm.IPC.Responses {
	public class GenericResponse(bool success = true, string message = "OK") {
		public bool Success { get; } = success;
		public string Message { get; } = message;
	}

	public sealed class GenericResponse<T>(T result, bool success = true, string message = "OK") : GenericResponse(success, message) {
		public T Result { get; } = result;
	}
}

namespace SteamKit2 {
	public enum EResult {
		OK
	}
}
