using Flashcards.Desktop.ViewModels.Subjects;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Cards.Commands;
using Flashcards.Application.Cards.Queries;
using Flashcards.Application.Contracts;
using Flashcards.Application.Media.Commands;
using Flashcards.Application.Subjects.Commands;
using Flashcards.Application.Subjects.Queries;
using Flashcards.Desktop.Services;
using Flashcards.Desktop.ViewModels.Shared;
using Flashcards.Domain.Cards;
using Flashcards.Domain.Subjects;

namespace Flashcards.Desktop.ViewModels.Design;

/// <summary>
/// Drives the card designer — the create and edit flows are the same surface, and the only
/// difference is whether <see cref="EditingId"/> is set.
/// <para>
/// The designer is four artboards behind one chrome. <see cref="CardType"/> chooses which of them
/// the canvas shows, and each has its own shape rules (see <see cref="EnsureShapeForType"/>):
/// a standard card is a free stack of blocks per face, a multiple-choice card is one question plus
/// four fixed answer slots, a cloze card is a single passage you carve blanks out of, and a
/// freeform card is a canvas of positioned elements plus an ink layer.
/// </para>
/// <para>
/// Selection is the other half of the designer feel: exactly one element is active at a time, and
/// it is edited in place on the artboard. There is no separate inspector — every control that
/// changes an element sits on the element.
/// </para>
/// </summary>
public sealed partial class CardEditorViewModel : ViewModelBase
{
    /// <summary>What a fresh multiple-choice card starts with. Slots can be added and removed after.</summary>
    public const int DefaultChoiceSlots = 4;

    /// <summary>
    /// The fewest answers a card may carry. Below two there is nothing to choose between, which is
    /// the same rule <c>FlashcardRules</c> enforces on the way to the database — this is that rule
    /// stated early, so the button is simply unavailable rather than the save being refused.
    /// </summary>
    public const int MinChoiceSlots = 2;

    /// <summary>Straight from the aggregate, so the board can never build a card it cannot store.</summary>
    public const int MaxChoiceSlots = Flashcard.MaxChoices;

    private static readonly string[] SlotLabels = ["A", "B", "C", "D", "E", "F", "G", "H"];

    private readonly IDispatcher _dispatcher;
    private readonly IImageCache _imageCache;
    private readonly IDialogService _dialogs;

    /// <summary>
    /// The type the staged draft actually matches. <see cref="CardType"/> is bound two-way to the
    /// picker, so it moves the instant the user chooses — this is what it moved *from*, and what
    /// it is put back to if they decline to discard.
    /// </summary>
    private CardType _confirmedType = CardType.Standard;

    /// <summary>Set while the code itself is setting the type, so loading a card never prompts.</summary>
    private bool _settingTypeInternally;

    /// <summary>Coalesces keystrokes so the prediction list opens once, after typing stops.</summary>
    private CancellationTokenSource? _suggestDebounce;

    public CardEditorViewModel(IDispatcher dispatcher, IImageCache imageCache, IDialogService dialogs)
    {
        _dispatcher = dispatcher;
        _imageCache = imageCache;
        _dialogs = dialogs;

        QuestionBlocks.CollectionChanged += OnBlocksChanged;
        AnswerBlocks.CollectionChanged += OnBlocksChanged;
        Choices.CollectionChanged += OnChoicesChanged;

        // Ink lives outside the block lists, so it needs its own hook to keep HasDraftContent honest.
        QuestionStrokes.CollectionChanged += OnStrokesChanged;
        AnswerStrokes.CollectionChanged += OnStrokesChanged;
    }

    /// <summary>
    /// Whether there is anything staged worth warning about before it is thrown away.
    /// <para>
    /// Tags count now. They used to be excluded because a reset kept them, so they were never at
    /// risk; a reset clears them along with everything else, and anything a reset destroys has to
    /// be something a reset asks about first. It also means the discard button is enabled for a
    /// draft that is nothing but tags, which would otherwise have no way to be cleared at all.
    /// </para>
    /// </summary>
    public bool HasDraftContent =>
        !string.IsNullOrWhiteSpace(Name)
        || SubjectTags.Count > 0
        || QuestionBlocks.Any(b => !b.IsEmpty)
        || AnswerBlocks.Any(b => !b.IsEmpty)
        || Choices.Any(c => !c.IsBlank)
        || QuestionStrokes.Count > 0
        || AnswerStrokes.Count > 0;

    private bool _isDirty;

    /// <summary>
    /// Whether anything has been edited since the draft was started or loaded.
    /// <para>
    /// Distinct from <see cref="HasDraftContent"/>, and the distinction matters: a card just opened
    /// from the manage panel is full of content while nothing has been changed about it. Warning
    /// that changes are about to be lost when none have been made trains people to click through
    /// the warning, which is exactly when it stops protecting anything.
    /// </para>
    /// <para>
    /// So the prompts key off this, and the discard button's enabled state keeps keying off
    /// <see cref="HasDraftContent"/> — there is still something to clear even when it is untouched.
    /// </para>
    /// </summary>
    public bool IsDirty => _isDirty;

    /// <summary>Supplied by the view, which is the only thing that has a Visual to hang the clipboard off.</summary>
    public Func<Task<PastedImage?>>? ClipboardImageProvider { get; set; }

    public Func<Task<PastedImage?>>? FileImageProvider { get; set; }

    /// <summary>Raised after a successful save so the shell can refresh the other panels.</summary>
    public event EventHandler<Guid>? Saved;

    // ---- card identity --------------------------------------------------

    [ObservableProperty]
    private Guid? _editingId;

    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>
    /// The tags on this card. Subjects are typed, not picked from a fixed list, and a card may
    /// wear as many as the user likes — "SQL Server internals" is reasonably both SQL and
    /// Databases. At least one is required, which <see cref="Save"/> enforces.
    /// </summary>
    public ObservableCollection<SubjectTagViewModel> SubjectTags { get; } = [];

    /// <summary>
    /// The subjects this card picks up for free, because they sit above the tags it does carry.
    /// Shown beside the real tags and not removable — see <see cref="SubjectTagViewModel"/>.
    /// </summary>
    public ObservableCollection<SubjectTagViewModel> InheritedTags { get; } = [];

    public bool HasInheritedTags => InheritedTags.Count > 0;

    /// <summary>What the user is currently typing into the subject box.</summary>
    [ObservableProperty]
    private string? _tagDraft;

    /// <summary>
    /// Existing subjects whose name contains what has been typed, offered in the drop-down.
    /// <para>
    /// This replaced a plain autocomplete. Autocomplete only helps once you know roughly what you
    /// are looking for, and with a tree the useful question is often "what is there?" — so the list
    /// opens on demand showing everything, and typing narrows it.
    /// </para>
    /// </summary>
    public ObservableCollection<SubjectSummary> TagMatches { get; } = [];

