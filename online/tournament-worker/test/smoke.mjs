// A run-through of one tournament against the Worker (node test/smoke.mjs [base URL], default the local `cf dev`):
// a host opens it, two players join, the host answers one and refuses the other, posts the board, starts, ends; the lists follow.
const base = process.argv[2] ?? "http://localhost:5173";
const wsBase = base.replace(/^http/, "ws");
const V = "smoke-test";
let fails = 0;
const check = (ok, what) => { console.log((ok ? "ok   " : "FAIL ") + what); if (!ok) fails++; };

function open(url) {
	return new Promise((res, rej) => {
		const ws = new WebSocket(url);
		const inbox = [];
		const waiters = [];
		ws.onmessage = (e) => {
			const m = e.data === "pong" ? { t: "pong" } : JSON.parse(e.data);
			const w = waiters.shift();
			if (w) w(m); else inbox.push(m);
		};
		ws.next = () => new Promise((r, j) => {
			if (inbox.length) return r(inbox.shift());
			const t = setTimeout(() => j(new Error("no message")), 5000);
			waiters.push((m) => { clearTimeout(t); r(m); });
		});
		ws.onopen = () => res(ws);
		ws.onerror = (e) => rej(new Error("socket error " + url));
	});
}

// true when the socket opens (a refused one errors instead)
const opens = (url) => new Promise((res) => { const ws = new WebSocket(url); ws.onopen = () => { ws.close(); res(true); }; ws.onerror = () => res(false); });

const list = async () => (await (await fetch(`${base}/t?v=${V}`)).json()).tournaments;

const r = await fetch(`${base}/t`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ v: V, host: "Ken", cup: "Grade A Cup", time: "99 s" }) });
const made = await r.json();
check(r.ok && /^[A-Z2-9]{6}$/.test(made.code) && made.hostKey, "open a tournament: " + JSON.stringify({ code: made.code }));
const code = made.code;
check((await list()).some((t) => t.code === code && t.state === "open"), "it is in the list");

check(!(await opens(`${wsBase}/t/${code}/ws?role=host&key=wrong`)), "a wrong host key is refused");

const host = await open(`${wsBase}/t/${code}/ws?role=host&key=${encodeURIComponent(made.hostKey)}`);
check((await host.next()).t === "hello", "host linked");
host.send("ping");
check((await host.next()).t === "pong", "ping answered");

const p1 = await open(`${wsBase}/t/${code}/ws?role=player`);
const h1 = await p1.next();
check(h1.t === "hello" && h1.host === "Ken", "player 1 sees the tournament");
p1.send(JSON.stringify({ t: "join", name: "Ann<@everyone>", offer: "OFFER-1", data: "partner:3" }));
const j1 = await host.next();
check(j1.t === "join" && j1.offer === "OFFER-1" && !j1.name.includes("@"), "host gets player 1's offer, name cleaned: " + j1.name);
host.send(JSON.stringify({ t: "answer", pid: j1.pid, sdp: "ANSWER-1" }));
const a1 = await p1.next();
check(a1.t === "answer" && a1.sdp === "ANSWER-1", "player 1 gets the answer");

const p2 = await open(`${wsBase}/t/${code}/ws?role=player`);
await p2.next();
p2.send(JSON.stringify({ t: "join", name: "Bob", offer: "OFFER-2", data: "partner:99" }));
const j2 = await host.next();
host.send(JSON.stringify({ t: "refuse", pid: j2.pid, reason: "Your partner cannot enter this cup." }));
check((await p2.next()).t === "refused", "player 2 refused");
p2.close();
check((await host.next()).t === "leave", "host told player 2 left");

host.send(JSON.stringify({ t: "board", players: [{ name: "Ken", digimon: "Greymon" }, { name: "Ann", digimon: "Agumon" }] }));
const b = await p1.next();
check(b.t === "board" && b.players.length === 2, "player 1 gets the board");
check((await list()).find((t) => t.code === code)?.players.length === 2, "the list shows 2 players");

host.send(JSON.stringify({ t: "start" }));
check((await p1.next()).t === "start", "player 1 told of the start");
check(!(await opens(`${wsBase}/t/${code}/ws?role=player`)), "nobody joins once started");

host.send(JSON.stringify({ t: "end", champion: "Ann" }));
const e = await p1.next();
check(e.t === "end" && e.champion === "Ann", "the end and the champion");
check(!(await list()).some((t) => t.code === code), "gone from the list");
host.close(); p1.close();

// a host who leaves early closes the tournament for everyone
const r2 = await (await fetch(`${base}/t`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ v: V, host: "Sam", cup: "Grade B Cup" }) })).json();
const host2 = await open(`${wsBase}/t/${r2.code}/ws?role=host&key=${encodeURIComponent(r2.hostKey)}`);
await host2.next();
const p3 = await open(`${wsBase}/t/${r2.code}/ws?role=player`);
await p3.next();
host2.close();
check((await p3.next()).t === "closed", "the host left: players told it is closed");
check(!(await list()).some((t) => t.code === r2.code), "and it is gone from the list");
p3.close();

console.log(fails ? `${fails} FAILED` : "all passed");
process.exit(fails ? 1 : 0);
