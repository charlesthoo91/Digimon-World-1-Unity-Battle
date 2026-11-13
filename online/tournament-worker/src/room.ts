import { DurableObject } from "cloudflare:workers";
import { editTournament, postTournament } from "./discord.ts";
import { type Env, type Summary, EndedLifeMs, HostGraceMs, MaxPlaces, RoomLifeMs, cleanName, cleanText, newKey } from "./shared.ts";

/**
 * One tournament (idFromName(code)): its board and the mailboxes between the host's game and each player's game, over WebSockets that
 * sleep between messages (the hibernation API: a sleeping room costs nothing). The host's game decides everything (the entry check, the
 * board, the bracket); the room only passes messages on, keeps the summary for the lists and Discord, and wipes itself at the end.
 *
 * Messages (JSON):
 *   player → room   {t:"join", name, offer, data}       → host   {t:"join", pid, name, offer, data}
 *   host → room     {t:"answer", pid, sdp}               → player {t:"answer", sdp}
 *                   {t:"refuse", pid, reason}            → player {t:"refused", reason}
 *                   {t:"board", players:[{name,digimon}]} → every player {t:"board", players}; lists and Discord
 *                   {t:"start"} / {t:"end", champion}    → every player, lists and Discord
 *                   {t:"kick", pid}                      → that player's socket is closed
 *   player closes                                         → host   {t:"leave", pid}
 *   host closes before the end                            → every player {t:"closed"}
 * Any socket may send the text "ping"; the room answers "pong" without waking.
 */
interface Meta extends Summary { hostKey: string; messageId?: string; nextPid: number; wipeAt?: number; hostSeenAt?: number }
interface Tag { role: "host" | "player"; pid: number; joined?: boolean }

const MaxSockets = 24;          // the host and players, joining or joined
const MaxMessage = 32 * 1024;

export class TournamentRoom extends DurableObject<Env> {
	constructor(ctx: DurableObjectState, env: Env) {
		super(ctx, env);
		this.ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
	}

	/** Opens the room (the Worker calls this once, for a code the lobby reserved); returns the host's key. */
	async open(s: Summary): Promise<string> {
		const hostKey = newKey();
		const now = Date.now();
		const meta: Meta = { ...s, hostKey, nextPid: 1, wipeAt: now + RoomLifeMs, hostSeenAt: now };
		await this.ctx.storage.put("meta", meta);
		await this.ctx.storage.setAlarm(now + HostGraceMs);        // the host's link is checked every 3 minutes
		await this.env.LOBBY.get(this.env.LOBBY.idFromName("lobby")).put(s);
		meta.messageId = await postTournament(this.env, s);
		if (meta.messageId) await this.ctx.storage.put("meta", meta);
		return hostKey;
	}

	async fetch(request: Request): Promise<Response> {
		if (request.headers.get("Upgrade") !== "websocket") return new Response("Expected a WebSocket", { status: 426 });
		const meta = await this.ctx.storage.get<Meta>("meta");
		if (!meta || meta.state === "ended" || meta.state === "closed") return new Response("No such tournament", { status: 404 });
		const url = new URL(request.url);
		const role = url.searchParams.get("role");
		let tag: Tag;
		if (role === "host") {
			if (url.searchParams.get("key") !== meta.hostKey) return new Response("Wrong key", { status: 403 });
			for (const old of this.ctx.getWebSockets("host")) old.close(4000, "replaced");      // the host came back: the newest link wins
			tag = { role: "host", pid: 0 };
			meta.hostSeenAt = Date.now();
			await this.ctx.storage.put("meta", meta);
		} else {
			if (meta.state !== "open") return new Response("Already started", { status: 409 });
			if (this.ctx.getWebSockets().length >= MaxSockets) return new Response("Full", { status: 429 });
			tag = { role: "player", pid: meta.nextPid++ };
			await this.ctx.storage.put("meta", meta);
		}
		const pair = new WebSocketPair();
		const [client, server] = Object.values(pair);
		this.ctx.acceptWebSocket(server, [tag.role === "host" ? "host" : "p" + tag.pid]);
		server.serializeAttachment(tag);
		if (tag.role === "player") server.send(JSON.stringify({ t: "hello", pid: tag.pid, players: meta.players, host: meta.host, cup: meta.cup, time: meta.time }));
		else server.send(JSON.stringify({ t: "hello", code: meta.code }));
		return new Response(null, { status: 101, webSocket: client });
	}

