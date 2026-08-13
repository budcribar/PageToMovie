using PageToMovie.Core.Models;
using PageToMovie.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace PageToMovie.Web.Components.Pages;

public abstract partial class AdaptationPageBase
{
    /// <summary>Book / outline / shots pipeline actions for adaptation step pages.</summary>
    public sealed class AdaptationPipeline
    {
        private readonly AdaptationPageBase S;
        public AdaptationPipeline(AdaptationPageBase host) => S = host;

        public IBrowserFile? PendingFile;
        public int TotalMinutes = 5;
        public int ChunkPages = 10;
        public string Model = "";
        public bool Resume;

        public bool CanRunOutline =>
            S.Status is not null &&
            (S.Status.Book.ReadyForStage1 ||
             (S.Status.Stage1.Present &&
              S.Status.Stage1.SceneCount > 0 &&
              S.Status.Book.BookTextExists));

        public void OnFileSelected(InputFileChangeEventArgs e)
        {
            PendingFile = e.File;
            S.Message = $"Selected {e.File.Name} ({e.File.Size:N0} bytes)";
        }

        public async Task UploadAsync()
        {
            if (PendingFile is null) return;
            S.Busy = true;
            S.Error = null;
            try
            {
                const long max = 80 * 1024 * 1024;
                await using var stream = PendingFile.OpenReadStream(max);
                await S.Engine.UploadBookAsync(S.ProjectId, PendingFile.Name, stream);
                S.Message = $"Saved {PendingFile.Name}";
                PendingFile = null;
                await S.LoadAsync();
            }
            catch (Exception ex) { S.Error = ex.Message; }
            finally { S.Busy = false; }
        }

        public async Task PrepareBookAsync(bool forceVision)
        {
            S.Busy = true;
            S.Error = null;
            S.Message = null;
            try
            {
                await S.Jobs.EnsureHubAsync();
                await S.Engine.StartBookPrepareAsync(
                    S.ProjectId,
                    forceExtract: true,
                    forceVision: forceVision,
                    autoVision: true);
                S.Message = forceVision
                    ? "Re-reading book pages… watch the log below"
                    : "Preparing book… watch the log below";
                var jobs = await S.Engine.GetJobAsync();
                S.Jobs.Job = jobs?.Job;
                S.Jobs.StartJobPolling();
            }
            catch (Exception ex) { S.Error = ex.Message; }
            finally { S.Busy = false; }
        }

        /// <summary>
        /// Book → Fountain draft (and approve for shot build). Uses prompts/book_to_fountain.txt only.
        /// </summary>
        public async Task RunOutlineAsync()
        {
            S.Busy = true;
            S.Error = null;
            S.Message = null;
            try
            {
                await S.Jobs.EnsureHubAsync();
                S.Jobs.ProgressIndex = 0;
                S.Jobs.ProgressTotal = 10;
                await S.Engine.StartStage1Async(new StartStage1Request
                {
                    ProjectId = S.ProjectId,
                    TotalMinutes = TotalMinutes,
                    Model = Model,
                });
                S.Message = null; // live progress card is enough
                var jobs = await S.Engine.GetJobAsync();
                S.Jobs.Job = jobs?.Job;
                S.Jobs.AbsorbProgressFromSnapshot(S.Jobs.Job ?? new JobSnapshot());
                S.Jobs.StartJobPolling();
            }
            catch (Exception ex) { S.Error = ex.Message; }
            finally { S.Busy = false; }
        }

        public async Task RunShotsAsync()
        {
            S.Busy = true;
            S.Error = null;
            S.Message = null;
            try
            {
                await S.Jobs.EnsureHubAsync();
                S.Jobs.ProgressIndex = 0;
                S.Jobs.ProgressTotal = 10;
                await S.Engine.StartStage2Async(new StartStage2Request)
                {
                    ProjectId = S.ProjectId,
                    Scenes = "all",
                });
                S.Message = null; // live progress card is enough
                var jobs = await S.Engine.GetJobAsync();
                S.Jobs.Job = jobs?.Job;
                S.Jobs.AbsorbProgressFromSnapshot(S.Jobs.Job ?? new JobSnapshot());
                S.Jobs.StartJobPolling();
            }
            catch (Exception ex) { S.Error = ex.Message; }
            finally { S.Busy = false; }
        }
    }
}
