using Avalonia;
using Playnite.Core;

namespace Playnite.Desktop.Bootstrap;

/// <summary>
/// Holds the resolved startup services and applies the theme and localisation seams after the
/// Avalonia application is set up. Dependencies arrive through the constructor from the
/// composition root; nothing is reached through a static locator.
/// </summary>
public sealed class DesktopBootstrapper
{
    private const string LocalisationKey = LOC.AboutWindowTitle;

    private readonly IAppLogger logger;
    private readonly BootstrapLocalization localization;
    private readonly string? themePath;
    private readonly IRegularFileProbe probe;

    public DesktopBootstrapper(
        IAppLogger logger,
        BootstrapLocalization localization,
        string? themePath,
        IRegularFileProbe probe)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.localization = localization ?? throw new ArgumentNullException(nameof(localization));
        this.themePath = themePath;
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public ThemeLoadResult? AppliedTheme { get; private set; }

    public string LocalisedProbe => localization.Resolve(LocalisationKey);

    public string LocalisationSource => localization.Source;

    public void Apply(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        AppliedTheme = BootstrapTheme.Apply(application, themePath, logger, probe);
        logger.Info($"localisation: {LocalisationKey}={LocalisedProbe}");
    }
}
