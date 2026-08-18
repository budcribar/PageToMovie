import os, json, subprocess

ffmpeg = r"C:\Users\budcr\.nuget\packages\soenneker.libraries.ffmpeg\4.0.1095\contentFiles\any\any\Resources\ffmpeg.exe"

def get_duration(video_path):
    if not os.path.exists(video_path):
        return None
    cmd = [ffmpeg, "-i", video_path]
    res = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    for line in res.stderr.splitlines():
        if "Duration:" in line:
            parts = line.split("Duration:")[1].split(",")[0].strip()
            h, m, s = parts.split(":")
            return float(h)*3600 + float(m)*60 + float(s)
    return None

base = r"C:\Users\budcr\Videos\PageToMovie\budcribar\Mary18\assets\video"
for f in sorted(os.listdir(base)):
    if "clip_03" in f:
        full = os.path.join(base, f)
        if f.endswith(".mp4"):
            print(f, f"{os.path.getsize(full)} bytes", "->", get_duration(full), "s", "modified:", os.path.getmtime(full))
        elif f.endswith(".json") and not f.endswith(".client.json"):
            print(f, "modified:", os.path.getmtime(full))
            with open(full, "r", encoding="utf-8") as fp:
                d = json.load(fp)
                print("  take data:", {
                    "mode": d.get("mode"),
                    "duration_seconds": d.get("duration_seconds"),
                    "source_file_id": d.get("source_file_id"),
                    "prompt": d.get("prompt", "")[:120],
                    "ref_images": d.get("reference_images"),
                    "extend_source": d.get("extend_source")
                })