    /// <summary>
    /// Whether the prediction list is showing.
    /// <para>
    /// Two-way, because it opens three ways: the chevron, and — after a pause in typing — by
    /// itself. See <see cref="QueueSuggestions"/> for why the pause is there.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _isSuggestionsOpen;

    /// <summary>Why the typed name cannot be used. Shown under the box, cleared as you type.</summary>
    [ObservableProperty]
    private string? _tagError;

    /// <summary>
    /// The known subjects behind the drop-down, kept so a chip can show the tag's
    /// real colour. A name that is not in here yet is a tag this save will mint, and its chip
    /// renders uncoloured — a useful "this one is new" signal in itself.
    /// </summary>
    private IReadOnlyList<SubjectSummary> _knownSubjects = [];

    /// <summary>The subject tree behind _knownSubjects, for working out what a tag implies.</summary>
    private SubjectHierarchy? _hierarchy;

    public bool HasSubjectTags => SubjectTags.Count > 0;

    private string? ColorForTag(string name) => _knownSubjects
        .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
        ?.ColorHex;

    [ObservableProperty]
    private CardType _cardType = CardType.Standard;

    [ObservableProperty]
    private string? _notes;

    [ObservableProperty]
    private bool _isSuspended;

    // ---- artboard contents ----------------------------------------------

    public ObservableCollection<BlockEditorViewModel> QuestionBlocks { get; } = [];

    public ObservableCollection<BlockEditorViewModel> AnswerBlocks { get; } = [];

    public ObservableCollection<ChoiceEditorViewModel> Choices { get; } = [];

    // ---- designer selection ---------------------------------------------

    [ObservableProperty]
    private BlockEditorViewModel? _selectedBlock;

    [ObservableProperty]
    private ChoiceEditorViewModel? _selectedChoice;

    // ---- live preview ----------------------------------------------------

    [ObservableProperty]
    private IReadOnlyList<ContentBlockDto> _questionPreview = [];

    [ObservableProperty]
    private IReadOnlyList<ContentBlockDto> _answerPreview = [];


    // ---- derived ---------------------------------------------------------

    public bool IsEditing => EditingId is not null;

    public bool IsStandard => CardType == CardType.Standard;

    public bool IsMultipleChoice => CardType == CardType.MultipleChoice;

    public bool IsCloze => CardType == CardType.Cloze;

    public bool IsFreeform => CardType == CardType.Freeform;

    /// <summary>Everything that is not a designed card stacks its elements vertically.</summary>
    public bool IsFlowLayout => CardType != CardType.Freeform;

    public string Title => IsEditing ? "Edit card" : "New card";

    public string SaveLabel => IsEditing ? "Save" : "Create";

    /// <summary>The four board types, in the order the header's drop-down offers them.</summary>
    public IReadOnlyList<CardType> CardTypes { get; } =
        [CardType.Standard, CardType.MultipleChoice, CardType.Cloze, CardType.Freeform];

    // ---- freeform designer state ----------------------------------------

    /// <summary>
    /// Which face the canvas is showing. Designed cards are edited one side at a time, so this is
    /// what the toggle above the canvas flips.
    /// </summary>
    [ObservableProperty]
    private CardFace _designFace = CardFace.Question;

    [ObservableProperty]
    private FreeformTool _activeTool = FreeformTool.Select;

    [ObservableProperty]
    private string _inkColor = "#4C9AFF";

    [ObservableProperty]
    private double _inkThickness = 3d;

    /// <summary>
    /// Ink is held per face, separately from the elements, and only folded into a Drawing block
    /// when the card is saved. Keeping it out of the element list means the canvas can bind one
    /// collection of draggable elements and one ink layer, instead of filtering a mixed list on
    /// every change.
    /// </summary>
    public ObservableCollection<InkStroke> QuestionStrokes { get; } = [];

    public ObservableCollection<InkStroke> AnswerStrokes { get; } = [];

    public ObservableCollection<InkStroke> ActiveStrokes
        => DesignFace == CardFace.Question ? QuestionStrokes : AnswerStrokes;

    /// <summary>The elements on the face currently being designed.</summary>
    public ObservableCollection<BlockEditorViewModel> ActiveFaceBlocks
        => DesignFace == CardFace.Question ? QuestionBlocks : AnswerBlocks;

    public bool IsShowingQuestion => DesignFace == CardFace.Question;

    public bool IsDrawingTool => ActiveTool == FreeformTool.Draw;

    public bool IsErasingTool => ActiveTool == FreeformTool.Erase;

    /// <summary>The ink layer only takes the pointer while one of the ink tools is chosen.</summary>
    public bool IsInkActive => IsDrawingTool || IsErasingTool;

    public double CanvasWidth => CardCanvas.Width;

    public double CanvasHeight => CardCanvas.Height;

    public IReadOnlyList<string> InkPalette { get; } =
        ["#4C9AFF", "#22C55E", "#F59E0B", "#EF4444", "#EC4899", "#E2E8F0", "#0F172A"];

    // ---- cloze designer state -------------------------------------------

    /// <summary>The passage a cloze card is carved out of — always the first question block.</summary>
    public BlockEditorViewModel? ClozeBlock => QuestionBlocks.FirstOrDefault();

    [ObservableProperty]
    private string _clozePrompt = string.Empty;

    [ObservableProperty]
    private string _clozeSolution = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ClozeBlank> _clozeBlanks = [];

    public bool HasBlanks => ClozeBlanks.Count > 0;

    // ---- lifecycle -------------------------------------------------------

    public override async Task ActivateAsync()
    {
        await LoadSubjectsAsync();

        if (QuestionBlocks.Count == 0 && !IsEditing)
        {
            StartNewCard();
        }
    }

    public async Task LoadSubjectsAsync()
    {
        var summaries = await _dispatcher.QueryAsync(new GetSubjectsQuery());

        _knownSubjects = summaries;

        // The same tree the write side validates against, so the designer's idea of what a tag
        // implies and what may be nested cannot drift from the rules the commands enforce.
        _hierarchy = new SubjectHierarchy(
            summaries.Select(s => new SubjectPlacement(s.Id, s.ParentId, s.Name)));

        ApplyTagFilter();

        // Tags already on the card may have just been minted, or recoloured; refresh their chips.
        foreach (var tag in SubjectTags)
        {
            tag.ColorHex = ColorForTag(tag.Name);
        }

        RefreshInheritedTags();
    }

