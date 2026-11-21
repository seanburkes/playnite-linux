using System.Globalization;
using Playnite.Core.Database;
using Playnite.Core.Models;

namespace Playnite.Desktop.Shell;

/// <summary>
/// Draft state for editing the single selected game. The original <see cref="StoredGame"/>
/// (including its preserved <see cref="StoredGame.LegacyJson"/>) is kept intact; a deep
/// <see cref="Game.GetCopy"/> carries the bounded editable field set. Every untouched field and
/// relation is written back unchanged, so this unit never reshapes the p4/p6-owned model.
/// Multi-edit "Use*Changes" gating from v10 is deliberately deferred.
/// </summary>
public sealed class GameEditViewModel
{
    private readonly GameEditStore store;
    private StoredGame? original;
    private Game? draft;
    private string userScoreInput = string.Empty;

    public GameEditViewModel(GameEditStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    private Game Draft => draft ?? throw new InvalidOperationException("No game has been loaded for editing.");

    public bool IsLoaded => draft is not null;

    public string Status { get; private set; } = "empty";

    public Guid GameId => original?.Value.Id ?? Guid.Empty;

    public string GameIdentity => original?.Value.GameId ?? string.Empty;

    public string Name
    {
        get => draft?.Name ?? string.Empty;
        set => Draft.Name = value;
    }

    public string Description
    {
        get => draft?.Description ?? string.Empty;
        set => Draft.Description = value;
    }

    public string Version
    {
        get => draft?.Version ?? string.Empty;
        set => Draft.Version = value;
    }

    public string InstallDirectory
    {
        get => draft?.InstallDirectory ?? string.Empty;
        set => Draft.InstallDirectory = value;
    }

    public bool IsInstalled
    {
        get => draft?.IsInstalled ?? false;
        set => Draft.IsInstalled = value;
    }

    public bool Hidden
    {
        get => draft?.Hidden ?? false;
        set => Draft.Hidden = value;
    }

    public bool Favorite
    {
        get => draft?.Favorite ?? false;
        set => Draft.Favorite = value;
    }

    public int? UserScore
    {
        get => draft?.UserScore;
        set
        {
            Draft.UserScore = value;
            UserScoreInput = value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    public string UserScoreInput
    {
        get => userScoreInput;
        set
        {
            userScoreInput = value ?? string.Empty;
            UserScoreValidationError = null;
            if (Status == "invalid-user-score")
            {
                Status = "loaded";
            }
        }
    }

    public string? UserScoreValidationError { get; private set; }

    public bool TryGetValidatedUserScore(out int? score)
    {
        if (string.IsNullOrWhiteSpace(UserScoreInput))
        {
            score = null;
            ClearUserScoreValidation();
            return true;
        }

        if (int.TryParse(UserScoreInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed is >= 0 and <= 100)
        {
            score = parsed;
            ClearUserScoreValidation();
            return true;
        }

        score = null;
        UserScoreValidationError = "Enter a whole number from 0 to 100, or leave blank to clear.";
        Status = "invalid-user-score";
        return false;
    }

    // Raw references only: this unit has no image picker, byte loading or download (captain ruling).
    public string Icon
    {
        get => draft?.Icon ?? string.Empty;
        set => Draft.Icon = value;
    }

    public string CoverImage
    {
        get => draft?.CoverImage ?? string.Empty;
        set => Draft.CoverImage = value;
    }

    public string BackgroundImage
    {
        get => draft?.BackgroundImage ?? string.Empty;
        set => Draft.BackgroundImage = value;
    }

    /// <summary>Read-only metadata shown beside the bounded editable fields.</summary>
    public string MetadataSummary
    {
        get
        {
            if (draft is null)
            {
                return "(none)";
            }

            var release = Draft.ReleaseDate?.Year.ToString(CultureInfo.InvariantCulture) ?? "-";
            var platforms = Draft.PlatformIds?.Count ?? 0;
            var tags = Draft.TagIds?.Count ?? 0;
            var developers = Draft.DeveloperIds?.Count ?? 0;
            return $"Release={release}; Platforms={platforms}; Tags={tags}; Developers={developers}";
        }
    }

    /// <summary>Loads the full graph behind a selected <c>LibraryRow</c> identity.</summary>
    public async Task LoadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (stored is null)
        {
            original = null;
            draft = null;
            Status = "missing";
            return;
        }

        original = stored;
        draft = stored.Value.GetCopy();
        UserScoreInput = stored.Value.UserScore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Status = "loaded";
    }

    /// <summary>
    /// Commits the bounded edits as one atomic full-graph replace, preserving every untouched
    /// field, all relations and the original legacy JSON. A blank name is rejected without a
    /// write, matching the v10 non-empty-name validation.
    /// </summary>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (draft is null || original is null)
        {
            Status = "not-loaded";
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetValidatedUserScore(out var userScore))
        {
            return false;
        }

        var name = draft.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            Status = "invalid-name";
            return false;
        }

        draft.Name = name;
        draft.UserScore = userScore;
        var updated = new StoredGame(draft, original.LegacyJson);
        await store.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
        original = updated;
        Status = "saved";
        return true;
    }

    /// <summary>Discards the current draft without touching the database.</summary>
    public void Clear()
    {
        original = null;
        draft = null;
        UserScoreInput = string.Empty;
        UserScoreValidationError = null;
        Status = "empty";
    }

    private void ClearUserScoreValidation()
    {
        UserScoreValidationError = null;
        if (Status == "invalid-user-score")
        {
            Status = "loaded";
        }
    }
}
