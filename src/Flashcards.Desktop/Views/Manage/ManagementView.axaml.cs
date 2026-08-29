using Avalonia.Controls;

namespace Flashcards.Desktop.Views.Manage;

/// <summary>
/// The manage panel's view.
/// <para>
/// Deliberately empty. All the subject drag-and-drop that used to live here moved into
/// <see cref="Controls.SubjectTreeView"/> once the create dialog needed the same behaviour, and the
/// panel now just binds a command to it.
/// </para>
/// </summary>
public partial class ManagementView : UserControl
{
    public ManagementView() => InitializeComponent();
}