	async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
		if (typeof message !== "string" || message.length > MaxMessage) return;
		let m: Record<string, unknown>;
		try { m = JSON.parse(message); } catch { return; }
		const tag = ws.deserializeAttachment() as Tag;
		const meta = await this.ctx.storage.get<Meta>("meta");
		if (!meta) { ws.close(4004, "gone"); return; }
		if (tag.role === "player") await this.fromPlayer(ws, tag, m, meta);
		else await this.fromHost(m, meta);
	}

	private host(): WebSocket | undefined { return this.ctx.getWebSockets("host")[0]; }
	private player(pid: unknown): WebSocket | undefined { return typeof pid === "number" ? this.ctx.getWebSockets("p" + pid)[0] : undefined; }

	private async fromPlayer(ws: WebSocket, tag: Tag, m: Record<string, unknown>, meta: Meta): Promise<void> {
		if (m.t !== "join" || tag.joined) return;
		const h = this.host();
		if (!h) { ws.send(JSON.stringify({ t: "refused", reason: "host-away" })); return; }
		if (meta.state !== "open") { ws.send(JSON.stringify({ t: "refused", reason: "started" })); return; }
		tag.joined = true;
		ws.serializeAttachment(tag);
		h.send(JSON.stringify({ t: "join", pid: tag.pid, name: cleanName(m.name), offer: cleanText(m.offer, 16000), data: cleanText(m.data, 8000), pass: cleanText(m.pass, 8) }));
	}

	private async fromHost(m: Record<string, unknown>, meta: Meta): Promise<void> {
		switch (m.t) {
			case "answer": this.player(m.pid)?.send(JSON.stringify({ t: "answer", sdp: cleanText(m.sdp, 16000) })); break;
			case "refuse": this.player(m.pid)?.send(JSON.stringify({ t: "refused", reason: cleanText(m.reason, 300) })); break;
			case "kick": this.player(m.pid)?.close(4001, "removed"); break;
			case "board": {
				const list = Array.isArray(m.players) ? m.players.slice(0, MaxPlaces) : [];
				meta.players = list.map((p) => ({ name: cleanName((p as Record<string, unknown>)?.name), digimon: cleanText((p as Record<string, unknown>)?.digimon, 24) }));
				await this.changed(meta);
				this.toPlayers({ t: "board", players: meta.players });
				break;
			}
			case "start":
				if (meta.state !== "open") break;
				meta.state = "started";
				await this.changed(meta);
				// only a tournament that is played (the host and at least one other player) counts toward the host's hourly limit
				if (meta.addr && meta.players.length >= 2) await this.env.LOBBY.get(this.env.LOBBY.idFromName("lobby")).counted(meta.addr);
				this.toPlayers({ t: "start" });
				break;
			case "end":
				meta.state = "ended";
				meta.champion = cleanName(m.champion);
				meta.wipeAt = Date.now() + EndedLifeMs;
				await this.changed(meta);
				this.toPlayers({ t: "end", champion: meta.champion });
				await this.ctx.storage.setAlarm(meta.wipeAt);
				break;
		}
	}

	private toPlayers(m: unknown): void {
		const s = JSON.stringify(m);
		for (const ws of this.ctx.getWebSockets()) {
			const tag = ws.deserializeAttachment() as Tag;
			if (tag.role === "player") try { ws.send(s); } catch { /* already closing */ }
		}
	}

	// the summary changed: keep it, and update the lobby's list and the Discord post
	private async changed(meta: Meta): Promise<void> {
		await this.ctx.storage.put("meta", meta);
		const { hostKey: _k, messageId, nextPid: _n, ...summary } = meta;
		await this.env.LOBBY.get(this.env.LOBBY.idFromName("lobby")).put(summary);
		this.ctx.waitUntil(editTournament(this.env, messageId, summary));
	}

	async webSocketClose(ws: WebSocket, code: number): Promise<void> {
		const tag = ws.deserializeAttachment() as Tag;
		const meta = await this.ctx.storage.get<Meta>("meta");
		if (!meta) return;
		if (tag.role === "player") { this.host()?.send(JSON.stringify({ t: "leave", pid: tag.pid })); return; }
		// the host's link: a replaced one (the host came back) or the end changes nothing; otherwise the tournament is over for everyone
		if (code === 4000 || meta.state === "ended" || meta.state === "closed" || this.ctx.getWebSockets("host").some((h) => h !== ws)) return;
		meta.state = "closed";
		meta.wipeAt = Date.now() + EndedLifeMs;
		await this.changed(meta);
		this.toPlayers({ t: "closed" });
		await this.ctx.storage.setAlarm(meta.wipeAt);
	}

	async webSocketError(ws: WebSocket): Promise<void> { await this.webSocketClose(ws, 1006); }

	/** Every 3 minutes while open: a host gone that long (or never there) closes the tournament. At wipeAt everything is wiped (the board,
	 *  the codes and their internet addresses). */
	async alarm(): Promise<void> {
		const meta = await this.ctx.storage.get<Meta>("meta");
		const now = Date.now();
		if (meta && now < (meta.wipeAt ?? 0)) {
			const live = meta.state === "open" || meta.state === "started";
			if (live && this.ctx.getWebSockets("host").length > 0) meta.hostSeenAt = now;
			if (live && now - (meta.hostSeenAt ?? meta.created) >= HostGraceMs) {
				meta.state = "closed";
				meta.wipeAt = now + EndedLifeMs;
				await this.changed(meta);
				this.toPlayers({ t: "closed" });
			}
			else await this.ctx.storage.put("meta", meta);
			const stillLive = meta.state === "open" || meta.state === "started";
			await this.ctx.storage.setAlarm(stillLive ? Math.min(meta.wipeAt!, now + HostGraceMs) : meta.wipeAt!);
			return;
		}
		if (meta) {
			if (meta.state === "open" || meta.state === "started") { meta.state = "closed"; const { hostKey: _k, messageId, nextPid: _n, ...summary } = meta; await editTournament(this.env, messageId, summary); }
			await this.env.LOBBY.get(this.env.LOBBY.idFromName("lobby")).remove(meta.code);
		}
		for (const ws of this.ctx.getWebSockets()) try { ws.close(4004, "gone"); } catch { /* closed */ }
		await this.ctx.storage.deleteAll();
	}
}
