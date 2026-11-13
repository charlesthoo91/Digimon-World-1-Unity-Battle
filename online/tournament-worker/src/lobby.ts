import { DurableObject } from "cloudflare:workers";
import { type Env, type Summary, HostGraceMs, MaxOpen, MaxOpenPerAddress, MaxPerAddressHour, RoomLifeMs, newCode } from "./shared.ts";

/**
 * The one list of open tournaments (a single Durable Object, idFromName("lobby")). Each tournament room keeps its own entry up to date.
 * Also counts how many tournaments each internet address opened in the last hour.
 */
export class Lobby extends DurableObject<Env> {
	/** A free code for a new tournament, or the reason there is none. */
	async reserve(address: string): Promise<{ code?: string; error?: string }> {
		const now = Date.now();
		await this.sweep(now);
		const open = await this.ctx.storage.list<Summary>({ prefix: "t:" });
		if (open.size >= MaxOpen) return { error: "busy" };
		let mine = 0;
		for (const s of open.values()) if (s.addr === address) mine++;
		if (mine >= MaxOpenPerAddress) return { error: "open-already" };
		const times = ((await this.ctx.storage.get<number[]>("a:" + address)) ?? []).filter((t) => now - t < 3600_000);
		if (times.length >= MaxPerAddressHour) return { error: "too-many" };
		let code = newCode();
		for (let i = 0; i < 5 && open.has("t:" + code); i++) code = newCode();
		if (open.has("t:" + code)) return { error: "busy" };
		return { code };
	}

	/** A tournament of this address started with at least one other player: it counts toward the hourly limit. */
	async counted(address: string): Promise<void> {
		const now = Date.now(), key = "a:" + address;
		const times = ((await this.ctx.storage.get<number[]>(key)) ?? []).filter((t) => now - t < 3600_000);
		times.push(now);
		await this.ctx.storage.put(key, times);
	}

	async put(s: Summary): Promise<void> {
		if (s.state === "open" || s.state === "started") await this.ctx.storage.put("t:" + s.code, s);
		else await this.ctx.storage.delete("t:" + s.code);
	}

	async remove(code: string): Promise<void> {
		await this.ctx.storage.delete("t:" + code);
	}

	/** The tournaments of one game version (all when version is empty), newest first. */
	async list(version: string): Promise<Summary[]> {
		await this.sweep(Date.now());
		const all = [...(await this.ctx.storage.list<Summary>({ prefix: "t:" })).values()];
		return all.filter((s) => !version || s.version === version).sort((a, b) => b.created - a.created);
	}

	// entries whose room should already be gone (a room that failed to remove itself), and old address counts
	private async sweep(now: number): Promise<void> {
		const gone: string[] = [];
		for (const [k, s] of await this.ctx.storage.list<Summary>({ prefix: "t:" })) if (now - s.created > RoomLifeMs || (s.state === "open" && s.players.length === 0 && now - s.created > HostGraceMs)) gone.push(k);
		for (const [k, t] of await this.ctx.storage.list<number[]>({ prefix: "a:" })) if (!t.some((x) => now - x < 3600_000)) gone.push(k);
		if (gone.length) await this.ctx.storage.delete(gone);
	}
}
