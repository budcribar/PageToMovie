using PageToMovie.Core.Models;

namespace PageToMovie.Web.Components.Pages;

public abstract partial class AdaptationPageBase
{
    /// <summary>Static UI helpers for adaptation step strip, job panel, and progress display.</summary>
    public static class AdaptationStepUi
    {
        public static string NextStepLabel(string step) => step switch
        {
            "import_book" => "Import a screenplay, PDF, or text file",
            "fix_book_text" => "Prepare imported text, or import a screenplay",
            "sign_screenplay" => "Open Screenplay, edit if needed, then approve",
            "draft_screenplay" => "Create a screenplay draft from the book",
            "run_stage1" => "Build the screenplay from the book",
            "pin_characters" => "Approve cast voices and locked images on Characters",
            "run_stage2" => "Build the shot plan",
            "replan_stage2" => "Update the shot plan (screenplay changed)",
            "generate_clips" => "Open Scenes and create video clips",
            _ => "Looks complete — refine on Characters or Scenes",
        };

        /// <summary>Short operator copy when a background job finishes (no OCR/engine jargon).</summary>
        public static string OperatorJobDoneMessage(JobSnapshot snap) => snap.Kind switch
        {
            "book_prepare" => "Book text is ready",
            "book_import" => "Screenplay draft ready",
            "stage1" => snap.Message is { Length: > 0 } m && !m.Contains("quality=", StringComparison.Ordinal)
                ? m
                : "Screenplay draft ready",
            "stage2" => "Shot plan ready",
            _ => string.IsNullOrWhiteSpace(snap.Message) || snap.Message.Contains("quality=", StringComparison.Ordinal)
                ? "Step finished"
                : snap.Message!,
        };

        public static string NextStepAlertClass(string step) => step switch
        {
            "generate_clips" or "done" => "alert-success",
            "replan_stage2" or "fix_book_text" or "sign_screenplay" => "alert-warning",
            _ => "alert-info",
        };

        public static string JobKindLabel(string? kind) => kind switch
        {
            "book_prepare" => "book",
            "book_import" => "import",
            "stage1" => "screenplay",
            "stage2" => "shot plan",
            _ => kind ?? "",
        };

        /// <summary>
        /// Suggested path for /adaptation redirect.
        /// Prefers server <see cref="AdaptationStatus.NextStep"/> (from <see cref="StudioStateMachine.DetermineNextStep"/>);
        /// falls back to <see cref="StudioStateMachine.DeterminePhase"/> when NextStep is empty/unknown.
        /// </summary>
        public static string SuggestedStepPath(AdaptationStatus? status)
        {
            if (status is null) return "/adaptation/import";

            var fromNext = MapNextStepToPath(status.NextStep);
            if (fromNext is not null)
                return fromNext;

            return StudioStateMachine.DeterminePhase(status) switch
            {
                StudioPhase.SetupRequired or StudioPhase.ImportRequired or StudioPhase.TextExtractionPending
                    => "/adaptation/import",
                StudioPhase.ScreenplayDraft or StudioPhase.ScreenplayApproved
                    => "/adaptation/screenplay",
                StudioPhase.ShotPlanReady or StudioPhase.ReviewReady
                    => "/adaptation/shots",
                _ => "/adaptation/import",
            };
        }

        /// <returns>Mapped path, or null when <paramref name="nextStep"/> is empty/unrecognized.</returns>
        private static string? MapNextStepToPath(string? nextStep) => nextStep switch
        {
            null or "" => null,
            "import_book" or "fix_book_text" => "/adaptation/import",
            "sign_screenplay" or "draft_screenplay" or "run_stage1" => "/adaptation/screenplay",
            // Book strip routes through /adaptation — land on screenplay when cast is next
            // (Characters has its own top-nav step).
            "pin_characters" => "/adaptation/screenplay",
            // Shot plan rebuild lives under Adaptation; do not bounce to /scenes.
            "run_stage2" or "replan_stage2" or "generate_clips" or "done" => "/adaptation/shots",
            _ => null,
        };

        /// <summary>
        /// Book sub-strip: Screenplay tab unlocks once any import/source exists
        /// (aligned with <see cref="StudioStateMachine.DetectSourceType"/>, plus Stage1 package).
        /// </summary>
        public static bool OutlineEnabled(AdaptationStatus? status)
        {
            if (status is null) return false;

            if (StudioStateMachine.DetectSourceType(status.Book, status.Screenplay) != SourceDocumentType.None)
                return true;

            // Stage1 scenes package without fountain/book flags still unlocks the Screenplay tab.
            return status.Stage1.Present && status.Stage1.SceneCount > 0;
        }

        /// <summary>
        /// Book sub-strip: Shots tab unlocks once the screenplay is signed off
        /// (same approval gate as Cast — <see cref="StudioStateMachine.IsScreenplayApproved"/>).
        /// </summary>
        public static bool ShotsEnabled(AdaptationStatus? status) =>
            status is not null && StudioStateMachine.IsScreenplayApproved(status.Screenplay);

