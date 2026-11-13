using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace DW1Remake.Game
{
    /// <summary>
    /// Ours (online tournament; docs/ONLINE-TOURNAMENT-PLAN.md): the game's side of the tournament service, a Cloudflare Worker
    /// (online/tournament-worker). Plain web requests list and open tournaments; each game then keeps one text WebSocket to its
    /// tournament's room while signing up, through which the WebRTC link codes pass between the host's game and each player's
    /// (nobody copies codes). The fights never go through the service: they use the direct links (NetLink).
    /// </summary>
    public static class TournamentClient
    {
        /// <summary>The service's address (the organiser's free Cloudflare account).</summary>
        public const string Base = "https://dw1-tournament.dw1unitybattle.workers.dev";

        /// <summary>Only games of the same version see each other's tournaments (the link codes and messages must match).</summary>
        public static string GameVersion { get { return "dw1r-" + NetLink.LinkVersion; } }

        [Serializable] public sealed class Player { public string name = "", digimon = ""; }

        [Serializable] public sealed class Info
        {
            public string code = "", host = "", cup = "", time = "", state = "";
            public bool locked;             // joining needs the host's 4-digit password
            public Player[] players = new Player[0];
        }

        [Serializable] sealed class ListReply { public Info[] tournaments = new Info[0]; public string error = ""; }
        [Serializable] sealed class OpenRequest { public string v, host, cup, time; public bool locked; }
        [Serializable] sealed class OpenReply { public string code = "", hostKey = "", error = ""; }

        /// <summary>One message to or from a room (the Worker's JSON; unused fields stay empty).</summary>
        [Serializable] public sealed class RoomMsg
        {
            public string t = "", name = "", offer = "", data = "", sdp = "", reason = "", champion = "", host = "", cup = "", time = "", code = "", pass = "";
            public int pid;
            public Player[] players = new Player[0];
        }

        /// <summary>The open tournaments of this game version; error = "" when it worked.</summary>
        public static IEnumerator List(Action<List<Info>, string> done)
        {
            using (var r = UnityWebRequest.Get(Base + "/t?v=" + UnityWebRequest.EscapeURL(GameVersion)))
            {
                r.timeout = 15;
                yield return r.SendWebRequest();
                if (r.result != UnityWebRequest.Result.Success) { done(new List<Info>(), Unreachable); yield break; }
                ListReply rep = null;
                try { rep = JsonUtility.FromJson<ListReply>(r.downloadHandler.text); } catch { }
                if (rep == null) { done(new List<Info>(), Unreachable); yield break; }
                var list = new List<Info>();
                foreach (var t in rep.tournaments) if (t != null && t.state == "open") list.Add(t);
                done(list, "");
            }
        }

        public const string Unreachable = "The tournament service could not be reached. Check the internet connection and try again.";

        /// <summary>Opens a tournament: its code and the host's key, or an error text.</summary>
        public static IEnumerator Open(string host, string cup, string time, bool locked, Action<string, string, string> done)
        {
            var body = JsonUtility.ToJson(new OpenRequest { v = GameVersion, host = host, cup = cup, time = time, locked = locked });
            using (var r = new UnityWebRequest(Base + "/t", "POST"))
            {
                r.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
                r.downloadHandler = new DownloadHandlerBuffer();
                r.SetRequestHeader("Content-Type", "application/json");
                r.timeout = 15;
                yield return r.SendWebRequest();
                OpenReply rep = null;
                try { rep = JsonUtility.FromJson<OpenReply>(r.downloadHandler.text); } catch { }
                if (rep != null && rep.error == "too-many") { done(null, null, "This internet connection has played 20 tournaments in the last hour. Try again later."); yield break; }
                if (rep != null && rep.error == "open-already") { done(null, null, "This internet connection already has 3 tournaments open. Close one first."); yield break; }
                if (rep != null && rep.error == "busy") { done(null, null, "Too many tournaments are open right now. Try again later."); yield break; }
                if (r.result != UnityWebRequest.Result.Success || rep == null || string.IsNullOrEmpty(rep.code)) { done(null, null, Unreachable); yield break; }
                done(rep.code, rep.hostKey, "");
            }
        }

        public static string HostUrl(string code, string key) { return Base.Replace("https://", "wss://") + "/t/" + code + "/ws?role=host&key=" + Uri.EscapeDataString(key); }
        public static string PlayerUrl(string code) { return Base.Replace("https://", "wss://") + "/t/" + code + "/ws?role=player"; }

        // ---------------------------------------------------------------- the room's WebSocket

        /// <summary>A text WebSocket to a room: Send / Next from the main thread; "ping" every 30 s keeps it open (the room answers without waking).</summary>
        public sealed class Socket : IDisposable
        {
            float _lastPing;
#if UNITY_WEBGL && !UNITY_EDITOR
            [System.Runtime.InteropServices.DllImport("__Internal")] static extern int DW1_WsOpen(string url);
            [System.Runtime.InteropServices.DllImport("__Internal")] static extern int DW1_WsState(int id);
            [System.Runtime.InteropServices.DllImport("__Internal")] static extern void DW1_WsSend(int id, string text);
            [System.Runtime.InteropServices.DllImport("__Internal")] static extern string DW1_WsNext(int id);
            [System.Runtime.InteropServices.DllImport("__Internal")] static extern void DW1_WsClose(int id);
            int _id;
            public Socket(string url) { _id = DW1_WsOpen(url); _lastPing = Time.realtimeSinceStartup; }
            /// <summary>0 opening, 1 open, 3 closed.</summary>
            public int State { get { return _id > 0 ? DW1_WsState(_id) : 3; } }
            public void Send(string text) { if (_id > 0) DW1_WsSend(_id, text); }
            string NextRaw() { if (_id <= 0) return null; string s = DW1_WsNext(_id); return string.IsNullOrEmpty(s) ? null : s; }
            public void Dispose() { if (_id > 0) DW1_WsClose(_id); _id = 0; }
#else
            System.Net.WebSockets.ClientWebSocket _ws;
            readonly System.Collections.Concurrent.ConcurrentQueue<string> _in = new System.Collections.Concurrent.ConcurrentQueue<string>();
            readonly System.Collections.Concurrent.ConcurrentQueue<string> _out = new System.Collections.Concurrent.ConcurrentQueue<string>();
            readonly System.Threading.CancellationTokenSource _stop = new System.Threading.CancellationTokenSource();
            volatile int _state;

            public Socket(string url)
            {
                _lastPing = Time.realtimeSinceStartup;
                _ws = new System.Net.WebSockets.ClientWebSocket();
                Run(url);
            }

            async void Run(string url)
            {
                try
                {
                    await _ws.ConnectAsync(new Uri(url), _stop.Token);
                    _state = 1;
                    SendLoop();
                    var buf = new byte[16384];
                    var text = new System.Text.StringBuilder();
                    while (_ws.State == System.Net.WebSockets.WebSocketState.Open)
                    {
                        var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), _stop.Token);
                        if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                        text.Append(System.Text.Encoding.UTF8.GetString(buf, 0, r.Count));
                        if (r.EndOfMessage) { _in.Enqueue(text.ToString()); text.Length = 0; }
                    }
                }
                catch (Exception e) { if (!_stop.IsCancellationRequested) Debug.LogWarning("[tournament] socket: " + e.Message); }
                _state = 3;
            }

            async void SendLoop()
            {
                try
                {
                    while (!_stop.IsCancellationRequested && _ws.State == System.Net.WebSockets.WebSocketState.Open)
                    {
                        string s;
                        if (_out.TryDequeue(out s))
                            await _ws.SendAsync(new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(s)), System.Net.WebSockets.WebSocketMessageType.Text, true, _stop.Token);
                        else await System.Threading.Tasks.Task.Delay(20);
                    }
                }
                catch (Exception e) { if (!_stop.IsCancellationRequested) Debug.LogWarning("[tournament] send: " + e.Message); }
            }

            public int State { get { return _state; } }
            public void Send(string text) { if (_state == 1) _out.Enqueue(text); }
            string NextRaw() { string s; return _in.TryDequeue(out s) ? s : null; }

            public void Dispose()
            {
                _stop.Cancel();
                try { if (_ws != null && _ws.State == System.Net.WebSockets.WebSocketState.Open) _ws.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", System.Threading.CancellationToken.None); } catch { }
                _state = 3;
            }
#endif
            public void Send(RoomMsg m) { Send(JsonUtility.ToJson(m)); }

            /// <summary>The next message from the room, or null; keeps the link alive.</summary>
            public RoomMsg Next()
            {
                float t = Time.realtimeSinceStartup;
                if (State == 1 && t - _lastPing > 30f) { Send("ping"); _lastPing = t; }
                for (int guard = 0; guard < 64; guard++)
                {
                    string s = NextRaw();
                    if (s == null) return null;
                    if (s == "pong") continue;
                    try { var m = JsonUtility.FromJson<RoomMsg>(s); if (m != null) return m; } catch { }
                }
                return null;
            }
        }
    }
}
