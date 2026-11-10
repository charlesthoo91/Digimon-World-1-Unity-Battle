using System;
using System.Collections.Generic;
using UnityEngine;
#if !UNITY_WEBGL || UNITY_EDITOR
using Unity.WebRTC;
#endif

namespace DW1Remake.Game
{
    /// <summary>
    /// Ours (mod "Online battle"): one WebRTC peer with two data channels, 0 = sure (ordered, resent until it arrives) and 1 = fast
    /// (unordered, sent once). The browser version uses the browser's own WebRTC (Plugins/WebGL/DW1Web.jslib), the PC version Unity's
    /// WebRTC package (com.unity.webrtc). Both speak the same standard, so a PC and a browser can link. NetLink makes the codes.
    /// </summary>
    public interface IRtcPeer : IDisposable
    {
        /// <summary>The local description plus every candidate found so far ("a=candidate:" lines); null until there is one.</summary>
        string LocalText { get; }
        /// <summary>True once the search for addresses has finished.</summary>
        bool Gathered { get; }
        /// <summary>0 connecting, 1 the sure channel is open, 2 failed, 3 closed.</summary>
        int State { get; }
        string Error { get; }
        void SetRemote(string sdp, bool isOffer);
        void Send(int channel, byte[] data);
        byte[] Next();
    }

    public static class NetRtc
    {
        public static readonly string[] Stun = { "stun:stun.l.google.com:19302", "stun:stun1.l.google.com:19302", "stun:stun.cloudflare.com:3478" };

        public static IRtcPeer Create(bool offerer)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new WebPeer(offerer);
#else
            return new NativePeer(offerer);
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        // ---------------------------------------------------------------- the browser's RTCPeerConnection (DW1Web.jslib)

        [System.Runtime.InteropServices.DllImport("__Internal")] static extern int DW1_RtcNew(int offerer, string stun);
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern string DW1_RtcLocal(int id);
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern int DW1_RtcGathered(int id);
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern int DW1_RtcState(int id);
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern string DW1_RtcError(int id);
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern void DW1_RtcRemote(int id, string sdp, int isOffer);
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern void DW1_RtcSend(int id, int channel, byte[] data, int length);
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern int DW1_RtcNext(int id, byte[] buffer, int max);
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern void DW1_RtcClose(int id);

        sealed class WebPeer : IRtcPeer
        {
            int _id;
            readonly byte[] _buf = new byte[16384];
            public WebPeer(bool offerer) { _id = DW1_RtcNew(offerer ? 1 : 0, string.Join(" ", Stun)); }
            public string LocalText { get { string s = _id > 0 ? DW1_RtcLocal(_id) : null; return string.IsNullOrEmpty(s) ? null : s; } }
            public bool Gathered { get { return _id > 0 && DW1_RtcGathered(_id) != 0; } }
            public int State { get { return _id > 0 ? DW1_RtcState(_id) : 3; } }
            public string Error { get { return _id > 0 ? DW1_RtcError(_id) : ""; } }
            public void SetRemote(string sdp, bool isOffer) { if (_id > 0) DW1_RtcRemote(_id, sdp, isOffer ? 1 : 0); }
            public void Send(int channel, byte[] data) { if (_id > 0) DW1_RtcSend(_id, channel, data, data.Length); }
            public byte[] Next()
            {
                if (_id <= 0) return null;
                int n = DW1_RtcNext(_id, _buf, _buf.Length);
                if (n < 0) return null;
                var r = new byte[n]; Array.Copy(_buf, r, n); return r;
            }
            public void Dispose() { if (_id > 0) DW1_RtcClose(_id); _id = 0; }
        }
#else
        // ---------------------------------------------------------------- Unity's WebRTC package (Windows and the editor)

        sealed class NativePeer : IRtcPeer
        {
            RTCPeerConnection _pc;
            RTCDataChannel _sure, _fast;
            readonly bool _offerer;
            readonly List<string> _cands = new List<string>();
            readonly Queue<byte[]> _inbox = new Queue<byte[]>();
            RTCSessionDescriptionAsyncOperation _create;
            RTCSetSessionDescriptionAsyncOperation _setLocal, _setRemote;
            bool _gathered, _open, _closed, _failed, _answerPending;
            string _error = "";

