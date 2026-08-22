using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Cut.Cut;
using PageToMovie.Cut.Services;

namespace PageToMovie.Cut.Pages;

public partial class CutTimeline
{
    [Inject] private IJSRuntime Js { get; set; } = default!;

    [Parameter] public IReadOnlyList<CutClip> Clips { get; set; } = [];
    [Parameter] public CutClip? Selected { get; set; }
    [Parameter] public EventCallback<CutClip> SelectedChanged { get; set; }
    [Parameter] public double PlayheadSec { get; set; }
    [Parameter] public EventCallback<double> PlayheadChanged { get; set; }
    [Parameter] public bool HasAudio { get; set; }
    [Parameter] public string? AudioName { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public bool PlayDisabled { get; set; }
    [Parameter] public EventCallback OnPlay { get; set; }
    [Parameter] public EventCallback OnSkipStart { get; set; }
    [Parameter] public EventCallback OnStepBack { get; set; }
    [Parameter] public EventCallback OnStepForward { get; set; }
    [Parameter] public EventCallback OnEdited { get; set; }

    private ElementReference _scroll;
    private ElementReference _inner;
    private double _pxPerSec = CutTimelineLayout.DefaultPxPerSec;
    private int _lastCount = -1;
    private bool _needFit;
    private DragKind _drag;
    private CutClip? _trimClip;
    private double _dragOriginX;
    private double _dragMarkIn;
    private double _dragMarkOut;
    private double _rangeA = -1;
    private double _rangeB = -1;
    private int? _joinMenu;

    private CutTimelineLayout Layout => CutTimelineLayout.Build(Clips, _pxPerSec);
    private bool HasRange => _rangeA >= 0 && _rangeB >= 0 && Math.Abs(_rangeB - _rangeA) >= CutRangeDelete.MinSpanSeconds;
    private double RangeLo => Math.Min(_rangeA, _rangeB);
    private double RangeHi => Math.Max(_rangeA, _rangeB);
    private double RangePx => (RangeHi - RangeLo) * _pxPerSec;
    private string PlayheadClock => CutTimelineLayout.Clock(PlayheadSec);
    private string TotalClock => CutTimelineLayout.Clock(Layout.PlayableSec > 0 ? Layout.PlayableSec : Layout.TotalSec);

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
        _pxPerSec = Math.Min(CutTimelineLayout.MaxPxPerSec, _pxPerSec * 1.35);

    private void ZoomOut() =>
        _pxPerSec = Math.Max(CutTimelineLayout.MinPxPerSec, _pxPerSec / 1.35);

    private async Task SelectClip(CutClip clip)
    {
        _joinMenu = null;
        await SelectedChanged.InvokeAsync(clip);
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
        await Js.InvokeVoidAsync("PageToMovieCut.setPointerCapture", _inner, e.PointerId);
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
        await Js.InvokeVoidAsync("PageToMovieCut.setPointerCapture", _inner, e.PointerId);
    }

    private async Task BeginPlayheadAsync(PointerEventArgs e)
    {
        if (Busy)
            return;
        _drag = DragKind.Playhead;
        await Js.InvokeVoidAsync("PageToMovieCut.setPointerCapture", _inner, e.PointerId);
        await SeekToAsync(await TimeAtAsync(e.ClientX));
    }

    private async Task OnPointerMove(PointerEventArgs e)
    {
        if (_drag == DragKind.None)
            return;
        if (_drag is DragKind.TrimIn or DragKind.TrimOut && _trimClip is { } clip)
        {
            var dt = (e.ClientX - _dragOriginX) / _pxPerSec;
            if (_drag == DragKind.TrimIn)
                CutTimelineLayout.TrimIn(clip, _dragMarkIn + dt);
            else
                CutTimelineLayout.TrimOut(clip, _dragMarkOut + dt);
            return;
        }

        var t = await TimeAtAsync(e.ClientX);
        if (_drag == DragKind.Range)
            _rangeB = t;
        else if (_drag == DragKind.Playhead)
            await SeekToAsync(t);
    }

    private async Task OnPointerUp(PointerEventArgs e)
    {
        if (_drag == DragKind.None)
            return;
        var kind = _drag;
        _drag = DragKind.None;
        if (kind is DragKind.TrimIn or DragKind.TrimOut)
        {
            _trimClip = null;
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
        if (e.Key is "Delete" or "Backspace")
            await DeleteRangeAsync();
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

    private async Task<double> TimeAtAsync(double clientX)
    {
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
    }
}
