using System.Net;
using ArchiSteamFarm.IPC.Controllers.Api;
using ArchiSteamFarm.IPC.Responses;
using ArchiSteamFarm.Steam;
using Microsoft.AspNetCore.Mvc;

namespace AsfPlayStatus;

/// <summary>
///     自定义 IPC 端点：GET /Api/PlayStatus 返回各 bot 当前正在游玩列表；
///     POST /Api/PlayStatus 记录手动 play 的目标游戏列表（内存 + 持久化）。
///     与 ASF 内置端点一样需要 Authentication 请求头。
/// </summary>
[Route("/Api/PlayStatus")]
public sealed class PlayStatusController : ArchiController {
	[HttpGet]
	[ProducesResponseType<GenericResponse<IReadOnlyDictionary<string, BotPlayStatus>>>((int) HttpStatusCode.OK)]
	public ActionResult<GenericResponse> PlayStatusGet() {
		IReadOnlyDictionary<string, BotPlayStatus> snapshot = PlayStatusPlugin.GetStatesSnapshot();

		return Ok(new GenericResponse<IReadOnlyDictionary<string, BotPlayStatus>>(snapshot));
	}

	[HttpPost]
	[ProducesResponseType<GenericResponse<BotPlayStatus>>((int) HttpStatusCode.OK)]
	[ProducesResponseType<GenericResponse>((int) HttpStatusCode.BadRequest)]
	public async Task<ActionResult<GenericResponse>> PlayStatusSet([FromBody] PlayStatusSetRequest request) {
		if (string.IsNullOrEmpty(request.Bot)) {
			return BadRequest(new GenericResponse(false, "Bot is required"));
		}

		if ((request.AppIDs == null) || (request.AppIDs.Count == 0)) {
			return BadRequest(new GenericResponse(false, "AppIDs is required and must not be empty"));
		}

		Bot? bot = Bot.GetBot(request.Bot);

		if (bot == null) {
			return BadRequest(new GenericResponse(false, $"Bot {request.Bot} was not found"));
		}

		BotPlayStatus status = await PlayStatusPlugin.SetManualGames(bot, request.AppIDs, request.GameNames).ConfigureAwait(false);

		return Ok(new GenericResponse<BotPlayStatus>(status));
	}
}
