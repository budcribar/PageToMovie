using PageToMovie.Core.Models;

namespace PageToMovie.Web.Components.Pages;

public abstract partial class AdaptationPageBase
{
    /// <summary>Static UI helpers for adaptation step strip, job panel, and progress display.</summary>
    public static class AdaptationStepUi
    {
        private const string SignScreenplay = "sign_screenplay";
        private const string DraftScreenplay = "draft_screenplay";
        private const string RunStage1 = "run_stage1";
        private const string PinCharacters = "pin_characters";
        private const string RunStage2 = "run_stage2";
        private const string ReplanStage2 = "replan_stage2";
        private const string GenerateClips = "generate_clips";

        public static string NextStepLabel(string step) => step switch
        {
            "import_book" => "Import a screenplay, PDF, or text file",
            "fix_book_text" => "Prepare imported text, or import a screenplay",
            SignScreenplay => "Open Screenplay, edit if needed, then approve",
            DraftScreenplay => "Create a screenplay draft from the book",
            RunStage1 => "Build the screenplay from the book",
            PinCharacters => "Approve cast voices and locked images on Characters",
            RunStage2 => "Build the shot plan",
            ReplanStage2 => "Update the shot plan (screenplay changed)",
            GenerateClips => "Open Scenes and create video clips",
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
                : snap.Message,
        };

        public static string NextStepAlertClass(string step) => step switch
        {
            GenerateClips or "done" => "alert-success",
            ReplanStage2 or "fix_book_text" or SignScreenplay => "alert-warning",
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

        /// <summary>Suggested path for /adaptation redirect.</summary>
        public static string SuggestedStepPath(AdaptationStatus? status)
        {
            if (status is null) return "/adaptation/import";
            return status.NextStep switch
            {
                "import_book" or "fix_book_text" => "/adaptation/import",
                SignScreenplay or DraftScreenplay or RunStage1 => "/adaptation/screenplay",
                // The Book strip step routes through /adaptation. When cast is the next step the book itself is
                // done, so land on the screenplay editor — never bounce out to /characters (that has its own
                // strip step), or clicking Book from Cast just returns to Cast.
                PinCharacters => "/adaptation/screenplay",
                // Shot plan is still an Adaptation step (rebuild lives here). Scenes has its own nav item —
                // do not bounce /adaptation → /scenes or operators cannot find Rebuild shot plan.
                RunStage2 or ReplanStage2 or GenerateClips or "done" => "/adaptation/shots",
                _ => "/adaptation/import",
            };
        }

        /// <summary>Step strip: Screenplay tab unlocks once a draft/outline exists in some form.</summary>
        public static bool OutlineEnabled(AdaptationStatus? status) =>
            status is not null &&
            (status.Screenplay.DraftExists ||
             status.Book.ReadyForStage1 ||
             status.Book.BookTextExists ||
             status.Book.PdfExists ||
             (status.Stage1.Present && status.Stage1.SceneCount > 0));

        /// <summary>Step strip: Characters/Shot-plan tabs unlock once the screenplay is signed off.</summary>
        public static bool ShotsEnabled(AdaptationStatus? status) =>
            status is not null && status.Screenplay.ReadyForShots;

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
                if (next is SignScreenplay or GenerateClips or RunStage2 or ReplanStage2
                    or PinCharacters)
                    return status.Screenplay.DraftExists;
                return next is DraftScreenplay or RunStage1;
            }
            if (step == "screenplay")
            {
                // Hide when the action is already "edit/approve screenplay"
                return next is not (SignScreenplay or DraftScreenplay or RunStage1);
            }
            if (step == "shots")
            {
                // Hide plain "build shot plan"; keep replan / go elsewhere.
                // Hide "generate_clips" too — the page's own "Open Scenes" button already
                // says this; it carries the hint as a hover tooltip instead.
                return next is not (RunStage2 or PinCharacters or GenerateClips);
            }
            return true;
        }
    }
}
