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
print("=== Video Files in assets/video ===")
for f in sorted(os.listdir(base)):
    full = os.path.join(base, f)
    if os.path.isfile(full) and f.endswith(".mp4"):
        print(f, f"{os.path.getsize(full)} bytes", "->", get_duration(full), "s")
    elif os.path.isfile(full) and f.endswith(".clip.json") and not f.endswith(".client.json"):
        with open(full, "r", encoding="utf-8") as fp:
            d = json.load(fp)
            print(f, "mode:", d.get("mode"), "dur:", d.get("duration_seconds"), "src_file_id:", d.get("source_file_id"))

raw_dir = os.path.join(base, "_raw_extensions")
if os.path.exists(raw_dir):
    print("\n=== Files in _raw_extensions ===")
    for f in sorted(os.listdir(raw_dir)):
        full = os.path.join(raw_dir, f)
        if os.path.isfile(full) and f.endswith(".mp4"):
            print(f, f"{os.path.getsize(full)} bytes", "->", get_duration(full), "s")
