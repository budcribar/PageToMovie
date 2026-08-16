using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// A-3: drives the Characters operator flow through the UI (select a character → generate looks →
/// see the pick grid; voice section for speakers). These are the operator components that will be
/// extracted from Characters.razor (~3k lines), so behaviour must be pinned before the refactor.
/// </summary>
[Collection("ui-pipeline")]
public class CharactersFlowTests
{
    private readonly PipelineFixture _fx;
    public CharactersFlowTests(PipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Generate_looks_from_description_shows_the_pick_grid()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToCharactersAsync(page, _fx.BaseUrl, "CharUI_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            // Select the first character; its detail panel opens on the "choose a look route" state.
            await Assertions.Expect(page.GetByTestId("char-list-item").First).ToBeVisibleAsync(new() { Timeout = 60_000 });
            await page.GetByTestId("char-list-item").First.ClickAsync();

            // Choose the "generate from description" route → description form appears.
            await page.GetByTestId("char-route-generate").ClickAsync(new() { Timeout = 30_000 });
            var desc = page.GetByPlaceholder("How they look");
            await desc.WaitForAsync(new() { Timeout = 30_000 });
            if (string.IsNullOrWhiteSpace(await desc.InputValueAsync()))
                await desc.FillAsync("A pale, thin adult with dark hair and a dark wool coat, photoreal.");

            // Generate looks (fake image) → the variant pick grid appears with options.
            await page.GetByTestId("char-generate-looks").ClickAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("char-pick-grid")).ToBeVisibleAsync(new() { Timeout = 90_000 });
            await Assertions.Expect(page.GetByTestId("char-pick-card").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    [Fact]
    public async Task Speaking_character_shows_a_voice_section()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToCharactersAsync(page, _fx.BaseUrl, "CharVoice_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            // Narrator speaks, so its detail panel offers a voice section (not the silent/animal hint).
            await Assertions.Expect(page.GetByTestId("char-list-item").First).ToBeVisibleAsync(new() { Timeout = 60_000 });
            await page.EvaluateAsync(
                "() => [...document.querySelectorAll('[data-testid=char-list-item]')].find(b => /narrator/i.test(b.textContent))?.click()");
            await Assertions.Expect(page.GetByTestId("char-voice-section")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }

    // Selecting a character happens inside the CharactersCastList slice and the look panel (with
    // the photo upload) is rendered by the Characters page only once List._selected is set. After
    // the page split the page never re-rendered on that click, so the panel — and the upload —
    // were unreachable. This drives the real click → upload → thumbnail path.
    [Fact]
    public async Task Selecting_a_character_and_uploading_a_photo_sets_its_look()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await PipelineFlow.RunToCharactersAsync(page, _fx.BaseUrl, "CharUp_" + Guid.NewGuid().ToString("N")[..6], "tell_tale_heart.fountain");

            var item = page.Locator("[data-testid='char-list-item'][data-char-voice-only='false'][data-char-group='false']").First;
            await Assertions.Expect(item).ToBeVisibleAsync(new() { Timeout = 60_000 });
            var key = await item.GetAttributeAsync("data-char-key");
            await item.ClickAsync();

            // The click lives in the cast-list slice; the page must re-render to show the panel.
            await Assertions.Expect(item).ToHaveClassAsync(new Regex("\bactive\b"), new() { Timeout = 15_000 });
            var uploadRoute = page.GetByTestId("char-route-upload");
            await Assertions.Expect(uploadRoute).ToBeVisibleAsync(new() { Timeout = 30_000 });

            // Upload a real (tiny) PNG through the hidden InputFile inside the route label.
            var png = TinyPng(64, 64);
            await uploadRoute.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
            {
                Name = "face.png",
                MimeType = "image/png",
                Buffer = png,
            });

            // The saved look is the confirmation: the cast-list row for this character now shows an
            // <img> thumbnail instead of the empty placeholder.
            var row = page.Locator($"[data-testid='char-list-item'][data-char-key='{key}']");
            await Assertions.Expect(row.Locator("img.char-list-thumb")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            await Assertions.Expect(page.Locator(".alert-danger")).ToHaveCountAsync(0);
        }
        finally { await ctx.CloseAsync(); }
    }

    /// <summary>Minimal valid RGBA PNG (solid colour) so the upload exercises the real image path.</summary>
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
            for (var x = 0; x < w; x++) { raw[o + 1 + x * 4] = 200; raw[o + 2 + x * 4] = 150; raw[o + 3 + x * 4] = 120; raw[o + 4 + x * 4] = 255; }
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
