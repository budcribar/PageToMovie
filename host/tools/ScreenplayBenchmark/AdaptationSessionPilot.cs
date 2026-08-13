using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using PageToMovie.Engine;

using PageToMovie.Core.Utils;
namespace ScreenplayBenchmark;

/// <summary>
/// Benchmark-only prototype of the staged adaptation-session pipeline described in
/// <c>host/docs/adaptation-session-pipeline.md</c>:
///
///   Book retained in adaptation session
///     → source-grounded beat plan
///     → Fountain screenplay
///     → validate and repair Fountain
///     → EDL / shot plan
///     → cast, wardrobe, and locations
///     → audio plan
///
/// Every stage after the first is a small follow-up call using xAI's <c>previous_response_id</c>
/// chaining (<see cref="XaiResponsesClient"/>) — the book is uploaded and attached exactly once.
/// Media (image/audio/video) generation is intentionally out of scope: only descriptions.
///
/// This is not wired into <c>PageToMovie.Engine</c>/<c>PageToMovie.Api</c> — it exists to prove the
/// staged approach out (scene coverage, dialogue coverage, cross-file reconciliation, and real
/// per-call payload size) before any product change is proposed.
/// </summary>
public static class AdaptationSessionPilot
{
    private sealed class SessionRecord
    {
        public string Provider { get; set; } = "xai";
        public string Model { get; set; } = "";
        public string? PromptRevision { get; set; }
        public string BookSha256 { get; set; } = "";
        public string FileId { get; set; } = "";
        public long? ExpiresAtUnixSeconds { get; set; }
        public string CreatedAtUtc { get; set; } = "";
        public Dictionary<string, string> StageResponseIds { get; set; } = new();

        /// <summary>
        /// Generation temperature this session's cached stages were produced at. A session created
        /// before temperature control existed has this null — treated the same as "different value"
        /// below, so an unknown-temperature artifact is never silently mixed with a controlled one.
        /// </summary>
        public double? Temperature { get; set; }
        public double? JudgeTemperature { get; set; }
        public int? TargetRuntimeMinutes { get; set; }

        /// <summary>What was actually uploaded (raw text vs. paragraph-tagged) — bumped whenever the
        /// upload format changes, so an old raw-text upload is never silently reused for prompts that
        /// now expect [P#] citations to exist. Unlike temperature/runtime, this also forces a real
        /// re-upload, not just cleared downstream stages.</summary>
        public int? UploadFormatVersion { get; set; }

        /// <summary>Separate upload of the approved, scene-tagged Fountain — only populated when the
        /// dual-attach experiment runs. Keyed to a hash of the Fountain text so a regenerated
        /// screenplay doesn't silently reuse a stale upload.</summary>
        public string? FountainFileId { get; set; }
        public string? FountainFileSha256 { get; set; }

        /// <summary>Separate from <see cref="FountainFileId"/> — the full dual-attach pipeline
        /// generates its OWN Fountain (never mixed with the chained pipeline's), so its upload is
        /// tracked independently.</summary>
        public string? FountainFileIdAlt { get; set; }
        public string? FountainFileShaAlt { get; set; }
    }

    private sealed class PilotRunState
    {
        public XaiResponsesClient Client { get; }
        public SessionRecord Session { get; }
        public string SessionPath { get; }
        public string Model { get; }
        public double Temperature { get; }
        public double JudgeTemperature { get; }
        public int TargetRuntimeMinutes { get; }
        public string WorkspaceRoot { get; }
        public string OutDir { get; }
        public string BookSlug { get; }
        public string BookSha256 { get; }
        public List<string> Paragraphs { get; }
        public string IndexedBookPath { get; }
        public int DurMinSeconds { get; }
        public int DurMaxSeconds { get; }
        public int DurAbsMaxSeconds { get; }
        public int SceneMin { get; }
        public int SceneMax { get; }
        public CancellationToken Ct { get; }
        public Dictionary<string, (long Input, long Output, long Cached)> TokensByModel { get; } = new();
        public long TotalRequestBytes { get; set; }
        public string LastResponseId { get; set; } = "";

        public PilotRunState(
            XaiResponsesClient client,
            SessionRecord session,
            string sessionPath,
            string model,
            double temperature,
            double judgeTemperature,
            int targetRuntimeMinutes,
            string workspaceRoot,
            string outDir,
            string bookSlug,
            string bookSha256,
            List<string> paragraphs,
            string indexedBookPath,
            int durMinSeconds,
            int durMaxSeconds,
            int durAbsMaxSeconds,
            int sceneMin,
            int sceneMax,
            CancellationToken ct)
        {
            Client = client;
            Session = session;
            SessionPath = sessionPath;
            Model = model;
            Temperature = temperature;
            JudgeTemperature = judgeTemperature;
            TargetRuntimeMinutes = targetRuntimeMinutes;
            WorkspaceRoot = workspaceRoot;
            OutDir = outDir;
            BookSlug = bookSlug;
            BookSha256 = bookSha256;
            Paragraphs = paragraphs;
            IndexedBookPath = indexedBookPath;
            DurMinSeconds = durMinSeconds;
            DurMaxSeconds = durMaxSeconds;
            DurAbsMaxSeconds = durAbsMaxSeconds;
            SceneMin = sceneMin;
            SceneMax = sceneMax;
            Ct = ct;
        }

        public void TrackGeneration(XaiResponsesClient.SessionTurnResult result)
        {
            TotalRequestBytes += result.RequestBytesSent;
            TrackUsage(TokensByModel, Model, result.UsageJson);
        }
    }

    private sealed class DualAttachRunState
    {
        public Dictionary<string, (long Input, long Output, long Cached)> TokensByModelAlt { get; } = new();
        public int TotalBytesAlt { get; set; }
    }

    /// <summary>Bump when <see cref="BuildIndexedBookText"/> or what gets uploaded changes shape.</summary>
    private const int CurrentUploadFormatVersion = 2;

    public static async Task<int> RunAsync(
        string bookPath,
        string? bookSlugArg,
        string model,
        int targetRuntimeMinutes,
        string workspaceRoot,
        string promptRevision,
        CancellationToken ct,
        string? judgeModel = null,
        double temperature = 0.2,
        double judgeTemperature = 0.0,
        bool clipShotPlan = false,
        bool dualAttachClipPlan = false,
        bool dualAttachAll = true,
        string? judgeModel2 = null,
        string? videoModel = null)
    {
        if (!File.Exists(bookPath))
        {
            Console.WriteLine($"❌ Error: book file not found: {bookPath}");
            return 1;
        }

        var modelValidationExit = ValidateModelsAndJudges(model, judgeModel, judgeModel2);
        if (modelValidationExit is { } exitCode) return exitCode;

        // Resolved once per run from the actual target video model — never a hardcoded constant.
        // Different video models have different max clip lengths; ResolveBoundsForModel falls back
        // to ClipDurationEstimator's generic defaults for any model without catalog-specific bounds
        // (true for every video model in models_catalog.json today, but the wiring is model-aware
        // the moment those catalog fields get populated).
        var (durMinSeconds, durMaxSeconds, durAbsMaxSeconds) = ClipDurationEstimator.ResolveBoundsForModel(videoModel);

        var bookSlug = string.IsNullOrWhiteSpace(bookSlugArg)
            ? Path.GetFileNameWithoutExtension(bookPath).ToLowerInvariant()
            : bookSlugArg.ToLowerInvariant();

        var outDir = Path.Combine(workspaceRoot, "evals", "adaptation_sessions", bookSlug);
        Directory.CreateDirectory(outDir);

        Console.WriteLine("==========================================================================");
        Console.WriteLine($" Adaptation-session pilot — {bookSlug} — model={model} — target={targetRuntimeMinutes}min");
        Console.WriteLine("==========================================================================");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        var client = new XaiResponsesClient(http);
        if (!client.IsConfigured)
        {
            Console.WriteLine("❌ Error: XAI_API_KEY is not set. This pilot makes real, paid xAI calls — no fake mode.");
            return 1;
        }

        var bookBytes = await File.ReadAllBytesAsync(bookPath, ct).ConfigureAwait(false);
        // Hash the ORIGINAL raw bytes for change detection (session invalidation) — the indexed
        // text derived from it below is what actually gets uploaded, but "did the source book
        // change" must still mean the real source text, not our own paragraph tags.
        var bookSha256 = Convert.ToHexStringLower(SHA256.HashData(bookBytes));

        // Paragraph-index the book LOCALLY (no LLM cost) so every downstream stage can cite an exact
        // paragraph id instead of a free-text quote — a quote can occur more than once (or nearly so)
        // in a book, making after-the-fact text search an unreliable way to locate it. Citing a number
        // the model saw directly on its own attached copy has no such ambiguity.
        var paragraphs = SplitBookIntoParagraphs(System.Text.Encoding.UTF8.GetString(bookBytes));
        var indexedBookText = BuildIndexedBookText(paragraphs);
        var indexedBookPath = Path.Combine(outDir, "book_indexed.txt");
        await File.WriteAllTextAsync(indexedBookPath, indexedBookText, ct).ConfigureAwait(false);

        var sessionPath = Path.Combine(outDir, "session.json");
        var session = await EnsureBookSessionUploadedAsync(
            client, sessionPath, indexedBookPath, paragraphs.Count, bookSha256, model, promptRevision, ct)
            .ConfigureAwait(false);

        InvalidateSessionOnSettingsChange(
            session, sessionPath, outDir, model, promptRevision, temperature, judgeTemperature, targetRuntimeMinutes);
        session.Model = model;
        session.PromptRevision = promptRevision;
        session.Temperature = temperature;
        session.JudgeTemperature = judgeTemperature;
        session.TargetRuntimeMinutes = targetRuntimeMinutes;
        SaveSession(sessionPath, session);
        Console.WriteLine($"🌡️  temperature={temperature} judge_temperature={judgeTemperature} target_runtime_minutes={targetRuntimeMinutes}");

        var (sceneMin, sceneMax) = TargetSceneBand(targetRuntimeMinutes);
        var state = new PilotRunState(
            client, session, sessionPath, model, temperature, judgeTemperature, targetRuntimeMinutes,
            workspaceRoot, outDir, bookSlug, bookSha256, paragraphs, indexedBookPath,
            durMinSeconds, durMaxSeconds, durAbsMaxSeconds, sceneMin, sceneMax, ct);

        var beatPlanJson = await RunBeatPlanStageAsync(state).ConfigureAwait(false);
        var castLocationJson = await RunCastLocationsStageAsync(state, beatPlanJson).ConfigureAwait(false);
        var fountainText = await RunFountainStageAsync(state, beatPlanJson, castLocationJson).ConfigureAwait(false);
        fountainText = await RunFountainRepairLoopAsync(state, fountainText).ConfigureAwait(false);

        if (dualAttachAll)
        {
            await RunDualAttachFullPipelineAsync(
                client, session, sessionPath, model, temperature, judgeModel, judgeModel2, judgeTemperature,
                beatPlanJson, workspaceRoot, targetRuntimeMinutes, sceneMin, sceneMax, paragraphs.Count, outDir,
                durMinSeconds, durMaxSeconds, durAbsMaxSeconds, ct)
                .ConfigureAwait(false);
        }

        var edlJson = await RunEdlStageAsync(state, fountainText, castLocationJson).ConfigureAwait(false);
        var clipPlanJson = clipShotPlan
            ? await RunChainedClipShotPlanStageAsync(state, edlJson, fountainText, beatPlanJson).ConfigureAwait(false)
            : null;

        if (dualAttachClipPlan)
            await RunDualAttachClipPlanExperimentAsync(state, edlJson, fountainText, beatPlanJson, castLocationJson)
                .ConfigureAwait(false);

        var audioJson = await RunAudioPlanStageAsync(state, edlJson, clipPlanJson).ConfigureAwait(false);

        // ---- Stage 7 (optional): one or two independent, book-attached LLM judges ----
        // Unlike the existing multi-model peer-judge path (ScreenplayJudgmentRubric.BuildPrompt,
        // Program.cs), which embeds the FULL book text in every judge call for every judge model on
        // every run, each judge here attaches the book file directly (small, cached-hit-friendly)
        // rather than inlining it. Deliberately an independent CompleteWithFilesAsync — NOT a
        // continuation of the generation session — so a judge never inherits chain memory/bias from
        // having produced the content itself. judgeModel2 supplies a second independent xAI judge.
        var judgeResults = await RunJudgesAsync(
            client, new[] { judgeModel, judgeModel2 }, new[] { session.FileId }, fountainText, judgeTemperature,
            outDir, "judge_review", "Stage 7",
            (r, m) => { state.TotalRequestBytes += r.RequestBytesSent; TrackUsage(state.TokensByModel, m, r.UsageJson); },
            ct).ConfigureAwait(false);

        var citationWarnings = ValidateParagraphCitations(edlJson, paragraphs.Count);
        var citingScenesByParagraph = BuildCitingScenesByParagraph(edlJson, paragraphs.Count);
        await WriteAnnotatedBookAsync(state, citingScenesByParagraph).ConfigureAwait(false);

        return await FinalizeChainedRunAsync(
            state, fountainText, edlJson, castLocationJson, audioJson, clipPlanJson, clipShotPlan, judgeResults)
            .ConfigureAwait(false);
    }

    private static int? ValidateModelsAndJudges(string model, string? judgeModel, string? judgeModel2)
    {
        if (!UsesXaiResponsesApi(model, out var modelError))
        {
            Console.WriteLine($"❌ Error: {modelError}");
            return 1;
        }
        foreach (var candidateJudge in new[] { judgeModel, judgeModel2 })
        {
            if (!string.IsNullOrWhiteSpace(candidateJudge) && !UsesXaiResponsesApi(candidateJudge, out var judgeError))
            {
                Console.WriteLine($"❌ Error: {judgeError}");
                Console.WriteLine("   This pilot's file-attached judge calls currently use the xAI Responses API; choose an enabled xAI chat judge.");
                return 1;
            }
        }
        return null;
    }

    private static async Task<SessionRecord> EnsureBookSessionUploadedAsync(
        XaiResponsesClient client,
        string sessionPath,
        string indexedBookPath,
        int paragraphCount,
        string bookSha256,
        string model,
        string promptRevision,
        CancellationToken ct)
    {
        var session = LoadSession(sessionPath);
        if (session is not null && session.BookSha256 == bookSha256 && !IsExpired(session.ExpiresAtUnixSeconds) &&
            session.UploadFormatVersion == CurrentUploadFormatVersion)
        {
            Console.WriteLine($"📎 Reusing uploaded book: file_id={session.FileId} (no re-upload — proves the no-double-upload rule)");
            return session;
        }

        if (session is not null && session.UploadFormatVersion != CurrentUploadFormatVersion)
            Console.WriteLine($"⚠️  Upload format changed (v{session.UploadFormatVersion?.ToString() ?? "none"} -> v{CurrentUploadFormatVersion}) — forcing a fresh upload.");
        Console.WriteLine($"📤 Uploading paragraph-indexed book ({paragraphCount} paragraphs, first and only upload for this session)...");
        var upload = await client.UploadBookAsync(indexedBookPath, ct: ct).ConfigureAwait(false);
        session = new SessionRecord
        {
            Model = model,
            PromptRevision = promptRevision,
            BookSha256 = bookSha256,
            FileId = upload.FileId,
            ExpiresAtUnixSeconds = upload.ExpiresAtUnixSeconds,
            CreatedAtUtc = DateTime.UtcNow.ToString("O"),
            UploadFormatVersion = CurrentUploadFormatVersion,
        };
        SaveSession(sessionPath, session);
        Console.WriteLine($"   file_id={upload.FileId} bytes={upload.Bytes} expires_at={upload.ExpiresAtUnixSeconds}");
        return session;
    }

