import { bindings, defineConfig, exports } from "cf/config";
import * as entrypoint from "./src/index.ts" with { type: "cf-worker" };

// The meeting point of the remake's online tournament (docs/ONLINE-TOURNAMENT-PLAN.md): the list of open tournaments, the WebRTC
// link codes passed between the host's game and each player's, and the Discord noticeboard. The fights never go through here.
// The Discord values are Worker secrets (set with `npx wrangler secret put <NAME> --name dw1-tournament`), all optional:
// DISCORD_WEBHOOK_URL, DISCORD_ROLE_ID, DISCORD_APP_ID, DISCORD_PUBLIC_KEY, DISCORD_BOT_TOKEN.
export default defineConfig({
	worker: {
		name: "dw1-tournament",
		compatibilityDate: "2026-11-10",
		entrypoint,
		env: {
			ROOMS: bindings.durableObject({ worker: "dw1-tournament", exportName: "TournamentRoom" }),
			LOBBY: bindings.durableObject({ worker: "dw1-tournament", exportName: "Lobby" }),
		},
		exports: {
			TournamentRoom: exports.durableObject({ storage: "sqlite" }),
			Lobby: exports.durableObject({ storage: "sqlite" }),
		},
	},
});
