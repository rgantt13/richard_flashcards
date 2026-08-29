using Flashcards.Domain.Subjects;

namespace Flashcards.Application.Subjects;

/// <summary>
/// Puts a flat list of subjects into the order a tree is drawn in: each subject immediately
/// followed by its own subtree, siblings alphabetically.
/// <para>
/// The read store returns subjects alphabetically because that is the order SQLite can produce
/// without a second pass. Every screen that shows subjects wants them nested instead, and doing the
/// arrangement once here — over a handful of rows, using the same
/// <see cref="SubjectHierarchy"/> the write side validates against — keeps the three panels from
/// each inventing their own idea of the order.
/// </para>
/// </summary>
internal static class SubjectOrdering
{
    public static IReadOnlyList<T> InTreeOrder<T>(
        IReadOnlyList<T> subjects,
        Func<T, Guid> id,
        Func<T, Guid?> parentId,
        Func<T, string> name)
    {
        if (subjects.Count == 0)
        {
            return subjects;
        }

        var hierarchy = new SubjectHierarchy(
            subjects.Select(s => new SubjectPlacement(id(s), parentId(s), name(s))));

        var byId = subjects.ToDictionary(id);

        // Anything the walk cannot reach still has to appear — see SubjectHierarchy, which treats a
        // dangling parent as a root for the same reason. Losing a subject from a list is a much
        // worse failure than showing it in the wrong place.
        var ordered = hierarchy.InTreeOrder()
            .Where(node => byId.ContainsKey(node.Subject.Id))
            .Select(node => byId[node.Subject.Id])
            .ToList();

        if (ordered.Count != subjects.Count)
        {
            var seen = ordered.Select(id).ToHashSet();
            ordered.AddRange(subjects.Where(s => !seen.Contains(id(s))));
        }

        return ordered;
    }
}
