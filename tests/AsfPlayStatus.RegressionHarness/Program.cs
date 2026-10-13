using System.Reflection;
using System.Text.Json;
using ArchiSteamFarm.IPC.Responses;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Cards;
using AsfPlayStatus;
using Microsoft.AspNetCore.Mvc;

static void Check(bool condition, string message) {
	if (!condition) {
		throw new InvalidOperationException(message);
	}
	Console.WriteLine($"PASS {message}");
}

static void ResetPluginState() {
	const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
	((Dictionary<string, BotPlayStatus>) typeof(PlayStatusPlugin).GetField("States", flags)!.GetValue(null)!).Clear();
	((Dictionary<string, BotPlayStatus>) typeof(PlayStatusPlugin).GetField("PendingManualRestore", flags)!.GetValue(null)!).Clear();
	((HashSet<string>) typeof(PlayStatusPlugin).GetField("DestroyedBots", flags)!.GetValue(null)!).Clear();
}

static async Task InvokeRestoreFromDisk() {
	MethodInfo method = typeof(PlayStatusPlugin).GetMethod("RestoreFromDisk", BindingFlags.Static | BindingFlags.NonPublic)!;
	await ((Task) method.Invoke(null, null)!).ConfigureAwait(false);
}

static string GetPersistencePath() => Path.Combine(Path.GetDirectoryName(typeof(PlayStatusPlugin).Assembly.Location)!, "PlayStatus.json");

string persistencePath = GetPersistencePath();
string tempPath = persistencePath + ".tmp";
File.Delete(persistencePath);
if (Directory.Exists(tempPath)) {
	Directory.Delete(tempPath, recursive: true);
}
File.Delete(tempPath);
ResetPluginState();
Bot.Clear();

PlayStatusController controller = new();
PlayStatusPlugin plugin = new();
Bot bot = new("Primary") { IsConnectedAndLoggedOn = true };
Bot.Register(bot);
await plugin.OnBotInit(bot);

// Filter by real bot name while exposing only the masked name; ASF's name lookup is case-insensitive.
await PlayStatusPlugin.SetManualGames(bot, [101], new Dictionary<string, string> { ["101"] = "Example" });
Bot secondaryBot = new("Secondary");
Bot.Register(secondaryBot);
await plugin.OnBotInit(secondaryBot);
await PlayStatusPlugin.SetManualGames(secondaryBot, [102], null);
ActionResult<GenericResponse> selectedResult = controller.PlayStatusGet("primary");
var selected = (GenericResponse<IReadOnlyDictionary<string, BotPlayStatus>>) ((ObjectResult) selectedResult.Result!).Value!;
Check(selected.Result.Count == 1 && selected.Result.ContainsKey("bot1") && selected.Result["bot1"].BotName == "bot1", "GET bot selector returns one state with masked key and name");
var selectedSecondary = (GenericResponse<IReadOnlyDictionary<string, BotPlayStatus>>) ((ObjectResult) controller.PlayStatusGet("Secondary").Result!).Value!;
Check(selectedSecondary.Result.Count == 1 && selectedSecondary.Result.ContainsKey("bot2"), "single-bot GET preserves stable masked position");
var allStates = (GenericResponse<IReadOnlyDictionary<string, BotPlayStatus>>) ((ObjectResult) controller.PlayStatusGet().Result!).Value!;
Check(allStates.Result.Count == 2, "GET without bot query retains the full response");
Check(controller.PlayStatusGet("Primary-extra").Result is BadRequestObjectResult, "GET rejects a non-exact bot name");

// Empty synchronization clears the manual cache without issuing an ASF action.
int playCallsBeforeClear = bot.Actions.PlayCalls;
await controller.PlayStatusSet(new PlayStatusSetRequest { Bot = "Primary", AppIDs = [] });
BotPlayStatus cleared = PlayStatusPlugin.GetStatesSnapshot()["bot1"];
Check(cleared.CurrentGames.Count == 0 && cleared.Source == PlayStatusPlugin.SourceNone, "empty POST clears list and changes source to None");
Check(bot.Actions.PlayCalls == playCallsBeforeClear, "status synchronization does not issue play action");