            public NativePeer(bool offerer)
            {
                _offerer = offerer;
                try
                {
                    var conf = new RTCConfiguration { iceServers = new[] { new RTCIceServer { urls = Stun } } };
                    _pc = new RTCPeerConnection(ref conf);
                    _pc.OnIceCandidate = c => { if (c != null && !string.IsNullOrEmpty(c.Candidate)) _cands.Add(c.Candidate); };
                    _pc.OnIceGatheringStateChange = s => { if (s == RTCIceGatheringState.Complete) _gathered = true; };
                    _pc.OnIceConnectionChange = s =>
                    {
                        if (s == RTCIceConnectionState.Failed) _failed = true;
                        if (s == RTCIceConnectionState.Closed) _closed = true;
                    };
                    // both sides make the same two channels by number (negotiated), so neither has to wait for the other's
                    _sure = _pc.CreateDataChannel("sure", new RTCDataChannelInit { negotiated = true, id = 0, ordered = true });
                    _fast = _pc.CreateDataChannel("fast", new RTCDataChannelInit { negotiated = true, id = 1, ordered = false, maxRetransmits = 0 });
                    _sure.OnOpen = () => _open = true;
                    _sure.OnClose = () => _closed = true;
                    _sure.OnMessage = b => _inbox.Enqueue(b);
                    _fast.OnMessage = b => _inbox.Enqueue(b);
                    if (offerer) _create = _pc.CreateOffer();
                }
                catch (Exception e) { _failed = true; _error = e.Message; }
            }

            // the steps of making and setting descriptions finish later; this moves them on (called from LocalText / State, every frame)
            void Step()
            {
                if (_pc == null) return;
                if (_create != null && _create.IsDone)
                {
                    if (_create.IsError) { _failed = true; _error = _create.Error.message; }
                    else { var d = _create.Desc; _setLocal = _pc.SetLocalDescription(ref d); }
                    _create = null;
                }
                if (_setLocal != null && _setLocal.IsDone)
                {
                    if (_setLocal.IsError) { _failed = true; _error = _setLocal.Error.message; }
                    _setLocal = null;
                }
                if (_setRemote != null && _setRemote.IsDone)
                {
                    if (_setRemote.IsError) { _failed = true; _error = _setRemote.Error.message; }
                    else if (_answerPending) _create = _pc.CreateAnswer();
                    _answerPending = false; _setRemote = null;
                }
            }

            public string LocalText
            {
                get
                {
                    Step();
                    if (_pc == null || _create != null || _setLocal != null) return null;
                    string sdp;
                    try { sdp = _pc.LocalDescription.sdp; } catch { return null; }
                    if (string.IsNullOrEmpty(sdp)) return null;
                    var sb = new System.Text.StringBuilder(sdp);
                    foreach (var c in _cands) sb.Append("\na=").Append(c.StartsWith("candidate:") ? c : "candidate:" + c);
                    return sb.ToString();
                }
            }

            public bool Gathered { get { return _gathered; } }
            public int State { get { Step(); return _failed ? 2 : _closed ? 3 : _open ? 1 : 0; } }
            public string Error { get { return _error; } }

            public void SetRemote(string sdp, bool isOffer)
            {
                if (_pc == null) return;
                try
                {
                    var d = new RTCSessionDescription { type = isOffer ? RTCSdpType.Offer : RTCSdpType.Answer, sdp = sdp };
                    _setRemote = _pc.SetRemoteDescription(ref d);
                    _answerPending = isOffer && !_offerer;
                }
                catch (Exception e) { _failed = true; _error = e.Message; }
            }

            public void Send(int channel, byte[] data)
            {
                var ch = channel == 0 ? _sure : _fast;
                try { if (ch != null && ch.ReadyState == RTCDataChannelState.Open) ch.Send(data); }
                catch (Exception e) { Debug.LogWarning("[online] send: " + e.Message); }
            }

            public byte[] Next() { return _inbox.Count > 0 ? _inbox.Dequeue() : null; }

            public void Dispose()
            {
                try { if (_sure != null) _sure.Close(); if (_fast != null) _fast.Close(); if (_pc != null) _pc.Close(); } catch { }
                if (_sure != null) _sure.Dispose(); if (_fast != null) _fast.Dispose(); if (_pc != null) _pc.Dispose();
                _sure = _fast = null; _pc = null; _closed = true;
            }
        }
#endif
    }
}
