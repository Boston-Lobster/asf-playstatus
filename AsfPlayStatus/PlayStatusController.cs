using System.Net;
using ArchiSteamFarm.IPC.Controllers.Api;
using ArchiSteamFarm.IPC.Responses;
using ArchiSteamFarm.Steam;
using Microsoft.AspNetCore.Mvc;

namespace AsfPlayStatus;

/// <summary>
///     Custom IPC endpoints for reading status, synchronizing a manual target, and issuing ASF's
///     own manual play action. Like other ASF IPC endpoints, requests require ASF authentication.
/// </summary>
[Route("/Api/PlayStatus")]
public sealed class PlayStatusController : ArchiController {
	// ASF 6.3.8.4's ArchiHandler.MaxGamesPlayedConcurrently is internal, so mirror its Steam limit here.
	private const int MaxGamesPlayedConcurrently = 32;

	[HttpGet]
	[ProducesResponseType<GenericResponse<IReadOnlyDictionary<string, BotPlayStatus>>>((int) HttpStatusCode.OK)]
	[ProducesResponseType<GenericResponse>((int) HttpStatusCode.BadRequest)]
	public ActionResult<GenericResponse> PlayStatusGet([FromQuery] string? bot = null) {
		// Do not set FromQuery.Name: ASF's trimmed host may omit its setter.
		if (bot != null && FindExactBot(bot) == null) {
			return BadRequest(new GenericResponse(false, $"Bot {bot} was not found"));
		}

		IReadOnlyDictionary<string, BotPlayStatus> snapshot = PlayStatusPlugin.GetStatesSnapshot(bot);
		return Ok(new GenericResponse<IReadOnlyDictionary<string, BotPlayStatus>>(snapshot));
	}

	/// <summary>Record a list already sent to ASF, or clear it by submitting an empty AppIDs list.</summary>
	[HttpPost]
	[ProducesResponseType<GenericResponse<BotPlayStatus>>((int) HttpStatusCode.OK)]
	[ProducesResponseType<GenericResponse>((int) HttpStatusCode.BadRequest)]
	[ProducesResponseType<GenericResponse>((int) HttpStatusCode.InternalServerError)]
	public async Task<ActionResult<GenericResponse>> PlayStatusSet([FromBody] PlayStatusSetRequest request) {
		if (string.IsNullOrEmpty(request.Bot)) {
			return BadRequest(new GenericResponse(false, "Bot is required"));
		}
		if (request.AppIDs == null) {
			return BadRequest(new GenericResponse(false, "AppIDs is required"));
		}

		Bot? bot = FindExactBot(request.Bot);
		if (bot == null) {
			return BadRequest(new GenericResponse(false, $"Bot {request.Bot} was not found"));
		}

		try {
			BotPlayStatus status = await PlayStatusPlugin.SetManualGames(bot, request.AppIDs, request.GameNames).ConfigureAwait(false);
			return Ok(new GenericResponse<BotPlayStatus>(PlayStatusPlugin.MaskForApi(status)));
		} catch (IOException) {
			return StatusCode((int) HttpStatusCode.InternalServerError, new GenericResponse(false, "Failed to persist play status"));
		}
	}

	/// <summary>Ask ASF to launch games, then persist the target only after ASF accepts the action.</summary>
	[HttpPost("Play")]
	[ProducesResponseType<GenericResponse<BotPlayStatus>>((int) HttpStatusCode.OK)]
	[ProducesResponseType<GenericResponse>((int) HttpStatusCode.BadRequest)]
	[ProducesResponseType<GenericResponse>((int) HttpStatusCode.InternalServerError)]
	public async Task<ActionResult<GenericResponse>> PlayStatusPlay([FromBody] PlayStatusSetRequest request) {
		if (string.IsNullOrEmpty(request.Bot)) {
			return BadRequest(new GenericResponse(false, "Bot is required"));
		}
		if (request.AppIDs == null) {
			return BadRequest(new GenericResponse(false, "AppIDs is required"));
		}

		if ((request.AppIDs.Count == 0) || (request.AppIDs.Count > MaxGamesPlayedConcurrently) || request.AppIDs.Any(static appID => appID == 0)) {
			return BadRequest(new GenericResponse(false, $"AppIDs must contain 1 to {MaxGamesPlayedConcurrently} non-zero IDs"));
		}

		Bot? bot = FindExactBot(request.Bot);
		if (bot == null) {
			return BadRequest(new GenericResponse(false, $"Bot {request.Bot} was not found"));
		}

		HashSet<uint> gameIDs = new(request.AppIDs);
		(bool success, string message) = await bot.Actions.Play(gameIDs).ConfigureAwait(false);
		if (!success) {
			return BadRequest(new GenericResponse(false, message));
		}

		try {
			BotPlayStatus status = await PlayStatusPlugin.SetManualGames(bot, gameIDs, request.GameNames).ConfigureAwait(false);
			return Ok(new GenericResponse<BotPlayStatus>(PlayStatusPlugin.MaskForApi(status)));
		} catch (IOException) {
			return StatusCode((int) HttpStatusCode.InternalServerError, new GenericResponse(false, "ASF accepted play, but status persistence failed"));
		}
	}

	private static Bot? FindExactBot(string botName) {
		return Bot.BotsReadOnly?.Values.FirstOrDefault(bot => string.Equals(bot.BotName, botName, StringComparison.OrdinalIgnoreCase));
	}
}