        /// <summary>
        /// Compact progress + Cancel while adaptation jobs run (operators and admin).
        /// Import page never shows this card (progress lives in the Import card).
        /// Admin can expand log after the job finishes; operators never see raw logs here.
        /// </summary>
        public static bool ShowJobPanel(bool isAdmin, JobSnapshot? job, string step)
        {
            if (job is null || step is "import" or "book")
                return false;

            var kind = job.Kind ?? "";
            var adaptationJob = kind is "stage1" or "stage2" or "book_prepare" or "book_import";
            if (job.Status is "running" or "queued")
                return adaptationJob || isAdmin;

            // Idle finished/error: admin-only so operators don't see leftover job cards
            if (!isAdmin)
                return false;
            return job.Status is "error" or "cancelled" ||
                   (job.Status == "done" && adaptationJob);
        }

        /// <summary>Merges job-reported and locally-tracked (log-scraped) progress into one index/total/waiting triple.</summary>
        public static (int Index, int Total, bool Waiting, int DisplayIndex) ComputeJobProgress(
            JobSnapshot job, int progressIndex, int progressTotal, bool jobRunning)
        {
            var index = Math.Max(job.Index, progressIndex);
            var total = Math.Max(job.Total, progressTotal);
            var waiting = jobRunning && AdaptationJobs.IsJobInFlightMessage(job.Message);
            var displayIndex = waiting && index >= total && total > 0
                ? Math.Max(0, total - 1)
                : index;
            return (index, total, waiting, displayIndex);
        }

        /// <summary>
        /// Progress-bar percent for a running job — never 0% or 100% while still running.
        /// When Total is missing or a long adapt call is in-flight, soft-crawls so the bar
        /// does not freeze at a single placeholder (old Total=0 → hard 35%).
        /// </summary>
        public static int ComputeProgressPercent(int displayIndex, int total, bool waiting, bool jobRunning)
            => ComputeProgressPercent(displayIndex, total, waiting, jobRunning, startedAt: null);

        public static int ComputeProgressPercent(
            int displayIndex, int total, bool waiting, bool jobRunning, DateTimeOffset? startedAt)
        {
            if (!jobRunning)
                return 100;

            int basePct;
            if (total <= 0)
            {
                // Soft indeterminate crawl ~12% → ~70% over a few minutes (no hard 35% stick).
                basePct = SoftCrawlPercent(startedAt, floor: 12, ceiling: 70, tauSeconds: 90);
            }
            else if (waiting)
            {
                var stepped = (int)Math.Round(100.0 * (displayIndex + 0.35) / total);
                // During long single-pass adapt, ease upward from the stepped floor so the bar
                // keeps moving between phase messages.
                var crawl = SoftCrawlPercent(startedAt, floor: stepped, ceiling: 88, tauSeconds: 120);
                basePct = Math.Max(stepped, crawl);
            }
            else
            {
                basePct = (int)Math.Round(100.0 * Math.Clamp(displayIndex, 0, total) / total);
            }

            return Math.Clamp(basePct, total > 0 ? 5 : 8, 92);
        }

        /// <summary>Asymptotic crawl floor→ceiling with half-life ~tauSeconds.</summary>
        public static int SoftCrawlPercent(DateTimeOffset? startedAt, int floor, int ceiling, double tauSeconds)
        {
            if (ceiling <= floor) return floor;
            if (startedAt is null) return floor;
            var sec = Math.Max(0, (DateTimeOffset.UtcNow - startedAt.Value).TotalSeconds);
            var t = 1.0 - Math.Exp(-sec / Math.Max(1.0, tauSeconds));
            return (int)Math.Round(floor + (ceiling - floor) * t);
        }

        /// <summary>
        /// Hide the "Next" banner when the current step already is the next action
        /// (avoids "Next: import…" on the Import page).
        /// Presentation keys off server <see cref="AdaptationStatus.NextStep"/>
        /// (<see cref="StudioStateMachine.DetermineNextStep"/>); do not invent a second banner vocabulary here.
        /// </summary>
        public static bool ShowNextStepBanner(AdaptationStatus? status, bool suppressGuidanceBanners, string step)
        {
            if (status is null) return false;
            // Never show "Next: approve…" while a draft is still being written
            if (suppressGuidanceBanners) return false;

            var next = status.NextStep ?? "";
            if (step is "import" or "book")
            {
                // On import: only after the pipeline is idle and they should leave Import
                // (draft exists → continue to screenplay). Don't say "approve" mid-import.
                if (next is "sign_screenplay" or "generate_clips" or "run_stage2" or "replan_stage2"
                    or "pin_characters")
                    return status.Screenplay.DraftExists;
                return next is "draft_screenplay" or "run_stage1";
            }
            if (step == "screenplay")
            {
                // Hide when the action is already "edit/approve screenplay"
                return next is not ("sign_screenplay" or "draft_screenplay" or "run_stage1");
            }
            if (step == "shots")
            {
                // Hide plain "build shot plan"; keep replan / go elsewhere.
                // Hide "generate_clips" too — the page's own "Open Scenes" button already
                // says this; it carries the hint as a hover tooltip instead.
                return next is not ("run_stage2" or "pin_characters" or "generate_clips");
            }
            return true;
        }
    }
}
