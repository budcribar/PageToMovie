using System.Text.Json.Nodes;
using PageToMovie.Web.Components.Pages;
using Xunit;

namespace PageToMovie.Tests;

public class AdminModelsCatalogSortTests
{
    [Fact]
    public void FilteredModels_preserves_catalog_order_until_a_header_is_clicked()
    {
        var list = CreateList(
            Model("zeta", "Zeta", "Video", "prov-b", enabled: true),
            Model("alpha", "Alpha", "Chat", "prov-a", enabled: false));

        Assert.Equal(new[] { "zeta", "alpha" }, Ids(list));
        Assert.Equal("⇅", list.SortArrow(AdminModelsCatalog.AdminModelsList.SortId));
    }

    [Fact]
    public void ToggleSort_first_click_sorts_ascending_second_click_reverses()
    {
        var list = CreateList(
            Model("zeta", "Zeta", "Video", "prov-b", enabled: true),
            Model("alpha", "Alpha", "Chat", "prov-a", enabled: false));

        list.ToggleSort(AdminModelsCatalog.AdminModelsList.SortId);
        Assert.True(list._sortAscending);
        Assert.Equal(new[] { "alpha", "zeta" }, Ids(list));
        Assert.Equal("▲", list.SortArrow(AdminModelsCatalog.AdminModelsList.SortId));
        Assert.Equal("⇅", list.SortArrow(AdminModelsCatalog.AdminModelsList.SortName));

        list.ToggleSort(AdminModelsCatalog.AdminModelsList.SortId);
        Assert.False(list._sortAscending);
        Assert.Equal(new[] { "zeta", "alpha" }, Ids(list));
        Assert.Equal("▼", list.SortArrow(AdminModelsCatalog.AdminModelsList.SortId));
    }

    [Fact]
    public void ToggleSort_switching_column_resets_to_ascending()
    {
        var list = CreateList(
            Model("zeta", "Zeta", "Video", "prov-b", enabled: true),
            Model("alpha", "Alpha", "Chat", "prov-a", enabled: false));

        list.ToggleSort(AdminModelsCatalog.AdminModelsList.SortId);
        list.ToggleSort(AdminModelsCatalog.AdminModelsList.SortId);
        Assert.False(list._sortAscending);

        list.ToggleSort(AdminModelsCatalog.AdminModelsList.SortName);
        Assert.Equal(AdminModelsCatalog.AdminModelsList.SortName, list._sortBy);
        Assert.True(list._sortAscending);
        Assert.Equal(new[] { "alpha", "zeta" }, Ids(list));
    }

    [Fact]
    public void Sort_enabled_is_boolean_false_then_true_when_ascending()
    {
        var list = CreateList(
            Model("on-model", "On", "Chat", "prov-a", enabled: true),
            Model("off-model", "Off", "Chat", "prov-a", enabled: false));

        list.ToggleSort(AdminModelsCatalog.AdminModelsList.SortEnabled);
        Assert.Equal(new[] { "off-model", "on-model" }, Ids(list));

        list.ToggleSort(AdminModelsCatalog.AdminModelsList.SortEnabled);
        Assert.Equal(new[] { "on-model", "off-model" }, Ids(list));
    }

    [Fact]
    public void Sort_strings_are_case_insensitive()
    {
        var list = CreateList(
            Model("b", "bravo", "Chat", "Prov-B", enabled: true),
            Model("a", "ALPHA", "Chat", "prov-a", enabled: true),
            Model("c", "Charlie", "Chat", "PROV-C", enabled: true));

        list.ToggleSort(AdminModelsCatalog.AdminModelsList.SortName);
        Assert.Equal(new[] { "a", "b", "c" }, Ids(list));

        list.ToggleSort(AdminModelsCatalog.AdminModelsList.SortProvider);
        Assert.Equal(new[] { "a", "b", "c" }, Ids(list));
    }

    [Fact]
    public void Sort_capability_and_provider_are_case_insensitive()
    {
        var list = CreateList(
            Model("v", "V", "video", "zeta", enabled: true),
            Model("c", "C", "Chat", "Alpha", enabled: true),
            Model("i", "I", "IMAGE", "mid", enabled: true));

        list.ToggleSort(AdminModelsCatalog.AdminModelsList.SortCapability);
        Assert.Equal(new[] { "c", "i", "v" }, Ids(list));

        list.ToggleSort(AdminModelsCatalog.AdminModelsList.SortProvider);
        Assert.Equal(new[] { "c", "i", "v" }, Ids(list));
    }

    [Fact]
    public void Sort_reviewed_uses_dates_chronologically()
    {
        var list = CreateList(
            Model("new", "New", "Chat", "prov-a", enabled: true, verified: "2026-08-01", priced: "2026-08-02"),
            Model("old", "Old", "Chat", "prov-a", enabled: true, verified: "2025-01-15", priced: "2025-02-01"),
            Model("unset", "Unset", "Chat", "prov-a", enabled: true));

        list.ToggleSort(AdminModelsCatalog.AdminModelsList.SortReviewed);
        Assert.Equal(new[] { "unset", "old", "new" }, Ids(list));

        list.ToggleSort(AdminModelsCatalog.AdminModelsList.SortReviewed);
        Assert.Equal(new[] { "new", "old", "unset" }, Ids(list));
    }

    [Fact]
    public void Sort_reviewed_breaks_ties_on_pricing_date()
    {
        var list = CreateList(
            Model("later-price", "Later", "Chat", "prov-a", enabled: true, verified: "2026-01-01", priced: "2026-06-01"),
            Model("earlier-price", "Earlier", "Chat", "prov-a", enabled: true, verified: "2026-01-01", priced: "2026-02-01"));

        list.ToggleSort(AdminModelsCatalog.AdminModelsList.SortReviewed);
        Assert.Equal(new[] { "earlier-price", "later-price" }, Ids(list));
    }

    [Fact]
    public void Sort_applies_to_the_filtered_in_memory_list()
    {
        var list = CreateList(
            Model("keep-z", "Zed", "Chat", "prov-a", enabled: true),
            Model("drop", "Drop", "Video", "prov-b", enabled: true),
            Model("keep-a", "Abe", "Chat", "prov-a", enabled: true));
        list._filterCapability = "Chat";

        list.ToggleSort(AdminModelsCatalog.AdminModelsList.SortName);
        Assert.Equal(new[] { "keep-a", "keep-z" }, Ids(list));
    }

    private static AdminModelsCatalog.AdminModelsList CreateList(params JsonObject[] models)
    {
        var list = new AdminModelsCatalog.AdminModelsList(new AdminModelsCatalog());
        list._modelList.AddRange(models);
        return list;
    }

    private static JsonObject Model(
        string id,
        string name,
        string capability,
        string provider,
        bool enabled,
        string? verified = null,
        string? priced = null)
    {
        var o = new JsonObject
        {
            ["id"] = id,
            ["displayName"] = name,
            ["capability"] = capability,
            ["provider"] = provider,
            ["enabled"] = enabled,
        };
        if (verified is not null) o["lastVerifiedAt"] = verified;
        if (priced is not null) o["pricingLastReviewedAt"] = priced;
        return o;
    }

    private static string[] Ids(AdminModelsCatalog.AdminModelsList list) =>
        list.FilteredModels.Select(m => m["id"]?.ToString() ?? "").ToArray();
}
