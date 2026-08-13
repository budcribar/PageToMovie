// Browser speech-to-text for look / image-edit prompts (Web Speech API).
// Optional mic level meter for the coach popover waveform.
// Blazor: ptmDictation.start(dotNetRef, fieldId, lang), .stop(), .isSupported()
(function () {
  "use strict";

  var _rec = null;
  var _dotNet = null;
  var _fieldId = null;
  var _final = "";
  var _audioStream = null;
  var _audioCtx = null;
  var _analyser = null;
  var _meterRaf = 0;
  var _meterData = null;

  function SpeechCtor() {
    return window.SpeechRecognition || window.webkitSpeechRecognition || null;
  }

  function stopMeter() {
    if (_meterRaf) {
      try { cancelAnimationFrame(_meterRaf); } catch (_) { /* ignore */ }
      _meterRaf = 0;
    }
    _analyser = null;
    _meterData = null;
    if (_audioStream) {
      try {
        _audioStream.getTracks().forEach(function (t) { t.stop(); });
      } catch (_) { /* ignore */ }
      _audioStream = null;
    }
    if (_audioCtx) {
      try { _audioCtx.close(); } catch (_) { /* ignore */ }
      _audioCtx = null;
    }
  }

  function startMeter(dotNetRef, fieldId) {
    if (!navigator.mediaDevices?.getUserMedia) {
      return Promise.resolve({ ok: false });
    }
    stopMeter();
    return navigator.mediaDevices.getUserMedia({ audio: true, video: false })
      .then(function (stream) {
        _audioStream = stream;
        var Ctx = window.AudioContext || window.webkitAudioContext;
        if (!Ctx) return { ok: false };
        _audioCtx = new Ctx();
        var src = _audioCtx.createMediaStreamSource(stream);
        _analyser = _audioCtx.createAnalyser();
        _analyser.fftSize = 256;
        _analyser.smoothingTimeConstant = 0.75;
        src.connect(_analyser);
        _meterData = new Uint8Array(_analyser.frequencyBinCount);

        function tick() {
          if (!_analyser || !_meterData) return;
          _analyser.getByteTimeDomainData(_meterData);
          var sum = 0;
          for (const sample of _meterData) {
            var v = (sample - 128) / 128;
            sum += v * v;
          }
          var rms = Math.sqrt(sum / _meterData.length);
          // Soft boost so quiet speech still moves bars
          var level = Math.min(1, rms * 4.5);
          if (dotNetRef) {
            dotNetRef.invokeMethodAsync("OnDictationLevel", fieldId, level).catch(function () { /* disposed */ });
          }
          _meterRaf = requestAnimationFrame(tick);
        }
        tick();
        return { ok: true };
      })
      .catch(function () {
        // Permission denied or no device — coach UI still works without a live meter
        return { ok: false };
      });
  }

  window.ptmDictation = {
    isSupported: function () {
      return !!SpeechCtor();
    },

    start: function (dotNetRef, fieldId, lang) {
      var Ctor = SpeechCtor();
      if (!Ctor) {
        return Promise.resolve({ ok: false, error: "Speech recognition is not supported in this browser." });
      }
      try {
        if (_rec) {
          try { _rec.stop(); } catch (_) { /* ignore */ }
          _rec = null;
        }
        stopMeter();
        _dotNet = dotNetRef;
        _fieldId = fieldId || "default";
        _final = "";
        var rec = new Ctor();
        rec.continuous = true;
        rec.interimResults = true;
        rec.lang = lang || (navigator.language || "en-US");
        rec.onresult = function (ev) {
          var interim = "";
          for (var i = ev.resultIndex; i < ev.results.length; i++) {
            var t = ev.results[i][0].transcript || "";
            if (ev.results[i].isFinal) _final += t;
            else interim += t;
          }
          var text = (_final + interim).trim();
          if (_dotNet) {
            _dotNet.invokeMethodAsync("OnDictationPartial", _fieldId, text).catch(function () { /* disposed */ });
          }
        };
        rec.onerror = function (ev) {
          var err = ev?.error || "speech_error";
          if (_dotNet) {
            _dotNet.invokeMethodAsync("OnDictationError", _fieldId, err).catch(function () { });
          }
          stopMeter();
        };
        rec.onend = function () {
          if (_dotNet) {
            _dotNet.invokeMethodAsync("OnDictationEnd", _fieldId, (_final || "").trim()).catch(function () { });
          }
          _rec = null;
          stopMeter();
        };
        _rec = rec;
        rec.start();
        // Live waveform for coach popover (best-effort; speech still works if meter fails)
        startMeter(dotNetRef, _fieldId);
        return Promise.resolve({ ok: true });
      } catch (ex) {
        stopMeter();
        return Promise.resolve({ ok: false, error: ex?.message || String(ex) });
      }
    },

    stop: function () {
      try {
        if (_rec) _rec.stop();
      } catch (_) { /* ignore */ }
      stopMeter();
      return Promise.resolve({ ok: true });
    },
  };

  // Back-compat aliases used by VoiceDictationButton
  window.isDictationSupported = function () { return window.ptmDictation.isSupported(); };
  window.startDictation = function (dotNetRef, fieldId, lang) {
    return window.ptmDictation.start(dotNetRef, fieldId, lang);
  };
  window.stopDictation = function () { return window.ptmDictation.stop(); };
})();
