namespace Flashcards.Desktop.ViewModels.Design;

/// <summary>
/// What a pointer press on the freeform canvas does. Exactly one is active at a time, the way a
/// drawing tool's toolbar works.
/// </summary>
public enum FreeformTool
{
    /// <summary>Click to select, drag to move, drag a corner grip to resize.</summary>
    Select = 0,

    /// <summary>Drop a new markdown text element.</summary>
    Text = 1,

    /// <summary>Drop a new image element and immediately ask for the picture.</summary>
    Image = 2,

    /// <summary>Draw freehand ink.</summary>
    Draw = 3,

    /// <summary>Remove whole ink strokes the pointer passes over.</summary>
    Erase = 4,
}