    private static void InvalidateSessionOnSettingsChange(
        SessionRecord session,
        string sessionPath,
        string outDir,
        string model,
        string promptRevision,
        double temperature,
        double judgeTemperature,
        int targetRuntimeMinutes)
    {
        // A model, prompt revision, temperature, or target-runtime change invalidates every cached generation stage (not just
        // the ones that differ) — a cached beat plan built for a 10-minute target is wrong context
        // for a 16-minute request, and reusing an unknown/different-temperature artifact alongside
        // newly-controlled ones would silently defeat the point of controlling it. The book
        // upload/file_id is unaffected by either (neither has any bearing on that).
        if (session.StageResponseIds.Count > 0 &&
            (!string.Equals(session.Model, model, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(session.PromptRevision, promptRevision, StringComparison.OrdinalIgnoreCase) ||
             session.Temperature != temperature || session.JudgeTemperature != judgeTemperature ||
             session.TargetRuntimeMinutes != targetRuntimeMinutes))
        {
            Console.WriteLine(
                $"⚠️  Settings changed (model {session.Model} -> {model}, prompt {session.PromptRevision ?? "none"} -> {promptRevision}, temperature {Fmt(session.Temperature)} -> {temperature}, judge " +
                $"{Fmt(session.JudgeTemperature)} -> {judgeTemperature}, target_runtime_minutes " +
                $"{session.TargetRuntimeMinutes?.ToString() ?? "none"} -> {targetRuntimeMinutes}) — invalidating " +
                "all cached generation stages so results stay comparable under one controlled setting.");
            session.StageResponseIds.Clear();
            InvalidateDualAttachArtifactCache(outDir, session);
        }
    }

    private static async Task<string> RunBeatPlanStageAsync(PilotRunState state)
    {
        // ---- Stage 1: source-grounded beat plan (first turn — attaches the file) ----
        var beatPlanPath = Path.Combine(state.OutDir, "adaptation_plan.json");
        if (state.Session.StageResponseIds.TryGetValue("beat_plan", out var cachedBeatResp) && File.Exists(beatPlanPath))
        {
            Console.WriteLine("♻️  Stage 1 (beat plan): reusing cached artifact.");
            state.LastResponseId = cachedBeatResp;
            return await File.ReadAllTextAsync(beatPlanPath, state.Ct).ConfigureAwait(false);
        }

        Console.WriteLine("🧭 Stage 1: source-grounded beat plan...");
        var beatInstruction = BuildBeatPlanInstruction(state.TargetRuntimeMinutes, state.SceneMin, state.SceneMax);
        var result = await state.Client.StartSessionAsync(
            state.Model, state.Session.FileId, beatInstruction, state.Ct, state.Temperature).ConfigureAwait(false);
        state.TrackGeneration(result);
        var beatPlanJson = ExtractJson(result.OutputText);
        await File.WriteAllTextAsync(beatPlanPath, PrettyJson(beatPlanJson), state.Ct).ConfigureAwait(false);
        state.LastResponseId = result.ResponseId;
        state.Session.StageResponseIds["beat_plan"] = state.LastResponseId;
        SaveSession(state.SessionPath, state.Session);
        Console.WriteLine($"   response_id={state.LastResponseId} request_bytes={result.RequestBytesSent} (book NOT resent)");
        return beatPlanJson;
    }

    private static async Task<string> RunCastLocationsStageAsync(PilotRunState state, string beatPlanJson)
    {
        // ---- Stage 2: cast, wardrobe, and locations (derived from the beat plan, BEFORE Fountain) ----
        // Reordered from the original design (which derived these AFTER the EDL) so the Fountain
        // stage can be handed already-established identities/places instead of being asked to both
        // invent and write them — this is what actually lets the shared book_to_fountain.txt's
        // CAST LOOKS & VOICES / LOCATIONS sections shrink to "apply consistently" rather than
        // "figure out and invent." Variants are tied to beat IDs here (no scene IDs exist yet).
        var castLocationPath = Path.Combine(state.OutDir, "cast_and_locations.json");
        if (state.Session.StageResponseIds.TryGetValue("cast_locations", out var cachedClResp) && File.Exists(castLocationPath))
        {
            Console.WriteLine("♻️  Stage 2 (cast/wardrobe/locations): reusing cached artifact.");
            state.LastResponseId = cachedClResp;
            return await File.ReadAllTextAsync(castLocationPath, state.Ct).ConfigureAwait(false);
        }

        Console.WriteLine("\uD83E\uDDD1\u200D\uD83C\uDFA4 Stage 2: cast, wardrobe, and locations...");
        var instruction = BuildCastLocationsInstruction(beatPlanJson);
        var result = await state.Client.ContinueSessionAsync(
            state.Model, state.LastResponseId, instruction, state.Ct, state.Temperature).ConfigureAwait(false);
        state.TrackGeneration(result);
        var castLocationJson = ExtractJson(result.OutputText);
        state.LastResponseId = result.ResponseId;

        // Corrective retry: a real Call of the Wild run showed the model can occasionally
        // deviate from the instructed {"cast_seeds":{...},"location_bible":{...}} shape (e.g.
        // a bare array of characters, no location_bible at all) — no exception, just missing
        // data. ExtractJson/ParseCastAndLocationKeys were hardened to no longer corrupt what
        // DOES come back, but a defensive parse can't recover data the model never produced;
        // asking it to look at its own output and reformat can. One retry, same session (cheap
        // follow-up, not a resend of the book), before accepting whatever's available.
        for (var attempt = 1; attempt < CastLocationsMaxAttempts && !HasValidCastLocationsShape(castLocationJson); attempt++)
        {
            Console.WriteLine(
                $"   ⚠️  Response missing cast_seeds/location_bible — requesting a corrected reformat (attempt {attempt + 1}/{CastLocationsMaxAttempts})...");
            var retryResult = await state.Client.ContinueSessionAsync(
                state.Model, state.LastResponseId, CastLocationsCorrectionInstruction, state.Ct, state.Temperature)
                .ConfigureAwait(false);
            state.TrackGeneration(retryResult);
            castLocationJson = ExtractJson(retryResult.OutputText);
            state.LastResponseId = retryResult.ResponseId;
        }
        if (!HasValidCastLocationsShape(castLocationJson))
            Console.WriteLine("   ⚠️  Cast/locations still incomplete after retry — proceeding with what's available (validator will flag any gaps).");

        await File.WriteAllTextAsync(castLocationPath, PrettyJson(castLocationJson), state.Ct).ConfigureAwait(false);
        state.Session.StageResponseIds["cast_locations"] = state.LastResponseId;
        SaveSession(state.SessionPath, state.Session);
        Console.WriteLine($"   response_id={state.LastResponseId} request_bytes={result.RequestBytesSent} (book NOT resent)");
        return castLocationJson;
    }

    private static async Task<string> RunFountainStageAsync(
        PilotRunState state, string beatPlanJson, string castLocationJson)
    {
        // ---- Stage 3: Fountain screenplay (fed the approved beat plan AND cast/locations) ----
        var fountainPath = Path.Combine(state.OutDir, "screenplay.fountain");
        if (state.Session.StageResponseIds.TryGetValue("fountain", out var cachedFountainResp) && File.Exists(fountainPath))
        {
            Console.WriteLine("♻️  Stage 3 (Fountain): reusing cached artifact.");
            state.LastResponseId = cachedFountainResp;
            return await File.ReadAllTextAsync(fountainPath, state.Ct).ConfigureAwait(false);
        }

        Console.WriteLine("🎬 Stage 3: Fountain screenplay...");
        var fountainInstruction = BuildFountainInstruction(
            state.WorkspaceRoot, state.TargetRuntimeMinutes, beatPlanJson, castLocationJson);
        var result = await state.Client.ContinueSessionAsync(
            state.Model, state.LastResponseId, fountainInstruction, state.Ct, state.Temperature).ConfigureAwait(false);
        state.TrackGeneration(result);
        var fountainText = StripFences(result.OutputText);
        await File.WriteAllTextAsync(fountainPath, fountainText, state.Ct).ConfigureAwait(false);
        state.LastResponseId = result.ResponseId;
        state.Session.StageResponseIds["fountain"] = state.LastResponseId;
        SaveSession(state.SessionPath, state.Session);
        Console.WriteLine($"   response_id={state.LastResponseId} request_bytes={result.RequestBytesSent} (book NOT resent)");
        return fountainText;
    }

    private static async Task<string> RunFountainRepairLoopAsync(PilotRunState state, string fountainText)
    {
        // ---- Stage 4: validate + repair Fountain (local gate, cap 2 repairs) ----
        var fountainPath = Path.Combine(state.OutDir, "screenplay.fountain");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var findings = AdaptationPackageValidator.ValidateFountainOnly(fountainText, state.SceneMin, state.SceneMax);
            if (findings.Count == 0)
            {
                Console.WriteLine("✅ Stage 4: Fountain passes local validation.");
                break;
            }

            Console.WriteLine($"🔧 Stage 4: repair attempt {attempt + 1} — {findings.Count} finding(s): {string.Join(" | ", findings)}");
            var repairInstruction =
                "The Fountain screenplay you just returned has these specific problems:\n" +
                string.Join("\n", findings.Select(f => $"- {f}")) +
                "\n\nReturn the corrected FULL Fountain screenplay only (no markdown fences, no commentary), " +
                "fixing exactly these issues. Do not change anything else that already works.";
            var repairResult = await state.Client.ContinueSessionAsync(
                state.Model, state.LastResponseId, repairInstruction, state.Ct, state.Temperature).ConfigureAwait(false);
            state.TrackGeneration(repairResult);
            fountainText = StripFences(repairResult.OutputText);
            var attemptPath = Path.Combine(state.OutDir, $"screenplay.repair{attempt + 1}.fountain");
            await File.WriteAllTextAsync(attemptPath, fountainText, state.Ct).ConfigureAwait(false);
            state.LastResponseId = repairResult.ResponseId;
            state.Session.StageResponseIds["fountain"] = state.LastResponseId;
            SaveSession(state.SessionPath, state.Session);
        }
        await File.WriteAllTextAsync(fountainPath, fountainText, state.Ct).ConfigureAwait(false);
        return fountainText;
    }

    private static async Task<string> RunEdlStageAsync(
        PilotRunState state, string fountainText, string castLocationJson)
    {
        // ---- Stage 5: EDL / shot plan (validates against the pre-established cast/location keys) ----
        var edlPath = Path.Combine(state.OutDir, "edit_decision_list.json");
        if (state.Session.StageResponseIds.TryGetValue("edl", out var cachedEdlResp) && File.Exists(edlPath))
        {
            Console.WriteLine("♻️  Stage 5 (EDL): reusing cached artifact.");
            state.LastResponseId = cachedEdlResp;
            return await File.ReadAllTextAsync(edlPath, state.Ct).ConfigureAwait(false);
        }

        Console.WriteLine("🎞️  Stage 5: EDL / shot plan...");
        var edlInstruction = BuildEdlInstruction(fountainText, castLocationJson);
        var result = await state.Client.ContinueSessionAsync(
            state.Model, state.LastResponseId, edlInstruction, state.Ct, state.Temperature).ConfigureAwait(false);
        state.TrackGeneration(result);
        var edlJson = ExtractJson(result.OutputText);
        await File.WriteAllTextAsync(edlPath, PrettyJson(edlJson), state.Ct).ConfigureAwait(false);
        state.LastResponseId = result.ResponseId;
        state.Session.StageResponseIds["edl"] = state.LastResponseId;
        SaveSession(state.SessionPath, state.Session);
        Console.WriteLine($"   response_id={state.LastResponseId} request_bytes={result.RequestBytesSent} (book NOT resent)");
        return edlJson;
    }

    private static async Task<string> RunChainedClipShotPlanStageAsync(
        PilotRunState state, string edlJson, string fountainText, string beatPlanJson)
    {
        // ---- Stage 5.5 (optional): clip-level shot plan, batched by scene group ----
        // Only runs with --clip-shot-plan. Expands each EDL scene into 2-7 short clips (camera
        // directive, performance intensity/note, exact dialogue-or-VO fragment, per-clip sound),
        // batched ~8 scenes per call and chained via previous_response_id — same no-book-resend
        // mechanism as every other stage. This is what the real product's Stage2PlannerService/
        // ClipVideoPromptBuilder does at much greater depth (~15 classifiers); this is a deliberately
        // trimmed version scoped for benchmarking, not product parity.
        var clipPlanPath = Path.Combine(state.OutDir, "clip_shot_plan.json");
        if (state.Session.StageResponseIds.TryGetValue("clip_plan", out var cachedClipResp) && File.Exists(clipPlanPath))
        {
            Console.WriteLine("♻️  Stage 5.5 (clip shot plan): reusing cached artifact.");
            state.LastResponseId = cachedClipResp;
            return await File.ReadAllTextAsync(clipPlanPath, state.Ct).ConfigureAwait(false);
        }

        Console.WriteLine("🎥 Stage 5.5: clip-level shot plan (batched)...");
        var edlScenes = ParseEdlSceneElements(edlJson);
        var fountainScenes = SplitFountainIntoScenes(fountainText);
        if (fountainScenes.Count != edlScenes.Count)
        {
            Console.WriteLine(
                $"   ⚠️ EDL has {edlScenes.Count} scenes but Fountain split into {fountainScenes.Count} — " +
                "batching by the smaller count; see the final reconciliation warning.");
        }
        var sceneCount = Math.Min(edlScenes.Count, fountainScenes.Count);
        var batchSize = ComputeSafeBatchSize(
            LookupMaxOutputTokens(state.WorkspaceRoot, state.Model), ClipPlanEstimatedTokensPerScene);
        var batchJsonTexts = new List<string>();
        for (var batchStart = 0; batchStart < sceneCount; batchStart += batchSize)
        {
            var batchEnd = Math.Min(batchStart + batchSize, sceneCount);
            var edlSlice = "{\"scenes\":[" +
                string.Join(",", edlScenes.GetRange(batchStart, batchEnd - batchStart).Select(e => e.GetRawText())) +
                "]}";
            var fountainExcerpt = string.Join("\n\n", fountainScenes.GetRange(batchStart, batchEnd - batchStart));
            var instruction = BuildClipPlanInstruction(
                PrettyJson(edlSlice), fountainExcerpt, beatPlanJson, state.DurMaxSeconds);
            var result = await state.Client.ContinueSessionAsync(
                state.Model, state.LastResponseId, instruction, state.Ct, state.Temperature).ConfigureAwait(false);
            state.TrackGeneration(result);
            batchJsonTexts.Add(ExtractJson(result.OutputText));
            state.LastResponseId = result.ResponseId;
            state.Session.StageResponseIds["clip_plan"] = state.LastResponseId;
            SaveSession(state.SessionPath, state.Session);
            Console.WriteLine(
                $"   batch [scenes {batchStart + 1}-{batchEnd}] response_id={state.LastResponseId} " +
                $"request_bytes={result.RequestBytesSent} (book NOT resent)");
        }
        var clipPlanJson = RecomputeClipDurations(
            MergeScenesBatches(batchJsonTexts), state.DurMinSeconds, state.DurMaxSeconds, state.DurAbsMaxSeconds);
        await File.WriteAllTextAsync(clipPlanPath, PrettyJson(clipPlanJson), state.Ct).ConfigureAwait(false);
        return clipPlanJson;
    }

