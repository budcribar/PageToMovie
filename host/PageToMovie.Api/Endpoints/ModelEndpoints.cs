using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using PageToMovie.Api.Auth;
using PageToMovie.Core.Auth;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.Collaboration;
using PageToMovie.Engine.ModelBacked;

namespace PageToMovie.Api;

public static class ModelEndpoints
{
    public static IEndpointRouteBuilder MapModelEndpoints(this IEndpointRouteBuilder app)
    {
        // <summary>Raw models_catalog.json for Blazor WASM bootstrap (public read).</summary>
        app.MapGet("/api/models/catalog-json", GetModelsCatalogJson);
        app.MapGet("/api/models", GetModels);
        return app;
    }

    private static IResult GetModelsCatalogJson(IUserContext user)
    {
    try
    {
        // Single source of truth: the catalog embedded in PageToMovie.Core (real, or the fake vendor
        // catalog in fakes mode). The WASM client hydrates from this so its dropdowns match the server.
        var raw = SupportedModelCatalog.GetEmbeddedCatalogJson();

        if (user.IsAdmin)
            return Results.Text(raw, JsonKeys.ApplicationJson);

        // Non-admin: strip labMode models so WASM bootstrap cannot offer them.
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("models", out var modelsEl)
            || modelsEl.ValueKind != System.Text.Json.JsonValueKind.Array)
            return Results.Text(raw, JsonKeys.ApplicationJson);

        using var streamOut = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(streamOut, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("models"))
                {
                    writer.WritePropertyName("models");
                    writer.WriteStartArray();
                    foreach (var m in modelsEl.EnumerateArray())
                    {
                        if (m.ValueKind == System.Text.Json.JsonValueKind.Object
                            && m.TryGetProperty("labMode", out var lab)
                            && lab.ValueKind == System.Text.Json.JsonValueKind.True)
                            continue;
                        m.WriteTo(writer);
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return Results.Text(System.Text.Encoding.UTF8.GetString(streamOut.ToArray()), JsonKeys.ApplicationJson);
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
    }
}

    private static IResult GetModels(string? capability, IUserContext user)
    {
    // Lab models are admin-only — never offer incomplete/experimental rows to regular users.
    var includeLab = user.IsAdmin;
    IReadOnlyList<SupportedModelDto> list;
    if (!string.IsNullOrWhiteSpace(capability) &&
        Enum.TryParse<ModelCapability>(capability, ignoreCase: true, out var cap))
    {
        list = SupportedModelCatalog.ForCapability(cap, includeLabModels: includeLab)
            .Select(SupportedModelCatalog.ToDto)
            .ToList();
    }
    else
    {
        list = SupportedModelCatalog.ToDtoList(enabledOnly: true, includeLabModels: includeLab);
    }

    return Results.Ok(new
    {
        ok = true,
        models = list,
        includeLabModels = includeLab,
        note =
            "User picks model ids only. Provider, API base, endpoint, and required env keys come from this catalog. " +
            "Lab-mode models are visible to admins only.",
    });
}
}
