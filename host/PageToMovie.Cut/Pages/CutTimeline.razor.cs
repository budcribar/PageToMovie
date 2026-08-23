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
    [Parameter] public CutMusic? Music { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public bool PlayDisabled { get; set; }
    [Parameter] public bool IsPlaying { get; set; }
    [Parameter] public EventCallback OnPlay { get; set; }
    [Parameter] public EventCallback OnSkipStart { get; set; }
    [Parameter] public EventCallback OnStepBack { get; set; }
    [Parameter] public EventCallback OnStepForward { get; set; }
    [Parameter] public EventCallback OnSplit { get; set; }
    [Parameter] public EventCallback OnEdited { get; set; }
    [Parameter] public EventCallback OnMusicEdited { get; set; }
    [Parameter] public EventCallback OnMusicRemoved { get; set; }
    [Parameter] public string? SelectedTextId { get; set; }
    [Parameter] public EventCallback<string?> SelectedTextIdChanged { get; set; }

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
    private double _dragTextPrev;
    private double _dragTextNext;
    private IReadOnlyList<CutTextBlock>? _dragRow;
    private List<CutTextPlace.Span>? _dragOccupied;
    private double _dragMusicStart;
    private double _dragMusicIn;
    private double _dragMusicOut;
    private double _rangeA = -1;
    private double _rangeB = -1;
    private int? _joinMenu;
    private string? _selectedTextId;
    private bool _textFieldFocused;
    private bool _focusTextInput;
    private bool _menuOpen;
    private CtxMenuKind _menuKind;
    private double _menuX;
    private double _menuY;
    private CutTextPayload? _clipboard;
    private CutMusicPlacement? _musicClipboard;
    private bool _musicSelected;
    private bool _musicNameEditing;
    private bool _focusMusicName;
    private bool _focusMusicOut;
    private bool _focusDuration;
    private bool _focusMenu;
    private string? _menuTitleId;
    private CutTimeline_TextMenu? _textMenu = null;
    private ElementReference _textLabelInput = default;
    private ElementReference _musicNameInput = default;
    private ElementReference _musicOutHandle = default;
    private CutTimeline_TextInspector? _inspector = null;
    private TimelineRenderSnap _renderSnap;

    private CutTimelineLayout Layout => CutTimelineLayout.Build(Clips, _pxPerSec);
    private IReadOnlyList<CutTextBlock> TextBlocks => CutTextTrack.Build(Clips, TextClips, _pxPerSec);
    private IReadOnlyList<CutTextBlock> TextRow =>
        _drag is DragKind.TextMove or DragKind.TextIn or DragKind.TextOut
        && _dragRow is { } row
        && _trimText is { } moving
            ? LiveTextRow(row, moving)
            : TextBlocks;
    private bool HasText => TextRow.Count > 0;
    private bool HasRange => _rangeA >= 0 && _rangeB >= 0 && Math.Abs(_rangeB - _rangeA) >= CutRangeDelete.MinSpanSeconds;
    private double RangeLo => Math.Min(_rangeA, _rangeB);
    private double RangeHi => Math.Max(_rangeA, _rangeB);
    private double RangePx => (RangeHi - RangeLo) * _pxPerSec;
    private string PlayheadClock => CutTimelineLayout.Clock(PlayheadSec);
    private string TotalClock => CutTimelineLayout.Clock(Layout.PlayableSec > 0 ? Layout.PlayableSec : Layout.TotalSec);
    private bool PlayOrStopDisabled => IsPlaying ? Busy : PlayDisabled;
    private bool SplitDisabled => Busy || !CutSplit.CanAt(Clips, PlayheadSec);
    private bool PreventTitleKey =>
        !_textFieldFocused && (!string.IsNullOrEmpty(_selectedTextId) || _musicSelected);
    private bool CanSplitSelectedTitle =>
        MenuTitle is { } title && CutTextEdit.CanSplit(title, PlayheadSec);
    private bool MenuCanPaste =>
        _menuKind == CtxMenuKind.Music ? _musicClipboard is not null : _clipboard is not null;
    private bool MenuCanSplit =>
        _menuKind == CtxMenuKind.Music
            ? CutMusicEdit.CanSplit(Music, PlayheadSec)
            : CanSplitSelectedTitle;
    private CutTextClip? SelectedTitle =>
        SelectedTextBlock is { Title: { } title } ? title : CutTextEdit.Find(TextClips, _selectedTextId);
    private CutTextClip? MenuTitle =>
        CutTextMenu.TargetOf(TextClips, _menuTitleId, _selectedTextId);

    private Task NotifyMusicEditedAsync() =>
        OnMusicEdited.HasDelegate ? OnMusicEdited.InvokeAsync() : OnEdited.InvokeAsync();

    private bool ShowMusic => Music is { HasFile: true };
    private double MusicLeftPx => (Music?.StartSec ?? 0) * _pxPerSec;
    private double MusicWidthPx
    {
        get
        {
            var hold = Music?.SlicedDurationSec ?? 0;
            if (hold < CutMusic.MinSpanSeconds)
                hold = Math.Max(Layout.TotalSec, 8);
            return Math.Max(hold * _pxPerSec, 36);
        }
    }
    private string MusicLabel => CutMusicEdit.Label(Music, AudioName);
    private double MovieEndSec
    {
        get
        {
            var total = Math.Max(Layout.TotalSec, Layout.PlayableSec);
            return total > CutCard.MinHoldSeconds ? total : double.PositiveInfinity;
        }
    }

    internal CutTextBlock? SelectedTextBlock
    {
        get
        {
            if (string.IsNullOrEmpty(_selectedTextId))
                return null;
            foreach (var block in TextRow)
            {
                if (block.Id == _selectedTextId)
                    return block;
            }

            return null;
        }
    }

    protected override void OnInitialized()
    {
        _scroll = default;
        _inner = default;
        _textLabelInput = default;
        _musicNameInput = default;
        _musicOutHandle = default;
    }

    protected override void OnParametersSet()
    {
        if (!string.IsNullOrEmpty(SelectedTextId) && SelectedTextId != _selectedTextId)
            _selectedTextId = SelectedTextId;
        if (Clips.Count != _lastCount)
        {
            _lastCount = Clips.Count;
            _needFit = Clips.Count > 0;
            _joinMenu = null;
        }
    }

    protected override bool ShouldRender()
    {
        if (_drag != DragKind.None)
            return true;
        if (!IsPlaying)
            return true;
        return CaptureRenderSnap() != _renderSnap;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _renderSnap = CaptureRenderSnap();
        if (Js is not null)
        {
            try
            {
                await Js.InvokeVoidAsync("PageToMovieCut.bindPlayClock", _pxPerSec, Layout.TotalSec);
            }
            catch (JSException)
            {
                // clock paint is best-effort
            }
        }

        if (_focusTextInput)
        {
            _focusTextInput = false;
            _textFieldFocused = true;
            await CutElementFocus.TryFocusAsync(_textLabelInput);
        }

        if (_focusMusicName)
        {
            _focusMusicName = false;
            _textFieldFocused = true;
            await CutElementFocus.TryFocusAsync(_musicNameInput);
        }

        if (_focusMusicOut)
        {
            _focusMusicOut = false;
            await CutElementFocus.TryFocusAsync(_musicOutHandle);
        }

        if (_focusDuration)
        {
            _focusDuration = false;
            await EditDurationAsync(_inspector);
        }

        if (_focusMenu)
        {
            _focusMenu = false;
            if (_textMenu is not null)
                await _textMenu.FocusPanelAsync();
        }

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
        CloseTitleMenu();
        _musicSelected = false;
        _musicNameEditing = false;
        await SetSelectedTextIdAsync(null);
        _textFieldFocused = false;
        await SelectedChanged.InvokeAsync(clip);
    }

    private async Task SelectSceneBlockAsync(MouseEventArgs e, CutTimelineVideoBlock block)
    {
        var t = await TimeAtAsync(e.ClientX);
        if (CutTimelineLayout.HitTest(Clips, t) is { } hit)
        {
            await SelectClip(hit.Clip);
            return;
        }

        if (block.FirstIndex >= 0 && block.FirstIndex < Clips.Count)
            await SelectClip(Clips[block.FirstIndex]);
    }

    internal void SelectTitle(string id) => _ = SelectTitleAsync(id);

    internal async Task SelectTitleAsync(string id)
    {
        _joinMenu = null;
        _musicSelected = false;
        _musicNameEditing = false;
        await SetSelectedTextIdAsync(id);
    }

    internal async Task OpenTitleMenuAsync(double clientX, double clientY, string titleId)
    {
        _menuTitleId = titleId;
        await SelectTitleAsync(titleId);
        if (CutTextMenu.TargetOf(TextClips, titleId, _selectedTextId) is null)
            return;
        await OpenCtxMenuAsync(clientX, clientY, CtxMenuKind.Title);
    }

    private void OpenTitleMenu(MouseEventArgs e, CutTextBlock block)
    {
        if (block.Kind != CutTextKind.Title)
            return;
        _ = OpenTitleMenuAsync(e.ClientX, e.ClientY, block.Id);
    }

    private void CloseTitleMenu()
    {
        _menuOpen = false;
        _menuKind = CtxMenuKind.None;
        _menuTitleId = null;
        _focusMenu = false;
    }

    private void SelectMusic()
    {
        _joinMenu = null;
        _musicSelected = true;
        _ = SetSelectedTextIdAsync(null);
    }

    private void OpenMusicMenu(MouseEventArgs e)
    {
        if (Music is null || !Music.HasFile)
            return;
        _menuTitleId = null;
        SelectMusic();
        _ = OpenCtxMenuAsync(e.ClientX, e.ClientY, CtxMenuKind.Music);
    }

    private async Task OpenCtxMenuAsync(double clientX, double clientY, CtxMenuKind kind)
    {
        _menuKind = kind;
        _menuX = clientX;
        _menuY = clientY;
        _menuOpen = true;
        _focusMenu = true;
        await InvokeAsync(StateHasChanged);
    }

    private void OnMenuCopy()
    {
        if (_menuKind == CtxMenuKind.Music)
            CopyMusicPlacement();
        else
            CopySelectedTitle();
    }

    private Task OnMenuPasteAsync() =>
        _menuKind == CtxMenuKind.Music ? PasteMusicPlacementAsync() : PasteTitleAsync();

    private Task OnMenuDeleteAsync() =>
        _menuKind == CtxMenuKind.Music ? DeleteMusicAsync() : DeleteMenuTitleAsync();

    private Task OnMenuSplitAsync() =>
        _menuKind == CtxMenuKind.Music ? Task.CompletedTask : SplitSelectedTitleAsync();

    private Task OnMenuEditDurationAsync() =>
        _menuKind == CtxMenuKind.Music ? EditMusicDurationAsync() : EditDurationAsync();

    private void CopyMusicPlacement()
    {
        if (Music is null || !Music.HasFile)
            return;
        _musicClipboard = CutMusicEdit.Copy(Music);
        CloseTitleMenu();
    }

    private async Task PasteMusicPlacementAsync()
    {
        if (Busy || Music is null || !Music.HasFile || _musicClipboard is not { } placed)
            return;
        CloseTitleMenu();
        CutMusicEdit.Paste(Music, placed, PlayheadSec);
        await NotifyMusicEditedAsync();
    }

    private async Task DeleteMusicAsync()
    {
        if (Busy || Music is null || !Music.HasFile)
            return;
        CloseTitleMenu();
        CutMusicEdit.Delete(Music);
        _musicSelected = false;
        _musicNameEditing = false;
        await OnMusicRemoved.InvokeAsync();
    }

    private async Task EditMusicDurationAsync()
    {
        CloseTitleMenu();
        _musicSelected = true;
        _focusMusicOut = true;
        await InvokeAsync(StateHasChanged);
    }

    private async Task RenameMusicAsync()
    {
        CloseTitleMenu();
        _musicSelected = true;
        _musicNameEditing = true;
        _focusMusicName = true;
        await InvokeAsync(StateHasChanged);
    }

    private async Task SetMusicNameAsync(ChangeEventArgs e)
    {
        if (Music is null)
            return;
        CutMusicEdit.Rename(Music, Convert.ToString(e.Value, CultureInfo.InvariantCulture));
        _musicNameEditing = false;
        _textFieldFocused = false;
        await NotifyMusicEditedAsync();
    }

    private async Task SetSelectedTextIdAsync(string? id)
    {
        _selectedTextId = id;
        if (SelectedTextId != id)
            await SelectedTextIdChanged.InvokeAsync(id);
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
        var title = CutTextTrack.Add(TextClips, startSec, null, OccupiedExcept(null), MovieEndSec);
        await SetSelectedTextIdAsync(title.Id);
        _textFieldFocused = true;
        _focusTextInput = true;
        await OnEdited.InvokeAsync();
    }

    private async Task SetTextLabelAsync(CutTextBlock block, ChangeEventArgs e)
    {
        CutTextTrack.SetLabel(block, Convert.ToString(e.Value, CultureInfo.InvariantCulture));
        await OnEdited.InvokeAsync();
    }

    private async Task DeleteSelectedTextAsync()
    {
        if (SelectedTextBlock is not { } block)
            return;
        await DeleteTextAsync(block);
    }

    private async Task DeleteTextAsync(CutTextBlock block)
    {
        CloseTitleMenu();
        CutTextTrack.Delete(block, TextClips);
        if (_selectedTextId == block.Id)
            await SetSelectedTextIdAsync(null);
        _textFieldFocused = false;
        await OnEdited.InvokeAsync();
    }

    private async Task SetTextDurationAsync(double seconds)
    {
        if (SelectedTextBlock is not { } block)
            return;
        var max = CutTextPlace.HoldMax(block.StartSec, OccupiedExcept(block.Id), MovieEndSec);
        CutTextTrack.SetHold(block, seconds, max);
        await OnEdited.InvokeAsync();
    }

    private void OnInspectorFieldFocus(bool focused) => _textFieldFocused = focused;

    private async Task DuplicateSelectedTitleAsync()
    {
        if (!CutTextMenu.TryDuplicate(Busy, TextClips, MenuTitle, out var copy, OccupiedExcept(null), MovieEndSec)
            || copy is null)
            return;
        CloseTitleMenu();
        await SetSelectedTextIdAsync(copy.Id);
        await OnEdited.InvokeAsync();
    }

    private void CopySelectedTitle()
    {
        if (!CutTextMenu.TryCopy(MenuTitle, out var payload) || payload is null)
            return;
        _clipboard = payload;
        CloseTitleMenu();
    }

    private async Task PasteTitleAsync()
    {
        if (Busy || _clipboard is null)
            return;
        var start = CutTextEdit.PasteStart(PlayheadSec, MenuTitle);
        CloseTitleMenu();
        var pasted = CutTextEdit.Paste(TextClips, _clipboard, start, OccupiedExcept(null), MovieEndSec);
        await SetSelectedTextIdAsync(pasted.Id);
        await OnEdited.InvokeAsync();
    }

    private async Task SplitSelectedTitleAsync()
    {
        if (!CutTextMenu.TrySplit(Busy, TextClips, MenuTitle, PlayheadSec))
            return;
        CloseTitleMenu();
        await OnEdited.InvokeAsync();
    }

    private async Task DeleteMenuTitleAsync()
    {
        var id = _menuTitleId ?? _selectedTextId;
        if (!CutTextMenu.TryDelete(Busy, TextClips, id))
            return;
        CloseTitleMenu();
        if (_selectedTextId == id)
            await SetSelectedTextIdAsync(null);
        _textFieldFocused = false;
        await OnEdited.InvokeAsync();
    }

    private async Task EditDurationAsync()
    {
        if (MenuTitle is { } title && _selectedTextId != title.Id)
            await SetSelectedTextIdAsync(title.Id);
        CloseTitleMenu();
        _focusDuration = true;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Focus the inspector duration field after it is mounted. A missing
    /// inspector or unbound input must not throw.
    /// </summary>
    internal static Task EditDurationAsync(CutTimeline_TextInspector? inspector) =>
        inspector is null ? Task.CompletedTask : inspector.FocusDurationAsync();

    private async Task BeginTextMoveAsync(PointerEventArgs e, CutTextBlock block)
    {
        if (Busy)
            return;
        await SetSelectedTextIdAsync(block.Id);
        if (block.Kind != CutTextKind.Title || block.Title is null)
            return;
        _drag = DragKind.TextMove;
        _trimText = block;
        _dragOriginX = e.ClientX;
        _dragTextStart = block.StartSec;
        _dragTextHold = block.Seconds;
        BeginTextDragBounds(block);
        await CapturePointerAsync(e.PointerId);
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
        BeginTextDragBounds(block);
        await SetSelectedTextIdAsync(block.Id);
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
        if (TryApplyClipTrim(e.ClientX) || TryApplyTextTrim(e.ClientX) || TryApplyMusicTrim(e.ClientX))
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
        if (_drag is not (DragKind.TextMove or DragKind.TextIn or DragKind.TextOut) || _trimText is not { } text)
            return false;
        var dt = (clientX - _dragOriginX) / _pxPerSec;
        if (_drag == DragKind.TextMove)
        {
            if (text.Title is { } title)
                title.Move(CutTextPlace.Move(_dragTextStart, _dragTextHold, dt, _dragTextPrev, _dragTextNext, MovieEndSec));
            return true;
        }

        var others = _dragOccupied ?? OccupiedExcept(text.Id);
        if (_drag == DragKind.TextIn)
            ApplyTextInTrim(text, dt, others);
        else
            ApplyTextOutTrim(text, dt, others);
        return true;
    }

    private void ApplyTextInTrim(CutTextBlock text, double dt, IReadOnlyList<CutTextPlace.Span> others)
    {
        if (text.Kind != CutTextKind.Title)
        {
            CutTextTrack.SetHold(text, _dragTextHold - dt, CutTextPlace.HoldMax(text.StartSec, others, MovieEndSec));
            return;
        }

        var (start, hold) = CutTextPlace.TrimIn(_dragTextStart, _dragTextHold, dt, others, MovieEndSec);
        CutTextTrack.SetStart(text, start);
        CutTextTrack.SetHold(text, hold, hold);
    }

    private void ApplyTextOutTrim(CutTextBlock text, double dt, IReadOnlyList<CutTextPlace.Span> others)
    {
        var (_, hold) = CutTextPlace.TrimOut(_dragTextStart, _dragTextHold, dt, others, MovieEndSec);
        CutTextTrack.SetHold(text, hold, hold);
    }

    private void CommitTextMove(double clientX)
    {
        if (_trimText?.Title is not { } title)
            return;
        var dt = (clientX - _dragOriginX) / _pxPerSec;
        title.Move(CutTextPlace.ResolveDrop(
            _dragTextStart + dt,
            _dragTextHold,
            _dragOccupied ?? OccupiedExcept(title.Id),
            MovieEndSec));
    }

    private void BeginTextDragBounds(CutTextBlock block)
    {
        _dragRow = TextBlocks;
        _dragOccupied = CutTextPlace.FromBlocks(_dragRow, block.Id);
        CutTextPlace.Neighbors(
            _dragTextStart,
            _dragTextHold,
            _dragOccupied,
            MovieEndSec,
            out _dragTextPrev,
            out _dragTextNext);
    }

    private IReadOnlyList<CutTextBlock> LiveTextRow(IReadOnlyList<CutTextBlock> row, CutTextBlock moving)
    {
        if (moving.Title is not { } title)
            return row;
        var start = title.StartSec;
        var hold = title.HoldSeconds;
        var copy = new CutTextBlock[row.Count];
        for (var i = 0; i < row.Count; i++)
        {
            var block = row[i];
            if (block.Id != moving.Id)
            {
                copy[i] = block;
                continue;
            }

            copy[i] = block with
            {
                StartSec = start,
                Seconds = hold,
                StartPx = start * _pxPerSec,
                WidthPx = hold * _pxPerSec,
                Title = title,
            };
        }

        return copy;
    }

    private List<CutTextPlace.Span> OccupiedExcept(string? id) =>
        CutTextPlace.FromBlocks(TextBlocks, id);

    private async Task BeginMusicMoveAsync(PointerEventArgs e)
    {
        if (Busy || Music is null || !Music.HasFile)
            return;
        SelectMusic();
        _drag = DragKind.MusicMove;
        _dragOriginX = e.ClientX;
        _dragMusicStart = Music.StartSec;
        await CapturePointerAsync(e.PointerId);
    }

    private async Task BeginMusicTrimAsync(PointerEventArgs e, bool fromStart)
    {
        if (Busy || Music is null || !Music.HasFile)
            return;
        SelectMusic();
        _drag = fromStart ? DragKind.MusicIn : DragKind.MusicOut;
        _dragOriginX = e.ClientX;
        _dragMusicStart = Music.StartSec;
        _dragMusicIn = Music.MarkIn;
        _dragMusicOut = Music.MarkOut > Music.MarkIn ? Music.MarkOut : Music.DurationSec;
        await CapturePointerAsync(e.PointerId);
    }

    private bool TryApplyMusicTrim(double clientX)
    {
        if (Music is null || _drag is not (DragKind.MusicMove or DragKind.MusicIn or DragKind.MusicOut))
            return false;
        var dt = (clientX - _dragOriginX) / _pxPerSec;
        if (_drag == DragKind.MusicMove)
            Music.Move(_dragMusicStart + dt);
        else if (_drag == DragKind.MusicIn)
            Music.TrimIn(_dragMusicIn + dt);
        else
            Music.TrimOut(_dragMusicOut + dt);
        return true;
    }

    private async Task OnPointerUp(PointerEventArgs e)
    {
        if (_drag == DragKind.None)
            return;
        var kind = _drag;
        _drag = DragKind.None;
        if (kind is DragKind.TrimIn or DragKind.TrimOut or DragKind.TextIn or DragKind.TextOut
            or DragKind.TextMove or DragKind.MusicMove or DragKind.MusicIn or DragKind.MusicOut)
        {
            if (kind == DragKind.TextMove)
                CommitTextMove(e.ClientX);
            _trimClip = null;
            _trimText = null;
            _dragRow = null;
            _dragOccupied = null;
            if (kind is DragKind.MusicMove or DragKind.MusicIn or DragKind.MusicOut)
                await NotifyMusicEditedAsync();
            else
                await OnEdited.InvokeAsync();
            return;
        }

        if (kind == DragKind.Playhead)
        {
            var drop = CutPlayMerge.ScrubCommitSec(Clips, await TimeAtAsync(e.ClientX));
            if (!CutPlayClock.ShouldSnapPlayheadOnScrubEnd)
                await SeekToAsync(drop);
            return;
        }

        if (kind == DragKind.Range)
        {
            var t = await TimeAtAsync(e.ClientX);
            _rangeB = t;
            if (Math.Abs(_rangeB - _rangeA) < 0.08)
            {
                await SeekToAsync(CutPlayMerge.ScrubCommitSec(Clips, _rangeA));
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
        if (_textFieldFocused)
            return;
        if (e.Key is "Escape")
        {
            CloseTitleMenu();
            return;
        }

        if (await TryHandleTitleShortcutAsync(e))
            return;
        if (await TryHandleMusicShortcutAsync(e))
            return;
        if (await TryHandleDeleteKeyAsync(e.Key))
            return;
        if (e.Key is "-" or "_" or "Minus" or "Subtract")
            ZoomOut();
        else if (e.Key is "+" or "=" or "Add")
            ZoomIn();
        else if (e.Key is "0" or "f" or "F")
            await FitAsync();
    }

    private async Task<bool> TryHandleTitleShortcutAsync(KeyboardEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedTextId))
            return false;
        var shortcut = CutTextEdit.ShortcutOf(e.Key, e.CtrlKey || e.MetaKey, _textFieldFocused);
        switch (shortcut)
        {
            case CutTextShortcut.Duplicate:
                await DuplicateSelectedTitleAsync();
                return true;
            case CutTextShortcut.Copy:
                CopySelectedTitle();
                return true;
            case CutTextShortcut.Paste:
                await PasteTitleAsync();
                return true;
            case CutTextShortcut.Split:
                await SplitSelectedTitleAsync();
                return true;
            default:
                return false;
        }
    }

    private async Task<bool> TryHandleMusicShortcutAsync(KeyboardEventArgs e)
    {
        if (!_musicSelected)
            return false;
        var shortcut = CutTextEdit.ShortcutOf(e.Key, e.CtrlKey || e.MetaKey, _textFieldFocused);
        switch (shortcut)
        {
            case CutTextShortcut.Duplicate:
            case CutTextShortcut.Split:
                return true;
            case CutTextShortcut.Copy:
                CopyMusicPlacement();
                return true;
            case CutTextShortcut.Paste:
                await PasteMusicPlacementAsync();
                return true;
            default:
                return false;
        }
    }

    private async Task<bool> TryHandleDeleteKeyAsync(string key)
    {
        if (key is not ("Delete" or "Backspace"))
            return false;
        if (_musicSelected && !_textFieldFocused)
        {
            await DeleteMusicAsync();
            return true;
        }

        if (CutTextTrack.TryDeleteSelectedOnKey(key, _textFieldFocused, _selectedTextId, TextBlocks, TextClips))
        {
            CloseTitleMenu();
            await SetSelectedTextIdAsync(null);
            _textFieldFocused = false;
            await OnEdited.InvokeAsync();
            return true;
        }

        await DeleteRangeAsync();
        return true;
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

    private TimelineRenderSnap CaptureRenderSnap() =>
        new(
            Clips.Count,
            _pxPerSec,
            IsPlaying,
            Busy,
            HasAudio,
            TextClips.Count,
            Selected?.Label,
            PlayDisabled,
            HasRange,
            _joinMenu,
            _selectedTextId,
            _menuOpen,
            _menuKind,
            _menuX,
            _menuY,
            _musicSelected,
            _musicNameEditing,
            _focusDuration,
            Music?.StartSec,
            Music?.MarkIn,
            Music?.MarkOut,
            PlayheadSec);

    private enum CtxMenuKind
    {
        None,
        Title,
        Music,
    }

    private enum DragKind
    {
        None,
        Range,
        Playhead,
        TrimIn,
        TrimOut,
        TextIn,
        TextOut,
        TextMove,
        MusicMove,
        MusicIn,
        MusicOut,
    }

    private readonly record struct TimelineRenderSnap(
        int Clips,
        double Px,
        bool Playing,
        bool Busy,
        bool Audio,
        int Texts,
        string? Selected,
        bool PlayDisabled,
        bool HasRange,
        int? Join,
        string? TextId,
        bool Menu,
        CtxMenuKind MenuKind,
        double MenuX,
        double MenuY,
        bool MusicOn,
        bool MusicName,
        bool FocusDuration,
        double? MusicStart,
        double? MusicIn,
        double? MusicOut,
        double Playhead);
}
