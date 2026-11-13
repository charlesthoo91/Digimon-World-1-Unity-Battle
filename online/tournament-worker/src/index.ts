import { listText, registerCommands, verifyDiscord } from "./discord.ts";
import { type Env, type Summary, Cors, cleanName, cleanText, isCode, json } from "./shared.ts";

export { Lobby } from "./lobby.ts";
export { TournamentRoom } from "./room.ts";

// The remake's online tournament service (docs/ONLINE-TOURNAMENT-PLAN.md):
//   GET  /t?v=<version>          the open tournaments of that game version
//   POST /t                      {v, host, cup, time} → {code, hostKey}: opens a tournament (and its Discord post)
//   GET  /t/<code>/ws?role=host&key=<hostKey>   the host's WebSocket
//   GET  /t/<code>/ws?role=player               a player's WebSocket
//   POST /discord                Discord's slash commands (/tournaments)
export default {
	async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
		const url = new URL(request.url);
		const parts = url.pathname.split("/").filter(Boolean);
		if (request.method === "OPTIONS") return new Response(null, { status: 204, headers: Cors });
		const lobby = env.LOBBY.get(env.LOBBY.idFromName("lobby"));

		if (parts.length === 0) return new Response("Digimon World 1 Remake: online tournament service\n", { headers: Cors });

		if (parts[0] === "t" && parts.length === 1) {
			if (request.method === "GET") {
				const v = cleanText(url.searchParams.get("v"), 32);
				const list = await lobby.list(v);
				return json({ tournaments: list.map(({ code, host, cup, time, players, state, locked }) => ({ code, host, cup, time, players, state, locked: !!locked })) });
			}
			if (request.method === "POST") {
				let b: Record<string, unknown>;
				try { b = await request.json(); } catch { return json({ error: "bad-request" }, 400); }
				const version = cleanText(b.v, 32);
				if (!version) return json({ error: "bad-request" }, 400);
				const address = request.headers.get("CF-Connecting-IP") ?? "unknown";
				const r = await lobby.reserve(address);
				if (!r.code) return json({ error: r.error }, 429);
				const s: Summary = {
					code: r.code, version, host: cleanName(b.host), cup: cleanText(b.cup, 40) || "Cup", time: cleanText(b.time, 16) || "99 s",
					players: [], state: "open", created: Date.now(), addr: address, locked: b.locked === true,
				};
				const hostKey = await env.ROOMS.get(env.ROOMS.idFromName(r.code)).open(s);
				return json({ code: r.code, hostKey });
			}
		}

		if (parts[0] === "t" && parts.length === 3 && parts[2] === "ws") {
			const code = parts[1].toUpperCase();
			if (!isCode(code)) return new Response("No such tournament", { status: 404 });
			return env.ROOMS.get(env.ROOMS.idFromName(code)).fetch(request);
		}

		if (parts[0] === "discord" && request.method === "POST") {
			const body = await request.text();
			if (!(await verifyDiscord(env, request, body))) return new Response("Bad signature", { status: 401 });
			const i = JSON.parse(body) as { type: number; data?: { name?: string } };
			if (i.type === 1) {
				// PING: Discord checks the endpoint when its URL is saved; the command is (re)registered then
				ctx.waitUntil(registerCommands(env));
				return Response.json({ type: 1 });
			}
			if (i.type === 2 && i.data?.name === "tournaments") {
				const list = await lobby.list("");
				return Response.json({ type: 4, data: { content: listText(list), flags: 64, allowed_mentions: { parse: [] } } });
			}
			return Response.json({ type: 4, data: { content: "Unknown command.", flags: 64 } });
		}

		return new Response("Not found", { status: 404, headers: Cors });
	},
} satisfies ExportedHandler<Env>;
