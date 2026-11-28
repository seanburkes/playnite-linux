using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Playnite.Core.Addons;
using Playnite.Core.Plugins;
using Playnite.Core.Themes;
using Playnite.Desktop.Themes;

namespace Playnite.Desktop.Shell;

/// <summary>
/// Modal Plugins &amp; Add-ons manager displaying built-in/external plugin states,
/// lifecycle toggles, diagnostics, host-rendered settings forms from <see cref="PluginSettingField"/> DTOs,
/// and declarative theme pack management with live switching.
/// </summary>
public sealed partial class PluginManagerWindow : Window
{
    private static readonly IImmutableBrush ActiveBadgeBg = new ImmutableSolidColorBrush(Color.Parse("#1F3F2B"));
    private static readonly IImmutableBrush ActiveBadgeFg = new ImmutableSolidColorBrush(Color.Parse("#86EFAC"));
    private static readonly IImmutableBrush LazyBadgeBg = new ImmutableSolidColorBrush(Color.Parse("#1E3A5F"));
    private static readonly IImmutableBrush LazyBadgeFg = new ImmutableSolidColorBrush(Color.Parse("#93C5FD"));
    private static readonly IImmutableBrush DisabledBadgeBg = new ImmutableSolidColorBrush(Color.Parse("#2B303A"));
    private static readonly IImmutableBrush DisabledBadgeFg = new ImmutableSolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IImmutableBrush FaultBadgeBg = new ImmutableSolidColorBrush(Color.Parse("#4C1D24"));
    private static readonly IImmutableBrush FaultBadgeFg = new ImmutableSolidColorBrush(Color.Parse("#FCA5A5"));

    private sealed record FieldEditor(
        PluginSettingField Field,
        Func<string?> ReadValue,
        Action<string> WriteValue,
        TextBlock ErrorText);

    private readonly PluginHost? host;
    private readonly LiveThemeManager? themeManager;
    private readonly TabControl managerTabs;
    private readonly TextBlock summaryText;
    private readonly ListBox pluginList;
    private readonly TextBlock selectedPluginName;
    private readonly TextBlock selectedPluginMeta;
    private readonly TextBlock selectedPluginDescription;
    private readonly CheckBox enabledToggle;
    private readonly Button loadSettingsButton;
    private readonly Border selectedDiagnosticsPanel;
    private readonly StackPanel selectedDiagnosticsList;
    private readonly TextBlock noSettingsHint;
    private readonly StackPanel settingsFieldsPanel;
    private readonly WrapPanel settingsActionsPanel;
    private readonly Button saveSettingsButton;
    private readonly Button resetDefaultsButton;
    private readonly TextBlock settingsStatusText;
    private readonly Border hostDiagnosticsPanel;
    private readonly StackPanel hostDiagnosticsList;
    private readonly ListBox themeList;
    private readonly TextBlock selectedThemeName;
    private readonly TextBlock selectedThemeMeta;
    private readonly TextBlock selectedThemeDescription;
    private readonly WrapPanel themeSwatchPreview;
    private readonly Button applyThemeButton;
    private readonly Button resetThemeButton;
    private readonly Button reloadThemesButton;
    private readonly TextBlock themeStatusText;
    private readonly Border themeDiagnosticsPanel;
    private readonly StackPanel themeDiagnosticsList;
    private readonly List<FieldEditor> fieldEditors = [];
    private readonly Dictionary<string, (ListBoxItem Item, Border Pill, TextBlock PillLabel)> listItemRows =
        new(StringComparer.Ordinal);

    private Control? firstInputControl;
    private bool updatingSelection;
    private bool updatingThemeSelection;
    private bool settingsDirty;
    private string? selectedPluginId;
    private ThemePackCatalogEntry? selectedThemeEntry;

    public PluginManagerWindow()
        : this(null, null, 0)
    {
    }

    internal PluginManagerWindow(PluginHost? host)
        : this(host, null, 0)
    {
    }

