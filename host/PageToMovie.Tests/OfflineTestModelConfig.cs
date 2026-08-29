using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine;

namespace PageToMovie.Tests;

internal static class OfflineTestModelConfig
{
    public static string Required(ModelCapability capability) =>
        SupportedModelCatalog.RequireDefaultModelIdForCapability(capability);

    public static string Required(string capability) =>
        SupportedModelCatalog.RequireDefaultModelIdForCapability(capability);

    public static Task ApplyAsync(ProjectStore store, string projectId) =>
        ApplyAsync(store, projectId, writeDecidedVision: true);

    /// <param name="writeDecidedVision">
    /// Stage 2 / generate require a decided medium. Tests that prove the fail-fast pass false.
    /// </param>
    public static async Task ApplyAsync(ProjectStore store, string projectId, bool writeDecidedVision)
    {
        await store.SaveConfigAsync(projectId, JsonSerializer.SerializeToElement(new
        {
            model_name = Required(ModelCapability.Video),
            image_model_name = Required(ModelCapability.Image),
            planning_model_name = Required(ModelCapability.Chat),
            chat_model_name = Required(ModelCapability.Chat),
            vision_model_name = Required(ModelCapability.Vision),
            quality_model_name = Required("video-review"),
            audio_model_name = Required(ModelCapability.Audio),
            voice_model_name = Required(ModelCapability.Voice)
        }));
        if (writeDecidedVision)
            WriteDecidedVision(store, projectId);
    }

    public static void WriteDecidedVision(
        ProjectStore store,
        string projectId,
        string medium = ProjectVisionMeta.MediumPhotoreal)
    {
        ProjectVisionMeta.Write(store.GetProjectDir(projectId), new ProjectVisionMeta.Document
        {
            VisualMedium = medium,
            DecidedBy = "adaptation",
        });
    }
}