// The structured Play endpoint calls ASF and records only accepted target lists.
bot.Actions.Success = true;
ActionResult<GenericResponse> playResult = await controller.PlayStatusPlay(new PlayStatusSetRequest {
	Bot = "Primary",
	AppIDs = [201, 202],
	GameNames = new Dictionary<string, string> { ["201"] = "First", ["202"] = "Second" }
});
Check(playResult.Result is OkObjectResult && bot.Actions.LastPlayedIDs.Count == 2, "Play endpoint calls ASF with requested IDs");
Check(PlayStatusPlugin.GetStatesSnapshot()["bot1"].CurrentGames.Count == 2, "successful ASF play is synchronized and persisted");

bot.Actions.Success = false;
bot.Actions.Message = "not connected";
ActionResult<GenericResponse> failedPlay = await controller.PlayStatusPlay(new PlayStatusSetRequest { Bot = "Primary", AppIDs = [303] });
Check(failedPlay.Result is BadRequestObjectResult, "ASF play failure becomes a failed API response");
GenericResponse failedPlayBody = (GenericResponse) ((ObjectResult) failedPlay.Result!).Value!;
Check(!failedPlayBody.Success && failedPlayBody.Message == "not connected", "ASF play failure preserves its structured error message");
Check(PlayStatusPlugin.GetStatesSnapshot()["bot1"].CurrentGames.Count == 2, "failed ASF play does not replace the saved target");
Check((await controller.PlayStatusPlay(new PlayStatusSetRequest { Bot = "Primary", AppIDs = [0] })).Result is BadRequestObjectResult, "Play endpoint rejects zero AppID");
Check((await controller.PlayStatusPlay(new PlayStatusSetRequest { Bot = "Primary", AppIDs = Enumerable.Range(1, 33).Select(static id => (uint) id).ToArray() })).Result is BadRequestObjectResult, "Play endpoint rejects more than 32 AppIDs");

// A later non-farming poll cannot know Steam's actual state; the caller must send an empty sync after reset/resume.
await plugin.OnBotDisconnected(bot, SteamKit2.EResult.OK);
Check(PlayStatusPlugin.GetStatesSnapshot()["bot1"].CurrentGames.Count == 2, "manual state remains the last synchronized target until explicitly cleared");
await controller.PlayStatusSet(new PlayStatusSetRequest { Bot = "Primary", AppIDs = [] });

// A manual POST persists only manual targets, never another bot's transient Farming view.
Bot transitionBot = new("Transition");
Bot.Register(transitionBot);
await PlayStatusPlugin.SetManualGames(transitionBot, [3030], null);
transitionBot.CardsFarmer.NowFarming = true;
transitionBot.CardsFarmer.CurrentGamesFarmingReadOnly = [new Game(3031, "Now farming", 1)];
transitionBot.CardsFarmer.GamesToFarmReadOnly = transitionBot.CardsFarmer.CurrentGamesFarmingReadOnly;
await plugin.OnBotLoggedOn(transitionBot);
await Task.Delay(100);
using (JsonDocument afterManualToFarming = JsonDocument.Parse(await File.ReadAllTextAsync(persistencePath))) {
	Check(!afterManualToFarming.RootElement.GetProperty("Bots").TryGetProperty("Transition", out _), "switching from Manual to Farming removes the old manual target from persistence");
}

Bot farmingBot = new("Farmer");
farmingBot.CardsFarmer.NowFarming = true;
farmingBot.CardsFarmer.CurrentGamesFarmingReadOnly = [new Game(404, "Farmed", 2)];
farmingBot.CardsFarmer.GamesToFarmReadOnly = farmingBot.CardsFarmer.CurrentGamesFarmingReadOnly;
Bot.Register(farmingBot);
await plugin.OnBotInit(farmingBot);
Bot otherManualBot = new("OtherManual");
Bot.Register(otherManualBot);
await PlayStatusPlugin.SetManualGames(otherManualBot, [505], null);
using (JsonDocument persisted = JsonDocument.Parse(await File.ReadAllTextAsync(persistencePath))) {
	JsonElement bots = persisted.RootElement.GetProperty("Bots");
	Check(bots.TryGetProperty("OtherManual", out _) && !bots.TryGetProperty("Farmer", out _), "persistence contains manual targets only");
}

