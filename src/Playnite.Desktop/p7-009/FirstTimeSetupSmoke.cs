using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace Playnite.Desktop.Setup;

internal static class FirstTimeSetupSmoke
{
    private const string Flag = "--first-run-smoke";

    internal static bool IsEnabled(string[] args) =>
        args.Contains("--headless", StringComparer.Ordinal) && args.Contains(Flag, StringComparer.Ordinal);

    internal static void Attach(FirstTimeStartupWindow wizard, string[] args)
    {
        if (!IsEnabled(args))
        {
            return;
        }
        var input = args.Skip(Array.IndexOf(args, Flag) + 1).ToArray();
        wizard.Opened += async (_, _) =>
        {
            try
            {
                var mode = input[0];
                var source = wizard.FindControl<TextBox>("SourcePath")!;
                var importCard = wizard.FindControl<Border>("ImportSource")!;
                var import = wizard.FindControl<Button>("ImportButton")!;
                var steam = wizard.FindControl<Button>("SteamButton")!;
                var keep = wizard.FindControl<Button>("KeepButton")!;
                var finish = wizard.FindControl<Button>("FinishButton")!;
                var progress = wizard.FindControl<ProgressBar>("ProgressBar")!;
                Console.WriteLine($"first-run: choices import={import.IsEffectivelyVisible}; steam={steam.IsEffectivelyVisible}; keep={keep.IsEffectivelyVisible}; finish={finish.IsEffectivelyVisible}");
                Console.WriteLine($"first-run: dark={wizard.ActualThemeVariant == ThemeVariant.Dark}; import card={importCard.IsEffectivelyVisible}; panel styled={importCard.Background is not null}");

                if (mode is "import" or "fail" or "leave" or "cancel")
                {
                    source.Text = input[1];
                    wizard.FindControl<TextBox>("WindowsPath")!.Text = input.ElementAtOrDefault(2);
                    Click(import);
                }
                else if (mode == "steam")
                {
                    Click(steam);
                }
                else if (mode == "keep")
                {
                    Click(keep);
                }
                else
                {
                    throw new ArgumentException("Unknown first-run smoke mode.");
                }
                Console.WriteLine($"first-run: progress={progress.IsVisible}; finish={finish.IsVisible}");
                if (mode == "cancel")
                {
                    Click(wizard.FindControl<Button>("CancelButton")!);
                }
                await wizard.Pending;
                Console.WriteLine($"first-run: status={wizard.FindControl<TextBlock>("StatusText")!.Text}");
                Console.WriteLine($"first-run: details={wizard.FindControl<SelectableTextBlock>("DetailsText")!.Text}");
                Console.WriteLine($"first-run: finish={finish.IsVisible}; progress={progress.IsVisible}");
                if (mode is not ("fail" or "leave" or "cancel"))
                {
                    Click(finish);
                    await wizard.Pending;
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"First-run smoke failed: {exception}");
            }
            finally
            {
                if (wizard.IsVisible)
                {
                    wizard.Close();
                }
            }
        };
    }

    private static void Click(Button button)
    {
        if (!button.IsEffectivelyEnabled || !button.IsEffectivelyVisible)
        {
            throw new InvalidOperationException($"The {button.Name} action is not available.");
        }
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