    /// <summary>Narrows the drop-down to subjects matching what has been typed.</summary>
    private void ApplyTagFilter()
    {
        TagMatches.Clear();

        var term = TagDraft?.Trim();

        // Already-worn tags are left out: offering to add one a second time is noise, and the
        // add is a no-op anyway.
        foreach (var summary in _knownSubjects)
        {
            var alreadyWorn = SubjectTags.Any(t => string.Equals(t.Name, summary.Name, StringComparison.OrdinalIgnoreCase));

            if (!alreadyWorn
                && (string.IsNullOrEmpty(term) || summary.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
            {
                TagMatches.Add(summary);
            }
        }
    }

    /// <summary>
    /// Recomputes what the card wears for free. Runs whenever the tags change, so the consequence
    /// of adding "MSSQL" — that this is now also a SQL card — is visible immediately rather than
    /// only after a save and reload.
    /// </summary>
    private void RefreshInheritedTags()
    {
        InheritedTags.Clear();

        if (_hierarchy is null)
        {
            OnPropertyChanged(nameof(HasInheritedTags));
            return;
        }

        var owned = SubjectTags
            .Select(t => _knownSubjects.FirstOrDefault(s => string.Equals(s.Name, t.Name, StringComparison.OrdinalIgnoreCase)))
            .Where(s => s is not null)
            .Select(s => s!.Id)
            .ToHashSet();

        var seen = new HashSet<Guid>(owned);

        foreach (var id in owned.SelectMany(_hierarchy.AncestorsOf))
        {
            if (!seen.Add(id))
            {
                continue;
            }

            if (_knownSubjects.FirstOrDefault(s => s.Id == id) is { } ancestor)
            {
                InheritedTags.Add(new SubjectTagViewModel(ancestor.Name, ancestor.ColorHex, isInherited: true));
            }
        }

        OnPropertyChanged(nameof(HasInheritedTags));
    }

    /// <summary>Opens an existing card into the designer.</summary>
    public Task LoadCardAsync(Guid id) => RunAsync(async () =>
    {
        await LoadSubjectsAsync();

        var detail = await _dispatcher.QueryAsync(new GetFlashcardDetailQuery(id));

        if (detail is null)
        {
            ErrorMessage = "That card no longer exists.";
            return;
        }

        // Opening a card is not the user switching type, so it must not prompt.
        _settingTypeInternally = true;

        EditingId = detail.Id;
        Name = detail.Name;
        CardType = detail.CardType;
        Notes = detail.Notes;
        IsSuspended = detail.IsSuspended;
        SetTags(detail.Subjects.Select(s => new SubjectTagViewModel(s.Name, s.ColorHex, s.IsInherited)));

        QuestionBlocks.Clear();
        AnswerBlocks.Clear();
        Choices.Clear();

        foreach (var block in detail.Blocks.OrderBy(b => b.Ordinal))
        {
            (block.Face == CardFace.Question ? QuestionBlocks : AnswerBlocks).Add(BlockEditorViewModel.FromDto(block));
        }

        foreach (var choice in detail.Choices.OrderBy(c => c.Ordinal))
        {
            Choices.Add(ChoiceEditorViewModel.FromDto(choice));
        }

        // Drawing blocks become the per-face ink layers rather than list entries.
        ExtractInkLayers();

        EnsureShapeForType();
        Select(QuestionBlocks.FirstOrDefault());
        RefreshPreview();
        StatusMessage = null;

        _settingTypeInternally = false;
        _confirmedType = CardType;
        RaiseDraftChanged();

        // The card as loaded is the baseline: opening one is not editing it.
        MarkClean();
    });

    /// <summary>
    /// Clears the designer back to an empty card, tags included.
    /// <para>
    /// Tags used to survive a reset on the theory that cards are made in runs of the same subject.
    /// In practice that meant a subject chosen once quietly rode along onto every card made
    /// afterwards, including ones it had nothing to do with — a wrong tag you have to notice and
    /// remove is worse than a right one you have to pick again.
    /// </para>
    /// </summary>
    /// <param name="type">
    /// The board the fresh card starts on. Defaults to a plain question and answer; the type picker
    /// passes the type that was just chosen, because switching type while editing starts a new card
    /// <em>of that type</em> rather than dropping you back to the default one.
    /// </param>
    public void StartNewCard(CardType type = CardType.Standard)
    {
        // The code is choosing the type here, not the user — no prompt, no draft to protect.
        _settingTypeInternally = true;

        EditingId = null;
        Name = string.Empty;
        CardType = type;
        Notes = null;
        IsSuspended = false;
        ErrorMessage = null;
        StatusMessage = null;

        SetTags([]);

        TagDraft = null;
        TagError = null;
        IsSuggestionsOpen = false;

        QuestionBlocks.Clear();
        AnswerBlocks.Clear();
        Choices.Clear();
        QuestionStrokes.Clear();
        AnswerStrokes.Clear();

        DesignFace = CardFace.Question;
        ActiveTool = FreeformTool.Select;

        EnsureShapeForType();
        Select(QuestionBlocks.FirstOrDefault());
        RefreshPreview();

        _settingTypeInternally = false;
        _confirmedType = CardType;
        RaiseDraftChanged();
        MarkClean();
    }

    /// <summary>
    /// Brings the artboard into the shape the current card type expects, adding what is missing
    /// and leaving anything the user already typed alone.
    /// </summary>
    private void EnsureShapeForType()
    {
        // A designed card starts empty on purpose: the canvas is the point, and seeding it with a
        // stray text box in the corner would be something to delete rather than something to use.
        if (CardType == CardType.Freeform)
        {
            return;
        }

        if (QuestionBlocks.Count == 0)
        {
            QuestionBlocks.Add(new BlockEditorViewModel(CardFace.Question, ContentKind.Markdown));
        }

        switch (CardType)
        {
            case CardType.MultipleChoice:
                // A fresh board is laid out with the usual four. A card being loaded keeps however
                // many it was saved with — opening a true/false card must not quietly pad it back
                // out to four and leave the author two blanks to tidy up again.
                var slots = Choices.Count == 0
                    ? DefaultChoiceSlots
                    : Math.Clamp(Choices.Count, MinChoiceSlots, MaxChoiceSlots);

                while (Choices.Count < slots)
                {
                    Choices.Add(new ChoiceEditorViewModel());
                }

                while (Choices.Count > slots)
                {
                    Choices.RemoveAt(Choices.Count - 1);
                }

                RelabelChoices();

                // Nothing marked right yet: the first slot is the least surprising default.
                if (!Choices.Any(c => c.IsCorrect))
                {
                    Choices[0].IsCorrect = true;
                }

                break;

            case CardType.Cloze:
                // The passage carries both sides, so a separate answer block is noise.
                AnswerBlocks.Clear();
                break;

            default:
                if (AnswerBlocks.Count == 0)
                {
                    AnswerBlocks.Add(new BlockEditorViewModel(CardFace.Answer, ContentKind.Markdown));
                }

                break;
        }
    }

    private void RelabelChoices()
    {
        for (var i = 0; i < Choices.Count; i++)
        {
            Choices[i].Label = i < SlotLabels.Length ? SlotLabels[i] : (i + 1).ToString();
        }
    }

    // ---- freeform designer ----------------------------------------------

    [RelayCommand]
    private void SetTool(string? toolName)
    {
        if (!Enum.TryParse<FreeformTool>(toolName, true, out var tool))
        {
            return;
        }

        ActiveTool = tool;

        // Picking an ink tool drops the element selection: the inspector would otherwise still be
        // editing a text box the user has visibly stopped working on.
        if (tool is FreeformTool.Draw or FreeformTool.Erase)
        {
            Select((BlockEditorViewModel?)null);
        }
    }

    [RelayCommand]
    private void ShowFace(string? faceName)
    {
        if (Enum.TryParse<CardFace>(faceName, true, out var face))
        {
            DesignFace = face;
        }
    }

    /// <summary>
    /// Drops a new element onto the canvas centred on where the pointer landed, the way a drawing
    /// tool places a shape. Called by the view, which is the only thing that knows where the click
    /// was; the coordinates are already in card space because the artboard is a fixed-size surface.
    /// </summary>
    public void PlaceElementAt(ContentKind kind, double x, double y)
    {
        var width = kind == ContentKind.Image ? 320d : 360d;
        var height = kind == ContentKind.Image ? 240d : 120d;

        var block = new BlockEditorViewModel(DesignFace, kind);

        // Centre it under the cursor; Place clamps it back inside the card if that overhangs.
        block.Place(x - (width / 2), y - (height / 2), width, height);

        ActiveFaceBlocks.Add(block);
        Select(block);

        // Back to the pointer. A tool that stayed armed would drop a second element on the next
        // click, which is not what any drawing app does.
        ActiveTool = FreeformTool.Select;

        if (kind == ContentKind.Image)
        {
            _ = AttachImageAsync(block, fromClipboardFirst: true);
        }
    }

    /// <summary>The content kind the armed tool places, or null when the tool does not place one.</summary>
    public ContentKind? PendingElementKind => ActiveTool switch
    {
        FreeformTool.Text => ContentKind.Markdown,
        FreeformTool.Image => ContentKind.Image,
        _ => null,
    };

    /// <summary>Clears the ink on the face being designed. Elements are untouched.</summary>
    [RelayCommand]
    private void ClearInk()
    {
        ActiveStrokes.Clear();
        StatusMessage = $"Cleared the {DesignFace.ToString().ToLowerInvariant()} side's drawing.";
    }

    [RelayCommand]
    private void PickInkColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            InkColor = hex;
            ActiveTool = FreeformTool.Draw;
        }
    }

