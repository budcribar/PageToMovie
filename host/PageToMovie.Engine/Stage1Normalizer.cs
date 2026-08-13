using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>Coerce Stage 1 JSON after LLM generation (schema cleanup).</summary>
public static class Stage1Normalizer
{
    private const string VisualEventKey = "visual_event";
    private const string CharacterSeedTokensKey = "character_seed_tokens";
    private const string LocationSeedTokensKey = "location_seed_tokens";
    private const string DescriptionKey = "description";
    private const string VisualLockKey = "visual_lock";
    private const string WardrobeAlwaysKey = "wardrobe_always";
    private const string MixedMode = "mixed";
    private const string MontageMode = "montage";
    private const string DreamMode = "dream";
    private const string DurationTargetSecondsKey = "duration_target_seconds";
    private const string SettingKey = "setting";

    private static readonly Dictionary<string, string> LocTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["interior"] = "int", ["int"] = "int", ["interior_only"] = "int",
        ["exterior"] = "ext", ["ext"] = "ext", ["exterior_only"] = "ext",
        [MixedMode] = MixedMode, ["interior_exterior_mix"] = MixedMode,
        ["interior/exterior"] = MixedMode, ["int/ext"] = MixedMode,
        ["montage_mix"] = MontageMode, [MontageMode] = MontageMode,
        ["flashback"] = "flashback", [DreamMode] = DreamMode, ["dreamscape"] = DreamMode,
    };

    public static Dictionary<string, object?> Normalize(Dictionary<string, object?> data)
    {
        if (data is null)
            data = new Dictionary<string, object?>();
        data = CleanNulls(data) as Dictionary<string, object?> ?? new();
        data["schema_version"] = "stage1.v1";
        ApplyMovieTitles(data);

        var gpv = GetOrCreateDict(data, "global_production_variables");
        ApplyGpvDefaults(gpv);

        var scenes = GetList(data, "scenes");
        ApplyRuntimeTarget(gpv, scenes);
        ApplySeedTokenDefaults(gpv);
        ApplyStyleLock(gpv);

        NormalizeCharacterSeeds(gpv);
        NormalizeLocationSeeds(gpv);

        foreach (var sObj in scenes.OfType<Dictionary<string, object?>>())
            NormalizeScene(sObj);

        data["cumulative_duration_target_seconds"] = SumSceneDurations(scenes);
        data["scenes"] = scenes;
        data["global_production_variables"] = gpv;
        return data;
    }

    private static void ApplyMovieTitles(Dictionary<string, object?> data)
    {
        data.TryGetValue("source_book_title", out var sbt);
        data.TryGetValue("movie_title", out var mt);
        data["movie_title"] = CoerceString(mt) is { Length: > 0 } mts ? mts : (CoerceString(sbt) ?? "Untitled");
        data["source_book_title"] = CoerceString(sbt) is { Length: > 0 } sbs ? sbs : data["movie_title"];
    }

    private static void ApplyGpvDefaults(Dictionary<string, object?> gpv)
    {
        gpv["target_aspect_ratio"] = gpv.TryGetValue("target_aspect_ratio", out var ar) && ar is not null
            ? ar : "16:9";
        gpv["resolution"] = gpv.TryGetValue("resolution", out var res) && res is not null ? res : "720p";
        gpv["frame_rate"] = ParseFrameRate(gpv.TryGetValue("frame_rate", out var fr) ? fr : 24);
        if (!gpv.TryGetValue("directorial_treatment", out var dirTreat) || dirTreat is null)
            gpv["directorial_treatment"] = "cinematic lighting, film grain, steady camera, high-contrast";
    }

    private static void ApplyRuntimeTarget(Dictionary<string, object?> gpv, List<object?> scenes)
    {
        if (gpv.TryGetValue("total_runtime_target_seconds", out var runtimeTarget) && runtimeTarget is not null)
            return;
        var sceneSum = SumSceneDurations(scenes);
        gpv["total_runtime_target_seconds"] = sceneSum > 0 ? sceneSum : 900;
    }

    private static void ApplySeedTokenDefaults(Dictionary<string, object?> gpv)
    {
        if (!gpv.TryGetValue(CharacterSeedTokensKey, out var charSeeds) || charSeeds is null)
            gpv[CharacterSeedTokensKey] = new Dictionary<string, object?>();
        if (!gpv.TryGetValue(LocationSeedTokensKey, out var locSeeds) || locSeeds is null)
            gpv[LocationSeedTokensKey] = new Dictionary<string, object?>();
    }

    private static void ApplyStyleLock(Dictionary<string, object?> gpv)
    {
        var treat = CoerceString(gpv.TryGetValue("directorial_treatment", out var dt) ? dt : "") ?? "";
        var rsl = CoerceString(ReadStyleLockRaw(gpv)) ?? "";
        if (string.IsNullOrWhiteSpace(rsl) &&
            CommonRegex.IsMatch(treat, @"styliz|animated|picture-book|cartoon|pixar|dreamworks|illustration|2d\b|3d\b",
                RegexOptions.IgnoreCase))
        {
            gpv["render_style_lock"] =
                "STYLE LOCK: stylized animated children's picture-book look for ALL on-screen " +
                "cast (animals and humans share the same medium) -- not photoreal, not live-action";
        }
        else if (!string.IsNullOrWhiteSpace(rsl))
        {
            gpv["render_style_lock"] = rsl;
        }
    }

    private static object? ReadStyleLockRaw(Dictionary<string, object?> gpv)
    {
        if (gpv.TryGetValue("render_style_lock", out var r1))
            return r1;
        if (gpv.TryGetValue("style_lock", out var r2))
            return r2;
        return null;
    }

    private static int SumSceneDurations(List<object?> scenes)
    {
        var total = 0;
        foreach (var s in scenes.OfType<Dictionary<string, object?>>())
            total += ToInt(s.TryGetValue("duration_target_seconds", out var d) ? d : 0);
        return total;
    }

    private static void NormalizeCharacterSeeds(Dictionary<string, object?> gpv)
    {
        var seeds = GetDict(gpv, "character_seed_tokens");
        foreach (var (key, val) in seeds.ToList())
        {
            if (val is not Dictionary<string, object?> seed) continue;
            NormalizeOneCharacterSeed(key, seed);
            seeds[key] = seed;
        }
        gpv["character_seed_tokens"] = seeds;
    }

    private static void NormalizeOneCharacterSeed(string key, Dictionary<string, object?> seed)
    {
        // Filmable identity only — strip nicknames + cross-species style bleed (any book)
        var rawDesc = CoerceString(seed.TryGetValue(DescriptionKey, out var d) ? d : null) ?? key;
        seed[DescriptionKey] = CharacterVisualTextScrubber.ScrubVisualProse(rawDesc);
        if (string.IsNullOrWhiteSpace(CoerceString(seed[DescriptionKey])))
            seed[DescriptionKey] = key;

        seed["reference_image_placeholder"] =
            CoerceString(seed.TryGetValue("reference_image_placeholder", out var ph) ? ph : null)
            ?? ProjectStore.CharacterRefFileName(key);
        seed["voice_profile"] =
            CoerceString(seed.TryGetValue("voice_profile", out var vp) ? vp : null)
            ?? "Consistent character voice every scene.";
        seed["voice_label"] =
            CoerceString(seed.TryGetValue("voice_label", out var vl) ? vl : null) ?? key;

        ApplyCharacterVisualPolicy(key, seed);
    }

    private static void ApplyCharacterVisualPolicy(string key, Dictionary<string, object?> seed)
    {
        // Voice-only = never appears on screen — from the cast seed's display_name_policy only,
        // NOT the name "Narrator". An on-camera / POV narrator (e.g. Tell-Tale Heart) is a real
        // character with a locked look; only a pure off-screen narrator (never_on_screen) has its
        // visual_lock/wardrobe stripped. Single source of truth: CastKindClassifier.IsVoiceOnlyPolicy.
        var pol = CoerceString(seed.TryGetValue("display_name_policy", out var polV) ? polV : null);
        if (CastKindClassifier.IsVoiceOnlyPolicy(pol))
        {
            seed.Remove(VisualLockKey);
            seed.Remove(WardrobeAlwaysKey);
            return;
        }

        ApplyOnScreenVisualLock(key, seed);
        var always = CharacterVisualTextScrubber.ScrubWardrobeList(
            CoerceStringList(seed.TryGetValue(WardrobeAlwaysKey, out var wa) ? wa : null));
        if (always.Count > 0)
            seed[WardrobeAlwaysKey] = always;
        else
            seed.Remove("wardrobe_always");
    }

    private static void ApplyOnScreenVisualLock(string key, Dictionary<string, object?> seed)
    {
        var vlck = CoerceString(seed.TryGetValue(VisualLockKey, out var v) ? v : null);
        if (string.IsNullOrWhiteSpace(vlck))
        {
            var desc = CoerceString(seed[DescriptionKey]) ?? key;
            seed[VisualLockKey] = desc.Length > 220 ? desc[..220] + "…" : desc;
            return;
        }

        seed[VisualLockKey] = CharacterVisualTextScrubber.ScrubVisualProse(vlck);
    }

    private static void NormalizeLocationSeeds(Dictionary<string, object?> gpv)
    {
        var seeds = GetDict(gpv, "location_seed_tokens");
        foreach (var (key, val) in seeds.ToList())
        {
            if (val is not Dictionary<string, object?> seed) continue;
            seed["display_name"] =
                CoerceString(seed.TryGetValue("display_name", out var dn) ? dn : null) ?? key;
            seed[DescriptionKey] =
                CoerceString(seed.TryGetValue(DescriptionKey, out var d) ? d : null)
                ?? seed["display_name"];
            seed["visual_lock"] =
                CoerceString(seed.TryGetValue("visual_lock", out var v) ? v : null)
                ?? seed[DescriptionKey];
            seeds[key] = seed;
        }
        gpv["location_seed_tokens"] = seeds;
    }

    private static void NormalizeScene(Dictionary<string, object?> s)
    {
        ApplySceneIdentityAndDuration(s);
        ApplySceneLocationIds(s);
        ApplySceneWardrobeByCharacter(s);

        foreach (var b in GetList(s, "story_beats").OfType<Dictionary<string, object?>>())
            NormalizeBeat(b);
    }

    private static void ApplySceneIdentityAndDuration(Dictionary<string, object?> s)
    {
        var sn = ToInt(s.TryGetValue("scene_number", out var n) ? n : 0);
        s["scene_filename"] =
            CoerceString(s.TryGetValue("scene_filename", out var sf) ? sf : null)
            ?? $"Scene_{sn:D2}";
        s[SettingKey] = CoerceString(s.TryGetValue(SettingKey, out var set) ? set : null) ?? "";
        s["story_day"] = NormStoryDay(s.TryGetValue("story_day", out var sd) ? sd : null);
        s["location_type"] = NormLocationType(s.TryGetValue("location_type", out var lt) ? lt : null);
        var dur = ToInt(s.TryGetValue(DurationTargetSecondsKey, out var d) ? d : 24);
        s[DurationTargetSecondsKey] = Math.Clamp(dur <= 0 ? 24 : dur, 8, 134);
        ApplySceneTextFields(s, sn);
    }

    private static void ApplySceneTextFields(Dictionary<string, object?> s, int sn)
    {
        s["dramatic_function"] =
            CoerceString(s.TryGetValue("dramatic_function", out var df) ? df : null) ?? "";
        s["summary"] =
            CoerceString(s.TryGetValue("summary", out var sum) ? sum : null)
            ?? DefaultSceneSummary(s, sn);
        s["transition_type"] =
            CoerceString(s.TryGetValue("transition_type", out var tt) ? tt : null) ?? "cut";
        s["lighting_continuity_token"] =
            CoerceString(s.TryGetValue("lighting_continuity_token", out var lc) ? lc : null)
            ?? "consistent scene lighting";
        if (!s.TryGetValue("story_beats", out var beats) || beats is null)
            s["story_beats"] = new List<object?>();
        s["music_intent"] = NormMusic(s.TryGetValue("music_intent", out var mi) ? mi : null);
    }

    private static string DefaultSceneSummary(Dictionary<string, object?> s, int sn)
    {
        if (s[SettingKey] is string settingStr && !string.IsNullOrEmpty(settingStr))
            return settingStr;
        return $"Scene {sn}";
    }

    private static void ApplySceneLocationIds(Dictionary<string, object?> s)
    {
        var lids = s.TryGetValue("location_ids", out var lidsRaw) ? lidsRaw : null;
        var lidList = new List<string>();
        if (lids is string one) lidList.Add(one);
        else if (lids is List<object?> list)
            lidList.AddRange(CoerceLocationIdList(list));
        s["location_ids"] = lidList;
        if (lidList.Count > 0 &&
            string.IsNullOrEmpty(CoerceString(s.TryGetValue("primary_location_id", out var pl) ? pl : null)))
            s["primary_location_id"] = lidList[0];
    }

    private static List<string> CoerceLocationIdList(List<object?> list)
    {
        var lidList = new List<string>();
        foreach (var x in list)
        {
            var id = CoerceString(x) ?? "";
            if (!string.IsNullOrEmpty(id))
                lidList.Add(id);
        }
        return lidList;
    }

    private static void ApplySceneWardrobeByCharacter(Dictionary<string, object?> s)
    {
        if (!s.TryGetValue("wardrobe_by_character", out var wbc) ||
            wbc is not Dictionary<string, object?> wmap)
            return;

        foreach (var (ck, items) in wmap.ToList())
        {
            var cleaned = CharacterVisualTextScrubber.ScrubWardrobeList(CoerceStringList(items));
            if (cleaned.Count > 0)
                wmap[ck] = cleaned;
            else
                wmap.Remove(ck);
        }
        s["wardrobe_by_character"] = wmap;
    }

    private static void NormalizeBeat(Dictionary<string, object?> b)
    {
        b["beat_id"] = CoerceString(b.TryGetValue("beat_id", out var bi) ? bi : null) ?? "b1";
        b["intent"] = CoerceString(b.TryGetValue("intent", out var intent) ? intent : null) ?? "";
        b[VisualEventKey] =
            CoerceString(b.TryGetValue(VisualEventKey, out var ve) ? ve : null)
            ?? (string)b["intent"]!;
        // visual_event may mention nicknames from the book VO — only scrub clear food-hat junk
        if (b[VisualEventKey] is string veStr &&
            CharacterVisualTextScrubber.LooksLikeNicknameVisualJunk(veStr))
            b[VisualEventKey] = CharacterVisualTextScrubber.ScrubVisualProse(veStr);
        b["shot_scale_hint"] =
            CoerceString(b.TryGetValue("shot_scale_hint", out var ssh) ? ssh : null) ?? "ms";
        b["continuity"] =
            CoerceString(b.TryGetValue("continuity", out var c) ? c : null) ?? "new_setup";
        foreach (var leak in new[] { "visual_prompt", "negative_prompt", "timestamp", "veo_continuation_source" })
            b.Remove(leak);

        ApplyBeatWardrobeKeys(b);
        NormalizeBeatAudioKeys(b);
    }

    private static void ApplyBeatWardrobeKeys(Dictionary<string, object?> b)
    {
        foreach (var wkey in new[] { "wardrobe_put_on", "wardrobe_remove" })
        {
            if (!b.TryGetValue(wkey, out var wraw)) continue;
            var cleaned = CharacterVisualTextScrubber.ScrubWardrobeList(CoerceStringList(wraw));
            if (cleaned.Count > 0)
                b[wkey] = cleaned;
            else
                b.Remove(wkey);
        }
    }

    /// <summary>
    /// Ensure separate <c>ambient</c> + <c>sfx</c> (root and nested audio).
    /// Separate <c>ambient</c> + <c>sfx</c> only; strips combined fields if present.
    /// </summary>
    public static void NormalizeBeatAudioKeys(Dictionary<string, object?> beat)
    {
        Dictionary<string, object?>? nested = null;
        if (beat.TryGetValue("audio", out var a) && a is Dictionary<string, object?> ad)
            nested = ad;

        var ambient = ReadBeatAudioValue(nested, beat, "ambient");
        var sfx = ReadBeatAudioValue(nested, beat, "sfx");
        (ambient, sfx) = InferBeatAmbientSfxIfEmpty(beat, ambient, sfx);

        beat["ambient"] = ambient;
        beat["sfx"] = sfx;
        beat.Remove("ambient_or_sfx");
        beat["audio"] = WriteBeatAudioNested(nested, beat, ambient, sfx);
    }

    private static string ReadBeatAudioValue(
        Dictionary<string, object?>? nested, Dictionary<string, object?> beat, string key)
    {
        if (nested is not null && nested.TryGetValue(key, out var nv))
        {
            var nestedVal = CoerceString(nv);
            if (!string.IsNullOrWhiteSpace(nestedVal))
                return nestedVal.Trim();
        }
        if (beat.TryGetValue(key, out var rv))
        {
            var beatVal = CoerceString(rv);
            if (!string.IsNullOrWhiteSpace(beatVal))
                return beatVal.Trim();
        }
        return "";
    }

    private static (string Ambient, string Sfx) InferBeatAmbientSfxIfEmpty(
        Dictionary<string, object?> beat, string ambient, string sfx)
    {
        if (!string.IsNullOrWhiteSpace(ambient) || !string.IsNullOrWhiteSpace(sfx))
            return (ambient, sfx);
        var ve = CoerceString(beat.TryGetValue(VisualEventKey, out var vev) ? vev : null) ?? "";
        var inferred = FountainStage1Importer.InferAmbientAndSfx(ve);
        return (inferred.Ambient, inferred.Sfx);
    }

    private static Dictionary<string, object?> WriteBeatAudioNested(
        Dictionary<string, object?>? nested, Dictionary<string, object?> beat, string ambient, string sfx)
    {
        nested ??= new Dictionary<string, object?>();
        nested["ambient"] = ambient;
        nested["sfx"] = sfx;
        if (!nested.ContainsKey("delivery"))
            nested["delivery"] = CoerceString(beat.TryGetValue("delivery", out var d) ? d : null) ?? "none";
        if (!nested.ContainsKey("speaker"))
            nested["speaker"] = CoerceString(beat.TryGetValue("speaker", out var sp) ? sp : null) ?? "";
        if (!nested.ContainsKey("dialogue"))
            nested["dialogue"] = CoerceString(beat.TryGetValue("dialogue", out var dlg) ? dlg : null) ?? "";
        nested.Remove("ambient_or_sfx");
        return nested;
    }

    private static string NormLocationType(object? v)
    {
        var s = (CoerceString(v) ?? MixedMode).Trim().ToLowerInvariant().Replace(' ', '_');
        if (LocTypeMap.TryGetValue(s, out var mapped)) return mapped;
        if (s.Contains("flash")) return "flashback";
        if (s.Contains(DreamMode)) return DreamMode;
        if (s.Contains(MontageMode)) return MontageMode;
        if (s.Contains("ext") && s.Contains("int")) return MixedMode;
        if (s.StartsWith("ext")) return "ext";
        if (s.StartsWith("int")) return "int";
        return MixedMode;
    }

    private static string NormStoryDay(object? v)
    {
        if (v is null) return "unspecified";
        if (v is long or int or double)
        {
            var n = Convert.ToInt32(v);
            return n <= 0 ? "Flashback / unspecified day" : $"Day {n}";
        }
        var s = CoerceString(v);
        return string.IsNullOrWhiteSpace(s) ? "unspecified" : s;
    }

    private static Dictionary<string, object?> NormMusic(object? raw)
    {
        if (raw is not Dictionary<string, object?> d)
            return new Dictionary<string, object?> { ["style_description"] = "cinematic underscore" };
        if (string.IsNullOrWhiteSpace(CoerceString(d.TryGetValue("style_description", out var sd) ? sd : null)))
        {
            d["style_description"] =
                CoerceString(d.TryGetValue("style", out var st) ? st : null)
                ?? CoerceString(d.TryGetValue(DescriptionKey, out var desc) ? desc : null)
                ?? CoerceString(d.TryGetValue("genre", out var g) ? g : null)
                ?? "cinematic underscore";
        }
        return d;
    }

    public static List<string> CoerceStringList(object? val, int maxItems = 12)
    {
        var raw = CollectCoerceStringListRaw(val);
        var outList = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in raw)
        {
            if (!TryAddCoercedListItem(item, maxItems, seen, outList))
                break;
        }
        return outList;
    }

    private static List<string> CollectCoerceStringListRaw(object? val)
    {
        var raw = new List<string>();
        if (val is null)
            return raw;
        if (val is string s)
        {
            raw.AddRange(CommonRegex.Split(s, @"\s+and\s+|[,;|/]")
                .Select(p => p.Trim())
                .Where(p => p.Length > 0));
            return raw;
        }
        if (val is not List<object?> list)
            return raw;
        foreach (var x in list)
        {
            var t = CoerceString(x);
            if (!string.IsNullOrWhiteSpace(t))
                raw.Add(t);
        }
        return raw;
    }

    private static bool TryAddCoercedListItem(
        string item, int maxItems, HashSet<string> seen, List<string> outList)
    {
        var cleaned = CommonRegex.Replace(item, @"\s+", " ").Trim(' ', '.', ',', ';', ':');
        if (cleaned.Length < 2)
            return true;
        if (!seen.Add(cleaned.ToLowerInvariant()))
            return true;
        outList.Add(cleaned.Length > 80 ? cleaned[..80] : cleaned);
        return outList.Count < maxItems;
    }

    private static object? CleanNulls(object? obj)
    {
        if (obj is Dictionary<string, object?> d)
        {
            var n = new Dictionary<string, object?>();
            foreach (var (k, v) in d)
            {
                if (v is null) continue;
                n[k] = CleanNulls(v);
            }
            return n;
        }
        if (obj is List<object?> list)
            return list.Select(CleanNulls).ToList();
        return obj;
    }

    private static Dictionary<string, object?> GetOrCreateDict(Dictionary<string, object?> d, string key)
    {
        if (d.TryGetValue(key, out var v) && v is Dictionary<string, object?> existing)
            return existing;
        var n = new Dictionary<string, object?>();
        d[key] = n;
        return n;
    }

    private static Dictionary<string, object?> GetDict(Dictionary<string, object?> d, string key) =>
        GetOrCreateDict(d, key);

    private static List<object?> GetList(Dictionary<string, object?> d, string key)
    {
        if (d.TryGetValue(key, out var v) && v is List<object?> list) return list;
        var n = new List<object?>();
        d[key] = n;
        return n;
    }

    private static int ParseFrameRate(object? fr)
    {
        if (fr is int i) return i;
        if (fr is long l) return (int)l;
        if (fr is double db) return (int)db;
        var s = CoerceString(fr) ?? "24";
        var m = CommonRegex.Match(s, @"\d+");
        return m.Success ? int.Parse(m.Value) : 24;
    }

    private static int ToInt(object? v)
    {
        try
        {
            return v switch
            {
                null => 0,
                int i => i,
                long l => (int)l,
                double d => (int)d,
                string s when int.TryParse(s, out var n) => n,
                _ => Convert.ToInt32(v),
            };
        }
        catch { return 0; }
    }

    private static string? CoerceString(object? v) => v switch
    {
        null => null,
        string s => s,
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
        _ => v.ToString(),
    };
}
