// Browser-only helpers for the web build (ours, not the game's). C# side: Game/WebFiles.cs.
mergeInto(LibraryManager.library, {

  // Opens the browser's "choose file" box. The chosen disc image is read into the page's own memory file system at /dw1disc/<name>
  // (it never leaves this browser), then SendMessage(objectName, method, path) tells the game; "" = cancelled, "!" + text = error.
  DW1_PickDisc: function (objectNamePtr, methodPtr) {
    var objectName = UTF8ToString(objectNamePtr), method = UTF8ToString(methodPtr);
    var input = document.createElement('input');
    input.type = 'file';
    input.accept = '.bin,.iso';
    input.style.display = 'none';
    document.body.appendChild(input);
    var done = function (text) {
      if (input.parentNode) input.parentNode.removeChild(input);
      SendMessage(objectName, method, text);
    };
    input.addEventListener('cancel', function () { done(''); });
    input.addEventListener('change', function () {
      var f = input.files && input.files[0];
      if (!f) { done(''); return; }
      SendMessage(objectName, method, '#Reading ' + f.name + ' ...');
      f.arrayBuffer().then(function (buf) {
        try { FS.mkdir('/dw1disc'); } catch (e) { }
        var path = '/dw1disc/' + f.name.replace(/[\/\\]/g, '_');
        FS.writeFile(path, new Uint8Array(buf), { canOwn: true });
        Module.dw1LastDisc = f;
        done(path);
      }).catch(function (e) { done('!The file could not be read: ' + e); });
    });
    input.click();
  },

  // Writes the save files (Application.persistentDataPath) to the browser's storage now, so a closed tab keeps them
  DW1_SyncFiles: function () {
    FS.syncfs(false, function (err) { if (err) console.log('[web] save sync failed: ' + err); });
  },

  // ---------------------------------------------------------------- online battle: one WebRTC peer, channels 0 sure and 1 fast (NetRtc.cs)

  DW1_RtcNew: function (offerer, stunPtr) {
    var R = Module.dw1rtc || (Module.dw1rtc = { next: 1, peers: {} });
    var id = R.next++;
    var p = { pc: null, ch: [], inbox: [], cands: [], gathered: false, state: 0, error: '' };
    R.peers[id] = p;
    var fail = function (e) { p.state = 2; p.error = '' + e; console.log('[online] ' + e); };
    try {
      var urls = UTF8ToString(stunPtr).split(' ');
      p.pc = new RTCPeerConnection({ iceServers: [{ urls: urls }] });
      p.pc.onicecandidate = function (e) { if (e.candidate && e.candidate.candidate) p.cands.push(e.candidate.candidate); else if (!e.candidate) p.gathered = true; };
      p.pc.onicegatheringstatechange = function () { if (p.pc.iceGatheringState === 'complete') p.gathered = true; };
      p.pc.oniceconnectionstatechange = function () { var s = p.pc.iceConnectionState; if (s === 'failed') p.state = 2; else if (s === 'closed') p.state = 3; };
      var make = function (label, n, opts) {
        opts.negotiated = true; opts.id = n;
        var c = p.pc.createDataChannel(label, opts);
        c.binaryType = 'arraybuffer';
        c.onmessage = function (e) { if (e.data instanceof ArrayBuffer) p.inbox.push(new Uint8Array(e.data)); };
        return c;
      };
      p.ch[0] = make('sure', 0, { ordered: true });
      p.ch[1] = make('fast', 1, { ordered: false, maxRetransmits: 0 });
      p.ch[0].onopen = function () { if (p.state === 0) p.state = 1; };
      p.ch[0].onclose = function () { if (p.state !== 2) p.state = 3; };
      if (offerer) p.pc.createOffer().then(function (d) { return p.pc.setLocalDescription(d); }).catch(fail);
    } catch (e) { fail(e); }
    return id;
  },

  DW1_RtcLocal: function (id) {
    var p = Module.dw1rtc && Module.dw1rtc.peers[id];
    var text = '';
    if (p && p.pc && p.pc.localDescription && p.pc.localDescription.sdp) {
      text = p.pc.localDescription.sdp;
      for (var i = 0; i < p.cands.length; i++) text += '\na=' + p.cands[i];
    }
    var n = lengthBytesUTF8(text) + 1, buf = _malloc(n);
    stringToUTF8(text, buf, n);
    return buf;
  },

  DW1_RtcGathered: function (id) { var p = Module.dw1rtc && Module.dw1rtc.peers[id]; return p && p.gathered ? 1 : 0; },

  DW1_RtcState: function (id) { var p = Module.dw1rtc && Module.dw1rtc.peers[id]; return p ? p.state : 3; },

  DW1_RtcError: function (id) {
    var p = Module.dw1rtc && Module.dw1rtc.peers[id], text = p ? p.error : '';
    var n = lengthBytesUTF8(text) + 1, buf = _malloc(n);
    stringToUTF8(text, buf, n);
    return buf;
  },

  DW1_RtcRemote: function (id, sdpPtr, isOffer) {
    var p = Module.dw1rtc && Module.dw1rtc.peers[id];
    if (!p || !p.pc) return;
    var fail = function (e) { p.state = 2; p.error = '' + e; console.log('[online] ' + e); };
    var sdp = UTF8ToString(sdpPtr);
    var set = p.pc.setRemoteDescription({ type: isOffer ? 'offer' : 'answer', sdp: sdp });
    if (isOffer) set = set.then(function () { return p.pc.createAnswer(); }).then(function (d) { return p.pc.setLocalDescription(d); });
    set.catch(fail);
  },

  DW1_RtcSend: function (id, channel, dataPtr, length) {
    var p = Module.dw1rtc && Module.dw1rtc.peers[id];
    var c = p && p.ch[channel];
    if (c && c.readyState === 'open') { try { c.send(HEAPU8.slice(dataPtr, dataPtr + length)); } catch (e) { console.log('[online] send: ' + e); } }
  },

  DW1_RtcNext: function (id, bufPtr, max) {
    var p = Module.dw1rtc && Module.dw1rtc.peers[id];
    if (!p || p.inbox.length === 0) return -1;
    var m = p.inbox.shift();
    if (m.length > max) return -1;
    HEAPU8.set(m, bufPtr);
    return m.length;
  },

  DW1_RtcClose: function (id) {
    var R = Module.dw1rtc, p = R && R.peers[id];
    if (!p) return;
    try { p.ch.forEach(function (c) { c.close(); }); if (p.pc) p.pc.close(); } catch (e) { }
    delete R.peers[id];
  },

  // ---------------------------------------------------------------- the clipboard (online codes)

  DW1_CopyText: function (textPtr) {
    var t = UTF8ToString(textPtr);
    if (navigator.clipboard) navigator.clipboard.writeText(t).catch(function (e) { console.log('[web] copy: ' + e); });
  },

  // the answer comes back as SendMessage(objectName, method, text); "" = nothing there or not allowed
  DW1_PasteText: function (objectNamePtr, methodPtr) {
    var o = UTF8ToString(objectNamePtr), m = UTF8ToString(methodPtr);
    if (!navigator.clipboard || !navigator.clipboard.readText) { SendMessage(o, m, ''); return; }
    navigator.clipboard.readText().then(function (t) { SendMessage(o, m, t || ''); }).catch(function (e) { console.log('[web] paste: ' + e); SendMessage(o, m, ''); });
  },

  // ---------------------------------------------------------------- the disc kept between visits, and save files in and out

  // keeps the last chosen disc file in the browser's storage (IndexedDB "DW1Remake", store "disc"), after the game has checked it
  DW1_KeepDisc: function () {
    var f = Module.dw1LastDisc;
    if (!f || !window.indexedDB) return;
    if (navigator.storage && navigator.storage.persist) navigator.storage.persist();
    var rq = indexedDB.open('DW1Remake', 1);
    rq.onupgradeneeded = function () { rq.result.createObjectStore('disc'); };
    rq.onsuccess = function () {
      try { var tx = rq.result.transaction('disc', 'readwrite'); tx.objectStore('disc').put(f, 'disc'); }
      catch (e) { console.log('[web] keep disc: ' + e); }
    };
  },

  // reads the kept disc back into the page's memory; answers like DW1_PickDisc ("" = none kept)
  DW1_LoadKeptDisc: function (objectNamePtr, methodPtr) {
    var o = UTF8ToString(objectNamePtr), m = UTF8ToString(methodPtr);
    var none = function () { SendMessage(o, m, ''); };
    if (!window.indexedDB) { none(); return; }
    var rq = indexedDB.open('DW1Remake', 1);
    rq.onupgradeneeded = function () { rq.result.createObjectStore('disc'); };
    rq.onerror = none;
    rq.onsuccess = function () {
      try {
        var get = rq.result.transaction('disc', 'readonly').objectStore('disc').get('disc');
        get.onerror = none;
        get.onsuccess = function () {
          var f = get.result;
          if (!f) { none(); return; }
          SendMessage(o, m, '#Reading the disc you chose before ...');
          f.arrayBuffer().then(function (buf) {
            try { FS.mkdir('/dw1disc'); } catch (e) { }
            var path = '/dw1disc/' + (f.name || 'game.bin').replace(/[\/\\]/g, '_');
            FS.writeFile(path, new Uint8Array(buf), { canOwn: true });
            Module.dw1LastDisc = f;
            SendMessage(o, m, path);
          }).catch(function (e) { console.log('[web] kept disc: ' + e); none(); });
        };
      } catch (e) { none(); }
    };
  },

  // a save file to the Downloads folder
  DW1_DownloadText: function (namePtr, textPtr) {
    var blob = new Blob([UTF8ToString(textPtr)], { type: 'application/json' });
    var a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = UTF8ToString(namePtr);
    document.body.appendChild(a); a.click();
    setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
  },

  // the choose-file box for a save file; its text comes back as SendMessage(objectName, method, text); "" = cancelled
  DW1_PickText: function (objectNamePtr, methodPtr) {
    var o = UTF8ToString(objectNamePtr), m = UTF8ToString(methodPtr);
    var input = document.createElement('input');
    input.type = 'file'; input.accept = '.json,.mcd,.mcr,.mc,.srm,.gme,.vmp,.mcs'; input.style.display = 'none';
    document.body.appendChild(input);
    var done = function (t) { if (input.parentNode) input.parentNode.removeChild(input); SendMessage(o, m, t); };
    input.addEventListener('cancel', function () { done(''); });
    input.addEventListener('change', function () {
      var f = input.files && input.files[0];
      if (!f) { done(''); return; }
      if (/\.json$/i.test(f.name)) { f.text().then(function (t) { done(t || ''); }).catch(function () { done(''); }); return; }
      // a memory card file: its bytes as base64 after a \u0001 mark (PlayableRoom.OnWebSaveText)
      f.arrayBuffer().then(function (buf) {
        var b = new Uint8Array(buf), s = '', step = 0x8000;
        for (var i = 0; i < b.length; i += step) s += String.fromCharCode.apply(null, b.subarray(i, i + step));
        done('\u0001' + btoa(s));
      }).catch(function () { done(''); });
    });
    input.click();
  },
});
