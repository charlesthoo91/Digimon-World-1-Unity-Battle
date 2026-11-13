import type { Env, Summary } from "./shared.ts";
import { MaxPlaces } from "./shared.ts";

// The #tournament noticeboard: one post per tournament through the channel's webhook, edited as the tournament changes.
// Without DISCORD_WEBHOOK_URL nothing is posted and the tournaments work all the same.

function esc(s: string): string {
	return s.replace(/([\\*_`~|>#\[\]()])/g, "\\$1").replace(/@/g, "@\u200b");
}

function content(s: Summary): string {
	const head =
		s.state === "open" ? `**${esc(s.host)}** is hosting the **${esc(s.cup)}** — ${s.players.length} / ${MaxPlaces}` :
		s.state === "started" ? `**${esc(s.cup)}** hosted by **${esc(s.host)}** — started` :
		s.state === "ended" ? `**${esc(s.cup)}** hosted by **${esc(s.host)}** — ended` :
		`**${esc(s.cup)}** hosted by **${esc(s.host)}** — closed`;
	const lines = [head];
	if (s.state === "open") lines.push(`Code \`${s.code}\` · time ${esc(s.time)}${s.locked ? " · 🔒 password" : ""} · join at Greymon's arena: Online tournament → Join`);
	if (s.state === "ended" && s.champion) lines.push(`🏆 Champion: **${esc(s.champion)}**`);
	for (const p of s.players) lines.push(`• ${esc(p.name)} — ${esc(p.digimon)}`);
	if (s.state === "open" && s.players.length < MaxPlaces) lines.push(`${MaxPlaces - s.players.length} place(s) left; empty places become computer Digimon.`);
	return lines.join("\n").slice(0, 1900);
}

/** Posts a new tournament (mentioning the role, if one is set); returns the message id. */
export async function postTournament(env: Env, s: Summary): Promise<string | undefined> {
	if (!env.DISCORD_WEBHOOK_URL) return undefined;
	const role = env.DISCORD_ROLE_ID && /^\d+$/.test(env.DISCORD_ROLE_ID) ? env.DISCORD_ROLE_ID : undefined;
	const body = {
		content: (role ? `<@&${role}> ` : "") + content(s),
		allowed_mentions: { parse: [], roles: role ? [role] : [] },
	};
	try {
		const r = await fetch(env.DISCORD_WEBHOOK_URL + "?wait=true", {
			method: "POST",
			headers: { "Content-Type": "application/json" },
			body: JSON.stringify(body),
		});
		if (!r.ok) { console.log("[discord] post failed", r.status, await r.text()); return undefined; }
		const m = (await r.json()) as { id?: string };
		return m.id;
	} catch (e) {
		console.log("[discord] post error", String(e));
		return undefined;
	}
}

/** Updates the tournament's post (no new mention). */
export async function editTournament(env: Env, messageId: string | undefined, s: Summary): Promise<void> {
	if (!env.DISCORD_WEBHOOK_URL || !messageId) return;
	const role = env.DISCORD_ROLE_ID && /^\d+$/.test(env.DISCORD_ROLE_ID) && s.state === "open" ? `<@&${env.DISCORD_ROLE_ID}> ` : "";
	try {
		const r = await fetch(`${env.DISCORD_WEBHOOK_URL}/messages/${messageId}`, {
			method: "PATCH",
			headers: { "Content-Type": "application/json" },
			body: JSON.stringify({ content: role + content(s), allowed_mentions: { parse: [] } }),
		});
		if (!r.ok) console.log("[discord] edit failed", r.status, await r.text());
	} catch (e) {
		console.log("[discord] edit error", String(e));
	}
}

// ---------------------------------------------------------------- slash commands (Discord sends them to POST /discord)

function hex(s: string): Uint8Array {
	const b = new Uint8Array(s.length / 2);
	for (let i = 0; i < b.length; i++) b[i] = parseInt(s.substr(i * 2, 2), 16);
	return b;
}

/** Discord signs every request: Ed25519 over timestamp + body with the application's public key. */
export async function verifyDiscord(env: Env, request: Request, body: string): Promise<boolean> {
	const sig = request.headers.get("X-Signature-Ed25519");
	const ts = request.headers.get("X-Signature-Timestamp");
	if (!sig || !ts || !env.DISCORD_PUBLIC_KEY || !/^[0-9a-f]{64}$/i.test(env.DISCORD_PUBLIC_KEY) || !/^[0-9a-f]+$/i.test(sig)) return false;
	try {
		const key = await crypto.subtle.importKey("raw", hex(env.DISCORD_PUBLIC_KEY), { name: "Ed25519" }, false, ["verify"]);
		return await crypto.subtle.verify("Ed25519", key, hex(sig), new TextEncoder().encode(ts + body));
	} catch {
		return false;
	}
}

/** Registers /tournaments (done when Discord checks the endpoint: the PING after the URL is saved in the Developer Portal). */
export async function registerCommands(env: Env): Promise<void> {
	if (!env.DISCORD_APP_ID || !env.DISCORD_BOT_TOKEN) return;
	const r = await fetch(`https://discord.com/api/v10/applications/${env.DISCORD_APP_ID}/commands`, {
		method: "PUT",
		headers: { "Content-Type": "application/json", Authorization: `Bot ${env.DISCORD_BOT_TOKEN}` },
		body: JSON.stringify([{ name: "tournaments", description: "List the open Digimon World online tournaments", type: 1 }]),
	});
	console.log("[discord] register commands", r.status);
}

export function listText(list: Summary[]): string {
	const open = list.filter((s) => s.state === "open");
	if (!open.length) return "No open tournaments. Anyone can host one: Greymon's arena → Online battle → Online tournament → Host.";
	return open
		.slice(0, 10)
		.map((s) => `\`${s.code}\` **${esc(s.cup)}** by **${esc(s.host)}** — ${s.players.length} / ${MaxPlaces}, time ${esc(s.time)}`)
		.join("\n");
}
