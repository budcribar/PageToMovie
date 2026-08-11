// Browser speech-to-text for look / image-edit prompts (Web Speech API).
// Blazor: startDictation(dotNetRef, fieldId), stopDictation(), isDictationSupported()
(function () {
  "use strict";

  var _rec = null;
  var _dotNet = null;
  var _fieldId = null;
  var _final = "";

  function SpeechCtor() {
    return window.SpeechRecognition || window.webkitSpeechRecognition || null;
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
          var err = (ev && ev.error) || "speech_error";
          if (_dotNet) {
            _dotNet.invokeMethodAsync("OnDictationError", _fieldId, err).catch(function () { });
          }
        };
        rec.onend = function () {
          if (_dotNet) {
            _dotNet.invokeMethodAsync("OnDictationEnd", _fieldId, (_final || "").trim()).catch(function () { });
          }
          _rec = null;
        };
        _rec = rec;
        rec.start();
        return Promise.resolve({ ok: true });
      } catch (ex) {
        return Promise.resolve({ ok: false, error: (ex && ex.message) || String(ex) });
      }
    },

    stop: function () {
      try {
        if (_rec) _rec.stop();
      } catch (_) { /* ignore */ }
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
