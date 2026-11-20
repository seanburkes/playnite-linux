using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Playnite.SDK;
using Playnite.SDK.Models;
using Xunit;

namespace Playnite.Architecture.Tests;

/// <summary>
/// p6-006 host-surface move evidence: the notification, dialog and main-view contracts live in
/// the abstraction assembly under their v10 namespaces, and a plugin can compile against that
/// assembly alone.
/// </summary>
public sealed class SdkHostSurfaceTests
{
    private static Assembly Abstractions => typeof(INotificationsAPI).Assembly;

    [Fact]
    public void Moved_host_contracts_live_in_the_abstractions_assembly()
    {
        Assert.Equal("Playnite.SDK.Abstractions", Abstractions.GetName().Name);

        var expected = new[]
        {
            "Playnite.SDK.INotificationsAPI",
            "Playnite.SDK.INotificationHost",
            "Playnite.SDK.NotificationMessage",
            "Playnite.SDK.NotificationType",
            "Playnite.SDK.IDialogsFactory",
            "Playnite.SDK.IDialogHost",
            "Playnite.SDK.MessageDialogRequest",
            "Playnite.SDK.FileDialogRequest",
            "Playnite.SDK.TextInputDialogRequest",
            "Playnite.SDK.IMainViewAPI",
            "Playnite.SDK.IMainViewHost",
            "Playnite.SDK.MainViewState",
            "Playnite.SDK.MainViewResponse",
            "Playnite.SDK.MainViewGamesResponse",
            "Playnite.SDK.DesktopView",
            "Playnite.SDK.FullscreenView",
            "Playnite.SDK.HostOperationStatus",
            "Playnite.SDK.Models.IGameView",
            "Playnite.SDK.Models.SortOrder",
            "Playnite.SDK.Models.LibraryGroupBy",
        };

        var exported = Abstractions.GetExportedTypes()
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(expected, type => Assert.Contains(type, exported));

        var core = typeof(Playnite.Core.Hosting.NotificationsApi).Assembly;
        var oldNames = core.GetExportedTypes()
            .Select(type => type.FullName!)
            .Where(type => type.StartsWith("Playnite.Core.Hosting.", StringComparison.Ordinal)
                || type is "Playnite.Core.Queries.SortOrder" or "Playnite.Core.Library.LibraryGroupBy")
            .ToArray();
        Assert.DoesNotContain("Playnite.Core.Hosting.INotificationsAPI", oldNames);
        Assert.DoesNotContain("Playnite.Core.Hosting.IDialogsFactory", oldNames);
        Assert.DoesNotContain("Playnite.Core.Hosting.IMainViewAPI", oldNames);
        Assert.DoesNotContain("Playnite.Core.Queries.SortOrder", oldNames);
        Assert.DoesNotContain("Playnite.Core.Library.LibraryGroupBy", oldNames);
    }

    [Fact]
    public void Sample_plugin_compiles_against_the_abstractions_assembly_alone()
    {
        // A plugin consumer sees only Playnite.SDK.Abstractions: no Core, no Avalonia, no SDK
        // implementation assembly. The sample uses every moved host surface and the reshaped
        // IMainViewAPI game view so a Core-type leak fails this compile, not just reflection.
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Playnite.SDK;
            using Playnite.SDK.Models;

            public sealed class SamplePlugin
            {
                private readonly INotificationsAPI notifications;
                private readonly IDialogsFactory dialogs;
                private readonly IMainViewAPI mainView;

                public SamplePlugin(
                    INotificationsAPI notifications,
                    IDialogsFactory dialogs,
                    IMainViewAPI mainView)
                {
                    this.notifications = notifications;
                    this.dialogs = dialogs;
                    this.mainView = mainView;
                }

                public async Task RunAsync(CancellationToken cancellationToken = default)
                {
                    await notifications.PublishAsync("sample", "text", NotificationType.Info, cancellationToken);
                    await dialogs.ShowMessageAsync(new MessageDialogRequest("text"), cancellationToken);
                    await dialogs.SelectFolderAsync(cancellationToken: cancellationToken);

                    MainViewResponse response = await mainView.GetStateAsync(cancellationToken);
                    IGameView? game = response.State?.SelectedGames.Count > 0
                        ? response.State.SelectedGames[0]
                        : null;
                    _ = game?.Id;
                    _ = mainView.GetFilteredGamesAsync(cancellationToken);
                }
            }
            """;

        var framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => !Path.GetFileName(path).StartsWith("Playnite", StringComparison.Ordinal))
            .Where(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                return name.StartsWith("System", StringComparison.Ordinal)
                    || name.StartsWith("Microsoft", StringComparison.Ordinal)
                    || name is "netstandard" or "mscorlib";
            })
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(Abstractions.Location));
        var compilation = CSharpCompilation.Create(
            "SamplePlugin",
            [CSharpSyntaxTree.ParseText(source)],
            framework,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();

        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors));
    }
}