    internal PluginManagerWindow(
        PluginHost? host, LiveThemeManager? themeManager, int initialTabIndex = 0, AddonInstaller? installer = null)
    {
        this.host = host;
        this.themeManager = themeManager;
        AvaloniaXamlLoader.Load(this);

        managerTabs = this.FindControl<TabControl>("ManagerTabs")!;
        summaryText = this.FindControl<TextBlock>("SummaryText")!;
        pluginList = this.FindControl<ListBox>("PluginList")!;
        selectedPluginName = this.FindControl<TextBlock>("SelectedPluginName")!;
        selectedPluginMeta = this.FindControl<TextBlock>("SelectedPluginMeta")!;
        selectedPluginDescription = this.FindControl<TextBlock>("SelectedPluginDescription")!;
        enabledToggle = this.FindControl<CheckBox>("EnabledToggle")!;
        loadSettingsButton = this.FindControl<Button>("LoadSettingsButton")!;
        selectedDiagnosticsPanel = this.FindControl<Border>("SelectedDiagnosticsPanel")!;
        selectedDiagnosticsList = this.FindControl<StackPanel>("SelectedDiagnosticsList")!;
        noSettingsHint = this.FindControl<TextBlock>("NoSettingsHint")!;
        settingsFieldsPanel = this.FindControl<StackPanel>("SettingsFieldsPanel")!;
        settingsActionsPanel = this.FindControl<WrapPanel>("SettingsActionsPanel")!;
        saveSettingsButton = this.FindControl<Button>("SaveSettingsButton")!;
        resetDefaultsButton = this.FindControl<Button>("ResetDefaultsButton")!;
        settingsStatusText = this.FindControl<TextBlock>("SettingsStatusText")!;
        hostDiagnosticsPanel = this.FindControl<Border>("HostDiagnosticsPanel")!;
        hostDiagnosticsList = this.FindControl<StackPanel>("HostDiagnosticsList")!;
        themeList = this.FindControl<ListBox>("ThemeList")!;
        selectedThemeName = this.FindControl<TextBlock>("SelectedThemeName")!;
        selectedThemeMeta = this.FindControl<TextBlock>("SelectedThemeMeta")!;
        selectedThemeDescription = this.FindControl<TextBlock>("SelectedThemeDescription")!;
        themeSwatchPreview = this.FindControl<WrapPanel>("ThemeSwatchPreview")!;
        applyThemeButton = this.FindControl<Button>("ApplyThemeButton")!;
        resetThemeButton = this.FindControl<Button>("ResetThemeButton")!;
        reloadThemesButton = this.FindControl<Button>("ReloadThemesButton")!;
        themeStatusText = this.FindControl<TextBlock>("ThemeStatusText")!;
        themeDiagnosticsPanel = this.FindControl<Border>("ThemeDiagnosticsPanel")!;
        themeDiagnosticsList = this.FindControl<StackPanel>("ThemeDiagnosticsList")!;

        pluginList.SelectionChanged += OnPluginSelected;
        enabledToggle.IsCheckedChanged += OnEnabledChanged;
        loadSettingsButton.Click += OnLoadSettingsClicked;
        saveSettingsButton.Click += OnSaveSettingsClicked;
        resetDefaultsButton.Click += OnResetDefaultsClicked;
        themeList.SelectionChanged += OnThemeSelected;
        applyThemeButton.Click += OnApplyThemeClicked;
        resetThemeButton.Click += OnResetThemeClicked;
        reloadThemesButton.Click += OnReloadThemesClicked;
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();

        managerTabs.SelectedIndex = Math.Clamp(initialTabIndex, 0, 1);
        RefreshListAndSelection(preferredPluginId: null);
        var initialThemeEntry = themeManager?.Catalog.Entries.FirstOrDefault(e =>
            e.IsValid && string.Equals(e.Id, themeManager.ActiveDocument.Descriptor.Id, StringComparison.Ordinal));
        RefreshThemesListAndSelection(initialThemeEntry);
        InitializePackages(installer);
    }

    /// <summary>Gets whether any plugin enablement state, activation, or setting was modified during this session.</summary>
    internal bool HasChanges { get; private set; }

    private void RefreshListAndSelection(string? preferredPluginId)
    {
        updatingSelection = true;
        try
        {
            if (host is null || host.IsDisposed)
            {
                HandleHostDisposedLocked();
                return;
            }

            IReadOnlyList<PluginRecord> records;
            IReadOnlyList<PluginDiagnostic> allDiagnostics;
            try
            {
                records = host.GetRecords();
                allDiagnostics = host.GetDiagnostics();
            }
            catch (ObjectDisposedException)
            {
                HandleHostDisposedLocked();
                return;
            }

            UpdateSummary(records, allDiagnostics);
            if (CanUpdateListInPlace(records))
            {
                UpdatePluginListStatusesInPlace(records);
            }
            else
            {
                RebuildPluginList(records, preferredPluginId);
            }

            // Inspecting an Active plugin's Fields getter may fault the plugin; capture & refresh cleanly.
            if (RenderSelectedPlugin(records, allDiagnostics))
            {
                try
                {
                    records = host.GetRecords();
                    allDiagnostics = host.GetDiagnostics();
                }
                catch (ObjectDisposedException)
                {
                    HandleHostDisposedLocked();
                    return;
                }

                UpdateSummary(records, allDiagnostics);
                UpdatePluginListStatusesInPlace(records);
                RenderSelectedPlugin(records, allDiagnostics);
            }
        }
        finally
        {
            updatingSelection = false;
        }
    }

