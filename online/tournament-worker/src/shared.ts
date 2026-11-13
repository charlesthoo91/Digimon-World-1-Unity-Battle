// Things both the Worker and its Durable Objects use.

export interface Env {
	ROOMS: DurableObjectNamespace<import("./room.ts").TournamentRoom>;
	LOBBY: DurableObjectNamespace<import("./lobby.ts").Lobby>;
	DISCORD_WEBHOOK_URL?: string;
	DISCORD_ROLE_ID?: string;
	DISCORD_APP_ID?: string;
	DISCORD_PUBLIC_KEY?: string;
	DISCORD_BOT_TOKEN?: string;
}

/** One open tournament as the lists show it. */
export interface Summary {
	code: string;
	version: string;
	host: string;
	cup: string;
	time: string;
	players: { name: string; digimon: string }[];
	state: "open" | "started" | "ended" | "closed";
	created: number;
	champion?: string;
	addr?: string;
	locked?: boolean;       // joining needs the host's 4-digit password (the host's game checks it)          // the host's internet address (never listed): the per-address limits
}

export const MaxPlaces = 8;
export const MaxOpen = 30;              // open tournaments at once
export const MaxPerAddressHour = 20;    // tournaments one internet address may play in an hour (counted when one starts with another player)
export const MaxOpenPerAddress = 3;     // tournaments one internet address may have open at once
export const RoomLifeMs = 3 * 3600_000; // a room is wiped this long after it opens, whatever happens
export const HostGraceMs = 3 * 60_000;   // a tournament whose host is gone this long is closed (and one never joined by its host)
export const EndedLifeMs = 3600_000;    // and this long after its tournament ends or is closed

// 6 letters without the look-alikes (0 O 1 I L)
const CodeLetters = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
export function newCode(): string {
	const b = crypto.getRandomValues(new Uint8Array(6));
	let s = "";
	for (const x of b) s += CodeLetters[x % CodeLetters.length];
	return s;
}

export function newKey(): string {
	const b = crypto.getRandomValues(new Uint8Array(18));
	return btoa(String.fromCharCode(...b)).replace(/\+/g, "-").replace(/\//g, "_");
}

export function isCode(s: string | null | undefined): s is string {
	return !!s && /^[A-HJ-KM-NP-Z2-9]{6}$/.test(s);
}

/** A player or host name as it may be shown: plain letters, at most 16. */
export function cleanName(s: unknown): string {
	const t = typeof s === "string" ? s : "";
	return t.replace(/[^\p{L}\p{N} ._-]/gu, "").trim().slice(0, 16) || "Player";
}

export function cleanText(s: unknown, max: number): string {
	const t = typeof s === "string" ? s : "";
	return t.replace(/[\u0000-\u001F]/g, "").slice(0, max);
}

export const Cors: Record<string, string> = {
	"Access-Control-Allow-Origin": "*",
	"Access-Control-Allow-Methods": "GET, POST, OPTIONS",
	"Access-Control-Allow-Headers": "Content-Type",
};

export function json(data: unknown, status = 200): Response {
	return new Response(JSON.stringify(data), { status, headers: { "Content-Type": "application/json", ...Cors } });
}
