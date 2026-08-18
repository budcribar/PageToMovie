import os, json

bp_path = r"C:\Users\budcr\Videos\PageToMovie\budcribar\Mary18\blueprint.clips.grok.json"
if not os.path.exists(bp_path):
    bp_path = r"C:\Users\budcr\Videos\PageToMovie\budcribar\Mary18\artifacts\model_operations\stage2_shot_plan.json"

with open(bp_path, "r", encoding="utf-8") as f:
    d = json.load(f)

scenes = d.get("scenes", [])
for s in scenes:
    if s.get("scene_number") == 1:
        for c in s.get("clips", []):
            print(f"=== S{s.get('scene_number')} C{c.get('clip_number')} ===")
            print("Action/Visual:", c.get("visual_prompt"))
            print("Characters:", c.get("characters_on_screen"))
            print("Duration:", c.get("duration_seconds"))
