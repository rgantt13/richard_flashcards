using Flashcards.Domain.Common;

namespace Flashcards.Domain.Subjects;

/// <summary>One subject's position in the tree, stripped to what the shape rules need.</summary>
public readonly record struct SubjectPlacement(Guid Id, Guid? ParentId, string Name);

/// <summary>
/// The subject tree, and every rule about its shape.
/// <para>
/// Subjects are stored as an adjacency list — one nullable <c>parent_id</c> per row — and this
/// type is what turns that flat set into a tree and answers questions about it. It lives in the
/// domain rather than in SQL because the interesting rules (a move may not create a cycle, a move
/// may not push any descendant past the depth limit) need to see the whole shape at once, and a
/// subject list is small enough that loading all of it is cheaper than a recursive query per
/// question.
/// </para>
/// <para>
/// Ancestry is <em>derived</em>, never stored on the card. A card tagged "MSSQL" is found by a
/// search for "SQL" because MSSQL sits under SQL right now — not because anything was written to
/// the card when the tag was applied. Re-parenting a subject therefore re-tags its whole subtree
/// for free, and there is no denormalised copy that can drift.
/// </para>
/// </summary>
public sealed class SubjectHierarchy
{
    /// <summary>
    /// How many levels deep the tree may go, counting the root as level 1.
    /// <para>
    /// This is a presentation constraint promoted to a domain rule: each level is rendered as an
    /// extra indent on the Study panel's subject grid, and past five the rows have no width left
    /// to say anything. Enforcing it here means the UI never has to render a shape it cannot draw.
    /// </para>
    /// </summary>
    public const int MaxDepth = 5;

    /// <summary>
    /// Stands in for "no parent" as a dictionary key.
    /// <para>
    /// <c>Dictionary</c> rejects a null key even when the key type is <c>Guid?</c>, so grouping
    /// roots under <c>null</c> throws rather than producing a roots bucket. Real subjects carry
    /// version-7 GUIDs, so the empty one cannot collide with a genuine id.
    /// </para>
    /// </summary>
    private static readonly Guid Root = Guid.Empty;

    private readonly Dictionary<Guid, SubjectPlacement> _byId;
    private readonly Dictionary<Guid, List<SubjectPlacement>> _byParent;

    public SubjectHierarchy(IEnumerable<SubjectPlacement> placements)
    {
        _byId = placements.ToDictionary(p => p.Id);

        // A parent id pointing at a subject that is not in the set would orphan its children
        // silently. Treat those as roots: a tree that renders everything is better than one that
        // quietly hides a branch.
        _byParent = _byId.Values
            .GroupBy(p => p.ParentId is { } parent && _byId.ContainsKey(parent) ? parent : Root)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    public int Count => _byId.Count;

    public bool Contains(Guid id) => _byId.ContainsKey(id);

    /// <summary>Direct children, ordered by name. Pass null for the top-level subjects.</summary>
    public IReadOnlyList<SubjectPlacement> ChildrenOf(Guid? parentId)
        => _byParent.TryGetValue(parentId ?? Root, out var children) ? children : [];

    /// <summary>
    /// The subjects between this one and the root, nearest first. Empty for a root.
    /// These are exactly the extra subjects a card tagged with <paramref name="id"/> also wears.
    /// </summary>
    public IReadOnlyList<Guid> AncestorsOf(Guid id)
    {
        var ancestors = new List<Guid>();
        var seen = new HashSet<Guid> { id };
        var current = _byId.TryGetValue(id, out var node) ? node.ParentId : null;

        // Bounded by the seen-set rather than by MaxDepth: a cycle that somehow reached storage
        // must not spin here, and this is read on every card render.
        while (current is { } parent && _byId.ContainsKey(parent) && seen.Add(parent))
        {
            ancestors.Add(parent);
            current = _byId[parent].ParentId;
        }

        return ancestors;
    }

    /// <summary>This subject and everything beneath it — what selecting a subject actually studies.</summary>
    public IReadOnlyList<Guid> WithDescendants(Guid id)
    {
        if (!_byId.ContainsKey(id))
        {
            return [];
        }

        var result = new List<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(id);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            if (result.Contains(current))
            {
                continue;
            }

            result.Add(current);

            foreach (var child in ChildrenOf(current))
            {
                pending.Push(child.Id);
            }
        }

        return result;
    }

