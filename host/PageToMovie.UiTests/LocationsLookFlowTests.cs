using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Locations page depth: generate 3 looks (fake image provider), pick a variant with the lock
/// tile, switch the preferred look — plus the Configuration page's media-folder connect flow
/// (the OPFS stub stands in for the real directory picker).
/// </summary>
[Collection("ui-pipeline")]
public class LocationsLookFlowTests
{
    private readonly PipelineFixture _fx;
    public LocationsLookFlowTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Generate_looks_then_lock_and_switch_preferred_variant()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToCharactersAsync(page, _fx.BaseUrl,
                "LocLook_" + Guid.NewGuid().ToString("N")[..6], "mary_had_a_lamb.fountain");

            await page.GetByTestId("nav-locations").ClickAsync();
            await Assertions.Expect(page.GetByTestId("loc-list-item").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await page.GetByTestId("loc-list-item").First.ClickAsync();

            // Generate 3 looks for the selected location (fake image provider answers instantly).
            var generate = page.GetByTestId("loc-generate-looks");
            await Assertions.Expect(generate).ToBeEnabledAsync(new() { Timeout = 15_000 });
            await generate.ClickAsync();

            // Variant tiles appear once the job lands.
            await Assertions.Expect(page.GetByTestId("loc-lock-v1")).ToBeVisibleAsync(new() { Timeout = 120_000 });
            await Assertions.Expect(page.GetByTestId("loc-lock-v2")).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Lock look #2 → its tile becomes the preferred one.
            await page.GetByTestId("loc-lock-v2").ClickAsync();
            await Assertions.Expect(
                page.Locator(".loc-variant-tile.is-preferred").Filter(new() { HasText = "#2" }))
                .ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Switch to look #1 — the preference follows.
            await page.GetByTestId("loc-lock-v1").ClickAsync();
            await Assertions.Expect(
                page.Locator(".loc-variant-tile.is-preferred").Filter(new() { HasText = "#1" }))
                .ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("locations-error")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Configuration_media_folder_connects_and_reports_name()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.CreateFreshProjectAsync(page, _fx.BaseUrl, "CfgMedia_" + Guid.NewGuid().ToString("N")[..6]);
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/configuration");
            await Ui.OpenConfigSectionAsync(page, "config-section-storage"); // media folder lives here

            var connect = page.GetByTestId("config-select-media-folder").First;
            await Assertions.Expect(connect).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await connect.ClickAsync();

            // The OPFS stub connects silently; the page shows the connected folder's name.
            // (Re-render can collapse the <details> section — reopen before asserting.)
            await Ui.OpenConfigSectionAsync(page, "config-section-storage");
            await Assertions.Expect(page.GetByTestId("config-media-folder-name")).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Assertions.Expect(page.GetByTestId("config-media-folder-name")).ToContainTextAsync("TestMediaFolder");
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Locking_location_look_persists_across_reload()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToCharactersAsync(page, _fx.BaseUrl,
                "LocPersist_" + Guid.NewGuid().ToString("N")[..6], "mary_had_a_lamb.fountain");

            await page.GetByTestId("nav-locations").ClickAsync();
            await Assertions.Expect(page.GetByTestId("loc-list-item").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await page.GetByTestId("loc-list-item").First.ClickAsync();

            var generate = page.GetByTestId("loc-generate-looks");
            await Assertions.Expect(generate).ToBeEnabledAsync(new() { Timeout = 15_000 });
            await generate.ClickAsync();

            await Assertions.Expect(page.GetByTestId("loc-lock-v1")).ToBeVisibleAsync(new() { Timeout = 120_000 });
            await page.GetByTestId("loc-lock-v1").ClickAsync();
            await Assertions.Expect(
                page.Locator(".loc-variant-tile.is-preferred").Filter(new() { HasText = "#1" }))
                .ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Reload page
            await page.ReloadAsync();
            await Assertions.Expect(page.GetByTestId("loc-list-item").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await page.GetByTestId("loc-list-item").First.ClickAsync();

            // Preferred locked look remains preferred after reload
            await Assertions.Expect(
                page.Locator(".loc-variant-tile.is-preferred").Filter(new() { HasText = "#1" }))
                .ToBeVisibleAsync(new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Editing_location_description_and_style_persists_across_reload()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToCharactersAsync(page, _fx.BaseUrl,
                "LocDesc_" + Guid.NewGuid().ToString("N")[..6], "mary_had_a_lamb.fountain");

            await page.GetByTestId("nav-locations").ClickAsync();
            await Assertions.Expect(page.GetByTestId("loc-list-item").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await page.GetByTestId("loc-list-item").First.ClickAsync();

            var descInput = page.GetByTestId("loc-look-panel").Locator("textarea").First;
            await Assertions.Expect(descInput).ToBeVisibleAsync(new() { Timeout = 15_000 });
            const string customDesc = "A rustic one-room classroom with timber walls and sunlight through large windows.";
            await descInput.FillAsync(customDesc);
            await descInput.BlurAsync();

            // Wait for autosave
            await page.WaitForTimeoutAsync(1200);

            // Reload page and reselect
            await page.ReloadAsync();
            await Assertions.Expect(page.GetByTestId("loc-list-item").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await page.GetByTestId("loc-list-item").First.ClickAsync();

            descInput = page.GetByTestId("loc-look-panel").Locator("textarea").First;
            await Assertions.Expect(descInput).ToHaveValueAsync(customDesc, new() { Timeout = 15_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Uploading_location_plate_image_locks_and_persists_as_preferred()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToCharactersAsync(page, _fx.BaseUrl,
                "LocUp_" + Guid.NewGuid().ToString("N")[..6], "mary_had_a_lamb.fountain");

            await page.GetByTestId("nav-locations").ClickAsync();
            await Assertions.Expect(page.GetByTestId("loc-list-item").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await page.GetByTestId("loc-list-item").First.ClickAsync();

            var uploadInput = page.Locator("input[type=file][accept*='image']").First;
            var png = TinyPng(64, 64);
            await uploadInput.SetInputFilesAsync(new FilePayload
            {
                Name = "location_plate.png",
                MimeType = "image/png",
                Buffer = png,
            });

            // Preferred plate element appears
            await Assertions.Expect(page.GetByTestId("loc-locked-plate")).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Reload and verify persistence
            await page.ReloadAsync();
            await Assertions.Expect(page.GetByTestId("loc-list-item").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await page.GetByTestId("loc-list-item").First.ClickAsync();
            await Assertions.Expect(page.GetByTestId("loc-locked-plate")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    private static byte[] TinyPng(int w, int h)
    {
        using var ms = new MemoryStream();
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); if (BitConverter.IsLittleEndian) Array.Reverse(len);
            ms.Write(len);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            ms.Write(typeBytes); ms.Write(data);
            var crc = Crc32(typeBytes.Concat(data).ToArray());
            var crcBytes = BitConverter.GetBytes(crc); if (BitConverter.IsLittleEndian) Array.Reverse(crcBytes);
            ms.Write(crcBytes);
        }
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BitConverter.GetBytes(w).Reverse().ToArray().CopyTo(ihdr, 0);
        BitConverter.GetBytes(h).Reverse().ToArray().CopyTo(ihdr, 4);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk("IHDR", ihdr);
        var raw = new byte[h * (1 + w * 4)];
        for (var y = 0; y < h; y++)
        {
            var o = y * (1 + w * 4);
            for (var x = 0; x < w; x++) { raw[o + 1 + x * 4] = 80; raw[o + 2 + x * 4] = 140; raw[o + 3 + x * 4] = 200; raw[o + 4 + x * 4] = 255; }
        }
        using var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            zs.Write(raw);
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }
}
