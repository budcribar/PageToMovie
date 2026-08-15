// Mic capture for voice-clone templates (MediaRecorder → Blob).
window.PageToMovieVoiceCapture = (function () {
  let mediaStream = null;
  let recorder = null;
  let chunks = [];

  async function start() {
    if (recorder?.state === "recording") return { ok: true, already: true };
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      return { ok: false, error: "This browser cannot use the microphone. Try Chrome or Edge over https." };
    }
    chunks = [];
    try {
      const mic = navigator.mediaDevices.getUserMedia({ audio: true });
      const timed = new Promise((_, reject) =>
        setTimeout(() => reject(new Error("Microphone timed out — click Record and allow the mic.")), 12000));
      mediaStream = await Promise.race([mic, timed]);
    } catch (err) {
      return { ok: false, error: (err && err.message) ? err.message : "Microphone blocked" };
    }
    let mime = "";
    if (MediaRecorder.isTypeSupported("audio/webm;codecs=opus")) {
      mime = "audio/webm;codecs=opus";
    } else if (MediaRecorder.isTypeSupported("audio/webm")) {
      mime = "audio/webm";
    } else if (MediaRecorder.isTypeSupported("audio/mp4")) {
      mime = "audio/mp4";
    }
    recorder = mime ? new MediaRecorder(mediaStream, { mimeType: mime }) : new MediaRecorder(mediaStream);
    recorder.ondataavailable = (e) => {
      if (e.data && e.data.size > 0) chunks.push(e.data);
    };
    recorder.start(200);
    return { ok: true, mimeType: recorder.mimeType || mime || "audio/webm" };
  }

  function readBlob(blob, type) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onloadend = () => {
        const dataUrl = reader.result;
        const base64 = typeof dataUrl === "string" ? dataUrl.split(",")[1] : null;
        resolve({
          ok: true,
          mimeType: type,
          byteLength: blob.size,
          base64,
          fileName: "voice_clone_sample.webm",
        });
      };
      reader.onerror = () => reject(reader.error || new Error("read failed"));
      reader.readAsDataURL(blob);
    });
  }

  function releaseMic() {
    if (mediaStream) {
      mediaStream.getTracks().forEach((t) => t.stop());
      mediaStream = null;
    }
    recorder = null;
  }

  function stop() {
    return new Promise((resolve) => {
      let settled = false;
      const finish = (payload) => {
        if (settled) return;
        settled = true;
        resolve(payload);
      };

      if (!recorder) {
        finish({ ok: false, error: "Not recording" });
        return;
      }

      const rec = recorder;
      const type = rec.mimeType || "audio/webm";
      const timer = setTimeout(() => {
        try {
          const blob = new Blob(chunks, { type });
          chunks = [];
          releaseMic();
          if (blob.size === 0) {
            finish({ ok: false, error: "No audio captured." });
            return;
          }
          readBlob(blob, type).then(finish).catch((err) => {
            finish({ ok: false, error: (err && err.message) || "read failed" });
          });
        } catch (err) {
          releaseMic();
          finish({ ok: false, error: (err && err.message) || "stop timed out" });
        }
      }, 2500);

      rec.onstop = () => {
        clearTimeout(timer);
        try {
          const blob = new Blob(chunks, { type });
          chunks = [];
          releaseMic();
          readBlob(blob, type).then(finish).catch((err) => {
            finish({ ok: false, error: (err && err.message) || "read failed" });
          });
        } catch (err) {
          releaseMic();
          finish({ ok: false, error: (err && err.message) || "stop failed" });
        }
      };

      try {
        if (rec.state === "recording") {
          try { rec.requestData(); } catch (_) { /* older browsers */ }
          rec.stop();
        } else {
          rec.onstop();
        }
      } catch (err) {
        clearTimeout(timer);
        releaseMic();
        finish({ ok: false, error: (err && err.message) || "stop failed" });
      }
    });
  }

  function cancel() {
    try {
      if (recorder?.state === "recording") recorder.stop();
    } catch (_) {}
    recorder = null;
    chunks = [];
    if (mediaStream) {
      mediaStream.getTracks().forEach((t) => t.stop());
      mediaStream = null;
    }
    return { ok: true };
  }

  return { start, stop, cancel };
})();