    /// <summary>Root is 1. An unknown id reports 0.</summary>
    public int DepthOf(Guid id) => _byId.ContainsKey(id) ? AncestorsOf(id).Count + 1 : 0;

    /// <summary>How many levels the subtree rooted here occupies, itself included.</summary>
    public int HeightOf(Guid id)
    {
        if (!_byId.ContainsKey(id))
        {
            return 0;
        }

        var children = ChildrenOf(id);

        return children.Count == 0 ? 1 : 1 + children.Max(c => HeightOf(c.Id));
    }

    /// <summary>
    /// Every subject in the order a tree view draws them: depth-first, siblings by name, each
    /// paired with its depth so the view can indent without walking back up.
    /// </summary>
    public IReadOnlyList<(SubjectPlacement Subject, int Depth)> InTreeOrder()
    {
        var ordered = new List<(SubjectPlacement, int)>(_byId.Count);

        Visit(null, 1);

        return ordered;

        void Visit(Guid? parentId, int depth)
        {
            foreach (var child in ChildrenOf(parentId))
            {
                ordered.Add((child, depth));
                Visit(child.Id, depth + 1);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="id"/> may be re-parented onto <paramref name="newParentId"/>, and
    /// if not, why — the message goes straight to the user, so it names the actual obstacle.
    /// </summary>
    public bool CanMove(Guid id, Guid? newParentId, out string? reason)
    {
        reason = null;

        if (!_byId.ContainsKey(id))
        {
            reason = "That subject no longer exists.";
            return false;
        }

        if (newParentId is null)
        {
            // Promoting to the top is always legal: it can only reduce depth.
            return true;
        }

        var parent = newParentId.Value;

        if (parent == id)
        {
            reason = "A subject cannot be placed inside itself.";
            return false;
        }

        if (!_byId.ContainsKey(parent))
        {
            reason = "That parent subject no longer exists.";
            return false;
        }

        // Dropping a subject onto its own descendant would detach the whole branch from the tree.
        if (WithDescendants(id).Contains(parent))
        {
            reason = $"\"{_byId[id].Name}\" cannot be placed inside its own child \"{_byId[parent].Name}\".";
            return false;
        }

        if (_byId[id].ParentId == parent)
        {
            reason = $"\"{_byId[id].Name}\" is already there.";
            return false;
        }

        // The deepest leaf under the subject being moved is what decides this, not the subject
        // itself: moving a two-level branch under a level-four parent would put its leaves at six.
        var resulting = DepthOf(parent) + HeightOf(id);

        if (resulting > MaxDepth)
        {
            reason = $"That would nest subjects {resulting} levels deep; the limit is {MaxDepth}.";
            return false;
        }

        return true;
    }

    /// <summary>Throwing form of <see cref="CanMove"/>, for command handlers.</summary>
    public void EnsureCanMove(Guid id, Guid? newParentId)
    {
        if (!CanMove(id, newParentId, out var reason))
        {
            throw new DomainException(reason!);
        }
    }

    /// <summary>
    /// Whether a brand new subject may be created under <paramref name="parentId"/>. A new subject
    /// is always a leaf, so only the parent's own depth matters.
    /// </summary>
    public bool CanAddUnder(Guid? parentId, out string? reason)
    {
        reason = null;

        if (parentId is null)
        {
            return true;
        }

        if (!_byId.ContainsKey(parentId.Value))
        {
            reason = "That parent subject no longer exists.";
            return false;
        }

        if (DepthOf(parentId.Value) >= MaxDepth)
        {
            reason = $"\"{_byId[parentId.Value].Name}\" is already {MaxDepth} levels deep, which is the limit.";
            return false;
        }

        return true;
    }

    public void EnsureCanAddUnder(Guid? parentId)
    {
        if (!CanAddUnder(parentId, out var reason))
        {
            throw new DomainException(reason!);
        }
    }
}