    /// <summary>
    /// Pulls the saved Drawing blocks out into the per-face stroke collections, leaving only
    /// placeable elements in the block lists.
    /// </summary>
    private void ExtractInkLayers()
    {
        QuestionStrokes.Clear();
        AnswerStrokes.Clear();

        foreach (var (blocks, strokes) in new[]
                 {
                     (QuestionBlocks, QuestionStrokes),
                     (AnswerBlocks, AnswerStrokes),
                 })
        {
            foreach (var drawing in blocks.Where(b => b.Kind == ContentKind.Drawing).ToList())
            {
                foreach (var stroke in InkSerializer.Parse(drawing.Text))
                {
                    strokes.Add(stroke);
                }

                blocks.Remove(drawing);
            }
        }
    }

    /// <summary>
    /// The inverse: turns each face's strokes back into a single Drawing block, appended after
    /// the elements. A face with no ink contributes nothing.
    /// </summary>
    private IEnumerable<ContentBlockDto> InkBlocks()
    {
        foreach (var (face, strokes) in new[]
                 {
                     (CardFace.Question, QuestionStrokes),
                     (CardFace.Answer, AnswerStrokes),
                 })
        {
            if (strokes.Count == 0)
            {
                continue;
            }

            yield return new ContentBlockDto(
                Guid.Empty,
                face,
                // Ordinal is fixed up by the caller once the elements are counted.
                0,
                ContentKind.Drawing,
                InkSerializer.Serialize(strokes),
                null,
                null,
                ImageStretch.Uniform,
                null,
                null,
                X: 0,
                Y: 0,
                Width: CardCanvas.Width,
                Height: CardCanvas.Height);
        }
    }

    // ---- subject tags ----------------------------------------------------