// Restore only known bots, sanitize live flags, and ignore old farming records.
Bot.Clear();
ResetPluginState();
Bot liveBot = new("Live") { IsConnectedAndLoggedOn = false };
Bot.Register(liveBot);
BotPlayStatus staleManual = new() {
	BotName = "Live", Connected = true, Paused = true, Farming = true,
	CurrentGames = [new PlayStatusGame { AppID = 606, GameName = "Saved", CardsRemaining = 4 }],
	Source = PlayStatusPlugin.SourceManual, SeenAt = "old", UpdatedAt = "old"
};
BotPlayStatus staleFarming = new() {
	BotName = "Farmer", CurrentGames = [new PlayStatusGame { AppID = 707, GameName = "Old farm" }],
	Source = PlayStatusPlugin.SourceFarming
};
PersistedPlayState persistedFixture = new() {
	Bots = new Dictionary<string, BotPlayStatus>(StringComparer.OrdinalIgnoreCase) {
		["Live"] = staleManual,
		["Deleted"] = new BotPlayStatus { BotName = "Deleted", CurrentGames = [new PlayStatusGame { AppID = 808, GameName = "Ghost" }], Source = PlayStatusPlugin.SourceManual },
		["Farmer"] = staleFarming
	}
};
await File.WriteAllTextAsync(persistencePath, JsonSerializer.Serialize(persistedFixture));
await InvokeRestoreFromDisk();
IReadOnlyDictionary<string, BotPlayStatus> restored = PlayStatusPlugin.GetStatesSnapshot();
Check(restored.Count == 1 && restored.ContainsKey("bot1"), "restore exposes only a currently existing bot");
BotPlayStatus restoredLive = restored["bot1"];
Check(!restoredLive.Connected && !restoredLive.Paused && !restoredLive.Farming && restoredLive.SeenAt == "", "restore does not reuse stale runtime flags or timestamps");
Check(restoredLive.Source == PlayStatusPlugin.SourceManual && restoredLive.CurrentGames.Single().AppID == 606, "restore keeps a known bot's manual target");

// Destroy removes the target from both memory and the next persisted snapshot.
await plugin.OnBotDestroy(liveBot);
using (JsonDocument persistedAfterDestroy = JsonDocument.Parse(await File.ReadAllTextAsync(persistencePath))) {
	Check(persistedAfterDestroy.RootElement.GetProperty("Bots").EnumerateObject().Count() == 0, "bot destruction persists removal of its target");
}

// Concurrent callers serialize writes to the shared temporary file.
Bot[] parallelBots = Enumerable.Range(0, 12).Select(index => new Bot($"Parallel{index}")).ToArray();
foreach (Bot parallelBot in parallelBots) {
	Bot.Register(parallelBot);
}
await Task.WhenAll(parallelBots.Select((parallelBot, index) => PlayStatusPlugin.SetManualGames(parallelBot, [(uint) (900 + index)], null)));
using (JsonDocument parallelPersisted = JsonDocument.Parse(await File.ReadAllTextAsync(persistencePath))) {
	Check(parallelPersisted.RootElement.GetProperty("Bots").EnumerateObject().Count() == parallelBots.Length, "parallel updates produce a valid complete persistence file");
}

// An unwritable temporary-file path must surface as an HTTP 500, not a false success.
File.Delete(persistencePath);
Directory.CreateDirectory(tempPath);
Bot.Register(bot);
ActionResult<GenericResponse> writeFailure = await controller.PlayStatusSet(new PlayStatusSetRequest { Bot = "Primary", AppIDs = [111] });
Check(writeFailure.Result is ObjectResult { StatusCode: 500 }, "persistence failure returns HTTP 500");
Check(!((GenericResponse) ((ObjectResult) writeFailure.Result!).Value!).Success, "persistence failure response is not reported as successful");
Directory.Delete(tempPath, recursive: true);

File.Delete(persistencePath);
File.Delete(tempPath);
Console.WriteLine("All original-source regression scenarios passed.");
