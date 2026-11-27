using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Playnite.Core.Plugins;

namespace Playnite.Desktop.Shell;

/// <summary>
/// Modal Plugins &amp; Add-ons manager displaying built-in/external plugin states,
/// lifecycle toggles, diagnostics, and host-rendered settings forms from <see cref="PluginSettingField"/> DTOs.
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
    private readonly List<FieldEditor> fieldEditors = [];
    private readonly Dictionary<string, (ListBoxItem Item, Border Pill, TextBlock PillLabel)> listItemRows =
        new(StringComparer.Ordinal);

    private Control? firstInputControl;
    private bool updatingSelection;
    private bool settingsDirty;
    private string? selectedPluginId;

    public PluginManagerWindow()
        : this(null)
    {
    }

    internal PluginManagerWindow(PluginHost? host)
    {
        this.host = host;
        AvaloniaXamlLoader.Load(this);

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

        pluginList.SelectionChanged += OnPluginSelected;
        enabledToggle.IsCheckedChanged += OnEnabledChanged;
        loadSettingsButton.Click += OnLoadSettingsClicked;
        saveSettingsButton.Click += OnSaveSettingsClicked;
        resetDefaultsButton.Click += OnResetDefaultsClicked;
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();

        RefreshListAndSelection(preferredPluginId: null);
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
}