    /// <summary>
    /// Adds the typed (or picked) tag. Case-insensitively de-duplicated, so pressing Enter twice
    /// on the same name is harmless.
    /// </summary>
    [RelayCommand]
    private void AddTag(string? name)
    {
        var candidate = (name ?? TagDraft ?? string.Empty).Trim();

        if (candidate.Length == 0)
        {
            return;
        }

        if (!SubjectTags.Any(t => string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            SubjectTags.Add(new SubjectTagViewModel(candidate, ColorForTag(candidate)));
            OnPropertyChanged(nameof(HasSubjectTags));
            RaiseDraftChanged();
            RefreshInheritedTags();
        }

        TagDraft = null;
        TagError = null;
        ErrorMessage = null;
        IsSuggestionsOpen = false;
        ApplyTagFilter();
    }

    /// <summary>Puts an existing subject on the card — the drop-down's job, as opposed to creating one.</summary>
    [RelayCommand]
    private void PickTag(SubjectSummary? subject)
    {
        if (subject is not null)
        {
            AddTag(subject.Name);
        }
    }

    /// <summary>
    /// Opens the create-a-subject dialog, seeded with whatever has been typed, and tags the card
    /// with the result.
    /// <para>
    /// Creation moved out of the header and into a dialog because placing a subject in a tree needs
    /// to <em>show</em> the tree. The header version asked for a parent from a flat drop-down, which
    /// meant answering a question about shape without being able to see the shape.
    /// </para>
    /// </summary>
    [RelayCommand]
    private Task CreateSubjectAsync() => RunAsync(async () =>
    {
        // Closing it first: a popup left open over a modal is both ugly and, on some window
        // managers, still on top of it.
        IsSuggestionsOpen = false;
        TagError = null;

        var created = await _dialogs.CreateSubjectAsync(
            new SubjectCreateViewModel(_dispatcher, TagDraft));

        if (created is null)
        {
            return;
        }

        // Reload first so the new subject is in _knownSubjects — that is what gives its chip the
        // right colour and lets RefreshInheritedTags see where it sits.
        await LoadSubjectsAsync();

        AddTag(created);
    });

    /// <summary>
    /// What Enter does in the subject box: applies the typed subject if it already exists, and
    /// otherwise creates it. One key for the two paths the buttons keep separate, because at the
    /// moment of pressing Enter the user has already said which they meant by what they typed.
    /// </summary>
    [RelayCommand]
    private Task CommitTagAsync()
    {
        var name = (TagDraft ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            return Task.CompletedTask;
        }

        if (_knownSubjects.Any(s => string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            AddTag(name);
            return Task.CompletedTask;
        }

        return CreateSubjectAsync();
    }

    [RelayCommand]
    private void RemoveTag(SubjectTagViewModel? tag)
    {
        // An inherited tag has no row to remove: it is a consequence of where its subject sits.
        if (tag is null || tag.IsInherited || !SubjectTags.Remove(tag))
        {
            return;
        }

        OnPropertyChanged(nameof(HasSubjectTags));
        RaiseDraftChanged();
        RefreshInheritedTags();
        ApplyTagFilter();
    }

    partial void OnTagDraftChanged(string? value)
    {
        TagError = null;
        ApplyTagFilter();
        QueueSuggestions();
    }

    /// <summary>
    /// Opens the prediction list a beat after typing stops.
    /// <para>
    /// The delay is the whole design. Opening on every keystroke would put a popup under the cursor
    /// while someone is still typing and make the box feel like it is fighting back; opening only
    /// on demand means the suggestions are never seen by anyone who does not already know they are
    /// there. Waiting for a pause reads as the box answering a question you stopped to ask.
    /// </para>
    /// <para>
    /// It stays shut when the box trims to empty — every subject "matches" nothing typed, and a
    /// list of everything is not a suggestion — and when nothing matches, since an empty popup
    /// would be a worse answer than none.
    /// </para>
    /// </summary>
    private void QueueSuggestions()
    {
        _suggestDebounce?.Cancel();
        _suggestDebounce?.Dispose();

        // Whatever was showing is now stale: it was filtered on the previous keystroke.
        IsSuggestionsOpen = false;

        if (string.IsNullOrWhiteSpace(TagDraft))
        {
            _suggestDebounce = null;
            return;
        }

        _suggestDebounce = new CancellationTokenSource();

        var token = _suggestDebounce.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Re-checked on the UI thread: the draft may have been cleared, or the tag added,
                // between the delay elapsing and this running.
                if (!token.IsCancellationRequested
                    && !string.IsNullOrWhiteSpace(TagDraft)
                    && TagMatches.Count > 0)
                {
                    IsSuggestionsOpen = true;
                }
            });
        });
    }

    private void SetTags(IEnumerable<SubjectTagViewModel> tags)
    {
        SubjectTags.Clear();

        // Inherited entries are filtered out on the way in: the card's own tags are the only thing
        // this collection holds, and the inherited strip is derived from them a moment later.
        // Keeping an ancestor here would make it look removable and would send it back on save as
        // though the user had applied it.
        foreach (var tag in tags.Where(t => !t.IsInherited))
        {
            SubjectTags.Add(tag);
        }

        OnPropertyChanged(nameof(HasSubjectTags));
        RaiseDraftChanged();
        RefreshInheritedTags();
        ApplyTagFilter();
    }

    // ---- selection -------------------------------------------------------

    [RelayCommand]
    private void SelectBlock(BlockEditorViewModel? block) => Select(block);

    [RelayCommand]
    private void SelectChoice(ChoiceEditorViewModel? choice) => Select(choice);

    private void Select(BlockEditorViewModel? block)
    {
        foreach (var candidate in QuestionBlocks.Concat(AnswerBlocks))
        {
            candidate.IsSelected = ReferenceEquals(candidate, block);
        }

        foreach (var choice in Choices)
        {
            choice.IsActive = false;
        }

        SelectedChoice = null;
        SelectedBlock = block;
    }

    private void Select(ChoiceEditorViewModel? choice)
    {
        foreach (var candidate in Choices)
        {
            candidate.IsActive = ReferenceEquals(candidate, choice);
        }

        foreach (var block in QuestionBlocks.Concat(AnswerBlocks))
        {
            block.IsSelected = false;
        }

        SelectedBlock = null;
        SelectedChoice = choice;
    }

    // ---- card type -------------------------------------------------------

    [RelayCommand]
    private void SetCardType(string? typeName)
    {
        if (Enum.TryParse<CardType>(typeName, true, out var parsed))
        {
            CardType = parsed;
        }
    }

    // ---- block commands -------------------------------------------------

    [RelayCommand]
    private void AddBlock(string? parameter)
    {
        // Parameter format "Face:Kind", e.g. "Question:Code" — one command instead of eight.
        if (string.IsNullOrWhiteSpace(parameter))
        {
            return;
        }

        var parts = parameter.Split(':', 2);

        if (parts.Length != 2
            || !Enum.TryParse<CardFace>(parts[0], true, out var face)
            || !Enum.TryParse<ContentKind>(parts[1], true, out var kind))
        {
            return;
        }

        var target = face == CardFace.Question ? QuestionBlocks : AnswerBlocks;
        var block = new BlockEditorViewModel(face, kind);
        target.Add(block);
        Select(block);

        if (kind == ContentKind.Image)
        {
            _ = AttachImageAsync(block, fromClipboardFirst: true);
        }
    }

    [RelayCommand]
    private void RemoveBlock(BlockEditorViewModel? block)
    {
        if (block is null)
        {
            return;
        }

        var target = block.Face == CardFace.Question ? QuestionBlocks : AnswerBlocks;

        // On the stacked layouts the question side must never be emptied — a card with no prompt
        // is not a card, and there is no other way to get an element back onto it.
        //
        // A designed card is different: its canvas starts empty and the toolbar can always add
        // something, so clearing it is a legitimate step rather than a dead end. The save-time
        // validation is what insists both faces end up with content.
        if (!IsFreeform && target.Count == 1 && block.Face == CardFace.Question)
        {
            ErrorMessage = "The question needs at least one element.";
            return;
        }

        target.Remove(block);

        if (ReferenceEquals(SelectedBlock, block))
        {
            // Never jump the selection to the face that is not on screen.
            Select(IsFreeform
                ? ActiveFaceBlocks.LastOrDefault()
                : target.FirstOrDefault() ?? QuestionBlocks.FirstOrDefault());
        }
    }

    [RelayCommand]
    private void MoveBlockUp(BlockEditorViewModel? block) => Move(block, -1);

    [RelayCommand]
    private void MoveBlockDown(BlockEditorViewModel? block) => Move(block, +1);

    private void Move(BlockEditorViewModel? block, int delta)
    {
        if (block is null)
        {
            return;
        }

        var target = block.Face == CardFace.Question ? QuestionBlocks : AnswerBlocks;
        var index = target.IndexOf(block);
        var destination = index + delta;

        if (index < 0 || destination < 0 || destination >= target.Count)
        {
            return;
        }

        target.Move(index, destination);
    }

    // ---- cloze designer --------------------------------------------------

    /// <summary>Wraps the current TextBox selection in cloze markers.</summary>
    [RelayCommand]
    private void MakeBlank(BlockEditorViewModel? block)
    {
        block ??= ClozeBlock;

        if (block is null)
        {
            return;
        }

        // Prefer the live selection, but fall back to the last non-empty one the text box
        // reported. Clicking the button is itself an interaction with the text box, and the
        // selection does not reliably survive it — remembering the last real selection means the
        // command works regardless, instead of depending on that timing.
        var (offset, length) = block.SelectionLength > 0
            ? (block.SelectionOffset, block.SelectionLength)
            : (block.LastSelectionOffset, block.LastSelectionLength);

        if (length <= 0 || offset < 0 || offset + length > block.Text.Length)
        {
            ErrorMessage = "Select the words you want to hide first.";
            return;
        }

        block.Text = ClozeParser.Wrap(block.Text, offset, length);
        CardType = CardType.Cloze;
        ErrorMessage = null;
        RefreshCloze();
        RefreshPreview();
    }

    /// <summary>Strips every <c>{{blank}}</c> marker, turning the passage back into plain prose.</summary>
    [RelayCommand]
    private void ClearBlanks()
    {
        if (ClozeBlock is not { } block)
        {
            return;
        }

        block.Text = ClozeParser.RenderSolution(block.Text);
        RefreshCloze();
        RefreshPreview();
    }

    private void RefreshCloze()
    {
        var text = ClozeBlock?.Text ?? string.Empty;

        ClozeBlanks = ClozeParser.Parse(text);
        ClozePrompt = ClozeParser.RenderPrompt(text);
        ClozeSolution = ClozeParser.RenderSolution(text);

        OnPropertyChanged(nameof(HasBlanks));
    }

    // ---- images ----------------------------------------------------------

    [RelayCommand]
    private Task PasteImage(BlockEditorViewModel? block) => AttachImageAsync(block, fromClipboardFirst: true);

    [RelayCommand]
    private Task BrowseImage(BlockEditorViewModel? block) => AttachImageAsync(block, fromClipboardFirst: false);

    /// <summary>Called by the view when an image is dropped onto a block.</summary>
    public Task AttachDroppedImageAsync(BlockEditorViewModel block, PastedImage image) => RunAsync(() => StoreAsync(block, image));

    private Task AttachImageAsync(BlockEditorViewModel? block, bool fromClipboardFirst) => RunAsync(async () =>
    {
        if (block is null)
        {
            return;
        }

        var image = await AcquireAsync(fromClipboardFirst);

        if (image is null)
        {
            return;
        }

        await StoreAsync(block, image);
    });

    private async Task StoreAsync(BlockEditorViewModel block, PastedImage image)
    {
        var descriptor = await _dispatcher.SendAsync(new SaveMediaCommand(image.Bytes, image.SuggestedFileName));

        // Seed the cache from the bytes we already have so the preview does not round-trip to disk.
        _imageCache.Put(descriptor.Id, image.Bytes);

        block.Kind = ContentKind.Image;
        block.MediaId = descriptor.Id;
        block.AltText ??= descriptor.FileName;

        StatusMessage = $"Attached {descriptor.FileName} ({descriptor.ByteSize / 1024d:0.#} KB).";
        RefreshPreview();
    }

    // ---- multiple-choice slots ------------------------------------------

    [RelayCommand]
    private Task PasteChoiceImage(ChoiceEditorViewModel? choice) => AttachChoiceImageAsync(choice, fromClipboardFirst: true);

    [RelayCommand]
    private Task BrowseChoiceImage(ChoiceEditorViewModel? choice) => AttachChoiceImageAsync(choice, fromClipboardFirst: false);

    /// <summary>Called by the view when an image is dropped onto an answer slot.</summary>
    public Task AttachDroppedChoiceImageAsync(ChoiceEditorViewModel choice, PastedImage image)
        => RunAsync(() => StoreChoiceAsync(choice, image));

    private Task AttachChoiceImageAsync(ChoiceEditorViewModel? choice, bool fromClipboardFirst) => RunAsync(async () =>
    {
        if (choice is null)
        {
            return;
        }

        var image = await AcquireAsync(fromClipboardFirst);

        if (image is null)
        {
            return;
        }

        await StoreChoiceAsync(choice, image);
    });

    private async Task StoreChoiceAsync(ChoiceEditorViewModel choice, PastedImage image)
    {
        var descriptor = await _dispatcher.SendAsync(new SaveMediaCommand(image.Bytes, image.SuggestedFileName));

        _imageCache.Put(descriptor.Id, image.Bytes);

        choice.MediaId = descriptor.Id;
        choice.AltText ??= descriptor.FileName;

        StatusMessage = $"Answer {choice.Label}: {descriptor.FileName} ({descriptor.ByteSize / 1024d:0.#} KB).";
    }

    [RelayCommand]
    private void ClearChoiceImage(ChoiceEditorViewModel? choice)
    {
        if (choice is not null)
        {
            choice.MediaId = null;
            choice.AltText = null;
        }
    }

    /// <summary>Radio behaviour by default; ticking a second slot turns the card multi-select.</summary>
    [RelayCommand]
    private void ToggleChoiceCorrect(ChoiceEditorViewModel? choice)
    {
        if (choice is null)
        {
            return;
        }

        choice.IsCorrect = !choice.IsCorrect;

        // Never leave the card with nothing marked right.
        if (!Choices.Any(c => c.IsCorrect))
        {
            choice.IsCorrect = true;
        }
    }

    /// <summary>Whether there is room for another answer. Drives the "add" button.</summary>
    public bool CanAddChoice => Choices.Count < MaxChoiceSlots;

    /// <summary>
    /// Whether an answer can be taken away. False at two, which is the floor: a question with one
    /// option left is not a question.
    /// </summary>
    public bool CanRemoveChoice => Choices.Count > MinChoiceSlots;

    /// <summary>
    /// Adds an empty answer slot. Selected on the way in, so the caret is already where you would
    /// type — adding one and then having to click it would be two gestures for one intention.
    /// </summary>
    [RelayCommand]
    private void AddChoice()
    {
        if (!CanAddChoice)
        {
            return;
        }

        var added = new ChoiceEditorViewModel();
        Choices.Add(added);
        Select(added);
    }

    /// <summary>
    /// Takes an answer off the board entirely.
    /// <para>
    /// This replaced a button that only emptied the slot. With a fixed four slots that was the
    /// most you could do; now that the count is yours to choose, emptying one is just leaving a
    /// blank where an answer should be — which is the thing this is here to stop.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void RemoveChoice(ChoiceEditorViewModel? choice)
    {
        if (choice is null || !CanRemoveChoice || !Choices.Remove(choice))
        {
            return;
        }

        // The inspector-less board keeps selection on a slot; the removed one cannot hold it.
        if (ReferenceEquals(SelectedChoice, choice))
        {
            SelectedChoice = null;
        }

        // Removing the only right answer would leave a card that cannot be answered correctly.
        if (!Choices.Any(c => c.IsCorrect))
        {
            Choices[0].IsCorrect = true;
        }
    }

    /// <summary>Clipboard first when asked, then the file picker, with one message if both come back empty.</summary>
    private async Task<PastedImage?> AcquireAsync(bool fromClipboardFirst)
    {
        var image = fromClipboardFirst
            ? await (ClipboardImageProvider?.Invoke() ?? Task.FromResult<PastedImage?>(null))
            : null;

        image ??= await (FileImageProvider?.Invoke() ?? Task.FromResult<PastedImage?>(null));

        if (image is null)
        {
            ErrorMessage = fromClipboardFirst
                ? "No image on the clipboard. Copy a screenshot, or use Browse."
                : "No image chosen.";
        }

        return image;
    }

    // ---- persistence ----------------------------------------------------

    [RelayCommand]
    private Task Save() => RunAsync(async () =>
    {
        // A tag left half-typed in the box is clearly intended; take it rather than losing it.
        if (!string.IsNullOrWhiteSpace(TagDraft))
        {
            AddTag(TagDraft);
        }

        if (SubjectTags.Count == 0)
        {
            ErrorMessage = "Give the card at least one subject tag — type any name.";
            return;
        }

        // An untouched element is a placeholder on the artboard, not content — the same rule the
        // answer slots follow. This matters most on the multiple-choice board, which does not
        // render the answer face at all: without the filter, the empty markdown element that a
        // standard card starts life with would be submitted invisibly and rejected by the
        // validator, complaining about a block the user cannot see.
        //
        // Ordinals are assigned after filtering so the survivors stay contiguous from zero.
        var blocks = new List<ContentBlockDto>();
        blocks.AddRange(QuestionBlocks.Where(b => !b.IsEmpty).Select((b, i) => b.ToDto(i)));
        blocks.AddRange(AnswerBlocks.Where(b => !b.IsEmpty).Select((b, i) => b.ToDto(i)));

        // Ink rejoins the block list here, one Drawing per face, numbered after that face's
        // elements so ordinals stay dense.
        foreach (var ink in InkBlocks())
        {
            var ordinal = blocks.Count(b => b.Face == ink.Face);
            blocks.Add(ink with { Ordinal = ordinal });
        }

        // Empty slots are placeholders on the artboard, not answers.
        var choices = IsMultipleChoice
            ? Choices.Where(c => !c.IsBlank).Select((c, i) => c.ToDto(i)).ToList()
            : [];

        var id = await _dispatcher.SendAsync(new SaveFlashcardCommand
        {
            Id = EditingId,
            SubjectNames = [.. SubjectTags.Select(t => t.Name)],
            Name = Name.Trim(),
            CardType = CardType,
            Notes = Notes,
            IsSuspended = IsSuspended,
            Blocks = blocks,
            Choices = choices,
        });

        Saved?.Invoke(this, id);

        if (!IsEditing)
        {
            StartNewCard();
            StatusMessage = "Card created. Ready for the next one.";
        }
        else
        {
            EditingId = id;
            StatusMessage = "Changes saved.";

            // Saved is the new baseline: leaving now would lose nothing, so nothing should be asked.
            MarkClean();
        }

        // Reload so tags minted by this save pick up their assigned colours.
        await LoadSubjectsAsync();
    });

    /// <summary>Throws the staged draft away, asking first when there is something to lose.</summary>
    [RelayCommand]
    private Task Reset() => RunAsync(async () =>
    {
        if (await TryStartNewCardAsync())
        {
            StatusMessage = "Draft cleared.";
        }
    });

    /// <summary>
    /// Leaves edit mode and starts a fresh card.
    /// <para>
    /// This is the Create half of the mode pill, and it exists because there was no obvious way out
    /// of editing. The discard bin did do it, but it reads as "clear what I have typed" rather than
    /// "stop editing that card".
    /// </para>
    /// </summary>
    [RelayCommand]
    private Task ExitEdit() => RunAsync(async () =>
    {
        if (!IsEditing)
        {
            return;
        }

        if (await TryStartNewCardAsync())
        {
            StatusMessage = "Started a new card.";
        }
    });

    /// <summary>
    /// Starts a blank draft, confirming first when one is staged. Returns false if the user
    /// declined and the draft was left alone.
    /// <para>
    /// Public because the shell's "New card" button lands here too — it discards the draft just as
    /// surely as the designer's own bin does, and should ask the same question before it happens.
    /// </para>
    /// </summary>
    public async Task<bool> TryStartNewCardAsync()
    {
        // Keyed on having been edited rather than on having content, for the same reason the type
        // picker is: leaving an untouched card loses nothing, so there is nothing to ask about.
        if (IsDirty)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                IsEditing ? "Discard your changes?" : "Discard this draft?",
                IsEditing
                    ? "Your unsaved changes to this card are lost and the designer starts a new card. "
                      + "The saved card itself is untouched."
                    : "Everything you have built here is lost.",
                confirmText: "Discard",
                destructive: true);

            if (!confirmed)
            {
                return false;
            }
        }

        StartNewCard();
        return true;
    }

    // ---- preview plumbing -----------------------------------------------

    private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var block in e.OldItems?.OfType<BlockEditorViewModel>() ?? [])
        {
            block.PropertyChanged -= OnBlockPropertyChanged;
        }

        foreach (var block in e.NewItems?.OfType<BlockEditorViewModel>() ?? [])
        {
            block.PropertyChanged += OnBlockPropertyChanged;
        }

        OnPropertyChanged(nameof(ClozeBlock));
        RefreshCloze();
        RefreshPreview();
        RaiseDraftChanged();
    }

    private void OnChoicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var choice in e.OldItems?.OfType<ChoiceEditorViewModel>() ?? [])
        {
            choice.PropertyChanged -= OnChoicePropertyChanged;
        }

        foreach (var choice in e.NewItems?.OfType<ChoiceEditorViewModel>() ?? [])
        {
            choice.PropertyChanged += OnChoicePropertyChanged;
        }

        RelabelChoices();

        // The letters shift when a slot goes, and both buttons live on the count.
        OnPropertyChanged(nameof(CanAddChoice));
        OnPropertyChanged(nameof(CanRemoveChoice));

        // Adding or removing an answer is an edit like any other. Without this, changing the
        // number of options would leave the draft looking untouched and the discard prompt
        // would not appear when it should.
        RaiseDraftChanged();
    }

    private void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Selection changes fire constantly while typing and never affect the render.
        if (e.PropertyName is nameof(BlockEditorViewModel.SelectionStart)
            or nameof(BlockEditorViewModel.SelectionEnd)
            or nameof(BlockEditorViewModel.SelectionOffset)
            or nameof(BlockEditorViewModel.SelectionLength)
            or nameof(BlockEditorViewModel.IsSelected))
        {
            return;
        }

        if (IsCloze && ReferenceEquals(sender, ClozeBlock) && e.PropertyName == nameof(BlockEditorViewModel.Text))
        {
            RefreshCloze();
        }

        RefreshPreview();
        RaiseDraftChanged();
    }

    partial void OnNameChanged(string value) => RaiseDraftChanged();

    private void OnChoicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChoiceEditorViewModel.IsCorrect))
        {
            OnPropertyChanged(nameof(Choices));
        }

        RaiseDraftChanged();
    }

    private void RefreshPreview()
    {
        QuestionPreview = [.. QuestionBlocks.Select((b, i) => b.ToDto(i))];
        AnswerPreview = [.. AnswerBlocks.Select((b, i) => b.ToDto(i))];
    }

    partial void OnCardTypeChanged(CardType value)
    {
        OnPropertyChanged(nameof(IsStandard));
        OnPropertyChanged(nameof(IsMultipleChoice));
        OnPropertyChanged(nameof(IsCloze));
        OnPropertyChanged(nameof(IsFreeform));
        OnPropertyChanged(nameof(IsFlowLayout));

        // Loading a card, or putting the picker back after a declined switch: shape the board and
        // ask nothing.
        if (_settingTypeInternally)
        {
            _confirmedType = value;

            // Reshaping the board is not an edit to it. EnsureShapeForType adds and removes blocks,
            // which runs through the same hooks a user's typing does, so the flag is put back the
            // way it was found — whoever asked for the reshape decides what the baseline is.
            var wasDirty = _isDirty;

            EnsureShapeForType();
            RefreshCloze();
            RefreshPreview();

            if (!wasDirty)
            {
                MarkClean();
            }

            return;
        }

        if (value == _confirmedType)
        {
            return;
        }

        _ = ChangeTypeAsync(value);
    }

    /// <summary>
    /// Switching board type starts a fresh draft.
    /// <para>
    /// The boards do not share content in any meaningful way — multiple-choice options mean
    /// nothing on a Q&amp;A card, cloze blanks are markup inside one passage, and a freeform card's
    /// elements carry coordinates the stacked boards ignore. Carrying the old content across left
    /// stale state behind it, which is how an image attached to one draft could still be announced
    /// in the status bar after switching to another.
    /// </para>
    /// <para>
    /// The card's identity survives: name, tags, and — when editing — which card is being edited.
    /// Those describe the card, not the shape of its content.
    /// </para>
    /// </summary>
    private async Task ChangeTypeAsync(CardType requested)
    {
        var wasEditing = IsEditing;

        // Only when something has actually been edited. A card opened from the manage panel is full
        // of content and has had nothing changed about it, and warning that changes will be lost
        // when none have been made is how a warning stops being read.
        if (IsDirty)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                wasEditing ? "Discard your changes?" : "Discard this draft?",
                wasEditing
                    ? $"A \"{Describe(requested)}\" card is a different card, so this starts a new "
                      + "one. Your unsaved changes are lost and the card you were editing is left as it is."
                    : $"Switching to \"{Describe(requested)}\" clears what you have built so far. "
                      + "The card name and its tags are kept.",
                confirmText: wasEditing ? "Discard and start new" : "Discard and switch",
                destructive: true);

            if (!confirmed)
            {
                // Put the picker back where it was. The guard stops this from prompting again.
                _settingTypeInternally = true;
                CardType = _confirmedType;
                _settingTypeInternally = false;
                return;
            }
        }

        _confirmedType = requested;

        if (wasEditing)
        {
            // Changing the type of a card being edited is not an edit to that card — a cloze card
            // and a question-and-answer card are different things, and the one on screen would no
            // longer be the one that was opened. So editing ends here and a new card of the chosen
            // type begins, which is also what puts the mode pill back to Create.
            StartNewCard(requested);
            StatusMessage = $"Started a new \"{Describe(requested)}\" card.";
            return;
        }

        ClearDraftContent();
    }

    private static string Describe(CardType type) => type switch
    {
        CardType.MultipleChoice => "Multiple choice",
        CardType.Cloze => "Fill in the blank",
        CardType.Freeform => "Custom design",
        _ => "Question & answer",
    };

    /// <summary>
    /// Empties everything the draft staged, leaving the card's identity alone. Nothing here
    /// touches the database — an edit is only committed by <see cref="Save"/>.
    /// </summary>
    private void ClearDraftContent()
    {
        QuestionBlocks.Clear();
        AnswerBlocks.Clear();
        Choices.Clear();
        QuestionStrokes.Clear();
        AnswerStrokes.Clear();

        SelectedBlock = null;
        SelectedChoice = null;
        DesignFace = CardFace.Question;
        ActiveTool = FreeformTool.Select;

        // The "Attached diagram.png" notice belonged to the draft that just went.
        StatusMessage = null;
        ErrorMessage = null;

        EnsureShapeForType();
        Select(QuestionBlocks.FirstOrDefault());
        RefreshCloze();
        RefreshPreview();
        RaiseDraftChanged();

        // The board this just built is the new baseline. Emptying and re-seeding it runs through
        // every collection-changed hook, so without this the act of switching type would leave the
        // draft looking edited — and the *next* switch would ask to discard work nobody had done.
        MarkClean();
    }

    private void OnStrokesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RaiseDraftChanged();

    /// <summary>
    /// Every edit path already funnels through here — typing in a block, adding or removing one,
    /// renaming the card, tagging it, drawing, changing a choice — so this is where the draft
    /// becomes dirty. <see cref="MarkClean"/> is what resets it once a draft is loaded or started.
    /// </summary>
    private void RaiseDraftChanged()
    {
        if (!_isDirty)
        {
            _isDirty = true;
            OnPropertyChanged(nameof(IsDirty));
        }

        OnPropertyChanged(nameof(HasDraftContent));
    }

    /// <summary>
    /// Declares the current contents to be the baseline — nothing has been edited yet.
    /// <para>
    /// Called at the end of starting a new card and of loading an existing one, after the populate
    /// has finished raising its change notifications. Doing it any earlier would leave the draft
    /// dirty from the act of filling it in.
    /// </para>
    /// </summary>
    private void MarkClean()
    {
        if (_isDirty)
        {
            _isDirty = false;
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    partial void OnDesignFaceChanged(CardFace value)
    {
        OnPropertyChanged(nameof(ActiveFaceBlocks));
        OnPropertyChanged(nameof(ActiveStrokes));
        OnPropertyChanged(nameof(IsShowingQuestion));

        // The selected element belongs to the face we just left.
        Select((BlockEditorViewModel?)null);
    }

    partial void OnActiveToolChanged(FreeformTool value)
    {
        OnPropertyChanged(nameof(IsDrawingTool));
        OnPropertyChanged(nameof(IsErasingTool));
        OnPropertyChanged(nameof(IsInkActive));
    }



    partial void OnEditingIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SaveLabel));
    }
}