    private static async Task RunDualAttachClipPlanExperimentAsync(
        PilotRunState state,
        string edlJson,
        string fountainText,
        string beatPlanJson,
        string castLocationJson)
    {
        // ---- EXPERIMENT (optional, --dual-attach-clip-plan): same clip-plan task, but with no
        // previous_response_id chaining at all — both the book and the approved Fountain are attached
        // directly by file_id on every independent batch call, with an explicit layout hint (the EDL's
        // own source_paragraphs / scene_id) telling the model where to look in each. Produces a
        // separate artifact and separate cost total so it can be compared against the chained Stage
        // 5.5 result above on both quality and actual billed tokens — see PrintCostSummary.
        var dualAttachTokens = new Dictionary<string, (long Input, long Output, long Cached)>();
        var dualAttachBytes = 0;

        var fountainTaggedText = BuildSceneTaggedFountainText(fountainText);
        var fountainSha256 = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fountainTaggedText)));
        if (state.Session.FountainFileId is null || state.Session.FountainFileSha256 != fountainSha256)
        {
            Console.WriteLine("📤 [experiment] Uploading scene-tagged Fountain for dual-attach test...");
            var fountainTaggedPath = Path.Combine(state.OutDir, "fountain_tagged.txt");
            await File.WriteAllTextAsync(fountainTaggedPath, fountainTaggedText, state.Ct).ConfigureAwait(false);
            var fUpload = await state.Client.UploadBookAsync(fountainTaggedPath, ct: state.Ct).ConfigureAwait(false);
            state.Session.FountainFileId = fUpload.FileId;
            state.Session.FountainFileSha256 = fountainSha256;
            SaveSession(state.SessionPath, state.Session);
            Console.WriteLine($"   fountain_file_id={fUpload.FileId} bytes={fUpload.Bytes}");
        }
        else
        {
            Console.WriteLine($"📎 [experiment] Reusing uploaded Fountain: fountain_file_id={state.Session.FountainFileId}");
        }

        var dualAttachPath = Path.Combine(state.OutDir, "clip_shot_plan_dualattach.json");
        Console.WriteLine("🧪 [experiment] Clip-level shot plan (dual-attach, no chaining)...");
        var edlScenesForDual = ParseEdlSceneElements(edlJson);
        var dualBatchSize = ComputeSafeBatchSize(
            LookupMaxOutputTokens(state.WorkspaceRoot, state.Model), ClipPlanEstimatedTokensPerScene);
        var dualBatchJsonTexts = new List<string>();
        for (var batchStart = 0; batchStart < edlScenesForDual.Count; batchStart += dualBatchSize)
        {
            var batchEnd = Math.Min(batchStart + dualBatchSize, edlScenesForDual.Count);
            var edlSlice = "{\"scenes\":[" +
                string.Join(",", edlScenesForDual.GetRange(batchStart, batchEnd - batchStart).Select(e => e.GetRawText())) +
                "]}";
            var instruction = BuildDualAttachClipPlanInstruction(
                PrettyJson(edlSlice), castLocationJson, beatPlanJson, state.DurMaxSeconds);
            var result = await state.Client.CompleteWithFilesAsync(
                state.Model, new[] { state.Session.FileId, state.Session.FountainFileId! }, instruction, state.Ct, state.Temperature)
                .ConfigureAwait(false);
            dualAttachBytes += result.RequestBytesSent;
            TrackUsage(dualAttachTokens, state.Model, result.UsageJson);
            dualBatchJsonTexts.Add(ExtractJson(result.OutputText));
            Console.WriteLine(
                $"   [experiment] batch [scenes {batchStart + 1}-{batchEnd}] response_id={result.ResponseId} " +
                $"request_bytes={result.RequestBytesSent} (independent call, no chain)");
        }
        var dualAttachJson = RecomputeClipDurations(
            MergeScenesBatches(dualBatchJsonTexts), state.DurMinSeconds, state.DurMaxSeconds, state.DurAbsMaxSeconds);
        await File.WriteAllTextAsync(dualAttachPath, PrettyJson(dualAttachJson), state.Ct).ConfigureAwait(false);

        Console.WriteLine($"🧪 [experiment] Dual-attach bytes sent: {dualAttachBytes} (vs. chained clip-plan stage above)");
        PrintCostSummary(
            state.WorkspaceRoot, dualAttachTokens, state.OutDir, "_dualattach_clipplan",
            dualAttachBytes, new FileInfo(state.IndexedBookPath).Length,
            bookResent: false); // re-attached by file_id, not resent as bytes — see PrintCostSummary doc
    }

    private static async Task<string> RunAudioPlanStageAsync(
        PilotRunState state, string edlJson, string? clipPlanJson)
    {
        // ---- Stage 6: audio plan (batched — a 68-scene unbatched call was observed to silently
        // truncate, with the model itself appending a "scenes_CONTINUED": [] marker acknowledging it
        // ran out of room; only 9 of 68 scenes got audio coverage. Same fix as the clip-plan stage.) ----
        var audioPath = Path.Combine(state.OutDir, "audio_plan.json");
        if (state.Session.StageResponseIds.TryGetValue("audio_plan", out var cachedAudioResp) && File.Exists(audioPath))
        {
            Console.WriteLine("♻️  Stage 6 (audio plan): reusing cached artifact.");
            state.LastResponseId = cachedAudioResp;
            return await File.ReadAllTextAsync(audioPath, state.Ct).ConfigureAwait(false);
        }

        Console.WriteLine("🎵 Stage 6: audio plan (batched)...");
        var edlScenesForAudio = ParseEdlSceneElements(edlJson);
        var audioBatchSize = ComputeSafeBatchSize(
            LookupMaxOutputTokens(state.WorkspaceRoot, state.Model), AudioPlanEstimatedTokensPerScene);
        var audioBatchJsonTexts = new List<string>();
        for (var batchStart = 0; batchStart < edlScenesForAudio.Count; batchStart += audioBatchSize)
        {
            var batchEnd = Math.Min(batchStart + audioBatchSize, edlScenesForAudio.Count);
            var sliceScenes = edlScenesForAudio.GetRange(batchStart, batchEnd - batchStart);
            var edlSlice = "{\"scenes\":[" + string.Join(",", sliceScenes.Select(e => e.GetRawText())) + "]}";
            var sceneIdsInBatch = sliceScenes
                .Select(e => e.TryGetProperty("scene_id", out var sid) ? sid.GetString() ?? "" : "")
                .Where(id => id.Length > 0).ToHashSet();
            var clipIntensityJson = ExtractClipIntensitySummary(clipPlanJson, sceneIdsInBatch);
            var instruction = BuildAudioPlanInstruction(PrettyJson(edlSlice), clipIntensityJson);
            var result = await state.Client.ContinueSessionAsync(
                state.Model, state.LastResponseId, instruction, state.Ct, state.Temperature).ConfigureAwait(false);
            state.TrackGeneration(result);
            audioBatchJsonTexts.Add(ExtractJson(result.OutputText));
            state.LastResponseId = result.ResponseId;
            state.Session.StageResponseIds["audio_plan"] = state.LastResponseId;
            SaveSession(state.SessionPath, state.Session);
            Console.WriteLine(
                $"   batch [scenes {batchStart + 1}-{batchEnd}] response_id={state.LastResponseId} " +
                $"request_bytes={result.RequestBytesSent} (book NOT resent)");
        }
        var audioJson = MergeScenesBatches(audioBatchJsonTexts);
        await File.WriteAllTextAsync(audioPath, PrettyJson(audioJson), state.Ct).ConfigureAwait(false);
        return audioJson;
    }

    private static Dictionary<int, List<string>> BuildCitingScenesByParagraph(string edlJson, int paragraphCount)
    {
        // ---- Local, zero-cost: paragraph-citation check + annotated book copy ----
        // The original book file is never touched — this only ever writes a separate output.
        var citingScenesByParagraph = new Dictionary<int, List<string>>();
        try
        {
            foreach (var scene in ParseEdlSceneElements(edlJson))
            {
                var sceneId = scene.TryGetProperty("scene_id", out var sidEl) ? sidEl.GetString() ?? "?" : "?";
                if (!scene.TryGetProperty("source_paragraphs", out var spEl) || spEl.ValueKind != JsonValueKind.Array) continue;
                foreach (var p in spEl.EnumerateArray())
                {
                    var m = ParagraphCitationRegex.Match(p.GetString() ?? "");
                    if (!m.Success) continue;
                    var n = int.Parse(m.Groups[1].Value);
                    if (n < 1 || n > paragraphCount) continue;
                    if (!citingScenesByParagraph.TryGetValue(n, out var list))
                        citingScenesByParagraph[n] = list = new List<string>();
                    if (!list.Contains(sceneId)) list.Add(sceneId);
                }
            }
        }
        catch { /* citation warnings above already cover parse issues */ }
        return citingScenesByParagraph;
    }

    private static async Task WriteAnnotatedBookAsync(
        PilotRunState state, Dictionary<int, List<string>> citingScenesByParagraph)
    {
        var annotatedBookPath = Path.Combine(state.OutDir, "source_book_annotated.txt");
        await File.WriteAllTextAsync(
            annotatedBookPath,
            $"Annotated from book_sha256: {state.BookSha256} — {state.Paragraphs.Count} paragraphs — generated {DateTime.UtcNow:O}\n\n" +
            BuildAnnotatedBookText(state.Paragraphs, citingScenesByParagraph),
            state.Ct).ConfigureAwait(false);
    }

    private static async Task<int> FinalizeChainedRunAsync(
        PilotRunState state,
        string fountainText,
        string edlJson,
        string castLocationJson,
        string audioJson,
        string? clipPlanJson,
        bool clipShotPlan,
        List<(string JudgeModel, string RelativeFileName)> judgeResults)
    {
        // ---- Full cross-artifact validation ----
        var report = AdaptationPackageValidator.ValidatePackage(
            fountainText, edlJson, castLocationJson, audioJson, state.SceneMin, state.SceneMax, clipPlanJson);
        var citationWarnings = ValidateParagraphCitations(edlJson, state.Paragraphs.Count);
        report.Warnings.AddRange(citationWarnings);
        var reportPath = Path.Combine(state.OutDir, "validation_report.json");
        await File.WriteAllTextAsync(
            reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), state.Ct)
            .ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"📦 Package: {state.OutDir}");
        Console.WriteLine($"📶 Total NEW bytes sent across all follow-up stages this run: {state.TotalRequestBytes} (book resent: 0 times)");
        PrintCostSummary(
            state.WorkspaceRoot, state.TokensByModel, state.OutDir, "",
            state.TotalRequestBytes, new FileInfo(state.IndexedBookPath).Length, bookResent: false);
        Console.WriteLine(report.Status == "pass"
            ? "✅ Validation: PASS"
            : $"❌ Validation: FAIL — {report.Failures.Count} failure(s), {report.Warnings.Count} warning(s)");
        foreach (var f in report.Failures) Console.WriteLine($"   FAIL: {f}");
        foreach (var w in report.Warnings) Console.WriteLine($"   WARN: {w}");
        Console.WriteLine($"   scenes={report.SceneCount} target_band=[{state.SceneMin},{state.SceneMax}]" +
            (report.ClipCount is { } cc ? $" clips={cc}" : ""));

        var mainArtifacts = new Dictionary<string, string>
        {
            ["beat_plan"] = "adaptation_plan.json",
            ["cast_and_locations"] = "cast_and_locations.json",
            ["screenplay"] = "screenplay.fountain",
            ["edit_decision_list"] = "edit_decision_list.json",
            ["audio_plan"] = "audio_plan.json",
            ["source_book_indexed"] = "book_indexed.txt",
            ["source_book_annotated"] = "source_book_annotated.txt",
            ["validation_report"] = "validation_report.json",
        };
        if (clipShotPlan) mainArtifacts["clip_shot_plan"] = "clip_shot_plan.json";
        foreach (var (jm, relPath) in judgeResults)
            mainArtifacts[$"judge_review_{jm.Replace('/', '_').Replace(':', '_')}"] = relPath;
        await WriteAdaptationPackageManifestAsync(
            state.OutDir, "adaptation_package.json", "chained", state.BookSlug, state.BookSha256, state.Model,
            judgeResults.Select(j => j.JudgeModel).ToList(),
            state.Temperature, state.JudgeTemperature, state.TargetRuntimeMinutes, report, mainArtifacts, state.Ct)
            .ConfigureAwait(false);

        return report.Status == "pass" ? 0 : 2;
    }

    /// <summary>
    /// Writes the formal, addressable description of one adaptation package (screenplay + every
    /// sidecar it came from) — schema version, source book hash, generation settings, validation
    /// status, and a path+sha256 per artifact. The book/screenplay/sidecars are never repackaged into
    /// a new container format; the manifest just makes the existing output folder self-describing and
    /// gives every artifact a content hash, so drift (a file edited by hand, a stale regeneration) is
    /// detectable rather than assumed away. No shipped project depends on any fixed schema yet, so this
    /// intentionally does not try to match the current product's cast_seeds.json/blueprint shapes —
    /// that translation, if ever needed, is a deliberate later step, not implied by this manifest.
    /// </summary>
    private static async Task WriteAdaptationPackageManifestAsync(
        string outDir,
        string manifestFileName,
        string pipelineVariant,
        string bookSlug,
        string bookSha256,
        string model,
        IReadOnlyList<string> judgeModels,
        double temperature,
        double judgeTemperature,
        int targetRuntimeMinutes,
        AdaptationPackageValidator.ValidationReport report,
        Dictionary<string, string> artifactRelativePaths,
        CancellationToken ct)
    {
        var artifacts = new List<Dictionary<string, object?>>();
        foreach (var (key, relPath) in artifactRelativePaths)
        {
            var fullPath = Path.Combine(outDir, relPath);
            var exists = File.Exists(fullPath);
            string? sha = null;
            if (exists)
            {
                var bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
                sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            }
            artifacts.Add(new Dictionary<string, object?>
            {
                ["key"] = key,
                ["path"] = relPath,
                ["exists"] = exists,
                ["sha256"] = sha,
            });
        }

        var manifest = new Dictionary<string, object?>
        {
            ["schema_version"] = "adaptation_package.v1",
            ["pipeline_variant"] = pipelineVariant,
            ["book_slug"] = bookSlug,
            ["book_sha256"] = bookSha256,
            ["provider"] = "xai",
            ["model"] = model,
            ["judge_models"] = judgeModels,
            ["temperature"] = temperature,
            ["judge_temperature"] = judgeTemperature,
            ["target_runtime_minutes"] = targetRuntimeMinutes,
            ["generated_at_utc"] = DateTime.UtcNow.ToString("O"),
            ["validation_status"] = report.Status,
            ["scene_count"] = report.SceneCount,
            ["clip_count"] = report.ClipCount,
            ["artifacts"] = artifacts,
        };

        var manifestPath = Path.Combine(outDir, manifestFileName);
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            ct).ConfigureAwait(false);
        Console.WriteLine($"📋 Adaptation package manifest: {manifestPath}");
    }

    /// <summary>
    /// The full pipeline re-run as an independent, non-chained sequence: its own cast/locations, its
    /// own Fountain, its own EDL, clip plan, audio plan, and judge — every call attaches only the
    /// files it needs directly (never previous_response_id) and never reads a chained-pipeline
    /// artifact. All outputs use a "_dualattach_full" suffix so they never collide with the main
    /// pipeline's files or with the narrower single-stage --dual-attach-clip-plan experiment.
    /// </summary>
    private static void TrackDualAttachUsage(
        DualAttachRunState tracking, XaiResponsesClient.SessionTurnResult r, string modelId)
    {
        tracking.TotalBytesAlt += r.RequestBytesSent;
        TrackUsage(tracking.TokensByModelAlt, modelId, r.UsageJson);
    }

    private static async Task RunDualAttachFullPipelineAsync(
        XaiResponsesClient client,
        SessionRecord session,
        string sessionPath,
        string model,
        double temperature,
        string? judgeModel,
        string? judgeModel2,
        double judgeTemperature,
        string beatPlanJson,
        string workspaceRoot,
        int targetRuntimeMinutes,
        int sceneMin,
        int sceneMax,
        int paragraphCount,
        string outDir,
        int durMinSeconds,
        int durMaxSeconds,
        int durAbsMaxSeconds,
        CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("==========================================================================");
        Console.WriteLine(" EXPERIMENT: full pipeline, dual-attach (no chaining) — stages 2 onward");
        Console.WriteLine("==========================================================================");

        var tracking = new DualAttachRunState();
        var castLocationJsonAlt = await RunDualAttachCastLocationsStageAsync(
            client, session, model, temperature, beatPlanJson, outDir, tracking, ct).ConfigureAwait(false);
        var fountainTextAlt = await RunDualAttachFountainStageAsync(
            client, session, model, temperature, workspaceRoot, targetRuntimeMinutes, beatPlanJson, castLocationJsonAlt,
            sceneMin, sceneMax, outDir, tracking, ct).ConfigureAwait(false);
        await UploadDualAttachFullFountainAsync(
            client, session, sessionPath, fountainTextAlt, outDir, ct).ConfigureAwait(false);
        var edlJsonAlt = await RunDualAttachEdlStageAsync(
            client, session, model, temperature, fountainTextAlt, castLocationJsonAlt, outDir, tracking, ct)
            .ConfigureAwait(false);
        var clipPlanJsonAlt = await RunDualAttachClipPlanStageAsync(
            client, session, model, temperature, beatPlanJson, castLocationJsonAlt, edlJsonAlt, workspaceRoot,
            durMinSeconds, durMaxSeconds, durAbsMaxSeconds, outDir, tracking, ct).ConfigureAwait(false);
        var audioJsonAlt = await RunDualAttachAudioPlanStageAsync(
            client, model, temperature, edlJsonAlt, clipPlanJsonAlt, workspaceRoot, outDir, tracking, ct)
            .ConfigureAwait(false);

        // ---- Stage 7-alt: one or two independent, book-attached judges — genuinely blind here, since
        // this call never inherited any sidecar artifacts from conversation memory. ----
        var judgeResultsAlt = await RunJudgesAsync(
            client, new[] { judgeModel, judgeModel2 }, new[] { session.FileId, session.FountainFileIdAlt! },
            fountainTextAlt, judgeTemperature, outDir, "judge_review_dualattach_full", "[full experiment] Stage 7",
            (r, m) => TrackDualAttachUsage(tracking, r, m), ct).ConfigureAwait(false);

        await FinalizeDualAttachFullPipelineAsync(
            workspaceRoot, outDir, session, model, temperature, judgeTemperature, targetRuntimeMinutes,
            sceneMin, sceneMax, paragraphCount, fountainTextAlt, edlJsonAlt, castLocationJsonAlt, audioJsonAlt,
            clipPlanJsonAlt, judgeResultsAlt, tracking, ct).ConfigureAwait(false);
    }

    private static async Task<string> RunDualAttachCastLocationsStageAsync(
        XaiResponsesClient client,
        SessionRecord session,
        string model,
        double temperature,
        string beatPlanJson,
        string outDir,
        DualAttachRunState tracking,
        CancellationToken ct)
    {
        // ---- Stage 2-alt: cast, wardrobe, and locations ----
        var castLocationPathAlt = Path.Combine(outDir, "cast_and_locations_dualattach_full.json");
        if (File.Exists(castLocationPathAlt))
        {
            Console.WriteLine("♻️  [full experiment] Stage 2: reusing existing artifact.");
            return await File.ReadAllTextAsync(castLocationPathAlt, ct).ConfigureAwait(false);
        }

        Console.WriteLine("\uD83E\uDDD1\u200D\uD83C\uDFA4 [full experiment] Stage 2: cast, wardrobe, and locations...");
        var instruction = BuildCastLocationsInstruction(beatPlanJson);
        var result = await client.CompleteWithFilesAsync(model, new[] { session.FileId }, instruction, ct, temperature)
            .ConfigureAwait(false);
        TrackDualAttachUsage(tracking, result, model);
        var castLocationJsonAlt = ExtractJson(result.OutputText);

        // Corrective retry — see the chained Stage 2's comment for why. No response-id chain
        // here (dual-attach is independent calls by design), so the correction note is appended
        // to the original instruction each retry rather than sent as a bare follow-up.
        for (var attempt = 1; attempt < CastLocationsMaxAttempts && !HasValidCastLocationsShape(castLocationJsonAlt); attempt++)
        {
            Console.WriteLine(
                $"   ⚠️  [full experiment] Response missing cast_seeds/location_bible — requesting a corrected reformat (attempt {attempt + 1}/{CastLocationsMaxAttempts})...");
            var correctedInstruction = instruction + "\n\n" + CastLocationsCorrectionInstruction;
            var retryResult = await client.CompleteWithFilesAsync(
                model, new[] { session.FileId }, correctedInstruction, ct, temperature).ConfigureAwait(false);
            TrackDualAttachUsage(tracking, retryResult, model);
            castLocationJsonAlt = ExtractJson(retryResult.OutputText);
        }
        if (!HasValidCastLocationsShape(castLocationJsonAlt))
            Console.WriteLine("   ⚠️  [full experiment] Cast/locations still incomplete after retry — proceeding with what's available.");

        await File.WriteAllTextAsync(castLocationPathAlt, PrettyJson(castLocationJsonAlt), ct).ConfigureAwait(false);
        return castLocationJsonAlt;
    }

    private static async Task<string> RunDualAttachFountainStageAsync(
        XaiResponsesClient client,
        SessionRecord session,
        string model,
        double temperature,
        string workspaceRoot,
        int targetRuntimeMinutes,
        string beatPlanJson,
        string castLocationJsonAlt,
        int sceneMin,
        int sceneMax,
        string outDir,
        DualAttachRunState tracking,
        CancellationToken ct)
    {
        // ---- Stage 3-alt: Fountain screenplay ----
        var fountainPathAlt = Path.Combine(outDir, "screenplay_dualattach_full.fountain");
        if (File.Exists(fountainPathAlt))
        {
            Console.WriteLine("♻️  [full experiment] Stage 3: reusing existing artifact.");
            return await File.ReadAllTextAsync(fountainPathAlt, ct).ConfigureAwait(false);
        }

        Console.WriteLine("🎬 [full experiment] Stage 3: Fountain screenplay...");
        var instruction = BuildFountainInstruction(workspaceRoot, targetRuntimeMinutes, beatPlanJson, castLocationJsonAlt);
        var result = await client.CompleteWithFilesAsync(model, new[] { session.FileId }, instruction, ct, temperature)
            .ConfigureAwait(false);
        TrackDualAttachUsage(tracking, result, model);
        var fountainTextAlt = StripFences(result.OutputText);

        // Stage 4-alt: validate + repair (no chain memory — the repair call must restate the
        // current draft explicitly, unlike the chained pipeline where "what you just returned"
        // is implicit).
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var findings = AdaptationPackageValidator.ValidateFountainOnly(fountainTextAlt, sceneMin, sceneMax);
            if (findings.Count == 0)
            {
                Console.WriteLine("✅ [full experiment] Stage 4: Fountain passes local validation.");
                break;
            }
            Console.WriteLine($"🔧 [full experiment] Stage 4: repair attempt {attempt + 1} — {findings.Count} finding(s).");
            var repairInstruction =
                "The following Fountain screenplay has these specific problems:\n" +
                string.Join("\n", findings.Select(f => $"- {f}")) +
                "\n\nCURRENT FOUNTAIN SCREENPLAY:\n" + fountainTextAlt +
                "\n\nReturn the corrected FULL Fountain screenplay only (no markdown fences, no " +
                "commentary), fixing exactly these issues. Do not change anything else that already works.";
            var repairResult = await client.CompleteWithFilesAsync(
                model, new[] { session.FileId }, repairInstruction, ct, temperature).ConfigureAwait(false);
            TrackDualAttachUsage(tracking, repairResult, model);
            fountainTextAlt = StripFences(repairResult.OutputText);
        }
        await File.WriteAllTextAsync(fountainPathAlt, fountainTextAlt, ct).ConfigureAwait(false);
        return fountainTextAlt;
    }

    private static async Task UploadDualAttachFullFountainAsync(
        XaiResponsesClient client,
        SessionRecord session,
        string sessionPath,
        string fountainTextAlt,
        string outDir,
        CancellationToken ct)
    {
        // Upload this pipeline's OWN Fountain (scene-tagged) — never the chained pipeline's.
        var fountainTaggedTextAlt = BuildSceneTaggedFountainText(fountainTextAlt);
        var fountainShaAlt = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fountainTaggedTextAlt)));
        if (session.FountainFileIdAlt is null || session.FountainFileShaAlt != fountainShaAlt)
        {
            Console.WriteLine("📤 [full experiment] Uploading this pipeline's own scene-tagged Fountain...");
            var taggedPath = Path.Combine(outDir, "fountain_tagged_full.txt");
            await File.WriteAllTextAsync(taggedPath, fountainTaggedTextAlt, ct).ConfigureAwait(false);
            var fUpload = await client.UploadBookAsync(taggedPath, ct: ct).ConfigureAwait(false);
            session.FountainFileIdAlt = fUpload.FileId;
            session.FountainFileShaAlt = fountainShaAlt;
            SaveSession(sessionPath, session);
            Console.WriteLine($"   fountain_file_id={fUpload.FileId} bytes={fUpload.Bytes}");
        }
        else
        {
            Console.WriteLine($"📎 [full experiment] Reusing this pipeline's Fountain upload: {session.FountainFileIdAlt}");
        }
    }

    private static async Task<string> RunDualAttachEdlStageAsync(
        XaiResponsesClient client,
        SessionRecord session,
        string model,
        double temperature,
        string fountainTextAlt,
        string castLocationJsonAlt,
        string outDir,
        DualAttachRunState tracking,
        CancellationToken ct)
    {
        // ---- Stage 5-alt: EDL / shot plan ----
        var edlPathAlt = Path.Combine(outDir, "edit_decision_list_dualattach_full.json");
        if (File.Exists(edlPathAlt))
        {
            Console.WriteLine("♻️  [full experiment] Stage 5: reusing existing artifact.");
            return await File.ReadAllTextAsync(edlPathAlt, ct).ConfigureAwait(false);
        }

        Console.WriteLine("🎞️  [full experiment] Stage 5: EDL / shot plan...");
        var instruction = BuildEdlInstruction(fountainTextAlt, castLocationJsonAlt);
        var result = await client.CompleteWithFilesAsync(model, new[] { session.FileId }, instruction, ct, temperature)
            .ConfigureAwait(false);
        TrackDualAttachUsage(tracking, result, model);
        var edlJsonAlt = ExtractJson(result.OutputText);
        await File.WriteAllTextAsync(edlPathAlt, PrettyJson(edlJsonAlt), ct).ConfigureAwait(false);
        return edlJsonAlt;
    }

    private static async Task<string> RunDualAttachClipPlanStageAsync(
        XaiResponsesClient client,
        SessionRecord session,
        string model,
        double temperature,
        string beatPlanJson,
        string castLocationJsonAlt,
        string edlJsonAlt,
        string workspaceRoot,
        int durMinSeconds,
        int durMaxSeconds,
        int durAbsMaxSeconds,
        string outDir,
        DualAttachRunState tracking,
        CancellationToken ct)
    {
        // ---- Stage 5.5-alt: clip-level shot plan (batched, book + Fountain both attached) ----
        var clipPlanPathAlt = Path.Combine(outDir, "clip_shot_plan_dualattach_full.json");
        if (File.Exists(clipPlanPathAlt))
        {
            Console.WriteLine("♻️  [full experiment] Stage 5.5: reusing existing artifact.");
            return await File.ReadAllTextAsync(clipPlanPathAlt, ct).ConfigureAwait(false);
        }

        Console.WriteLine("🎥 [full experiment] Stage 5.5: clip-level shot plan (batched)...");
        var edlScenes = ParseEdlSceneElements(edlJsonAlt);
        var batchSize = ComputeSafeBatchSize(
            LookupMaxOutputTokens(workspaceRoot, model), ClipPlanEstimatedTokensPerScene);
        var batchJsonTexts = new List<string>();
        for (var batchStart = 0; batchStart < edlScenes.Count; batchStart += batchSize)
        {
            var batchEnd = Math.Min(batchStart + batchSize, edlScenes.Count);
            var edlSlice = "{\"scenes\":[" +
                string.Join(",", edlScenes.GetRange(batchStart, batchEnd - batchStart).Select(e => e.GetRawText())) +
                "]}";
            var instruction = BuildDualAttachClipPlanInstruction(
                PrettyJson(edlSlice), castLocationJsonAlt, beatPlanJson, durMaxSeconds);
            var result = await client.CompleteWithFilesAsync(
                model, new[] { session.FileId, session.FountainFileIdAlt! }, instruction, ct, temperature)
                .ConfigureAwait(false);
            TrackDualAttachUsage(tracking, result, model);
            batchJsonTexts.Add(ExtractJson(result.OutputText));
            Console.WriteLine($"   [full experiment] clip-plan batch [scenes {batchStart + 1}-{batchEnd}]");
        }
        var clipPlanJsonAlt = RecomputeClipDurations(
            MergeScenesBatches(batchJsonTexts), durMinSeconds, durMaxSeconds, durAbsMaxSeconds);
        await File.WriteAllTextAsync(clipPlanPathAlt, PrettyJson(clipPlanJsonAlt), ct).ConfigureAwait(false);
        return clipPlanJsonAlt;
    }

    private static async Task<string> RunDualAttachAudioPlanStageAsync(
        XaiResponsesClient client,
        string model,
        double temperature,
        string edlJsonAlt,
        string clipPlanJsonAlt,
        string workspaceRoot,
        string outDir,
        DualAttachRunState tracking,
        CancellationToken ct)
    {
        // ---- Stage 6-alt: audio plan (batched, no files needed — self-contained from the EDL) ----
        var audioPathAlt = Path.Combine(outDir, "audio_plan_dualattach_full.json");
        if (File.Exists(audioPathAlt))
        {
            Console.WriteLine("♻️  [full experiment] Stage 6: reusing existing artifact.");
            return await File.ReadAllTextAsync(audioPathAlt, ct).ConfigureAwait(false);
        }

        Console.WriteLine("🎵 [full experiment] Stage 6: audio plan (batched)...");
        var edlScenes = ParseEdlSceneElements(edlJsonAlt);
        var batchSize = ComputeSafeBatchSize(
            LookupMaxOutputTokens(workspaceRoot, model), AudioPlanEstimatedTokensPerScene);
        var batchJsonTexts = new List<string>();
        for (var batchStart = 0; batchStart < edlScenes.Count; batchStart += batchSize)
        {
            var batchEnd = Math.Min(batchStart + batchSize, edlScenes.Count);
            var sliceScenes = edlScenes.GetRange(batchStart, batchEnd - batchStart);
            var edlSlice = "{\"scenes\":[" + string.Join(",", sliceScenes.Select(e => e.GetRawText())) + "]}";
            var sceneIdsInBatch = sliceScenes
                .Select(e => e.TryGetProperty("scene_id", out var sid) ? sid.GetString() ?? "" : "")
                .Where(id => id.Length > 0).ToHashSet();
            var clipIntensityJson = ExtractClipIntensitySummary(clipPlanJsonAlt, sceneIdsInBatch);
            var instruction = BuildAudioPlanInstruction(PrettyJson(edlSlice), clipIntensityJson);
            var result = await client.CompleteWithFilesAsync(model, Array.Empty<string>(), instruction, ct, temperature)
                .ConfigureAwait(false);
            TrackDualAttachUsage(tracking, result, model);
            batchJsonTexts.Add(ExtractJson(result.OutputText));
            Console.WriteLine($"   [full experiment] audio-plan batch [scenes {batchStart + 1}-{batchEnd}] (no files attached)");
        }
        var audioJsonAlt = MergeScenesBatches(batchJsonTexts);
        await File.WriteAllTextAsync(audioPathAlt, PrettyJson(audioJsonAlt), ct).ConfigureAwait(false);
        return audioJsonAlt;
    }

    private static async Task FinalizeDualAttachFullPipelineAsync(
        string workspaceRoot,
        string outDir,
        SessionRecord session,
        string model,
        double temperature,
        double judgeTemperature,
        int targetRuntimeMinutes,
        int sceneMin,
        int sceneMax,
        int paragraphCount,
        string fountainTextAlt,
        string edlJsonAlt,
        string castLocationJsonAlt,
        string audioJsonAlt,
        string clipPlanJsonAlt,
        List<(string JudgeModel, string RelativeFileName)> judgeResultsAlt,
        DualAttachRunState tracking,
        CancellationToken ct)
    {
        // ---- Validation (paragraph citations + full cross-artifact check) ----
        var citationWarningsAlt = ValidateParagraphCitations(edlJsonAlt, paragraphCount);
        var reportAlt = AdaptationPackageValidator.ValidatePackage(
            fountainTextAlt, edlJsonAlt, castLocationJsonAlt, audioJsonAlt, sceneMin, sceneMax, clipPlanJsonAlt);
        reportAlt.Warnings.AddRange(citationWarningsAlt);
        await File.WriteAllTextAsync(
            Path.Combine(outDir, "validation_report_dualattach_full.json"),
            JsonSerializer.Serialize(reportAlt, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"📦 [full experiment] Package suffix: _dualattach_full");
        Console.WriteLine($"📶 [full experiment] Total NEW bytes sent: {tracking.TotalBytesAlt} (independent calls, no chain)");
        PrintCostSummary(
            workspaceRoot, tracking.TokensByModelAlt, outDir, "_dualattach_full",
            tracking.TotalBytesAlt, new FileInfo(Path.Combine(outDir, "book_indexed.txt")).Length,
            bookResent: false); // re-attached by file_id, not resent as bytes — see PrintCostSummary doc
        Console.WriteLine(reportAlt.Status == "pass"
            ? "✅ [full experiment] Validation: PASS"
            : $"❌ [full experiment] Validation: FAIL — {reportAlt.Failures.Count} failure(s), {reportAlt.Warnings.Count} warning(s)");
        foreach (var f in reportAlt.Failures) Console.WriteLine($"   FAIL: {f}");
        foreach (var w in reportAlt.Warnings) Console.WriteLine($"   WARN: {w}");
        Console.WriteLine($"   [full experiment] scenes={reportAlt.SceneCount} target_band=[{sceneMin},{sceneMax}]" +
            (reportAlt.ClipCount is { } cc ? $" clips={cc}" : ""));

        var altArtifacts = new Dictionary<string, string>
        {
            ["cast_and_locations"] = "cast_and_locations_dualattach_full.json",
            ["screenplay"] = "screenplay_dualattach_full.fountain",
            ["edit_decision_list"] = "edit_decision_list_dualattach_full.json",
            ["clip_shot_plan"] = "clip_shot_plan_dualattach_full.json",
            ["audio_plan"] = "audio_plan_dualattach_full.json",
            ["validation_report"] = "validation_report_dualattach_full.json",
        };
        foreach (var (jm, relPath) in judgeResultsAlt)
            altArtifacts[$"judge_review_{jm.Replace('/', '_').Replace(':', '_')}"] = relPath;
        await WriteAdaptationPackageManifestAsync(
            outDir, "adaptation_package_dualattach_full.json", "dual_attach_full", Path.GetFileName(outDir),
            session.BookSha256, model, judgeResultsAlt.Select(j => j.JudgeModel).ToList(),
            temperature, judgeTemperature, targetRuntimeMinutes,
            reportAlt, altArtifacts, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rough scene-count band from target runtime, floor-anchored on this repo's own existing
    /// runtime guidance in prompts/book_to_fountain.txt ("8-18 scenes" for short pieces,
    /// "15-40 scenes" for a short-film cut of a novel) extended linearly toward feature length so a
    /// 90-minute target does not collapse to a short-film scene count the way the prior one-shot
    /// pilot did (9 scenes for Nick and Me).
    /// </summary>
    private static (int Min, int Max) TargetSceneBand(int targetRuntimeMinutes)
    {
        var min = Math.Max(8, (int)Math.Round(targetRuntimeMinutes / 4.0));
        var max = Math.Max(min + 4, (int)Math.Round(targetRuntimeMinutes / 2.2));
        return (min, max);
    }

    private static string BuildBeatPlanInstruction(int targetRuntimeMinutes, int sceneMin, int sceneMax) => $$"""
        You have access to the attached complete book. Read it in full before answering. It has been split
        into numbered paragraphs, each preceded by a tag like [P1], [P2], etc. — cite these exact paragraph
        numbers as your source locator; never invent a quote to search for later, since the same or similar
        wording can occur more than once in the book and would be ambiguous to relocate afterward.

        Produce a source-grounded ADAPTATION BEAT PLAN as a single JSON object — no markdown fences,
        no commentary before or after. Shape:
        {
          "characters": [ { "name": "...", "aka": ["..."], "withheld_until": "beat id or null", "notes": "short" } ],
          "locations_mentioned": ["..."],
          "timeline": ["ordered short list of major time markers"],
          "withheld_name_or_twist_rules": ["explicit rules: do not reveal X before beat Y"],
          "essential_beats": [ { "id": "B1", "summary": "...", "source_evidence": "short paraphrase for a human reader",
             "source_paragraphs": ["P12","P13"] } ],
          "target_runtime_minutes": {{targetRuntimeMinutes}},
          "target_scene_count_min": {{sceneMin}},
          "target_scene_count_max": {{sceneMax}}
        }

        This film targets {{targetRuntimeMinutes}} minutes of finished runtime. Do NOT compress this into a
        short-film beat list — list enough essential_beats that a screenplay covering all of them would need
        roughly {{sceneMin}}-{{sceneMax}} scenes, one clear location+purpose each. Every beat needs its own
        source_paragraphs citation — do not invent beats the book does not support. Return JSON only.
        """;

    private static string BuildFountainInstruction(
        string workspaceRoot, int targetRuntimeMinutes, string beatPlanJson, string castLocationJson)
    {
        var promptPath = Path.Combine(workspaceRoot, "prompts", "book_to_fountain.txt");
        var rules = File.Exists(promptPath)
            ? File.ReadAllText(promptPath).Replace("{{TOTAL_RUNTIME_MINUTES}}", targetRuntimeMinutes.ToString())
            : $"Target about {targetRuntimeMinutes} minutes of finished film. Write a complete Fountain 1.1 screenplay.";

        return $"""
            STAGED ADAPTATION SESSION — the complete book is already attached to this conversation from your
            first turn; it is not being resent. Below are the beat plan AND the cast/wardrobe/location
            descriptions you already produced in earlier turns; use them to guide scene selection and identity,
            but still ground every scene directly in the book's own text and voice, not only the summaries below.

            APPROVED BEAT PLAN:
            {beatPlanJson}

            APPROVED CAST, WARDROBE, AND LOCATIONS — these identities and places are already decided; use these
            exact tokens, descriptions, and location names in Action lines. Do not invent a new physical
            description or a new location name that conflicts with or duplicates one already established here.
            The CAST LOOKS & VOICES / LOCATIONS sections of the rules below tell you HOW to keep these consistent
            and filmable in Action — the WHAT (the actual descriptions) is already decided by the JSON below:
            {castLocationJson}

            Now write the full Fountain screenplay following these rules exactly:

            {rules}
            """;
    }

    // NOTE: these three templates are deliberately plain (non-interpolated) raw strings — they
    // contain literal JSON braces, which collide with C# raw-string interpolation's brace-counting
    // rules if written as $"""; the dynamic parts are appended afterward instead of interpolated.
    private static string BuildEdlInstruction(string fountainText, string castLocationJson)
    {
        const string instructions = """
            Using the APPROVED FOUNTAIN SCREENPLAY below (the book itself — with numbered [P#] paragraph tags —
            is already attached to this session for source grounding; it is not being resent), produce an EDIT
            DECISION LIST as a single JSON object — no markdown fences, no commentary. Shape:
            {
              "scenes": [
                { "scene_id": "S1", "heading": "<exact Fountain scene heading text>", "purpose": "...",
                   "location_key": "LOCATION_...", "cast": ["..."], "wardrobe_or_age_variant": ["..."],
                   "estimated_duration_seconds": 30, "visual_beats": ["..."],
                   "source_paragraphs": ["P12","P13"] }
              ]
            }

            Every scene heading in the Fountain below must have exactly one corresponding record, in the same
            order, using the exact heading text (do not merge or split scenes; do not add scenes that are not in
            the Fountain). "location_key" and cast/wardrobe entries MUST use the exact keys already established
            in the approved cast/wardrobe/location package below — do not invent new keys; if a scene genuinely
            needs one not covered there, use the closest existing key rather than fabricating a new one.
            "source_paragraphs" must cite the exact [P#] paragraph number(s) this specific scene draws from —
            a subset of its parent beat's paragraphs, narrowed to what THIS scene actually uses (a beat that
            expanded into several scenes should have its paragraphs split across them, not repeated identically
            on every one, unless a paragraph genuinely informs more than one scene). Return JSON only.

            APPROVED CAST, WARDROBE, AND LOCATIONS (established before this screenplay was written):
            """;
        return instructions + "\n" + castLocationJson + "\n\nAPPROVED FOUNTAIN SCREENPLAY:\n" + fountainText;
    }

    /// <summary>1 initial attempt + up to this many corrective retries for the cast/locations
    /// stage's required-shape check.</summary>
    private const int CastLocationsMaxAttempts = 2;

    private const string CastLocationsCorrectionInstruction =
        "Your previous response did not match the required shape. It must be a single JSON object " +
        "with BOTH \"cast_seeds\": {\"characters\": [...]} (covering every character from the beat " +
        "plan) AND \"location_bible\": {\"locations\": [...]} (covering every location) present and " +
        "non-empty — not a bare array, not one or the other. Return the complete corrected JSON now, " +
        "no markdown fences, no commentary.";

    /// <summary>True when the cast/locations JSON has both required top-level sections with at
    /// least one entry each — catches the exact malformed-shape regression (e.g. a bare array, or
    /// one section present but the other entirely missing) before it reaches disk, so a corrective
    /// retry can recover the missing data (a defensive parse alone cannot — it can only avoid
    /// corrupting what's already there, not generate what the model never produced).</summary>
    internal static bool HasValidCastLocationsShape(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var hasCast = root.TryGetProperty("cast_seeds", out var cs) &&
                          cs.TryGetProperty("characters", out var chars) &&
                          chars.ValueKind == JsonValueKind.Array && chars.GetArrayLength() > 0;
            var hasLocations = root.TryGetProperty("location_bible", out var lb) &&
                                lb.TryGetProperty("locations", out var locs) &&
                                locs.ValueKind == JsonValueKind.Array && locs.GetArrayLength() > 0;
            return hasCast && hasLocations;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildCastLocationsInstruction(string beatPlanJson)
    {
        const string instructions = """
            Using the APPROVED BEAT PLAN below (the book itself is already attached to this session — do not ask
            for it again), produce cast/wardrobe and location descriptions as a single JSON object, BEFORE any
            screenplay is written — no markdown fences, no commentary. Shape:
            {
              "cast_seeds": { "characters": [
                { "key": "NICK_ADULT", "display_name": "Nick", "age_link": null,
                   "identity_anchors": "stable physical description", "source_evidence": "...",
                   "render_style_lock": "photorealistic live-action", "voice_label": "...",
                   "wardrobe_variants": [
                     { "key": "NICK_PRISON", "description": "...", "source_facts": ["..."],
                        "creative_direction": "...", "beats": ["B7","B8"] }
                   ] }
              ] },
              "location_bible": { "locations": [
                { "key": "LOCATION_...", "canonical_name": "...", "source_evidence": "...",
                   "layout_anchors": ["..."], "lighting_states": ["..."], "persistent_props": ["..."],
                   "image_brief": "...", "beats": ["B1","B4"] }
              ] }
            }

            Cover every character in the beat plan's "characters" list and every entry in "locations_mentioned".
            Tag wardrobe variants and locations against the beat plan's "essential_beats" ids (no scene numbers
            exist yet — the screenplay has not been written). Distinguish source facts (book-established) from
            creative_direction defaults (production choices where the book is silent) — do not invent facts the
            book does not support. Descriptions only; no images are generated at this stage. Return JSON only.
            """;
        return instructions + "\n\nAPPROVED BEAT PLAN:\n" + beatPlanJson;
    }

    private static string BuildAudioPlanInstruction(string edlJson, string? clipIntensityJson = null)
    {
        const string instructions = """
            Using the EDL below, produce an AUDIO PLAN as a single JSON object — no markdown fences, no commentary.
            Shape:
            {
              "scenes": [
                { "scene_id": "S1", "score_intent": "...", "dynamics_arc": "...", "peak_clip_number": 4,
                   "timing": "...", "diegetic_sound": "...",
                   "silence_or_exclusions": "...", "alternatives": ["..."] }
              ]
            }

            "score_intent" is the scene's ENTIRE musical direction (genre, instrumentation, mood, tempo) as
            ONE continuous prompt — the real music generator accepts a single flat prompt per scene with no
            per-clip or timestamped control, so any rise/climax/release must be written as prose this one
            prompt can realize on its own (e.g. "opens hushed and sparse, tempo climbs from 70 to 130 BPM,
            explodes into a full orchestral hit in the final third, then cuts to near silence"). "dynamics_arc"
            is a one-sentence summary of where the scene's intensity rises, peaks, and releases, citing clip
            numbers. "peak_clip_number" is the single clip in this scene with the scene's highest
            performance_intensity — its emotional climax — omit only if the scene has no clips.

            Exactly one record per EDL scene_id below — same ids, no omissions, no extras. Return JSON only.
            """;
        var intensityBlock = string.IsNullOrWhiteSpace(clipIntensityJson)
            ? ""
            : "\n\nPER-CLIP PERFORMANCE INTENSITY (1-10 scale, from the approved clip shot plan — ground " +
              "score_intent, dynamics_arc, and peak_clip_number in these real values; do not re-guess the " +
              "emotional curve independently):\n" + clipIntensityJson;
        return instructions + intensityBlock + "\n\nEDL:\n" + edlJson;
    }

    /// <summary>Extracts a compact per-clip performance_intensity summary (scene_id -> clip_number,
    /// intensity, short cue) from an already-generated clip plan, scoped to just the given scene ids —
    /// lets the audio-plan stage ground its music arc in the same intensity signal
    /// CharacterEmotionArcClassifier drives acting from in the real product, instead of describing mood
    /// blind to which beat is the actual climax.</summary>
    private static object BuildClipIntensityEntry(JsonElement clip)
    {
        var clipNumber = clip.TryGetProperty("clip_number", out var cn) && cn.ValueKind == JsonValueKind.Number
            ? cn.GetInt32() : 0;
        int? intensity = clip.TryGetProperty("performance_intensity", out var pi) && pi.ValueKind == JsonValueKind.Number
            ? pi.GetInt32() : null;
        var cueSource = clip.TryGetProperty("dialogue_or_vo", out var dv) && dv.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(dv.GetString())
            ? dv.GetString()
            : (clip.TryGetProperty("visual_description", out var vd) && vd.ValueKind == JsonValueKind.String
                ? vd.GetString() : null);
        return new { clip_number = clipNumber, performance_intensity = intensity, cue = TruncateChars(cueSource ?? "", 60) };
    }

    private static object? BuildSceneClipIntensitySummary(JsonElement scene, HashSet<string> sceneIds)
    {
        var sceneId = scene.TryGetProperty("scene_id", out var sid) ? sid.GetString() ?? "" : "";
        if (sceneId.Length == 0 || !sceneIds.Contains(sceneId)) return null;
        if (!scene.TryGetProperty("clips", out var clipsEl) || clipsEl.ValueKind != JsonValueKind.Array) return null;

        var clips = new List<object>();
        foreach (var clip in clipsEl.EnumerateArray())
            clips.Add(BuildClipIntensityEntry(clip));
        return new { scene_id = sceneId, clips };
    }

    private static string? ExtractClipIntensitySummary(string? clipPlanJson, HashSet<string> sceneIds)
    {
        if (string.IsNullOrWhiteSpace(clipPlanJson) || sceneIds.Count == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(clipPlanJson);
            var scenesEl = doc.RootElement.TryGetProperty("scenes", out var s) ? s : doc.RootElement;
            var scenes = new List<object>();
            foreach (var scene in scenesEl.EnumerateArray())
            {
                var summary = BuildSceneClipIntensitySummary(scene, sceneIds);
                if (summary is not null) scenes.Add(summary);
            }
            return scenes.Count > 0 ? JsonSerializer.Serialize(new { scenes }) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string TruncateChars(string s, int maxChars) => s.Length <= maxChars ? s : s[..maxChars] + "…";

    /// <summary>Splits raw book text into paragraphs (blank-line-separated blocks), trimmed and with
    /// empty blocks dropped — the unit every source_paragraphs citation refers to by 1-based index.</summary>
    private static List<string> SplitBookIntoParagraphs(string bookText)
    {
        var normalized = bookText.Replace("\r\n", "\n").Replace('\r', '\n');
        var blocks = CommonRegex.Split(normalized, @"\n\s*\n+");
        return blocks.Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
    }

    /// <summary>Builds the paragraph-tagged text that is actually uploaded — each paragraph prefixed
    /// by a stable [P#] marker the model can cite directly instead of a free-text quote.</summary>
    private static string BuildIndexedBookText(List<string> paragraphs)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            sb.Append('[').Append('P').Append(i + 1).Append(']').Append('\n');
            sb.Append(paragraphs[i]).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>Builds a local, zero-LLM-cost annotated copy of the book: the same paragraph-tagged
    /// text, with each paragraph additionally showing which scene(s) cited it as source. The original
    /// book file is never modified — this is always a separate output file.</summary>
    private static string BuildAnnotatedBookText(List<string> paragraphs, Dictionary<int, List<string>> citingScenesByParagraph)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            var paragraphNumber = i + 1;
            sb.Append('[').Append('P').Append(paragraphNumber).Append(']');
            if (citingScenesByParagraph.TryGetValue(paragraphNumber, out var scenes) && scenes.Count > 0)
                sb.Append(" (used by: ").Append(string.Join(", ", scenes)).Append(')');
            sb.Append('\n').Append(paragraphs[i]).Append("\n\n");
        }
        return sb.ToString();
    }

    private static readonly Regex ParagraphCitationRegex = new(@"^P(\d+)$", RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>Checks every EDL scene's source_paragraphs cite an id that actually exists in the
    /// indexed book — catches a hallucinated citation a text-search approach could never detect.</summary>
    private static List<string> ValidateParagraphCitations(string edlJson, int maxParagraphId)
    {
        var warnings = new List<string>();
        List<JsonElement> scenes;
        try
        {
            scenes = ParseEdlSceneElements(edlJson);
        }
        catch
        {
            return warnings; // EDL parse failures are already reported elsewhere
        }
        foreach (var scene in scenes)
        {
            var sceneId = scene.TryGetProperty("scene_id", out var sid) ? sid.GetString() : "?";
            if (!scene.TryGetProperty("source_paragraphs", out var sp) || sp.ValueKind != JsonValueKind.Array) continue;
            foreach (var p in sp.EnumerateArray())
            {
                var text = p.GetString() ?? "";
                var match = ParagraphCitationRegex.Match(text);
                if (!match.Success)
                {
                    warnings.Add($"Scene {sceneId} cites malformed source_paragraphs entry '{text}' (expected 'P<n>').");
                    continue;
                }
                var n = int.Parse(match.Groups[1].Value);
                if (n < 1 || n > maxParagraphId)
                    warnings.Add($"Scene {sceneId} cites source_paragraphs '{text}' — out of range (book has {maxParagraphId} paragraphs).");
            }
        }
        return warnings;
    }

    private static readonly Regex SceneHeadingLineRegex = new(@"^\s*(INT\.|EXT\.|INT/EXT\.|I/E\.|EST\.)", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>Splits raw Fountain text into per-scene chunks (heading line + body), in order — used
    /// to hand the clip-planning stage only the text relevant to its current batch, not the whole
    /// screenplay every call.</summary>
    private static List<string> SplitFountainIntoScenes(string fountainText)
    {
        var lines = fountainText.Replace("\r\n", "\n").Split('\n');
        var scenes = new List<string>();
        var current = new List<string>();
        foreach (var line in lines)
        {
            if (SceneHeadingLineRegex.IsMatch(line))
            {
                if (current.Count > 0) scenes.Add(string.Join("\n", current));
                current = new List<string> { line };
            }
            else if (current.Count > 0)
            {
                current.Add(line);
            }
            // Lines before the first heading (title page) are dropped — not needed for clip planning.
        }
        if (current.Count > 0) scenes.Add(string.Join("\n", current));
        return scenes;
    }

    /// <summary>Builds the version of the Fountain that gets uploaded for the dual-attach experiment
    /// — same text, with an [S#] tag before each scene, mirroring the book's [P#] paragraph tags so a
    /// classifier attached to both files can navigate either one the same way.</summary>
    private static string BuildSceneTaggedFountainText(string fountainText)
    {
        var scenes = SplitFountainIntoScenes(fountainText);
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < scenes.Count; i++)
            sb.Append('[').Append('S').Append(i + 1).Append(']').Append('\n').Append(scenes[i]).Append("\n\n");
        return sb.ToString();
    }

    private static List<JsonElement> ParseEdlSceneElements(string edlJson)
    {
        using var doc = JsonDocument.Parse(edlJson);
        var root = doc.RootElement;
        var scenesEl = root.TryGetProperty("scenes", out var s) ? s : root;
        return scenesEl.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    /// <summary>Approximates the safe dialogue word count for a target video model's max clip length,
    /// using the same head/tail/words-per-second constants <see cref="ClipDurationEstimator"/> uses to
    /// compute real durations — so the LLM-facing prompt hint tracks the actual target model instead of
    /// a fixed guess. Different video models have different max clip lengths (and some have none set
    /// in the catalog yet, falling back to the generic default), so this must never be a hardcoded
    /// constant like the pilot's original "about 35 words" turned out to be.</summary>
    private static int ApproxSafeDialogueWordCount(int maxSeconds)
    {
        var budget = Math.Max(ClipDurationEstimator.MinSeconds, maxSeconds - ClipDurationEstimator.DialogueModelPaddingSeconds);
        var speechBudget = budget - ClipDurationEstimator.SpeechHeadSeconds - ClipDurationEstimator.SpeechTailSeconds;
        var words = (int)Math.Floor(Math.Max(1, speechBudget) * ClipDurationEstimator.DialogueWordsPerSecond);
        return Math.Max(10, words);
    }

    /// <summary>Shared clip-shape/duration guidance for both clip-plan instruction variants below —
    /// mirrors the real product's <c>ClipDurationEstimator</c>: clip COUNT follows the scene's actual
    /// dialogue turns and action beats (never a fixed target), and duration is not the model's guess —
    /// it's recomputed locally afterward from action_class/delivery via the same formula the product
    /// uses, so ask for those instead of an estimated_duration_seconds number. The dialogue split
    /// threshold is derived from <paramref name="maxSeconds"/> (the actual target video model's max
    /// clip length via <see cref="ClipDurationEstimator.ResolveBoundsForModel"/>), not a fixed number —
    /// this is only a first-pass hint anyway; <c>ExpandClipsInScene</c> mechanically guarantees the
    /// split afterward regardless of model compliance.</summary>
    private static string BuildClipCountAndDurationGuidance(int maxSeconds)
    {
        var safeWords = ApproxSafeDialogueWordCount(maxSeconds);
        return $$"""
        Break each scene into clips at its NATURAL dialogue-turn and action-beat boundaries — one clip
        per distinct spoken line/turn and per distinct visual action beat. Do NOT target a fixed clip
        count or a fixed seconds-per-clip; a scene with three lines of dialogue and one action beat gets
        four clips, a wordless scene gets as many clips as it has distinct visual beats. When a scene
        compresses a REPEATED occurrence (the same ritual/action happening multiple times — several
        nights, several attempts), give it one clip per occurrence with a deliberate tension arc across
        them: vary camera distance/lens and performance_intensity (for example rising tension, a peak,
        then a release) — do not just restate the same shot description for every repetition.
        HARD: the target video model's max clip length is {{maxSeconds}}s — if a single Fountain
        dialogue/VO turn for this scene runs longer than about {{safeWords}} words, split it across two
        or more consecutive clips (each carrying its own dialogue_or_vo portion of that turn) rather than
        one clip holding the whole line — a single long turn silently produces a duration estimate
        clamped to the model's max clip length, understating how long it actually runs. (This is a
        first-pass target only — any clip that still comes back too long is mechanically split afterward
        regardless, so do your best rather than counting words exactly.)
        For "action_class" (silent clips only — omit or use "dialogue" when dialogue_or_vo is non-empty),
        choose exactly one of: "big_action" (fast/violent movement), "establishing" (opening/orienting
        shot of a location), "hold" (a still beat, no new action), or "default" (ordinary silent action).
        For "delivery", use "dialogue" for spoken lines, or "voiceover_internal" for narration/V.O.
        Do not provide estimated_duration_seconds yourself — it is computed separately from these fields.
        Every clip's "beat_id" must be one of the approved beat plan's real ids (below) that this clip's
        content actually comes from — never invent an id and never leave it null when a real match exists.
        For each scene, set "negative_prompt": a comma-separated list of 5-15 concrete, era/style-specific
        exclusion tokens preventing anachronisms in this scene's setting (e.g. for an 1840s interior: "no
        modern wristwatches, no electric light bulbs, no plastic, no zippers, no printed logos"); tailor it
        to this scene's actual period/place, not a generic list. Per clip, set "continuation": "none" for
        an ordinary independent shot, or "continues_previous" only when this clip is meant to pick up
        exactly where the previous clip's last frame left off (same unbroken camera move/action) rather
        than cut to a new setup — use "continues_previous" sparingly, only for genuine continuous action.
        Split "sound" into two fields: "ambient_layer" (background tone: room hum, wind, crowd — usually
        continuous through the clip) and "foley_layer" (specific discrete sound effects tied to an
        on-screen action: footsteps, a door, cloth). Do NOT include a per-clip score/music field — the
        real product's music generator only accepts one continuous prompt per SCENE (no per-clip
        timestamped control), so score/music intent belongs solely in the audio-plan stage, grounded in
        this clip's "performance_intensity" rather than invented independently per clip.
        Add "depth_of_field" (e.g. "shallow, subject isolated" or "deep, background legible") and
        "color_grading" (a short palette/mood descriptor) per clip.
        """;
    }

    private static string BuildClipPlanInstruction(string edlSliceJson, string fountainExcerpt, string beatPlanJson, int maxSeconds)
    {
        const string instructions = """
            Using ONLY the scenes below (the book, approved Fountain, and full EDL are already established
            in this session — do not ask for them again), produce a CLIP-LEVEL SHOT PLAN as a single JSON
            object for just these scenes — no markdown fences, no commentary. Shape:
            {
              "scenes": [
                { "scene_id": "S1", "negative_prompt": "...",
                   "clips": [
                  { "clip_number": 1, "action_class": "default", "delivery": "dialogue",
                     "visual_description": "...", "camera_directive": "shot type + lens, e.g. tight close-up, 85mm",
                     "performance_intensity": 7, "performance_note": "short acting note",
                     "dialogue_or_vo": "exact line or fragment from the Fountain scene below, or empty",
                     "ambient_layer": "...", "foley_layer": "...",
                     "depth_of_field": "...", "color_grading": "...", "continuation": "none",
                     "beat_id": "B2" }
                ] }
              ]
            }

            """;
        return instructions + BuildClipCountAndDurationGuidance(maxSeconds) +
            "\n\ndialogue_or_vo must be an exact line or short fragment actually present in the Fountain " +
            "text below for that scene — never invent dialogue. Cover every scene_id listed below exactly " +
            "once. Return JSON only.\n\nAPPROVED BEAT PLAN (for beat_id citation):\n" + beatPlanJson +
            "\n\nEDL SCENES (this batch only):\n" + edlSliceJson +
            "\n\nFOUNTAIN TEXT FOR THESE SCENES ONLY:\n" + fountainExcerpt;
    }

    /// <summary>Dual-attach variant of <see cref="BuildClipPlanInstruction"/>: no session memory to
    /// rely on (this is an independent, non-chained call), so the cast/wardrobe/location context is
    /// passed explicitly, and the book + Fountain are referenced by their attached files plus an
    /// explicit paragraph/scene-tag layout instead of an inlined excerpt.</summary>
    private static string BuildDualAttachClipPlanInstruction(string edlSliceJson, string castLocationJson, string beatPlanJson, int maxSeconds)
    {
        const string instructions = """
            The complete source book and the complete approved Fountain screenplay are BOTH attached to
            this call as files — the book's paragraphs are tagged [P1], [P2], ...; the Fountain's scenes
            are tagged [S1], [S2], ... in the same style. This is an independent call with no memory of
            any earlier turn, so the approved cast/wardrobe/location package and beat plan are included
            below explicitly.

            Using ONLY the EDL scenes below, produce a CLIP-LEVEL SHOT PLAN as a single JSON object for
            just these scenes — no markdown fences, no commentary. Shape:
            {
              "scenes": [
                { "scene_id": "S1", "negative_prompt": "...",
                   "clips": [
                  { "clip_number": 1, "action_class": "default", "delivery": "dialogue",
                     "visual_description": "...", "camera_directive": "shot type + lens, e.g. tight close-up, 85mm",
                     "performance_intensity": 7, "performance_note": "short acting note",
                     "dialogue_or_vo": "exact line or fragment from the Fountain, or empty",
                     "ambient_layer": "...", "foley_layer": "...",
                     "depth_of_field": "...", "color_grading": "...", "continuation": "none",
                     "beat_id": "B2" }
                ] }
              ]
            }

            LAYOUT — use this to navigate the two attached files instead of reading them end to end: each
            EDL scene below carries "source_paragraphs" (the book's [P#] tags relevant to it) and its own
            "scene_id" (the Fountain's matching [S#] tag) — go directly to those tags in the attached
            files for source grounding and exact wording.

            """;
        return instructions + BuildClipCountAndDurationGuidance(maxSeconds) +
            "\n\ndialogue_or_vo must be an exact line or short fragment actually present in that scene's " +
            "tagged Fountain section — never invent dialogue. Cover every scene_id listed below exactly " +
            "once. Return JSON only.\n\nAPPROVED BEAT PLAN (for beat_id citation):\n" + beatPlanJson +
            "\n\nAPPROVED CAST, WARDROBE, AND LOCATIONS:\n" + castLocationJson +
            "\n\nEDL SCENES (this batch only):\n" + edlSliceJson;
    }

    private static string MergeScenesBatches(List<string> batchJsonTexts)
    {
        var docs = new List<JsonDocument>();
        try
        {
            var allScenes = new List<JsonElement>();
            foreach (var batchJson in batchJsonTexts)
            {
                var doc = JsonDocument.Parse(batchJson);
                docs.Add(doc);
                var scenesEl = doc.RootElement.TryGetProperty("scenes", out var s) ? s : doc.RootElement;
                foreach (var scene in scenesEl.EnumerateArray())
                    allScenes.Add(scene);
            }
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("scenes");
                writer.WriteStartArray();
                foreach (var scene in allScenes) scene.WriteTo(writer);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        finally
        {
            foreach (var d in docs) d.Dispose();
        }
    }

    /// <summary>
    /// Replaces whatever duration the model may have guessed with a deterministic value from the
    /// product's own <see cref="ClipDurationEstimator"/> — the same formula real clips are timed with
    /// (speech-rate word/syllable count for dialogue; a word-count curve keyed on action_class for
    /// silent beats). No extra API call: this is a pure local computation over fields the model already
    /// returned (dialogue_or_vo, visual_description, action_class, delivery).
    /// </summary>
    private readonly record struct ExpandedClip(JsonElement Source, string Dialogue, string Continuation, int ClipNumber, string CameraNudge);

    /// <summary>Small, local (zero-LLM-cost) camera nudges applied to continuation parts of a
    /// mechanically split monologue, cycled by part index, so back-to-back split clips aren't
    /// visually identical — without a follow-up LLM call re-authoring camera per part.</summary>
    private static readonly string[] ContinuationCameraNudges =
    {
        "; push in slightly tighter for the continuation",
        "; hold steady, subtle reframe",
        "; slow drift closer",
    };

    /// <summary>
    /// Deterministically splits any clip whose dialogue_or_vo exceeds the target video model's max
    /// clip length (<paramref name="maxSeconds"/>, resolved once per run via
    /// <see cref="ClipDurationEstimator.ResolveBoundsForModel"/> — never a hardcoded constant, since
    /// different video models have different max clip lengths), using the same
    /// <see cref="ClipDurationEstimator.DialogueExceedsModelMax"/> /
    /// <see cref="ClipDurationEstimator.SplitDialogueToFitModelMax"/> logic the real product's
    /// <c>ExpandLongDialogueBeats</c> uses — a code-level guarantee, not a prompt instruction the
    /// model can silently ignore. A real test run proved the prompt-only "split it yourself"
    /// instruction (<see cref="BuildClipCountAndDurationGuidance"/>) is not reliable enough on its
    /// own: 3 of ~50 dialogue clips in one Tell-Tale Heart run still came back unsplit despite the
    /// explicit instruction. This runs after generation and never depends on model compliance.
    /// Like the real product, the split otherwise keeps the source clip's camera/visual fields
    /// stable across parts; only a small local nudge (<see cref="ContinuationCameraNudges"/>) varies
    /// per continuation part, so no new LLM call is introduced for what is a rare fallback path.
    /// </summary>
    private static List<ExpandedClip> ExpandClipsInScene(JsonElement clipsArray, int maxSeconds)
    {
        var result = new List<ExpandedClip>();
        var nextNumber = 1;
        foreach (var clip in clipsArray.EnumerateArray())
        {
            var dialogue = clip.TryGetProperty("dialogue_or_vo", out var d) ? d.GetString() ?? "" : "";
            var delivery = clip.TryGetProperty("delivery", out var dl) ? dl.GetString() ?? "none" : "none";
            var originalContinuation = clip.TryGetProperty("continuation", out var c) ? c.GetString() ?? "none" : "none";

            if (string.IsNullOrWhiteSpace(dialogue) || !ClipDurationEstimator.DialogueExceedsModelMax(dialogue, delivery, maxSeconds))
            {
                result.Add(new ExpandedClip(clip, dialogue, originalContinuation, nextNumber++, ""));
                continue;
            }

            var parts = ClipDurationEstimator.SplitDialogueToFitModelMax(dialogue, delivery, maxSeconds);
            for (var i = 0; i < parts.Count; i++)
            {
                var continuation = i == 0 ? originalContinuation : "continues_previous";
                var nudge = i == 0 ? "" : ContinuationCameraNudges[(i - 1) % ContinuationCameraNudges.Length];
                result.Add(new ExpandedClip(clip, parts[i], continuation, nextNumber++, nudge));
            }
        }
        return result;
    }

    private static void WriteRecomputedClipObject(
        Utf8JsonWriter writer,
        ExpandedClip expanded,
        int minSeconds,
        int maxSeconds,
        int absMaxSeconds)
    {
        var clip = expanded.Source;
        var visual = clip.TryGetProperty("visual_description", out var v) ? v.GetString() ?? "" : "";
        var actionClass = clip.TryGetProperty("action_class", out var ac) ? ac.GetString() ?? "" : "";
        var delivery = clip.TryGetProperty("delivery", out var dl) ? dl.GetString() ?? "none" : "none";
        var duration = ClipDurationEstimator.Estimate(
            expanded.Dialogue, visual, actionClass, delivery, minSeconds, maxSeconds, absMaxSeconds);
        var originalCameraDirective = clip.TryGetProperty("camera_directive", out var cd) ? cd.GetString() ?? "" : "";
        var cameraDirective = expanded.CameraNudge.Length == 0
            ? originalCameraDirective
            : originalCameraDirective + expanded.CameraNudge;

        writer.WriteStartObject();
        foreach (var clipProp in clip.EnumerateObject())
        {
            if (clipProp.NameEquals("estimated_duration_seconds")) continue; // replaced below
            if (clipProp.NameEquals("dialogue_or_vo")) continue; // replaced below
            if (clipProp.NameEquals("continuation")) continue; // replaced below
            if (clipProp.NameEquals("clip_number")) continue; // renumbered below
            if (clipProp.NameEquals("camera_directive")) continue; // nudged below
            clipProp.WriteTo(writer);
        }
        writer.WriteNumber("clip_number", expanded.ClipNumber);
        writer.WriteString("dialogue_or_vo", expanded.Dialogue);
        writer.WriteString("continuation", expanded.Continuation);
        writer.WriteString("camera_directive", cameraDirective);
        writer.WriteNumber("estimated_duration_seconds", duration);
        writer.WriteEndObject();
    }

    private static void WriteRecomputedSceneObject(
        Utf8JsonWriter writer,
        JsonElement scene,
        int minSeconds,
        int maxSeconds,
        int absMaxSeconds)
    {
        foreach (var prop in scene.EnumerateObject())
        {
            if (prop.NameEquals("clips") && prop.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName("clips");
                writer.WriteStartArray();
                foreach (var expanded in ExpandClipsInScene(prop.Value, maxSeconds))
                    WriteRecomputedClipObject(writer, expanded, minSeconds, maxSeconds, absMaxSeconds);
                writer.WriteEndArray();
            }
            else
            {
                prop.WriteTo(writer);
            }
        }
    }

    private static string RecomputeClipDurations(
        string clipPlanJson, int minSeconds, int maxSeconds, int absMaxSeconds)
    {
        using var doc = JsonDocument.Parse(clipPlanJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("scenes");
            writer.WriteStartArray();
            var scenesEl = doc.RootElement.TryGetProperty("scenes", out var s) ? s : doc.RootElement;
            foreach (var scene in scenesEl.EnumerateArray())
            {
                writer.WriteStartObject();
                WriteRecomputedSceneObject(writer, scene, minSeconds, maxSeconds, absMaxSeconds);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildJudgeInstruction(string fountainText)
    {
        const string instructions = """
            You are an expert Hollywood Screenplay Coverage Executive and AI Film Director. The complete
            source book is attached to this call as a file — this is an independent, fresh evaluation
            call with no memory of how the screenplay below was produced; judge directly against the
            attached book, not against any assumed generation process.

            Independently judge the APPROVED FOUNTAIN SCREENPLAY below across these 6 dimensions
            (score 1-10 each, use the full range):
            1. Adaptation Fidelity & Source Coverage
            2. Character Disambiguation & Casting Clarity (stable visual descriptions; age/era variants disambiguated)
            3. AI Video Directibility ("show, don't tell" — concrete, camera-observable, one clip-sized action per beat)
            4. Dramatic Pacing & Structure
            5. Dialogue Authenticity & Subtext
            6. Sound Design & Background Music Scoring

            Also decide productionReady (true/false) independent of the 1-10 scores — false for any single
            deal-breaking issue (invented major plot, broken/unusable structure, closed-cast violation),
            even if the averaged scores look fine. List each such issue in disqualifyingIssues.

            Return ONLY valid JSON matching exactly:
            {
              "adaptationFidelity": 8.5, "characterDisambiguation": 9.0, "aiVideoDirectibility": 8.0,
              "dramaticPacing": 7.5, "dialogueAuthenticity": 8.5, "soundDesignMusic": 8.0,
              "overallQualitativeScore": 8.25, "productionReady": true, "disqualifyingIssues": [],
              "rationale": "Detailed evaluation rationale..."
            }
            """;
        return instructions + "\n\nAPPROVED FOUNTAIN SCREENPLAY:\n" + fountainText;
    }

    private static void PrintJudgeSummary(string judgeModel, string judgeJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(judgeJson);
            var root = doc.RootElement;
            var overall = root.TryGetProperty("overallQualitativeScore", out var o) ? o.ToString() : "?";
            var ready = root.TryGetProperty("productionReady", out var pr) ? pr.ToString() : "?";
            Console.WriteLine($"   judge={judgeModel} overallQualitativeScore={overall}/10 productionReady={ready}");
            if (root.TryGetProperty("disqualifyingIssues", out var issues) && issues.ValueKind == JsonValueKind.Array)
            {
                foreach (var issue in issues.EnumerateArray())
                    Console.WriteLine($"   DISQUALIFYING: {issue.GetString()}");
            }
        }
        catch
        {
            Console.WriteLine("   (judge review saved, but could not be summarized — see judge_review json file)");
        }
    }

    /// <summary>Runs one or two independent, book-attached LLM judges against the approved Fountain.
    /// Every judge call is a fresh <see cref="XaiResponsesClient.CompleteWithFilesAsync"/> — never
    /// previous_response_id — so a judge never inherits the conversation memory of the pipeline that
    /// generated the content (self-judging bias). The stored-file path currently supports enabled
    /// xAI chat judges only; a second judge remains an independent opinion.
    /// Returns (judgeModel, relativeFileName) per judge actually run, for the manifest.</summary>
    private static async Task<List<(string JudgeModel, string RelativeFileName)>> RunJudgesAsync(
        XaiResponsesClient client,
        IEnumerable<string?> judgeModels,
        IReadOnlyList<string> attachedFileIds,
        string fountainText,
        double judgeTemperature,
        string outDir,
        string fileNamePrefix,
        string stageLabel,
        Action<XaiResponsesClient.SessionTurnResult, string> track,
        CancellationToken ct)
    {
        var results = new List<(string, string)>();
        var distinctModels = judgeModels
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var judgeModel in distinctModels)
        {
            var judgeSlug = judgeModel.Replace('/', '_').Replace(':', '_');
            var relativeFileName = $"{fileNamePrefix}.{judgeSlug}.json";
            var judgePath = Path.Combine(outDir, relativeFileName);
            if (File.Exists(judgePath))
            {
                Console.WriteLine($"♻️  {stageLabel} (judge: {judgeModel}): reusing cached review.");
            }
            else
            {
                Console.WriteLine($"⚖️  {stageLabel}: independent LLM judge ({judgeModel}), book attached...");
                var judgeInstruction = BuildJudgeInstruction(fountainText);
                var result = await client.CompleteWithFilesAsync(
                    judgeModel, attachedFileIds, judgeInstruction, ct, judgeTemperature).ConfigureAwait(false);
                track(result, judgeModel);
                var judgeJson = ExtractJson(result.OutputText);
                await File.WriteAllTextAsync(judgePath, PrettyJson(judgeJson), ct).ConfigureAwait(false);
                Console.WriteLine($"   response_id={result.ResponseId} request_bytes={result.RequestBytesSent} (independent call, book attached)");
            }
            PrintJudgeSummary(judgeModel, await File.ReadAllTextAsync(judgePath, ct).ConfigureAwait(false));
            results.Add((judgeModel, relativeFileName));
        }
        return results;
    }

    private static bool IsExpired(long? expiresAtUnixSeconds) =>
        expiresAtUnixSeconds is { } exp && DateTimeOffset.FromUnixTimeSeconds(exp) <= DateTimeOffset.UtcNow;

    private static bool UsesXaiResponsesApi(string modelId, out string error)
    {
        var entry = SupportedModelCatalog.Find(modelId, ModelCapability.Chat);
        if (entry is null || !entry.Enabled)
        {
            error = $"'{modelId}' is not an enabled chat model in models_catalog.json.";
            return false;
        }
        if (entry.Provider != ModelProviderFamily.Xai)
        {
            error = $"'{modelId}' belongs to provider '{entry.ProviderId}', not xAI.";
            return false;
        }
        error = "";
        return true;
    }

    /// <summary>
    /// The dual-attach experiment deliberately uses independently-addressable artifact files rather
    /// than response-id chaining. Their filenames are stable for easy inspection, so clear only
    /// their cache entries when the generation provenance changes; the uploaded book remains valid
    /// and is never uploaded again merely because a model or prompt revision changed.
    /// </summary>
    private static void InvalidateDualAttachArtifactCache(string outDir, SessionRecord session)
    {
        var names = new[]
        {
            "cast_and_locations_dualattach_full.json",
            "screenplay_dualattach_full.fountain",
            "fountain_tagged_full.txt",
            "edit_decision_list_dualattach_full.json",
            "clip_shot_plan_dualattach_full.json",
            "audio_plan_dualattach_full.json",
            "validation_report_dualattach_full.json",
            "adaptation_package_dualattach_full.json",
        };
        foreach (var name in names)
        {
            var path = Path.Combine(outDir, name);
            if (File.Exists(path)) File.Delete(path);
        }
        foreach (var path in Directory.EnumerateFiles(outDir, "judge_review_dualattach_full.*.json"))
            File.Delete(path);

        session.FountainFileIdAlt = null;
        session.FountainFileShaAlt = null;
    }

    private static string Fmt(double? value) => value?.ToString() ?? "none";

    /// <summary>Accumulates real token usage (input/output/cached) per model from the Responses
    /// API's own "usage" field — see <see cref="XaiResponsesClient.SessionTurnResult.UsageJson"/>.
    /// This is the actual billed quantity; byte counts are only a rough proxy for it.</summary>
    private static void TrackUsage(Dictionary<string, (long Input, long Output, long Cached)> tokensByModel, string modelId, string? usageJson)
    {
        if (string.IsNullOrWhiteSpace(usageJson)) return;
        try
        {
            using var doc = JsonDocument.Parse(usageJson);
            var root = doc.RootElement;
            var input = root.TryGetProperty("input_tokens", out var i) ? i.GetInt64() : 0;
            var output = root.TryGetProperty("output_tokens", out var o) ? o.GetInt64() : 0;
            var cached = 0L;
            if (root.TryGetProperty("input_tokens_details", out var d) && d.TryGetProperty("cached_tokens", out var c))
                cached = c.GetInt64();
            var prior = tokensByModel.TryGetValue(modelId, out var existing) ? existing : (0, 0, 0);
            tokensByModel[modelId] = (prior.Item1 + input, prior.Item2 + output, prior.Item3 + cached);
        }
        catch { /* usage field absent/malformed — cost summary just omits this call */ }
    }

    private static readonly Dictionary<string, (double InputPerM, double OutputPerM)> ModelPricingCache = new();

    /// <summary>Reuses the product's own per-model pricing (models_catalog.json) rather than
    /// hardcoding a second copy of it here.</summary>
    private static (double InputPerM, double OutputPerM)? LookupModelPricing(string workspaceRoot, string modelId)
    {
        if (ModelPricingCache.TryGetValue(modelId, out var cached)) return cached;
        try
        {
            var catalogPath = Path.Combine(workspaceRoot, "host", "PageToMovie.Core", "config", "models_catalog.json");
            if (!File.Exists(catalogPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
            foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
            {
                if (m.TryGetProperty("id", out var idEl) && idEl.GetString() == modelId &&
                    m.TryGetProperty("inputCostPerMillionTokens", out var inEl) &&
                    m.TryGetProperty("outputCostPerMillionTokens", out var outEl))
                {
                    var pricing = (inEl.GetDouble(), outEl.GetDouble());
                    ModelPricingCache[modelId] = pricing;
                    return pricing;
                }
            }
        }
        catch { /* fall through */ }
        return null;
    }

    private static readonly Dictionary<string, int?> ModelMaxOutputTokensCache = new();

    /// <summary>Reuses the product's own per-model <c>maxOutputTokens</c> (models_catalog.json)
    /// rather than hardcoding a second copy of it here — same direct-JSON-read pattern as
    /// <see cref="LookupModelPricing"/>. Returns null when the catalog has no entry, or the entry
    /// has no maxOutputTokens (e.g. an unresearched/new model id) — callers must fall back to a
    /// flat default rather than guess in that case.</summary>
    private static int? LookupMaxOutputTokens(string workspaceRoot, string modelId)
    {
        if (ModelMaxOutputTokensCache.TryGetValue(modelId, out var cached)) return cached;
        try
        {
            var catalogPath = Path.Combine(workspaceRoot, "host", "PageToMovie.Core", "config", "models_catalog.json");
            if (!File.Exists(catalogPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
            foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
            {
                if (m.TryGetProperty("id", out var idEl) && idEl.GetString() == modelId)
                {
                    int? maxOut = m.TryGetProperty("maxOutputTokens", out var outEl) && outEl.ValueKind == JsonValueKind.Number
                        ? outEl.GetInt32()
                        : null;
                    ModelMaxOutputTokensCache[modelId] = maxOut;
                    return maxOut;
                }
            }
        }
        catch { /* fall through */ }
        return null;
    }

    /// <summary>Flat batch size used before this model-aware sizing existed, and still the fallback
    /// for any model without a researched <c>maxOutputTokens</c> in models_catalog.json — it was
    /// picked after a real, observed truncation: one unbatched 68-scene clip-plan call silently ran
    /// out of output budget and stopped mid-response (a "scenes_CONTINUED": [] marker acknowledging
    /// it, only 9/68 scenes actually covered).</summary>
    private const int FlatDefaultBatchSize = 8;

    /// <summary>
    /// Upper bound on computed batch size regardless of how much output budget a model claims to
    /// have. Very large batches trade away more than they're worth even when the token math allows
    /// them: a failed/truncated batch loses more work, per-batch progress/caching checkpoints
    /// (SaveSession after every batch) get coarser, and single requests get slower/more prone to
    /// timeouts. 40 is roughly 5x the historical flat default — enough to meaningfully cut the
    /// number of chained calls for a large-budget model without letting one call cover an entire
    /// book's scene list.
    /// </summary>
    private const int MaxReasonableBatchSize = 40;

    /// <summary>
    /// Fraction of a model's documented maxOutputTokens this pilot will actually plan to use per
    /// batch call. Deliberately well under 100%: maxOutputTokens numbers are ceilings on VISIBLE
    /// output only (reasoning/tool-call tokens aren't counted against them per-provider, but still
    /// consume the same generation pass and push visible content later into it), real per-scene
    /// output size varies scene-to-scene (a dialogue-heavy or multi-clip scene can run well above the
    /// empirical average used below), and this is exactly the class of bug (silent truncation) that
    /// motivated batching in the first place — so the margin needs to survive being wrong about the
    /// average, not just be right on average. 0.6 keeps 40% headroom.
    /// </summary>
    private const double OutputBudgetSafetyMargin = 0.6;

    /// <summary>
    /// Empirical average output size of one scene's worth of clip-shot-plan JSON (a scene object with
    /// its nested "clips" array — camera directive, performance intensity/note, dialogue/VO fragment,
    /// sound, etc.), in tokens (~4 chars/token). Measured across real artifacts on disk:
    /// evals/adaptation_sessions/nick_and_me/clip_shot_plan.json averaged ~483 tokens/scene (52
    /// scenes), but the richer-schema variants (adding negative_prompt, ambient_layer, foley_layer,
    /// depth_of_field, color_grading, continuation per clip) ran far higher —
    /// clip_shot_plan_dualattach_full.json averaged ~984 tokens/scene for nick_and_me (53 scenes) and
    /// ~1478 tokens/scene for the_tell-tale_heart (12 scenes, richer per-scene detail). This constant
    /// uses that observed worst case (rounded up) rather than the average, since the batch-size
    /// formula needs to stay safe for the richest schema variant a stage might actually produce, not
    /// just the leanest one seen so far.
    /// </summary>
    private const int ClipPlanEstimatedTokensPerScene = 1500;

    /// <summary>
    /// Empirical average output size of one scene's worth of audio-plan JSON, in tokens (~4
    /// chars/token). Measured across real artifacts on disk: nick_and_me/audio_plan.json averaged
    /// ~121 tokens/scene (52 scenes) but audio_plan_dualattach_full.json ran ~310/scene (53 scenes)
    /// and the_tell-tale_heart's audio_plan_dualattach_full.json ~332/scene (12 scenes). Rounded up
    /// from the observed worst case for the same reason as <see cref="ClipPlanEstimatedTokensPerScene"/>.
    /// </summary>
    private const int AudioPlanEstimatedTokensPerScene = 350;

    /// <summary>
    /// Computes a per-call scene batch size from a model's real output-token budget instead of the
    /// historical flat 8: <c>min(MaxReasonableBatchSize, floor(maxOutputTokens * safety_margin /
    /// estimatedTokensPerScene))</c>, never below 1. Falls back to <see cref="FlatDefaultBatchSize"/>
    /// verbatim — never a computed guess — when <paramref name="maxOutputTokens"/> is null (model not
    /// yet researched in models_catalog.json), so behavior for an unresearched model is unchanged
    /// from before this feature existed.
    /// </summary>
    private static int ComputeSafeBatchSize(int? maxOutputTokens, int estimatedTokensPerScene)
    {
        if (maxOutputTokens is not { } budget || budget <= 0) return FlatDefaultBatchSize;
        var usableBudget = budget * OutputBudgetSafetyMargin;
        var computed = (int)(usableBudget / estimatedTokensPerScene);
        return Math.Max(1, Math.Min(MaxReasonableBatchSize, computed));
    }

    /// <summary>
    /// Prints the per-model token/cost breakdown (as before) and — new — persists it to
    /// <c>cost_summary{suffix}.json</c> next to the other artifacts. Previously this data only ever
    /// reached the console: real, accurate numbers (actual billed `usage.input_tokens`/
    /// `output_tokens` from the API response, priced via the product's own models_catalog.json) were
    /// computed correctly but thrown away the moment the process exited, so the pilot's core "prove
    /// staged calls are much smaller than resending the whole book" claim had no durable evidence
    /// beyond a scene-count validation report. This closes that gap for every future run.
    /// </summary>
    private static void PrintCostSummary(
        string workspaceRoot,
        Dictionary<string, (long Input, long Output, long Cached)> tokensByModel,
        string? outDir = null,
        string suffix = "",
        long totalRequestBytes = 0,
        long? bookBytes = null,
        bool bookResent = false)
    {
        double totalCost = 0;
        var anyPricing = false;
        var perModel = new List<Dictionary<string, object?>>();
        foreach (var (modelId, usage) in tokensByModel)
        {
            var pricing = LookupModelPricing(workspaceRoot, modelId);
            var inputM = usage.Input / 1_000_000.0;
            var outputM = usage.Output / 1_000_000.0;
            double? cost = null;
            if (pricing is { } p)
            {
                anyPricing = true;
                cost = inputM * p.InputPerM + outputM * p.OutputPerM;
                totalCost += cost.Value;
                Console.WriteLine(
                    $"   {modelId}: input={usage.Input} (cached={usage.Cached}) output={usage.Output} tokens — ${cost:F4}");
            }
            else
            {
                Console.WriteLine($"   {modelId}: input={usage.Input} (cached={usage.Cached}) output={usage.Output} tokens — price unknown");
            }
            perModel.Add(new Dictionary<string, object?>
            {
                ["model"] = modelId,
                ["inputTokens"] = usage.Input,
                ["cachedInputTokens"] = usage.Cached,
                ["outputTokens"] = usage.Output,
                ["costUsd"] = cost,
            });
        }
        if (anyPricing)
            Console.WriteLine($"💵 Estimated real cost this run: ${totalCost:F4} (from actual billed token usage, not byte counts)");

        if (outDir is null) return;
        var summary = new Dictionary<string, object?>
        {
            ["generatedAtUtc"] = DateTime.UtcNow.ToString("O"),
            ["bookBytes"] = bookBytes,
            ["bookResentDuringRun"] = bookResent,
            ["totalNewRequestBytesThisRun"] = totalRequestBytes,
            ["totalCostUsd"] = anyPricing ? totalCost : (double?)null,
            ["perModel"] = perModel,
        };
        try
        {
            var path = Path.Combine(outDir, $"cost_summary{suffix}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"💾 Cost summary saved: {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Could not save cost_summary{suffix}.json: {ex.Message}");
        }
    }

    private static SessionRecord? LoadSession(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveSession(string path, SessionRecord session)
    {
        var json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Strips ```/```json fences a model may wrap plain-text output in.</summary>
    private static string StripFences(string text)
    {
        text = text.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0) return text;
        var withoutOpenFence = text[(firstNewline + 1)..];
        var closeIdx = withoutOpenFence.LastIndexOf("```", StringComparison.Ordinal);
        return closeIdx >= 0 ? withoutOpenFence[..closeIdx].Trim() : withoutOpenFence.Trim();
    }

    /// <summary>Extracts the first balanced top-level JSON value (object OR array) from model
    /// output, tolerating code fences and preamble/postamble commentary.
    ///
    /// Originally only recognized <c>{</c> — a real bug this caused: when a cast/locations stage
    /// response deviated from the instructed <c>{"cast_seeds":{"characters":[...]}}</c> shape and
    /// came back as a bare top-level array (<c>[{char1},{char2},...]</c> with no wrapper object),
    /// the old scan skipped past the array's opening <c>[</c> (not a <c>{</c>) and matched the
    /// FIRST character's own object instead — silently returning just one character's data as if
    /// it were the whole cast/locations payload, with no error anywhere (confirmed against a real
    /// Call of the Wild pilot run: cast_and_locations.json ended up containing only Buck's raw
    /// fields at the top level). Now recognizes whichever of <c>{</c>/<c>[</c> appears first and
    /// bracket-matches that type specifically (tracking depth only for the matching close-bracket,
    /// so nested mixed brackets inside don't confuse it), so a bare top-level array is returned
    /// intact rather than drilled into. Callers that expect an object with a "scenes"/"characters"
    /// property already have a tolerant fallback (treat the whole root as the array when the named
    /// property is absent) — this only had to stop feeding them a corrupted inner fragment.
    /// </summary>
    internal static string ExtractJson(string text)
    {
        var stripped = StripFences(text);
        for (var i = 0; i < stripped.Length; i++)
        {
            var openChar = stripped[i];
            char closeChar;
            if (openChar == '{') closeChar = '}';
            else if (openChar == '[') closeChar = ']';
            else continue;

            var depth = 0;
            for (var j = i; j < stripped.Length; j++)
            {
                if (stripped[j] == openChar) depth++;
                else if (stripped[j] == closeChar)
                {
                    depth--;
                    if (depth == 0)
                    {
                        var candidate = stripped[i..(j + 1)];
                        try
                        {
                            using var doc = JsonDocument.Parse(candidate);
                            if (doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                                return candidate;
                        }
                        catch { /* keep scanning */ }
                        break;
                    }
                }
            }
        }
        throw new InvalidOperationException("No JSON object or array found in model output.");
    }

    private static string PrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}
