import os, json

tel_file = r"C:\Users\budcr\Videos\PageToMovie\budcribar\Mary18\telemetry\api_calls.jsonl"
if os.path.exists(tel_file):
    with open(tel_file, "r", encoding="utf-8") as f:
        lines = f.readlines()
        print(f"Total API calls: {len(lines)}")
        for l in lines[-5:]:
            d = json.loads(l)
            print("CALL:", d.get("timestamp"), d.get("model"), d.get("endpoint"), d.get("clip_id") or d.get("scene_id"), d.get("duration_seconds"))
