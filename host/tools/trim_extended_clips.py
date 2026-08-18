import os
import sys
import subprocess
import json
import shutil

FFMPEG_PATH = r"C:\Users\budcr\.nuget\packages\soenneker.libraries.ffmpeg\4.0.1095\contentFiles\any\any\Resources\ffmpeg.exe"

def get_video_duration(video_path):
    if not os.path.exists(video_path):
        return None
    cmd = [FFMPEG_PATH, "-hide_banner", "-i", video_path]
    res = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    for line in res.stderr.splitlines():
        if "Duration:" in line:
            parts = line.split("Duration:")[1].split(",")[0].strip()
            h, m, s = parts.split(":")
            return float(h) * 3600 + float(m) * 60 + float(s)
    return None

def trim_video_start(input_path, output_path, start_seconds):
    """Extracts footage from start_seconds to the end of the video."""
    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    start_str = f"{start_seconds:.3f}"
    cmd = [
        FFMPEG_PATH,
        "-hide_banner",
        "-y",
        "-ss", start_str,
        "-i", input_path,
        "-c:v", "libx264",
        "-preset", "ultrafast",
        "-crf", "23",
        "-c:a", "aac",
        "-b:a", "128k",
        "-movflags", "+faststart",
        output_path
    ]
    res = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    return res.returncode == 0 and os.path.exists(output_path) and os.path.getsize(output_path) > 1024

def process_project(project_dir):
    video_dir = os.path.join(project_dir, "assets", "video")
    if not os.path.isdir(video_dir):
        print(f"Directory not found: {video_dir}")
        return

    print(f"\nProcessing project: {project_dir}")
    print(f"Scanning {video_dir} for clips...")

    clips = sorted([f for f in os.listdir(video_dir) if f.startswith("scene_") and f.endswith(".mp4") and not f.startswith("_")])
    if not clips:
        print("No clips found.")
        return

    for clip_name in clips:
        # e.g., scene_01_clip_02.mp4
        parts = clip_name.replace(".mp4", "").split("_")
        if len(parts) >= 4 and parts[0] == "scene" and parts[2] == "clip":
            scene_num = int(parts[1])
            clip_num = int(parts[3])
            clip_path = os.path.join(video_dir, clip_name)
            clip_dur = get_video_duration(clip_path)

            if clip_num > 1:
                prev_clip_name = f"scene_{scene_num:02d}_clip_{clip_num - 1:02d}.mp4"
                prev_clip_path = os.path.join(video_dir, prev_clip_name)
                prev_dur = get_video_duration(prev_clip_path)

                if prev_dur and clip_dur and clip_dur > prev_dur:
                    delta_dur = clip_dur - prev_dur
                    print(f"\n[Extended Clip Detected] {clip_name}: Total {clip_dur:.2f}s contains predecessor {prev_clip_name} ({prev_dur:.2f}s).")
                    print(f"  Action: Trimming first {prev_dur:.2f}s to create standalone delta clip of ~{delta_dur:.2f}s...")

                    backup_dir = os.path.join(video_dir, "_raw_extensions")
                    os.makedirs(backup_dir, exist_ok=True)
                    backup_path = os.path.join(backup_dir, clip_name)
                    
                    if not os.path.exists(backup_path):
                        shutil.copy2(clip_path, backup_path)
                        print(f"  Backed up raw combined video to: {backup_path}")

                    temp_trimmed = os.path.join(video_dir, f"_temp_{clip_name}")
                    if trim_video_start(backup_path, temp_trimmed, prev_dur):
                        trimmed_dur = get_video_duration(temp_trimmed)
                        shutil.move(temp_trimmed, clip_path)
                        print(f"  Successfully trimmed {clip_name}: New standalone duration is {trimmed_dur:.2f}s.")
                    else:
                        print(f"  ERROR: Trimming failed for {clip_name}")
                        if os.path.exists(temp_trimmed):
                            os.remove(temp_trimmed)
                else:
                    print(f"  {clip_name}: Standalone clip ({clip_dur:.2f}s).")
            else:
                print(f"  {clip_name}: Lead clip ({clip_dur:.2f}s).")

if __name__ == "__main__":
    target = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\budcr\Videos\PageToMovie\budcribar\Mary18"
    process_project(target)
