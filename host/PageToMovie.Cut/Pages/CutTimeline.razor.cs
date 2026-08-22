using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Cut.Cut;
using PageToMovie.Cut.Services;

namespace PageToMovie.Cut.Pages;

public partial class CutTimeline
{
    [Inject] private IJSRuntime? Js { get; set; }

    [Parameter] public IReadOnlyList<CutClip> Clips { get; set; } = [];
    [Parameter] public CutClip? Selected { get; set; }
    [Parameter] public EventCallback<CutClip> SelectedChanged { get; set; }
    [Parameter] public double PlayheadSec { get; set; }
    [Parameter] public EventCallback<double> PlayheadChanged { get; set; }
    [Parameter] public List<CutTextClip> TextClips { get; set; } = [];
    [Parameter] public bool HasAudio { get; set; }
    [Parameter] public string? AudioName { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public bool PlayDisabled { get; set; }
    [Parameter] public EventCallback OnPlay { get; set; }
    [Parameter] public EventCallback OnSkipStart { get; set; }
    [Parameter] public EventCallback OnStepBack { get; set; }
    [Parameter] public EventCallback OnStepForward { get; set; }
    [Parameter] public EventCallback OnEdited { get; set; }

    private ElementReference _scroll = default;
    private ElementReference _inner = default;
    private double _pxPerSec = CutTimelineLayout.DefaultPxPerSec;
    private int _lastCount = -1;
    private bool _needFit;
    private DragKind _drag;
    private CutClip? _trimClip;
    private CutTextBlock? _trimText;
    private double _dragOriginX;
    private double _dragMarkIn;
    private double _dragMarkOut;
    private double _dragTextStart;
    private double _dragTextHold;
    private double _rangeA = -1;
    private double _rangeB = -1;
    private int? _joinMenu;
    private string? _selectedTextId;

    private CutTimelineLayout Layout => CutTimelineLayout.Build(Clips, _pxPerSec);
    private IReadOnlyList<CutTextBlock> TextBlocks => CutTextTrack.Build(Clips, TextClips, _pxPerSec);
    private bool HasText => TextBlocks.Count > 0;
    private bool HasRange => _rangeA >= 0 && _rangeB >= 0 && Math.Abs(_rangeB - _rangeA) >= CutRangeDelete.MinSpanSeconds;
    private double RangeLo => Math.Min(_rangeA, _rangeB);
    private double RangeHi => Math.Max(_rangeA, _rangeB);
    private double RangePx => (RangeHi - RangeLo) * _pxPerSec;
    private string PlayheadClock => CutTimelineLayout.Clock(PlayheadSec);
    private string TotalClock => CutTimelineLayout.Clock(Layout.PlayableSec > 0 ? Layout.PlayableSec : Layout.TotalSec);

    protected override void OnInitialized()
    {
        _scroll = default;
        _inner = default;
    }

    protected override void OnParametersSet()
    {
        if (Clips.Count != _lastCount)
        {
            _lastCount = Clips.Count;
            _needFit = Clips.Count > 0;
            _joinMenu = null;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_needFit && Clips.Count > 0)
        {
            _needFit = false;
            await FitAsync();
        }
    }

    private async Task FitAsync()
    {
        if (Js is null)
            return;
        try
        {
            var rect = await Js.InvokeAsync<JsRect>("PageToMovieCut.elementRect", _scroll);
            var width = rect.Width > 40 ? rect.Width - 16 : 800;
            _pxPerSec = CutTimelineLayout.FitPxPerSec(Math.Max(Layout.TotalSec, 1), width);
            StateHasChanged();
        }
        catch (JSException)
        {
            _pxPerSec = CutTimelineLayout.DefaultPxPerSec;
        }
    }

    private void ZoomIn() =>
        _pxPerSec = CutTimelineLayout.ZoomInPxPerSec(_pxPerSec);

    private void ZoomOut() =>
        _pxPerSec = CutTimelineLayout.ZoomOutPxPerSec(_pxPerSec);

    private void OnZoomSlider(ChangeEventArgs e)
    {
        var raw = Convert.ToString(e.Value, CultureInfo.InvariantCulture);
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
            _pxPerSec = CutTimelineLayout.ClampPxPerSec(px);
    }

    private async Task SelectClip(CutClip clip)
    {
        _joinMenu = null;
        _selectedTextId = null;
        await SelectedChanged.InvokeAsync(clip);
    }

    private void SelectText(CutTextBlock block)
    {
        _joinMenu = null;
        _selectedTextId = block.Id;
    }

    private async Task OnTextRowDownAsync(PointerEventArgs e)
    {
        if (Busy)
            return;
        _joinMenu = null;
        var t = await TimeAtAsync(e.ClientX);
        await AddTextAtAsync(t);
    }

    private async Task AddTextAtAsync(double startSec)
    {
        if (Busy)
            return;
        var title = CutTextTrack.Add(TextClips, startSec);
        _selectedTextId = title.Id;
        await OnEdited.InvokeAsync();
    }

    private async Task SetTextLabelAsync(CutTextBlock block, ChangeEventArgs e)
    {
        CutTextTrack.SetLabel(block, Convert.ToString(e.Value, CultureInfo.InvariantCulture));
        await OnEdited.InvokeAsync();
    }

    private async Task DeleteTextAsync(CutTextBlock block)
    {
        CutTextTrack.Delete(block, TextClips);
        if (_selectedTextId == block.Id)
            _selectedTextId = null;
        await OnEdited.InvokeAsync();
    }

    private async Task BeginTextTrimAsync(PointerEventArgs e, CutTextBlock block, bool fromStart)
    {
        if (Busy)
            return;
        _drag = fromStart ? DragKind.TextIn : DragKind.TextOut;
        _trimText = block;
        _dragOriginX = e.ClientX;
        _dragTextStart = block.StartSec;
        _dragTextHold = block.Seconds;
        _selectedTextId = block.Id;
        await CapturePointerAsync(e.PointerId);
    }

    private async Task BeginTrimAsync(PointerEventArgs e, CutClip clip, bool markIn)
    {
        if (Busy || clip.SelectedTake is null)
            return;
        _drag = markIn ? DragKind.TrimIn : DragKind.TrimOut;
        _trimClip = clip;
        _dragOriginX = e.ClientX;
        _dragMarkIn = clip.MarkIn;
        _dragMarkOut = clip.MarkOut;
        await CapturePointerAsync(e.PointerId);
        await SelectClip(clip);
    }

    private async Task BeginRulerAsync(PointerEventArgs e)
    {
        if (Busy)
            return;
        _joinMenu = null;
        var t = await TimeAtAsync(e.ClientX);
        _drag = DragKind.Range;
        _rangeA = t;
        _rangeB = t;
        await CapturePointerAsync(e.PointerId);
    }

    private async Task BeginPlayheadAsync(PointerEventArgs e)
    {
        if (Busy)
            return;
        _drag = DragKind.Playhead;
        await CapturePointerAsync(e.PointerId);
        await SeekToAsync(await TimeAtAsync(e.ClientX));
    }

    private async Task OnPointerMove(PointerEventArgs e)
    {
        if (_drag == DragKind.None)
            return;
        if (TryApplyClipTrim(e.ClientX) || TryApplyTextTrim(e.ClientX))
            return;

        var t = await TimeAtAsync(e.ClientX);
        if (_drag == DragKind.Range)
            _rangeB = t;
        else if (_drag == DragKind.Playhead)
            await SeekToAsync(t);
    }

    private bool TryApplyClipTrim(double clientX)
    {
        if (_drag is not (DragKind.TrimIn or DragKind.TrimOut) || _trimClip is not { } clip)
            return false;
        var dt = (clientX - _dragOriginX) / _pxPerSec;
        if (_drag == DragKind.TrimIn)
            CutTimelineLayout.TrimIn(clip, _dragMarkIn + dt);
        else
            CutTimelineLayout.TrimOut(clip, _dragMarkOut + dt);
        return true;
    }

    private bool TryApplyTextTrim(double clientX)
    {
        if (_drag is not (DragKind.TextIn or DragKind.TextOut) || _trimText is not { } text)
            return false;
        var dt = (clientX - _dragOriginX) / _pxPerSec;
        if (_drag == DragKind.TextIn)
            ApplyTextInTrim(text, dt);
        else
            CutTextTrack.SetHold(text, _dragTextHold + dt);
        return true;
    }

    private void ApplyTextInTrim(CutTextBlock text, double dt)
    {
        if (text.Kind != CutTextKind.Title)
        {
            CutTextTrack.SetHold(text, _dragTextHold - dt);
            return;
        }

        var end = _dragTextStart + _dragTextHold;
        var start = Math.Max(0, _dragTextStart + dt);
        start = Math.Min(start, end - CutCard.MinHoldSeconds);
        CutTextTrack.SetStart(text, start);
        CutTextTrack.SetHold(text, end - start);
    }

    private async Task OnPointerUp(PointerEventArgs e)
    {
        if (_drag == DragKind.None)
            return;
        var kind = _drag;
        _drag = DragKind.None;
        if (kind is DragKind.TrimIn or DragKind.TrimOut or DragKind.TextIn or DragKind.TextOut)
        {
            _trimClip = null;
            _trimText = null;
            await OnEdited.InvokeAsync();
            return;
        }

        if (kind == DragKind.Range)
        {
            var t = await TimeAtAsync(e.ClientX);
            _rangeB = t;
            if (Math.Abs(_rangeB - _rangeA) < 0.08)
            {
                await SeekToAsync(_rangeA);
                _rangeA = -1;
                _rangeB = -1;
            }
        }
    }

    private async Task DeleteRangeAsync()
    {
        if (!HasRange || Busy)
            return;
        if (!CutTimelineLayout.TryDeleteTimelineRange(Clips, RangeLo, RangeHi, out _))
            return;
        _rangeA = -1;
        _rangeB = -1;
        await OnEdited.InvokeAsync();
    }

    private async Task OnKey(KeyboardEventArgs e)
    {
        if (await TryHandleDeleteKeyAsync(e.Key))
            return;
        if (e.Key is "-" or "_" or "Minus" or "Subtract")
            ZoomOut();
        else if (e.Key is "+" or "=" or "Add")
            ZoomIn();
        else if (e.Key is "0" or "f" or "F")
            await FitAsync();
    }

    private async Task<bool> TryHandleDeleteKeyAsync(string key)
    {
        if (key is not ("Delete" or "Backspace"))
            return false;
        if (await TryDeleteSelectedTextAsync())
            return true;
        await DeleteRangeAsync();
        return true;
    }

    private async Task<bool> TryDeleteSelectedTextAsync()
    {
        if (_selectedTextId is not { } id)
            return false;
        foreach (var block in TextBlocks)
        {
            if (block.Id != id)
                continue;
            await DeleteTextAsync(block);
            return true;
        }

        return false;
    }

    private void ToggleJoinMenu(int afterIndex) =>
        _joinMenu = _joinMenu == afterIndex ? null : afterIndex;

    private async Task SetJoinAsync(int afterIndex, CutJoinKind kind)
    {
        if (afterIndex < 0 || afterIndex >= Clips.Count)
            return;
        Clips[afterIndex].JoinOverride = kind;
        _joinMenu = null;
        await OnEdited.InvokeAsync();
    }

    private async Task SeekToAsync(double timelineSec)
    {
        var total = Math.Max(Layout.TotalSec, 0);
        var t = Math.Clamp(timelineSec, 0, total);
        if (CutTimelineLayout.HitTest(Clips, t) is { } hit)
            await SelectedChanged.InvokeAsync(hit.Clip);
        await PlayheadChanged.InvokeAsync(t);
    }

    private async Task CapturePointerAsync(long pointerId)
    {
        if (Js is null)
            return;
        await Js.InvokeVoidAsync("PageToMovieCut.setPointerCapture", _inner, pointerId);
    }

    private async Task<double> TimeAtAsync(double clientX)
    {
        if (Js is null)
            return PlayheadSec;
        try
        {
            var rect = await Js.InvokeAsync<JsRect>("PageToMovieCut.elementRect", _inner);
            return Math.Max(0, (clientX - rect.X) / _pxPerSec);
        }
        catch (JSException)
        {
            return PlayheadSec;
        }
    }

    private static string JoinClass(CutTimelineJoinTick tick) =>
        "cut-tl-join"
        + (tick.SceneChange ? " is-scene" : "")
        + tick.Kind switch
        {
            CutJoinKind.Dissolve => " is-dissolve",
            CutJoinKind.Dip or CutJoinKind.FadeOut or CutJoinKind.FadeIn => " is-dip",
            CutJoinKind.FadeWhite => " is-white",
            CutJoinKind.CutToBlack => " is-black",
            _ => " is-cut",
        };

    private enum DragKind
    {
        None,
        Range,
        Playhead,
        TrimIn,
        TrimOut,
        TextIn,
        TextOut,
    }
}
