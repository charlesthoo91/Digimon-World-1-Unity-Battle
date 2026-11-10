using System;
using System.Collections.Generic;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Ours (mod "Online battle"; not in the original game): a direct link between two players with no server of our own, PC or browser.
    /// It is WebRTC (NetRtc): 1. the host's game asks public STUN servers (an "address helper": it only answers "this is your internet
    /// address") and makes an offer, packed into the host code (the address, the link's name and password, and the encryption key's
    /// fingerprint). 2. The friend pastes it, and their game makes a reply code the same way. 3. The host pastes the reply; both games
    /// then knock on each other's addresses, which opens the way through most home routers. Strict networks (some mobile and office ones)
    /// cannot link this way. Messages: sure ones (resent until they arrive, in order) and fast ones (sent once).
    /// Call Tick() every frame from the main thread.
    /// </summary>
    public sealed class NetLink : IDisposable
    {
        public enum State { Starting, Ready, Linking, Linked, Failed, Closed }

        const int Version = 4;            // 4: WebRTC (PC and browser); 3 was the PC-only UDP link
        const byte MsgData = 1, MsgPing = 2, MsgPong = 3, MsgBye = 4;

        public State Now { get; private set; }
        public string Status { get; private set; }
        public string MyCode { get; private set; }
        public bool IsHost { get; private set; }
        public int PingMs { get; private set; }
        /// <summary>True while the screen should offer to paste the friend's code (the host's reply code, or the friend's host code).</summary>
        public bool WantsFriendCode { get { return Now == State.Ready && !_pasted; } }

        IRtcPeer _peer;
        bool _pasted;
        string _myMid = "0";
        float _started, _lastHeard, _pingSent = -10f, _firstAddress = -1f;
        readonly Queue<byte[]> _inbox = new Queue<byte[]>();

        public NetLink(bool host)
        {
            IsHost = host;
            _started = Time.realtimeSinceStartup;
            try { _peer = NetRtc.Create(host); }
            catch (Exception e) { Fail("Could not start the link: " + e.Message); return; }
            if (host) { Now = State.Starting; Status = "Finding your internet address..."; }
            else { Now = State.Ready; Status = "Paste your friend's code. Then send them the reply code you get."; }
        }

        void Fail(string why) { Now = State.Failed; Status = why; Debug.LogWarning("[online] " + why); }

        // ---------------------------------------------------------------- link codes

        // capital letters and digits only: the game's font has those, and there is no I, O, 0 or 1 to mix up
        const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        // the characters an ICE name or password may use (RFC 5245 ice-char)
        const string IceChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

        sealed class Bits
        {
            public readonly List<bool> B = new List<bool>(); public int Pos;
            public void Put(int value, int n) { for (int i = n - 1; i >= 0; i--) B.Add(((value >> i) & 1) != 0); }
            public int Get(int n) { int v = 0; for (int i = 0; i < n; i++) { if (Pos >= B.Count) throw new FormatException(); v = (v << 1) | (B[Pos++] ? 1 : 0); } return v; }
            public string ToCode()
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < B.Count; i += 5) { int v = 0; for (int k = 0; k < 5; k++) v = (v << 1) | (i + k < B.Count && B[i + k] ? 1 : 0); sb.Append(Alphabet[v]); }
                return sb.ToString();
            }
            public static Bits FromCode(string s)
            {
                var r = new Bits();
                foreach (char c in s) { int v = Alphabet.IndexOf(c); if (v < 0) return null; r.Put(v, 5); }
                return r;
            }
        }

        // what a code holds: offer or answer, the DTLS role, the ICE name and password, the fingerprint and up to 4 addresses
        sealed class Desc
        {
            public bool Offer; public string Setup = "actpass", Ufrag, Pwd, Mid = "0";
            public byte[] Fingerprint;
            public readonly List<Cand> Cands = new List<Cand>();
        }

        // an address: 0 = this machine's own IPv4 (home network), 1 = as the internet sees it (STUN), 2 = a browser's hidden home-network
        // name (<uuid>.local; only reachable on the same home network, where the router often cannot send the internet address back in)
        sealed class Cand { public int Kind; public byte[] Addr; public int Port; }

        static Desc ParseLocal(string text, bool offer)
        {
            var d = new Desc { Offer = offer };
            var seen = new HashSet<string>();
            var found = new List<Cand>();
            foreach (var raw in text.Split('\n'))
            {
                string l = raw.Trim();
                if (l.StartsWith("a=ice-ufrag:")) d.Ufrag = l.Substring(12);
                else if (l.StartsWith("a=ice-pwd:")) d.Pwd = l.Substring(10);
                else if (l.StartsWith("a=setup:")) d.Setup = l.Substring(8);
                else if (l.StartsWith("a=mid:")) d.Mid = l.Substring(6);
                else if (l.StartsWith("a=fingerprint:sha-256 "))
                {
                    var parts = l.Substring(22).Split(':');
                    if (parts.Length == 32) { d.Fingerprint = new byte[32]; for (int i = 0; i < 32; i++) d.Fingerprint[i] = Convert.ToByte(parts[i], 16); }
                }
                else if (l.StartsWith("a=candidate:"))
                {
                    // candidate:foundation component transport priority address port typ type ...
                    var f = l.Substring(12).Split(' ');
                    int port;
                    if (f.Length < 8 || f[1] != "1" || f[2].ToLowerInvariant() != "udp" || !int.TryParse(f[5], out port)) continue;
                    if (!seen.Add(f[4] + ":" + port)) continue;
                    var c = new Cand { Port = port };
                    if (f[4].EndsWith(".local") && f[7] == "host")
                    {
                        string hex = f[4].Substring(0, f[4].Length - 6).Replace("-", "");
                        if (hex.Length != 32) continue;
                        c.Kind = 2; c.Addr = new byte[16];
                        try { for (int i = 0; i < 16; i++) c.Addr[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16); } catch { continue; }
                    }
                    else
                    {
                        var ip = f[4].Split('.');
                        if (ip.Length != 4) continue;                                     // IPv6 is left out
                        c.Addr = new byte[4]; bool ok = true;
                        for (int i = 0; i < 4; i++) { int v; if (!int.TryParse(ip[i], out v) || v < 0 || v > 255) ok = false; else c.Addr[i] = (byte)v; }
                        if (!ok || c.Addr[0] == 169 && c.Addr[1] == 254 || c.Addr[0] == 0 || c.Addr[0] == 127) continue;
                        if (f[7] == "srflx") c.Kind = 1; else if (f[7] == "host") c.Kind = 0; else continue;
                    }
                    found.Add(c);
                }
            }
            // the internet addresses first (two at most), then one home-network address of each kind
            foreach (var c in found) if (c.Kind == 1 && d.Cands.FindAll(x => x.Kind == 1).Count < 2) d.Cands.Add(c);
            var h = found.Find(c => c.Kind == 0); if (h != null) d.Cands.Add(h);
            var m = found.Find(c => c.Kind == 2); if (m != null && d.Cands.Count < 4) d.Cands.Add(m);
            return d;
        }

        static string Pack(Desc d)
        {
            var w = new Bits();
            w.Put(Version, 4); w.Put(d.Offer ? 1 : 0, 1); w.Put(d.Setup == "passive" ? 1 : 0, 1); w.Put(d.Cands.Count, 3);
            foreach (var s in new[] { d.Ufrag, d.Pwd })
            {
                w.Put(s.Length, 6);
                foreach (char c in s) w.Put(Math.Max(0, IceChars.IndexOf(c)), 6);
            }
            foreach (byte x in d.Fingerprint) w.Put(x, 8);
            foreach (var c in d.Cands) { w.Put(c.Kind, 2); foreach (byte x in c.Addr) w.Put(x, 8); w.Put(c.Port, 16); }
            return w.ToCode();
        }

        static Desc Unpack(Bits r, out int version)
        {
            version = r.Get(4);
            if (version != Version) return null;
            var d = new Desc { Offer = r.Get(1) == 1 };
            bool passive = r.Get(1) == 1;
            d.Setup = d.Offer ? "actpass" : passive ? "passive" : "active";
            int n = r.Get(3);
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < 2; k++)
            {
                sb.Length = 0; int len = r.Get(6);
                for (int i = 0; i < len; i++) sb.Append(IceChars[r.Get(6)]);
                if (k == 0) d.Ufrag = sb.ToString(); else d.Pwd = sb.ToString();
            }
            d.Fingerprint = new byte[32];
            for (int i = 0; i < 32; i++) d.Fingerprint[i] = (byte)r.Get(8);
            for (int k = 0; k < n; k++)
            {
                var c = new Cand { Kind = r.Get(2) };
                if (c.Kind > 2) throw new FormatException();
                c.Addr = new byte[c.Kind == 2 ? 16 : 4];
                for (int i = 0; i < c.Addr.Length; i++) c.Addr[i] = (byte)r.Get(8);
                c.Port = r.Get(16);
                d.Cands.Add(c);
            }
            return d;
        }

        // a whole session description again from a code (only the data channel part; both sides use the standard SCTP port)
        static string ToSdp(Desc d)
        {
            var s = new System.Text.StringBuilder();
            Action<string> line = x => s.Append(x).Append("\r\n");
            line("v=0"); line("o=- 1 2 IN IP4 127.0.0.1"); line("s=-"); line("t=0 0");
            line("a=group:BUNDLE " + d.Mid); line("a=msid-semantic: WMS");
            line("m=application 9 UDP/DTLS/SCTP webrtc-datachannel"); line("c=IN IP4 0.0.0.0");
            int f = 1;
            foreach (var c in d.Cands)
            {
                var b = c.Addr; string addr;
                if (c.Kind == 2)
                {
                    var hex = new System.Text.StringBuilder();
                    for (int i = 0; i < 16; i++) { if (i == 4 || i == 6 || i == 8 || i == 10) hex.Append('-'); hex.Append(b[i].ToString("x2")); }
                    addr = hex + ".local";
                }
                else addr = b[0] + "." + b[1] + "." + b[2] + "." + b[3];
                bool server = c.Kind == 1;
                line("a=candidate:" + f++ + " 1 udp " + (server ? 1694498815u : 2130706431u) + " " + addr + " " + c.Port + " typ " + (server ? "srflx raddr 0.0.0.0 rport 0" : "host"));
            }
            line("a=end-of-candidates");
            line("a=ice-ufrag:" + d.Ufrag); line("a=ice-pwd:" + d.Pwd); line("a=ice-options:trickle");
            var fp = new System.Text.StringBuilder();
            foreach (byte x in d.Fingerprint) { if (fp.Length > 0) fp.Append(':'); fp.Append(x.ToString("X2")); }
            line("a=fingerprint:sha-256 " + fp);
            line("a=setup:" + d.Setup); line("a=mid:" + d.Mid);
            line("a=sctp-port:5000"); line("a=max-message-size:262144");
            return s.ToString();
        }

        /// <summary>Test hook (editor tools): a local description text -> code -> the description the friend's game rebuilds.</summary>
        public static string RoundTrip(string local, bool offer, out string code)
        {
            code = Pack(ParseLocal(local, offer));
            int v; return ToSdp(Unpack(Bits.FromCode(code), out v));
        }

        /// <summary>The code in groups of five, easier to read out.</summary>
        public static string Spaced(string code)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < code.Length; i++) { if (i > 0 && i % 5 == 0) sb.Append(' '); sb.Append(code[i]); }
            return sb.ToString();
        }

        /// <summary>Takes the friend's code; false (and Status says why) when it is not a code or is the wrong kind.</summary>
        public bool UseFriendCode(string code)
        {
            if (!WantsFriendCode) return false;
            var clean = new System.Text.StringBuilder();
            foreach (char c in (code ?? "").ToUpperInvariant()) if (!char.IsWhiteSpace(c)) clean.Append(c);
            Desc d = null; int version = -1;
            var r = Bits.FromCode(clean.ToString());
            try { if (r != null && clean.Length > 60) d = Unpack(r, out version); } catch (FormatException) { d = null; }
            if (d == null && version >= 0) { Status = "Your friend has a different version of the game."; return false; }
            if (d == null) { Status = "That is not a link code. Copy your friend's code and paste it again."; return false; }
            if (d.Offer != !IsHost) { Status = IsHost ? "That is a host code. Paste your friend's reply code." : "That is a reply code. Paste your friend's host code."; return false; }
            if (d.Cands.Count == 0) { Status = "Your friend's code has no internet address. They should make a new one."; return false; }
            if (IsHost) d.Mid = _myMid;          // the reply answers our own offer's part name
            _peer.SetRemote(ToSdp(d), d.Offer);
            _pasted = true;
            _started = Time.realtimeSinceStartup; _firstAddress = -1f;
            if (IsHost) { Now = State.Linking; Status = "Linking..."; }
            else { Now = State.Starting; Status = "Making your reply code..."; }
            return true;
        }

        // ---------------------------------------------------------------- sending

        void Raw(int channel, byte type, byte[] body)
        {
            if (_peer == null) return;
            var p = new byte[1 + (body != null ? body.Length : 0)];
            p[0] = type;
            if (body != null) body.CopyTo(p, 1);
            _peer.Send(channel, p);
        }

        /// <summary>Sends once; it may be lost (positions, button states sent every frame).</summary>
        public void SendFast(byte[] data) { if (Now == State.Linked) Raw(1, MsgData, data); }

        /// <summary>Sent until the friend's game has it; delivered once and in order.</summary>
        public void SendSure(byte[] data) { if (Now == State.Linked) Raw(0, MsgData, data); }

        /// <summary>The next message from the friend, or null.</summary>
        public byte[] Receive() { return _inbox.Count > 0 ? _inbox.Dequeue() : null; }

        // ---------------------------------------------------------------- every frame

        public void Tick()
        {
            if (_peer == null || Now == State.Failed || Now == State.Closed) return;
            float t = Time.realtimeSinceStartup;
            int st = _peer.State;
            // a failed check before both codes are in is not the end (the host's knocking can still open the way); a refused code is
            string e = _peer.Error;
            if (st == 2 && (Now == State.Linked || Now == State.Starting || e.Length > 0))
            {
                Fail(Now == State.Linked ? "The link was lost." : "The link could not start." + (e.Length > 0 ? " (" + e + ")" : ""));
                return;
            }
            switch (Now)
            {
                case State.Starting:
                {
                    // the code is made once the address search is over, or a little after the internet address came
                    string local = _peer.LocalText;
                    if (local == null) { if (t - _started > 15f) Fail("Your internet address could not be found. Check that the game is allowed through the firewall."); break; }
                    var d = ParseLocal(local, IsHost && !_pasted);
                    bool any = d.Cands.Exists(c => c.Kind == 1);
                    if (any && _firstAddress < 0f) _firstAddress = t;
                    bool done = _peer.Gathered || any && t - _firstAddress > 1.5f || t - _started > 8f;
                    if (!done || d.Ufrag == null || d.Pwd == null || d.Fingerprint == null)
                    {
                        if (t - _started > 15f) Fail("Your link code could not be made. Check that the game is allowed through the firewall.");
                        break;
                    }
                    if (d.Cands.Count == 0) { Fail("No internet address was found. Check that the game is allowed through the firewall."); break; }
                    _myMid = d.Mid;
                    MyCode = Pack(d);
                    Debug.Log("[online] code made, " + d.Cands.Count + " addresses, " + MyCode.Length + " letters");
                    if (IsHost) { Now = State.Ready; Status = "Send your code to your friend, then paste their reply code."; }
                    else { Now = State.Linking; _started = t; Status = "Send this reply code to your friend. The link starts when they paste it."; }
                    break;
                }
                case State.Linking:
                    if (st == 1) { Now = State.Linked; _lastHeard = t; Status = "Linked with your friend."; Debug.Log("[online] linked"); }
                    else if (t - _started > 180f) Fail("Your friend's game could not be reached. Check both codes, and that the game is allowed through the firewall.");
                    break;
                case State.Linked:
                    if (st == 3) { Now = State.Closed; Status = "Your friend left."; return; }
                    if (t - _pingSent > 1f) { Raw(1, MsgPing, BitConverter.GetBytes(t)); _pingSent = t; }
                    if (t - _lastHeard > 10f) Fail("The link was lost. Nothing came from your friend for 10 seconds.");
                    break;
            }
            ReadAll(t);
        }

        void ReadAll(float t)
        {
            byte[] p;
            for (int guard = 0; guard < 512 && (p = _peer.Next()) != null; guard++)
            {
                if (p.Length < 1 || Now != State.Linked) continue;
                _lastHeard = t;
                var body = new byte[p.Length - 1]; Array.Copy(p, 1, body, 0, body.Length);
                switch (p[0])
                {
                    case MsgData: _inbox.Enqueue(body); break;
                    case MsgPing: Raw(1, MsgPong, body); break;
                    case MsgPong: if (body.Length >= 4) PingMs = Mathf.RoundToInt((t - BitConverter.ToSingle(body, 0)) * 1000f); break;
                    case MsgBye: Now = State.Closed; Status = "Your friend left."; return;
                }
            }
        }

        public void Dispose()
        {
            if (_peer == null) return;
            if (Now == State.Linked) Raw(0, MsgBye, null);
            _peer.Dispose();
            _peer = null;
            if (Now != State.Failed) Now = State.Closed;
        }
    }
}
