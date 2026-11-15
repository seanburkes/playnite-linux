namespace Playnite.SDK.Models;

/// <summary>Describes an object identifiable by an id.</summary>
public interface IIdentifiable
{
    Guid Id { get; }
}
