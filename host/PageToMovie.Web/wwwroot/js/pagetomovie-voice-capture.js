// Mic capture for voice-clone templates (MediaRecorder → Blob).
window.PageToMovieVoiceCapture = (function () {
  let mediaStream = null;
  let recorder = null;
  let chunks = [];

  async function start() {
    if (recorder?.state === "recording") return { ok: true, already: true };
    chunks = [];
    mediaStream = await navigator.mediaDevices.getUserMedia({ audio: true });
    const mime = MediaRecorder.isTypeSupported("audio/webm;codecs=opus")
      ? "audio/webm;codecs=opus"
      : MediaRecorder.isTypeSupported("audio/webm")
        ? "audio/webm"
        : "";
    recorder = mime ? new MediaRecorder(mediaStream, { mimeType: mime }) : new MediaRecorder(mediaStream);
    recorder.ondataavailable = (e) => {
      if (e.data && e.data.size > 0) chunks.push(e.data);
    };
    recorder.start(200);
    return { ok: true, mimeType: recorder.mimeType || "audio/webm" };
  }

  function stop() {
    return new Promise((resolve, reject) => {
      if (!recorder) {
        resolve({ ok: false, error: "Not recording" });
        return;
      }
      const rec = recorder;
      rec.onstop = () => {
        try {
          const type = rec.mimeType || "audio/webm";
          const blob = new Blob(chunks, { type });
          chunks = [];
          if (mediaStream) {
            mediaStream.getTracks().forEach((t) => t.stop());
            mediaStream = null;
          }
          recorder = null;
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
        } catch (err) {
          reject(err);
        }
      };
      if (rec.state === "recording") rec.stop();
      else rec.onstop();
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
