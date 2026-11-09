using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Ours (mod "Online battle"; not in the original game): a direct link between two PCs with no server of our own.
    /// 1. Each PC opens a UDP port and asks a public STUN server (an "address helper": it only answers "this is your internet address")
    ///    which address and port the internet sees. 2. Its link code holds that address, its home-network address and a random session
    ///    number. 3. The friends swap codes by chat; both PCs then send small "hello" packets to each other's addresses at the same time,
    ///    which opens the way through most home routers (UDP hole punching). Strict networks (some mobile and office ones) cannot link
    ///    this way. Messages: unreliable ones (sent once) and reliable ones (numbered, sent again until the other side confirms).
    /// Call Tick() every frame from the main thread; nothing runs on other threads.
    /// </summary>
    public sealed class NetLink : IDisposable
    {
        public enum State { Starting, Ready, Linking, Linked, Failed, Closed }

        const byte Version = 3;           // 2: profiles carry techniques, the match messages
        static readonly string[] StunHosts = { "stun.l.google.com", "stun1.l.google.com", "stun.cloudflare.com" };
        static readonly int[] StunPorts = { 19302, 19302, 3478 };
        const byte MsgHello = 1, MsgHelloAck = 2, MsgData = 3, MsgAck = 4, MsgPing = 5, MsgPong = 6, MsgBye = 7;

        public State Now { get; private set; }
        public string Status { get; private set; }
        public string MyCode { get; private set; }
        public bool IsHost { get; private set; }
        public int PingMs { get; private set; }

        UdpClient _udp;
        IPEndPoint _public, _local, _peer;
        readonly List<IPEndPoint> _candidates = new List<IPEndPoint>();
        uint _myNonce, _peerNonce;
        readonly byte[] _stunId = new byte[12];
        float _stunSent = -10f, _helloSent = -10f, _lastHeard, _pingSent = -10f, _started;
        int _stunTry;

        // reliable messages: numbered; kept until acknowledged; the receiver delivers each number once, in order
        sealed class Pending { public ushort Seq; public byte[] Packet; public float Sent; }
        readonly List<Pending> _unacked = new List<Pending>();
        ushort _nextSeq = 1, _expectSeq = 1;
        readonly SortedDictionary<ushort, byte[]> _early = new SortedDictionary<ushort, byte[]>();
        readonly Queue<byte[]> _inbox = new Queue<byte[]>();

        public NetLink(bool host)
        {
            IsHost = host;
            var rng = new System.Random();
            _myNonce = (uint)rng.Next(1, int.MaxValue);
            rng.NextBytes(_stunId);
            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                _udp.Client.Blocking = false;
                // Windows: an ICMP "port unreachable" would otherwise make the next receive throw and stop the link
                try { const int SioUdpConnReset = -1744830452; _udp.Client.IOControl(SioUdpConnReset, new byte[] { 0 }, null); } catch { }
                int port = ((IPEndPoint)_udp.Client.LocalEndPoint).Port;
                _local = new IPEndPoint(LocalAddress(), port);
                Now = State.Starting; Status = "Finding your internet address...";
                _started = Time.realtimeSinceStartup;
            }
            catch (Exception e) { Fail("Could not open a network port: " + e.Message); }
        }

        // the address this PC uses on its home network (a UDP "connect" sends nothing; it only picks the route)
        static IPAddress LocalAddress()
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("8.8.8.8", 65530);
                    return ((IPEndPoint)s.LocalEndPoint).Address;
                }
            }
            catch { return IPAddress.Loopback; }
        }

        void Fail(string why) { Now = State.Failed; Status = why; Debug.LogWarning("[online] " + why); }

        // ---------------------------------------------------------------- link codes

        // the 18 bytes (version, host flag, nonce 4, internet address 4 and port 2, home address 4 and port 2) as 29 capital letters and digits: the
        // game's font has those (base64's + / = are not in it), and there is no I, O, 0 or 1 to mix up
        const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        static string ToCode(byte[] b)
        {
            var sb = new System.Text.StringBuilder(); int acc = 0, bits = 0;
            foreach (byte x in b) { acc = (acc << 8) | x; bits += 8; while (bits >= 5) { bits -= 5; sb.Append(Alphabet[(acc >> bits) & 31]); } }
            if (bits > 0) sb.Append(Alphabet[(acc << (5 - bits)) & 31]);
            return sb.ToString();
        }

        static byte[] FromCode(string s)
        {
            var r = new List<byte>(); int acc = 0, bits = 0;
            foreach (char c in s)
            {
                int v = Alphabet.IndexOf(c);
                if (v < 0) return null;
                acc = (acc << 5) | v; bits += 5;
                if (bits >= 8) { bits -= 8; r.Add((byte)((acc >> bits) & 255)); }
            }
            return r.ToArray();
        }

        /// <summary>The code in groups of five, easier to read out.</summary>
        public static string Spaced(string code)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < code.Length; i++) { if (i > 0 && i % 5 == 0) sb.Append(' '); sb.Append(code[i]); }
            return sb.ToString();
        }

        string MakeCode()
        {
            var b = new byte[18];
            b[0] = Version; b[1] = (byte)(IsHost ? 1 : 0);
            BitConverter.GetBytes(_myNonce).CopyTo(b, 2);
            var pub = _public ?? _local;
            pub.Address.GetAddressBytes().CopyTo(b, 6); b[10] = (byte)(pub.Port >> 8); b[11] = (byte)pub.Port;
            _local.Address.GetAddressBytes().CopyTo(b, 12); b[16] = (byte)(_local.Port >> 8); b[17] = (byte)_local.Port;
            return ToCode(b);
        }

        /// <summary>Takes the friend's code; false (and Status says why) when it is not a code or is the wrong kind.</summary>
        public bool UseFriendCode(string code)
        {
            var clean = new System.Text.StringBuilder();
            foreach (char c in (code ?? "").ToUpperInvariant()) if (!char.IsWhiteSpace(c)) clean.Append(c);
            byte[] b = FromCode(clean.ToString());
            if (b == null || b.Length != 18) { Status = "That is not a link code. Copy your friend's code and paste it again."; return false; }
            if (b[0] != Version) { Status = "Your friend has a different version of the game."; return false; }
            if ((b[1] == 1) == IsHost) { Status = IsHost ? "That is a host code. Paste your friend's reply code." : "That is a reply code. Paste your friend's host code."; return false; }
            _peerNonce = BitConverter.ToUInt32(b, 2);
            _candidates.Clear();
            _candidates.Add(new IPEndPoint(new IPAddress(new[] { b[6], b[7], b[8], b[9] }), (b[10] << 8) | b[11]));
            var home = new IPEndPoint(new IPAddress(new[] { b[12], b[13], b[14], b[15] }), (b[16] << 8) | b[17]);
            if (!home.Equals(_candidates[0])) _candidates.Add(home);
            Now = State.Linking; Status = "Linking...";
            _started = Time.realtimeSinceStartup;
            return true;
        }

        // ---------------------------------------------------------------- sending

        void Raw(IPEndPoint to, byte type, byte[] body)
        {
            if (_udp == null || to == null) return;
            var p = new byte[7 + (body != null ? body.Length : 0)];
            p[0] = (byte)'D'; p[1] = (byte)'W'; p[2] = type;
            BitConverter.GetBytes(_myNonce).CopyTo(p, 3);
            if (body != null) body.CopyTo(p, 7);
            try { _udp.Send(p, p.Length, to); } catch (Exception e) { Debug.LogWarning("[online] send: " + e.Message); }
        }

        /// <summary>Sends once; it may be lost (positions, button states sent every frame).</summary>
        public void SendFast(byte[] data)
        {
            if (Now != State.Linked) return;
            var body = new byte[2 + data.Length]; data.CopyTo(body, 2);
            Raw(_peer, MsgData, body);           // number 0 = unreliable
        }

        /// <summary>Sends until the friend's PC confirms it; delivered once and in order.</summary>
        public void SendSure(byte[] data)
        {
            if (Now != State.Linked) return;
            ushort seq = _nextSeq++;
            if (_nextSeq == 0) _nextSeq = 1;
            var body = new byte[2 + data.Length];
            body[0] = (byte)(seq >> 8); body[1] = (byte)seq; data.CopyTo(body, 2);
            _unacked.Add(new Pending { Seq = seq, Packet = body, Sent = Time.realtimeSinceStartup });
            Raw(_peer, MsgData, body);
        }

        /// <summary>The next message from the friend, or null.</summary>
        public byte[] Receive() { return _inbox.Count > 0 ? _inbox.Dequeue() : null; }

        // ---------------------------------------------------------------- every frame

        public void Tick()
        {
            if (_udp == null || Now == State.Failed || Now == State.Closed) return;
            float t = Time.realtimeSinceStartup;
            ReadAll(t);
            switch (Now)
            {
                case State.Starting:
                    if (t - _stunSent > 0.5f)
                    {
                        if (_stunTry >= StunHosts.Length * 3)
                        {
                            // no answer: home network only (both PCs on the same router)
                            MyCode = MakeCode(); Now = State.Ready;
                            Status = "No internet address was found. This code only works on the same home network.";
                            break;
                        }
                        SendStun(_stunTry % StunHosts.Length); _stunTry++; _stunSent = t;
                    }
                    break;
                case State.Linking:
                    if (t - _helloSent > 0.2f) { foreach (var c in _candidates) Raw(c, MsgHello, null); _helloSent = t; }
                    if (t - _started > 30f) Fail("Your friend's PC could not be reached in 30 seconds. Check both codes, or try on the same home network.");
                    break;
                case State.Linked:
                    foreach (var p in _unacked) if (t - p.Sent > 0.25f) { Raw(_peer, MsgData, p.Packet); p.Sent = t; }
                    if (t - _pingSent > 1f) { Raw(_peer, MsgPing, BitConverter.GetBytes(t)); _pingSent = t; }
                    if (t - _lastHeard > 10f) Fail("The link was lost. Nothing came from your friend's PC for 10 seconds.");
                    break;
            }
        }

        void ReadAll(float t)
        {
            for (int guard = 0; guard < 256; guard++)
            {
                byte[] p; IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                try { if (_udp.Available <= 0) return; p = _udp.Receive(ref from); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                if (Now == State.Starting && p.Length >= 20 && p[0] == 0x01 && p[1] == 0x01) { ReadStun(p); continue; }
                if (p.Length < 7 || p[0] != 'D' || p[1] != 'W') continue;
                uint nonce = BitConverter.ToUInt32(p, 3);
                if (_peerNonce == 0 || nonce != _peerNonce) continue;          // not our friend
                byte type = p[2];
                if (Now == State.Linking && (type == MsgHello || type == MsgHelloAck))
                {
                    _peer = from; Now = State.Linked; _lastHeard = t;
                    Status = "Linked with your friend's PC.";
                    Debug.Log("[online] linked with " + from);
                }
                if (Now != State.Linked || !from.Equals(_peer)) continue;
                _lastHeard = t;
                switch (type)
                {
                    case MsgHello: Raw(_peer, MsgHelloAck, null); break;
                    case MsgPing: { var b = new byte[p.Length - 7]; Array.Copy(p, 7, b, 0, b.Length); Raw(_peer, MsgPong, b); break; }
                    case MsgPong: if (p.Length >= 11) PingMs = Mathf.RoundToInt((t - BitConverter.ToSingle(p, 7)) * 1000f); break;
                    case MsgAck: if (p.Length >= 9) { ushort s = (ushort)((p[7] << 8) | p[8]); _unacked.RemoveAll(x => x.Seq == s); } break;
                    case MsgBye: Now = State.Closed; Status = "Your friend left."; break;
                    case MsgData:
                    {
                        if (p.Length < 9) break;
                        ushort seq = (ushort)((p[7] << 8) | p[8]);
                        var data = new byte[p.Length - 9]; Array.Copy(p, 9, data, 0, data.Length);
                        if (seq == 0) { _inbox.Enqueue(data); break; }
                        Raw(_peer, MsgAck, new[] { p[7], p[8] });
                        if (Before(seq, _expectSeq)) break;                    // a copy of one already delivered
                        _early[seq] = data;
                        byte[] next;
                        while (_early.TryGetValue(_expectSeq, out next))
                        {
                            _early.Remove(_expectSeq); _inbox.Enqueue(next);
                            _expectSeq++; if (_expectSeq == 0) _expectSeq = 1;
                        }
                        break;
                    }
                }
            }
        }

        static bool Before(ushort a, ushort b) { return (short)(a - b) < 0; }

        // ---------------------------------------------------------------- STUN (RFC 5389 binding request; XOR-MAPPED-ADDRESS answer)

        void SendStun(int i)
        {
            try
            {
                var addrs = Dns.GetHostAddresses(StunHosts[i]);
                IPAddress v4 = null;
                foreach (var a in addrs) if (a.AddressFamily == AddressFamily.InterNetwork) { v4 = a; break; }
                if (v4 == null) return;
                var req = new byte[20];
                req[0] = 0x00; req[1] = 0x01;                                  // binding request, no attributes
                req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42;    // magic cookie
                _stunId.CopyTo(req, 8);
                _udp.Send(req, req.Length, new IPEndPoint(v4, StunPorts[i]));
            }
            catch (Exception e) { Debug.LogWarning("[online] STUN " + StunHosts[i] + ": " + e.Message); }
        }

        void ReadStun(byte[] p)
        {
            for (int k = 0; k < 12; k++) if (p[8 + k] != _stunId[k]) return;
            int len = (p[2] << 8) | p[3], o = 20;
            while (o + 4 <= 20 + len && o + 4 <= p.Length)
            {
                int type = (p[o] << 8) | p[o + 1], alen = (p[o + 2] << 8) | p[o + 3];
                if ((type == 0x0020 || type == 0x0001) && alen >= 8 && p[o + 5] == 0x01 && o + 12 <= p.Length)
                {
                    int port = (p[o + 6] << 8) | p[o + 7];
                    var ip = new[] { p[o + 8], p[o + 9], p[o + 10], p[o + 11] };
                    if (type == 0x0020) { port ^= 0x2112; ip[0] ^= 0x21; ip[1] ^= 0x12; ip[2] ^= 0xA4; ip[3] ^= 0x42; }
                    _public = new IPEndPoint(new IPAddress(ip), port);
                    MyCode = MakeCode(); Now = State.Ready;
                    Status = IsHost ? "Send your code to your friend, then paste their reply code." : "Paste your friend's code, then send them your reply code.";
                    Debug.Log("[online] internet address " + _public + ", home " + _local);
                    return;
                }
                o += 4 + ((alen + 3) & ~3);
            }
        }

        public void Dispose()
        {
            if (_udp == null) return;
            if (Now == State.Linked) for (int i = 0; i < 3; i++) Raw(_peer, MsgBye, null);
            try { _udp.Close(); } catch { }
            _udp = null;
            if (Now != State.Failed) Now = State.Closed;
        }
    }
}
