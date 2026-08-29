namespace Flashcards.Domain.Common;

/// <summary>
/// Base class for entities with a <see cref="Guid"/> identity.
/// Equality is by identity, never by property values.
/// </summary>
public abstract class Entity : IEquatable<Entity>
{
    protected Entity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("Entity id cannot be empty.");
        }

        Id = id;
    }

    public Guid Id { get; }

    public bool Equals(Entity? other) => other is not null && other.GetType() == GetType() && other.Id == Id;

    public override bool Equals(object? obj) => obj is Entity entity && Equals(entity);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}

/// <summary>Marks the entry point of a consistency boundary. Repositories only ever load and save aggregate roots.</summary>
public interface IAggregateRoot;