    private bool CanUpdateListInPlace(IReadOnlyList<PluginRecord> records)
    {
        if (pluginList.Items.Count != records.Count || listItemRows.Count != records.Count)
        {
            return false;
        }

        for (var i = 0; i < records.Count; i++)
        {
            if (pluginList.Items[i] is not ListBoxItem item
                || !string.Equals(item.Tag as string, records[i].Descriptor.Id, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void UpdatePluginListStatusesInPlace(IReadOnlyList<PluginRecord> records)
    {
        foreach (var record in records)
        {
            if (!listItemRows.TryGetValue(record.Descriptor.Id, out var row))
            {
                continue;
            }

            var (statusText, badgeBg, badgeFg) = FormatStatusBadge(record.State);
            var originText = record.Descriptor.LoadKind == PluginLoadKind.ExternalAssembly ? "External" : "Built-in";
            row.Pill.Background = badgeBg;
            row.PillLabel.Text = statusText;
            row.PillLabel.Foreground = badgeFg;
            AutomationProperties.SetName(row.Item, $"{record.Descriptor.Name} ({statusText}, {originText})");
        }
    }

    private void RebuildPluginList(IReadOnlyList<PluginRecord> records, string? preferredPluginId)
    {
        pluginList.Items.Clear();
        listItemRows.Clear();
        var selectIndex = -1;
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var (statusText, badgeBg, badgeFg) = FormatStatusBadge(record.State);
            var originText = record.Descriptor.LoadKind == PluginLoadKind.ExternalAssembly ? "External" : "Built-in";

            var rowGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                RowDefinitions = new RowDefinitions("Auto,Auto"),
                ColumnSpacing = 6,
                RowSpacing = 2,
            };

            var nameText = new TextBlock
            {
                Text = record.Descriptor.Name,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetRow(nameText, 0);
            Grid.SetColumn(nameText, 0);

            var pillLabel = new TextBlock
            {
                Text = statusText,
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = badgeFg,
            };
            var statusPill = new Border
            {
                Background = badgeBg,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = pillLabel,
            };
            Grid.SetRow(statusPill, 0);
            Grid.SetColumn(statusPill, 1);

            var subText = new TextBlock
            {
                Text = $"{originText} · v{record.Descriptor.Version}",
                FontSize = 11,
                Opacity = 0.72,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetRow(subText, 1);
            Grid.SetColumn(subText, 0);
            Grid.SetColumnSpan(subText, 2);

            rowGrid.Children.Add(nameText);
            rowGrid.Children.Add(statusPill);
            rowGrid.Children.Add(subText);

            var item = new ListBoxItem
            {
                Tag = record.Descriptor.Id,
                Content = rowGrid,
                Padding = new Thickness(8, 6),
            };
            AutomationProperties.SetName(item, $"{record.Descriptor.Name} ({statusText}, {originText})");
            pluginList.Items.Add(item);
            listItemRows[record.Descriptor.Id] = (item, statusPill, pillLabel);

            if (preferredPluginId is not null
                && string.Equals(record.Descriptor.Id, preferredPluginId, StringComparison.Ordinal))
            {
                selectIndex = index;
            }
        }

        if (selectIndex < 0 && records.Count > 0)
        {
            selectIndex = 0;
        }

        pluginList.SelectedIndex = selectIndex;
    }

    private void UpdateSummary(IReadOnlyList<PluginRecord> records, IReadOnlyList<PluginDiagnostic> allDiagnostics)
    {
        var activeCount = records.Count(r => r.State == PluginLifecycleState.Active);
        var lazyCount = records.Count(r => r.State == PluginLifecycleState.Registered);
        var disabledCount = records.Count(r => r.State == PluginLifecycleState.Disabled);
        var faultedCount = records.Count(r => r.State == PluginLifecycleState.Faulted);
        summaryText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{records.Count} plugins ({activeCount} active, {lazyCount} lazy, {disabledCount} disabled, {faultedCount} faulted) · {allDiagnostics.Count} diagnostics");
    }

    private void OnPluginSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (updatingSelection || host is null)
        {
            return;
        }

        updatingSelection = true;
        try
        {
            if (host.IsDisposed)
            {
                HandleHostDisposedLocked();
                return;
            }

            settingsDirty = false;
            settingsStatusText.IsVisible = false;
            settingsStatusText.Text = string.Empty;

            var records = host.GetRecords();
            var allDiagnostics = host.GetDiagnostics();
            if (RenderSelectedPlugin(records, allDiagnostics))
            {
                records = host.GetRecords();
                allDiagnostics = host.GetDiagnostics();
                UpdateSummary(records, allDiagnostics);
                UpdatePluginListStatusesInPlace(records);
                RenderSelectedPlugin(records, allDiagnostics);
            }
        }
        catch (ObjectDisposedException)
        {
            HandleHostDisposedLocked();
        }
        finally
        {
            updatingSelection = false;
        }
    }

    private bool RenderSelectedPlugin(
        IReadOnlyList<PluginRecord> records,
        IReadOnlyList<PluginDiagnostic> allDiagnostics)
    {
        ClearSettingsEditor();
        loadSettingsButton.IsVisible = false;
        selectedDiagnosticsList.Children.Clear();
        selectedDiagnosticsPanel.IsVisible = false;

        var selectedId = (pluginList.SelectedItem as ListBoxItem)?.Tag as string;
        var record = selectedId is null
            ? null
            : records.FirstOrDefault(r => string.Equals(r.Descriptor.Id, selectedId, StringComparison.Ordinal));

        if (record is null || host is null || host.IsDisposed)
        {
            selectedPluginId = null;
            selectedPluginName.Text = "No plugins registered";
            selectedPluginMeta.Text = string.Empty;
            selectedPluginDescription.IsVisible = false;
            enabledToggle.IsEnabled = false;
            enabledToggle.IsChecked = false;
            noSettingsHint.Text = "Select a plugin to view details.";
            noSettingsHint.IsVisible = true;
            RenderHostDiagnostics(records, allDiagnostics);
            return false;
        }

        // Read Active plugin schema first; if its Fields getter throws, signal caller to refresh Faulted UI/badges.
        IReadOnlyList<PluginSettingField> activeSchema = [];
        IReadOnlyDictionary<string, string>? activeValues = null;
        if (record.State == PluginLifecycleState.Active)
        {
            activeSchema = host.GetSettingsSchema(record.Descriptor.Id, activateIfLazy: false);
            var refreshed = host.GetRecords().FirstOrDefault(r =>
                string.Equals(r.Descriptor.Id, record.Descriptor.Id, StringComparison.Ordinal));
            if (refreshed is not null && refreshed.State != PluginLifecycleState.Active)
            {
                HasChanges = true;
                return true;
            }

            if (activeSchema.Count > 0)
            {
                activeValues = host.GetSettingsValues(record.Descriptor.Id, activateIfLazy: false);
            }
        }

        selectedPluginId = record.Descriptor.Id;
        selectedPluginName.Text = record.Descriptor.Name;

        var metaParts = new List<string>
        {
            $"ID: {record.Descriptor.Id}",
            $"Version: {record.Descriptor.Version}",
            $"Origin: {(record.Descriptor.LoadKind == PluginLoadKind.ExternalAssembly ? "External" : "Built-in")}",
            $"State: {FormatStatusBadge(record.State).Label}",
            $"Materialized: {(record.IsMaterialized ? "Yes" : "No")}",
            $"Surfaces: {record.Descriptor.Surfaces}",
            $"Capabilities: {record.Descriptor.Capabilities}",
        };
        if (!string.IsNullOrWhiteSpace(record.Descriptor.RootDirectory))
        {
            metaParts.Add($"Root: {record.Descriptor.RootDirectory}");
        }

        selectedPluginMeta.Text = string.Join(" · ", metaParts);

        if (!string.IsNullOrWhiteSpace(record.Descriptor.Description))
        {
            selectedPluginDescription.Text = record.Descriptor.Description;
            selectedPluginDescription.IsVisible = true;
        }
        else
        {
            selectedPluginDescription.Text = string.Empty;
            selectedPluginDescription.IsVisible = false;
        }

        enabledToggle.IsEnabled = record.State != PluginLifecycleState.Disposed;
        enabledToggle.IsChecked = record.State is PluginLifecycleState.Active or PluginLifecycleState.Registered;

        var pluginDiagnostics = host.GetDiagnostics(record.Descriptor.Id);
        if (!string.IsNullOrWhiteSpace(record.LastError))
        {
            AddDiagnosticLine(selectedDiagnosticsList, $"Last error: {record.LastError}");
        }

        foreach (var diagnostic in pluginDiagnostics)
        {
            AddDiagnosticLine(selectedDiagnosticsList, $"[{diagnostic.Code}] {diagnostic.Message}");
        }

        selectedDiagnosticsPanel.IsVisible = selectedDiagnosticsList.Children.Count > 0;

        RenderSettingsForPlugin(record, activeSchema, activeValues);
        RenderHostDiagnostics(records, allDiagnostics);
        return false;
    }

    private void RenderSettingsForPlugin(
        PluginRecord record,
        IReadOnlyList<PluginSettingField> activeSchema,
        IReadOnlyDictionary<string, string>? activeValues)
    {
        if (record.State == PluginLifecycleState.Disabled)
        {
            noSettingsHint.Text = "Enable this plugin to view or edit its settings.";
            noSettingsHint.IsVisible = true;
            return;
        }

        if (record.State == PluginLifecycleState.Faulted)
        {
            noSettingsHint.Text = "This plugin faulted. Enable it again to retry activation before editing settings.";
            noSettingsHint.IsVisible = true;
            return;
        }

        if (record.State == PluginLifecycleState.Registered)
        {
            if (record.Descriptor.Capabilities.HasFlag(PluginCapabilityKind.Settings))
            {
                noSettingsHint.Text = "Plugin is registered lazily and has not loaded yet. Click Load Settings to activate it.";
                loadSettingsButton.IsVisible = true;
            }
            else
            {
                noSettingsHint.Text = "Plugin is registered lazily and declares no editable settings.";
            }

            noSettingsHint.IsVisible = true;
            return;
        }

        if (activeSchema.Count == 0)
        {
            noSettingsHint.Text = "This plugin does not declare editable settings.";
            noSettingsHint.IsVisible = true;
            return;
        }

        noSettingsHint.IsVisible = false;
        foreach (var field in activeSchema)
        {
            var initialValue = activeValues is not null && activeValues.TryGetValue(field.Key, out var value)
                ? value
                : field.DefaultValue ?? string.Empty;
            AddFieldEditor(field, initialValue);
        }

        settingsFieldsPanel.IsVisible = fieldEditors.Count > 0;
        settingsActionsPanel.IsVisible = fieldEditors.Count > 0;
    }

    private void AddFieldEditor(PluginSettingField field, string initialValue)
    {
        var container = new StackPanel { Spacing = 4 };
        var errorText = new TextBlock
        {
            IsVisible = false,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = FaultBadgeFg,
        };
        AutomationProperties.SetLiveSetting(errorText, AutomationLiveSetting.Assertive);

        Control inputControl;
        Func<string?> readValue;
        Action<string> writeValue;

        if (field.Type == PluginSettingType.Boolean)
        {
            var checkBox = new CheckBox
            {
                Content = field.Label,
                IsChecked = string.Equals(initialValue, "true", StringComparison.OrdinalIgnoreCase),
            };
            AutomationProperties.SetName(checkBox, field.Label);
            checkBox.IsCheckedChanged += OnFieldValueEdited;
            container.Children.Add(checkBox);
            inputControl = checkBox;
            readValue = () => checkBox.IsChecked == true ? "true" : "false";
            writeValue = val => checkBox.IsChecked = string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
        }
        else if (field.Type == PluginSettingType.Choice && field.Choices is { Count: > 0 } choices)
        {
            var labelBlock = new TextBlock
            {
                Text = field.Label,
                FontWeight = FontWeight.SemiBold,
            };
            var combo = new ComboBox
            {
                ItemsSource = choices,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedItem = choices.Contains(initialValue, StringComparer.Ordinal) ? initialValue : choices[0],
            };
            AutomationProperties.SetLabeledBy(combo, labelBlock);
            combo.SelectionChanged += OnFieldSelectionEdited;
            container.Children.Add(labelBlock);
            container.Children.Add(combo);
            inputControl = combo;
            readValue = () => combo.SelectedItem as string;
            writeValue = val => combo.SelectedItem = choices.Contains(val, StringComparer.Ordinal) ? val : choices[0];
        }
        else
        {
            var labelBlock = new TextBlock
            {
                Text = field.Type switch
                {
                    PluginSettingType.Path => $"{field.Label} (Path)",
                    PluginSettingType.Integer => $"{field.Label} (Integer)",
                    _ => field.Label,
                },
                FontWeight = FontWeight.SemiBold,
            };
            var textBox = new TextBox
            {
                Text = initialValue,
                PlaceholderText = field.DefaultValue ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetLabeledBy(textBox, labelBlock);
            textBox.TextChanged += OnFieldTextEdited;
            container.Children.Add(labelBlock);
            container.Children.Add(textBox);
            inputControl = textBox;
            readValue = () => textBox.Text ?? string.Empty;
            writeValue = val => textBox.Text = val;
        }

        if (!string.IsNullOrWhiteSpace(field.Description))
        {
            AutomationProperties.SetHelpText(inputControl, field.Description);
            container.Children.Add(new TextBlock
            {
                Text = field.Description,
                FontSize = 11,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        firstInputControl ??= inputControl;
        container.Children.Add(errorText);
        settingsFieldsPanel.Children.Add(container);
        fieldEditors.Add(new FieldEditor(field, readValue, writeValue, errorText));
    }

    private void ClearSettingsEditor()
    {
        firstInputControl = null;
        fieldEditors.Clear();
        settingsFieldsPanel.Children.Clear();
        settingsFieldsPanel.IsVisible = false;
        settingsActionsPanel.IsVisible = false;
    }

    private void OnFieldValueEdited(object? sender, RoutedEventArgs args) => MarkSettingsDirty();

    private void OnFieldSelectionEdited(object? sender, SelectionChangedEventArgs args) => MarkSettingsDirty();

    private void OnFieldTextEdited(object? sender, TextChangedEventArgs args) => MarkSettingsDirty();

    private void MarkSettingsDirty()
    {
        if (updatingSelection || selectedPluginId is null || fieldEditors.Count == 0)
        {
            return;
        }

        if (!settingsDirty)
        {
            settingsDirty = true;
            settingsStatusText.Text = "Unsaved changes. Click Save Settings to apply.";
            settingsStatusText.Foreground = LazyBadgeFg;
            settingsStatusText.IsVisible = true;
        }
    }

    private void RenderHostDiagnostics(
        IReadOnlyList<PluginRecord> records,
        IReadOnlyList<PluginDiagnostic> allDiagnostics)
    {
        hostDiagnosticsList.Children.Clear();
        var knownIds = new HashSet<string>(records.Select(r => r.Descriptor.Id), StringComparer.Ordinal);
        var hostLevel = allDiagnostics.Where(d => string.IsNullOrWhiteSpace(d.PluginId) || !knownIds.Contains(d.PluginId));
        foreach (var diagnostic in hostLevel)
        {
            var prefix = string.IsNullOrWhiteSpace(diagnostic.PluginId) ? "host" : diagnostic.PluginId;
            AddDiagnosticLine(hostDiagnosticsList, $"[{prefix} · {diagnostic.Code}] {diagnostic.Message}");
        }

        hostDiagnosticsPanel.IsVisible = hostDiagnosticsList.Children.Count > 0;
    }

    private static void AddDiagnosticLine(StackPanel target, string text)
    {
        target.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private void OnEnabledChanged(object? sender, RoutedEventArgs args)
    {
        if (updatingSelection || selectedPluginId is not { } pluginId || host is null)
        {
            return;
        }

        if (host.IsDisposed)
        {
            HandleHostDisposed();
            return;
        }

        try
        {
            settingsDirty = false;
            var enable = enabledToggle.IsChecked == true;
            var ok = enable ? host.Enable(pluginId) : host.Disable(pluginId);
            if (ok)
            {
                HasChanges = true;
                ShowStatus(enable ? "Plugin enabled." : "Plugin disabled.", isError: false);
            }
            else
            {
                if (!enable)
                {
                    HasChanges = true;
                }

                ShowStatus(enable ? "Plugin failed to enable. See diagnostics above." : "Could not disable plugin.", isError: true);
            }

            RefreshListAndSelection(pluginId);
            if (themeManager is not null)
            {
                themeManager.RefreshCatalog();
                RefreshThemesListAndSelection(selectedThemeEntry);
            }
        }
        catch (ObjectDisposedException)
        {
            HandleHostDisposed();
        }
    }

    private void OnLoadSettingsClicked(object? sender, RoutedEventArgs args)
    {
        if (selectedPluginId is not { } pluginId || host is null)
        {
            return;
        }

        if (host.IsDisposed)
        {
            HandleHostDisposed();
            return;
        }

        try
        {
            settingsDirty = false;
            var ok = host.Activate(pluginId);
            HasChanges = true;
            if (ok)
            {
                ShowStatus("Plugin activated and settings loaded.", isError: false);
            }
            else
            {
                ShowStatus("Plugin activation failed. See diagnostics above.", isError: true);
            }

            RefreshListAndSelection(pluginId);
            (firstInputControl ?? enabledToggle).Focus();
        }
        catch (ObjectDisposedException)
        {
            HandleHostDisposed();
        }
    }

    private void OnSaveSettingsClicked(object? sender, RoutedEventArgs args)
    {
        if (selectedPluginId is not { } pluginId || host is null)
        {
            return;
        }

        PersistCurrentEditorValues(pluginId, successMessage: "Settings saved.");
    }

    private void OnResetDefaultsClicked(object? sender, RoutedEventArgs args)
    {
        if (selectedPluginId is not { } pluginId || host is null)
        {
            return;
        }

        if (host.IsDisposed)
        {
            HandleHostDisposed();
            return;
        }

        updatingSelection = true;
        try
        {
            foreach (var editor in fieldEditors)
            {
                var defaultValue = editor.Field.DefaultValue ?? editor.Field.Type switch
                {
                    PluginSettingType.Boolean => "false",
                    PluginSettingType.Integer => "0",
                    PluginSettingType.Choice => editor.Field.Choices is { Count: > 0 } ? editor.Field.Choices[0] : string.Empty,
                    _ => string.Empty,
                };
                editor.WriteValue(defaultValue);
            }
        }
        finally
        {
            updatingSelection = false;
        }

        PersistCurrentEditorValues(pluginId, successMessage: "Defaults restored and saved.");
    }

    private void PersistCurrentEditorValues(string pluginId, string successMessage)
    {
        if (host is null || host.IsDisposed)
        {
            HandleHostDisposed();
            return;
        }

        var values = new Dictionary<string, string?>(fieldEditors.Count, StringComparer.Ordinal);
        foreach (var editor in fieldEditors)
        {
            editor.ErrorText.Text = string.Empty;
            editor.ErrorText.IsVisible = false;
            values[editor.Field.Key] = editor.ReadValue();
        }

        PluginSettingsSaveResult result;
        IReadOnlyDictionary<string, string>? refreshed = null;
        try
        {
            result = host.SaveSettings(pluginId, values);
            if (result.Succeeded)
            {
                refreshed = host.GetSettingsValues(pluginId, activateIfLazy: false);
            }
        }
        catch (ObjectDisposedException)
        {
            HandleHostDisposed();
            return;
        }

        if (result.Succeeded)
        {
            HasChanges = true;
            settingsDirty = false;
            if (refreshed is not null)
            {
                updatingSelection = true;
                try
                {
                    foreach (var editor in fieldEditors)
                    {
                        if (refreshed.TryGetValue(editor.Field.Key, out var normalized))
                        {
                            editor.WriteValue(normalized);
                        }
                    }
                }
                finally
                {
                    updatingSelection = false;
                }
            }

            ShowStatus(successMessage, isError: false);
            return;
        }

        if (result.FieldErrors is { Count: > 0 } fieldErrors)
        {
            foreach (var editor in fieldEditors)
            {
                if (fieldErrors.TryGetValue(editor.Field.Key, out var fieldError))
                {
                    editor.ErrorText.Text = fieldError;
                    editor.ErrorText.IsVisible = true;
                }
            }
        }

        ShowStatus(result.ErrorMessage ?? "Could not save plugin settings.", isError: true);
    }

    private void HandleHostDisposed()
    {
        updatingSelection = true;
        try
        {
            HandleHostDisposedLocked();
        }
        finally
        {
            updatingSelection = false;
        }
    }

    private void HandleHostDisposedLocked()
    {
        selectedPluginId = null;
        settingsDirty = false;
        summaryText.Text = "Plugin host unavailable.";
        pluginList.Items.Clear();
        listItemRows.Clear();
        ClearSettingsEditor();
        enabledToggle.IsChecked = false;
        enabledToggle.IsEnabled = false;
        loadSettingsButton.IsVisible = false;
        saveSettingsButton.IsEnabled = false;
        resetDefaultsButton.IsEnabled = false;
        noSettingsHint.Text = "Plugin host is no longer available.";
        noSettingsHint.IsVisible = true;
        ShowStatus("Plugin host is no longer available.", isError: true);
    }

    private void ShowStatus(string message, bool isError)
    {
        settingsStatusText.Text = message;
        settingsStatusText.Foreground = isError ? FaultBadgeFg : ActiveBadgeFg;
        settingsStatusText.IsVisible = true;
    }

    private static (string Label, IImmutableBrush Background, IImmutableBrush Foreground) FormatStatusBadge(
        PluginLifecycleState state) => state switch
    {
        PluginLifecycleState.Active => ("Active", ActiveBadgeBg, ActiveBadgeFg),
        PluginLifecycleState.Registered => ("Lazy", LazyBadgeBg, LazyBadgeFg),
        PluginLifecycleState.Disabled => ("Disabled", DisabledBadgeBg, DisabledBadgeFg),
        PluginLifecycleState.Faulted => ("Faulted", FaultBadgeBg, FaultBadgeFg),
        _ => ("Disposed", DisabledBadgeBg, DisabledBadgeFg),
    };

    private void RefreshThemesListAndSelection(ThemePackCatalogEntry? preferredEntry)
    {
        updatingThemeSelection = true;
        try
        {
            themeList.Items.Clear();
            if (themeManager is null)
            {
                selectedThemeEntry = null;
                selectedThemeName.Text = "Themes unavailable";
                selectedThemeMeta.Text = string.Empty;
                selectedThemeDescription.IsVisible = false;
                themeSwatchPreview.Children.Clear();
                applyThemeButton.IsEnabled = false;
                resetThemeButton.IsEnabled = false;
                reloadThemesButton.IsEnabled = false;
                themeDiagnosticsList.Children.Clear();
                themeDiagnosticsPanel.IsVisible = false;
                return;
            }

            var activeId = themeManager.ActiveDocument.Descriptor.Id;
            var entries = themeManager.Catalog.Entries;
            var selectIndex = -1;
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var isActive = entry.IsValid && string.Equals(entry.Id, activeId, StringComparison.Ordinal);
                var (statusText, badgeBg, badgeFg) = isActive
                    ? ("Active", ActiveBadgeBg, ActiveBadgeFg)
                    : entry.IsValid
                        ? ("Available", LazyBadgeBg, LazyBadgeFg)
                        : ("Invalid", FaultBadgeBg, FaultBadgeFg);

                var rowGrid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    RowDefinitions = new RowDefinitions("Auto,Auto"),
                    ColumnSpacing = 6,
                    RowSpacing = 2,
                };

                var nameText = new TextBlock
                {
                    Text = entry.DisplayName,
                    FontWeight = FontWeight.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetRow(nameText, 0);
                Grid.SetColumn(nameText, 0);

                var pillLabel = new TextBlock
                {
                    Text = statusText,
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = badgeFg,
                };
                var statusPill = new Border
                {
                    Background = badgeBg,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = pillLabel,
                };
                Grid.SetRow(statusPill, 0);
                Grid.SetColumn(statusPill, 1);

                var subTextText = string.Equals(entry.Id, ThemePackDefaults.DefaultDesktopThemeId, StringComparison.Ordinal)
                    ? "Built-in · Charcoal baseline"
                    : entry.Descriptor is { } descriptor
                        ? $"{descriptor.BaseVariant} · v{descriptor.Version} · API {descriptor.ThemeApiVersion}"
                        : "Validation error · see diagnostics";
                var subText = new TextBlock
                {
                    Text = subTextText,
                    FontSize = 11,
                    Opacity = 0.72,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetRow(subText, 1);
                Grid.SetColumn(subText, 0);
                Grid.SetColumnSpan(subText, 2);

                rowGrid.Children.Add(nameText);
                rowGrid.Children.Add(statusPill);
                rowGrid.Children.Add(subText);

                var item = new ListBoxItem
                {
                    Tag = entry,
                    Content = rowGrid,
                    Padding = new Thickness(8, 6),
                };
                AutomationProperties.SetName(item, $"{entry.DisplayName} ({statusText})");
                themeList.Items.Add(item);

                if (selectIndex < 0 && preferredEntry is not null && MatchesThemeEntry(entry, preferredEntry))
                {
                    selectIndex = index;
                }
            }

            if (selectIndex < 0 && entries.Count > 0)
            {
                selectIndex = 0;
            }

            themeList.SelectedIndex = selectIndex;
            RenderSelectedTheme();
        }
        finally
        {
            updatingThemeSelection = false;
        }
    }

    private static bool MatchesThemeEntry(ThemePackCatalogEntry candidate, ThemePackCatalogEntry target)
    {
        if (ReferenceEquals(candidate, target))
        {
            return true;
        }

        if (candidate.RootDirectory is not null && target.RootDirectory is not null)
        {
            return string.Equals(candidate.RootDirectory, target.RootDirectory, StringComparison.Ordinal);
        }

        return candidate.IsValid == target.IsValid
            && string.Equals(candidate.Id, target.Id, StringComparison.Ordinal);
    }

    private void OnThemeSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (updatingThemeSelection || themeManager is null)
        {
            return;
        }

        themeStatusText.IsVisible = false;
        themeStatusText.Text = string.Empty;
        RenderSelectedTheme();
    }

    private void RenderSelectedTheme()
    {
        themeSwatchPreview.Children.Clear();
        themeDiagnosticsList.Children.Clear();
        themeDiagnosticsPanel.IsVisible = false;

        if (themeManager is null)
        {
            return;
        }

        var entry = (themeList.SelectedItem as ListBoxItem)?.Tag as ThemePackCatalogEntry;
        if (entry is null)
        {
            selectedThemeEntry = null;
            selectedThemeName.Text = "No themes discovered";
            selectedThemeMeta.Text = string.Empty;
            selectedThemeDescription.IsVisible = false;
            applyThemeButton.IsEnabled = false;
            resetThemeButton.IsEnabled = true;
            reloadThemesButton.IsEnabled = true;
            return;
        }

        selectedThemeEntry = entry;
        selectedThemeName.Text = entry.DisplayName;
        var activeId = themeManager.ActiveDocument.Descriptor.Id;
        var isActive = entry.IsValid && string.Equals(entry.Id, activeId, StringComparison.Ordinal);

        if (entry.Descriptor is { } descriptor)
        {
            var statusLabel = isActive ? "Active" : entry.IsValid ? "Available" : "Invalid";
            var metaParts = new List<string>
            {
                $"ID: {descriptor.Id}",
                $"Version: {descriptor.Version}",
                $"API: {descriptor.ThemeApiVersion}",
                $"Variant: {descriptor.BaseVariant}",
                $"Status: {statusLabel}",
            };
            if (!string.IsNullOrWhiteSpace(descriptor.Author))
            {
                metaParts.Add($"Author: {descriptor.Author}");
            }

            if (!string.IsNullOrWhiteSpace(descriptor.RootDirectory))
            {
                metaParts.Add($"Root: {descriptor.RootDirectory}");
            }

            selectedThemeMeta.Text = string.Join(" · ", metaParts);
            if (!string.IsNullOrWhiteSpace(descriptor.Description))
            {
                selectedThemeDescription.Text = descriptor.Description;
                selectedThemeDescription.IsVisible = true;
            }
            else
            {
                selectedThemeDescription.Text = string.Empty;
                selectedThemeDescription.IsVisible = false;
            }
        }
        else
        {
            selectedThemeMeta.Text = $"ID: {entry.Id} · Status: Invalid · Root: {entry.RootDirectory ?? "(none)"}";
            selectedThemeDescription.IsVisible = false;
        }

        var previewValidation = entry.IsValid ? themeManager.LoadPreviewDocument(entry) : null;
        if (previewValidation is { IsValid: true, Document: { } previewDoc })
        {
            RenderThemeSwatches(previewDoc);
        }
        else
        {
            RenderThemeSwatches(ThemePackDefaults.BuiltInCharcoal);
        }

        var diagnostics = previewValidation is { IsValid: false, Diagnostics.Count: > 0 }
            ? previewValidation.Diagnostics
            : entry.Diagnostics;
        foreach (var diag in diagnostics)
        {
            AddDiagnosticLine(themeDiagnosticsList, $"[{diag.Code} · {diag.Path}] {diag.Message}");
        }

        themeDiagnosticsPanel.IsVisible = themeDiagnosticsList.Children.Count > 0;
        applyThemeButton.IsEnabled = true;
        resetThemeButton.IsEnabled = true;
        reloadThemesButton.IsEnabled = true;
    }

    private void RenderThemeSwatches(ThemePackDocument document)
    {
        var effective = ThemePackDefaults.ResolveEffectiveColors(document);
        var tokens = new (string Label, string BrushKey, string ColorKey)[]
        {
            ("Canvas", "PlayniteCanvasBrush", "PlayniteCanvasBottomColor"),
            ("Surface", "PlayniteSurfaceBrush", "PlayniteSurfaceColor"),
            ("Card", "PlayniteCardBackgroundBrush", "PlayniteCardBackgroundColor"),
            ("Accent", "PlayniteAccentBrush", "PlayniteAccentColor"),
            ("Text", "PlayniteTextPrimaryBrush", "PlayniteTextPrimaryColor"),
            ("Border", "PlayniteBorderBrush", "PlayniteBorderColor"),
        };

        foreach (var (label, brushKey, colorKey) in tokens)
        {
            var rgba = document.BrushColors.TryGetValue(brushKey, out var brushOverride)
                ? brushOverride
                : effective[colorKey];
            var swatchColor = Color.FromArgb(rgba.A, rgba.R, rgba.G, rgba.B);
            var chip = new Border
            {
                Background = new ImmutableSolidColorBrush(swatchColor),
                BorderBrush = DisabledBadgeFg,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Width = 22,
                Height = 22,
            };
            var caption = new TextBlock
            {
                Text = label,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var pair = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { chip, caption },
            };
            themeSwatchPreview.Children.Add(pair);
        }
    }

    private void OnApplyThemeClicked(object? sender, RoutedEventArgs args)
    {
        if (themeManager is null || selectedThemeEntry is not { } entry)
        {
            return;
        }

        var result = themeManager.ApplyCatalogEntry(entry, persist: true);
        if (result.FellBackToCharcoal)
        {
            themeManager.RefreshCatalog();
        }

        RefreshThemesListAndSelection(entry);
        ShowThemeStatus(result);
    }

    private void OnResetThemeClicked(object? sender, RoutedEventArgs args)
    {
        if (themeManager is null)
        {
            return;
        }

        var result = themeManager.ResetToCharcoal(persist: true);
        var charcoalEntry = themeManager.Catalog.Entries.FirstOrDefault(e =>
            e.IsValid && string.Equals(e.Id, ThemePackDefaults.DefaultDesktopThemeId, StringComparison.Ordinal));
        RefreshThemesListAndSelection(charcoalEntry);
        ShowThemeStatus(result);
    }

    private void OnReloadThemesClicked(object? sender, RoutedEventArgs args)
    {
        if (themeManager is null)
        {
            return;
        }

        var priorEntry = selectedThemeEntry;
        var discovery = themeManager.RefreshCatalog();
        RefreshThemesListAndSelection(priorEntry);
        themeStatusText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"Discovered {discovery.Entries.Count} themes ({discovery.Diagnostics.Count} diagnostics).");
        themeStatusText.Foreground = LazyBadgeFg;
        themeStatusText.IsVisible = true;
    }

    private void ShowThemeStatus(ThemeSwitchResult result)
    {
        themeStatusText.Text = result.StatusMessage;
        themeStatusText.Foreground = result.FellBackToCharcoal || result.Diagnostics.Count > 0
            ? FaultBadgeFg
            : ActiveBadgeFg;
        themeStatusText.IsVisible = true;

        if (result.Diagnostics.Count > 0)
        {
            themeDiagnosticsList.Children.Clear();
            foreach (var diag in result.Diagnostics)
            {
                AddDiagnosticLine(themeDiagnosticsList, $"[{diag.Code} · {diag.Path}] {diag.Message}");
            }

            themeDiagnosticsPanel.IsVisible = true;
        }
    }
}
