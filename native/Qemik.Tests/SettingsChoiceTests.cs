using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Qemik.Core;
using Qemik.Desktop;
using Xunit;

namespace Qemik.Tests;

public sealed class SettingsChoiceTests
{
    private static readonly ChoiceOption[] Machines = [new("q35", "Standard PC (Q35 + ICH9, 2009)"), new("pc", "Standard PC (i440FX + PIIX, 1996)"), new("microvm", "")];
    private static ComboBox Combo(Control choice) => choice.GetVisualDescendants().OfType<ComboBox>().First();
    private static TextBox Custom(Control choice) => ((Panel)choice).Children.OfType<TextBox>().Single();
    private static string Label(object? item) => (item as ComboBoxItem)?.Content?.ToString() ?? "";
    private static Control Host(Control content) { var w = new Window { Content = content }; w.Show(); Settle(w); return content; }
    private static void Settle(Window w) { w.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
    private static void Open(SettingsWindow w, string section) { w.ShowSection(section); Settle(w); }

    [AvaloniaFact]
    public void DefaultIsMarkedAndListedValueSelected()
    {
        var choice = Host(Ui.Choice("pc", Machines, "q35", _ => { }));
        var combo = Combo(choice);
        Assert.Equal("q35 · Standard PC (Q35 + ICH9, 2009) (default)", Label(combo.Items[0]));
        Assert.Equal("microvm", Label(combo.Items[2]));
        Assert.Equal("Custom…", Label(combo.Items[^1]));
        Assert.Same(combo.Items[1], combo.SelectedItem);
        Assert.False(Custom(choice).IsVisible);
    }

    [AvaloniaFact]
    public void UnknownSavedValueOpensInCustomModeWithoutLosingIt()
    {
        var changes = new List<string>();
        var choice = Host(Ui.Choice("pc-q35-2.12", Machines, "q35", changes.Add));
        Assert.Equal("Custom…", Label(Combo(choice).SelectedItem));
        Assert.True(Custom(choice).IsVisible);
        Assert.Equal("pc-q35-2.12", Custom(choice).Text);
        Assert.Empty(changes);
        Custom(choice).Text = "pc-q35-3.0";
        Assert.Equal("pc-q35-3.0", changes[^1]);
    }

    [AvaloniaFact]
    public void CustomRevealsTextAndPickingAListedValueHidesIt()
    {
        var value = "q35";
        var choice = Host(Ui.Choice(value, Machines, "q35", s => value = s));
        var combo = Combo(choice);
        combo.SelectedIndex = combo.ItemCount - 1;
        Assert.True(Custom(choice).IsVisible);
        Assert.Equal("q35", Custom(choice).Text);
        Custom(choice).Text = "virt";
        Assert.Equal("virt", value);
        combo.SelectedIndex = 2;
        Assert.False(Custom(choice).IsVisible);
        Assert.Equal("microvm", value);
    }

    [AvaloniaFact]
    public void ChoicesWithoutCustomOfferOnlyTheirOptions()
    {
        var combo = Combo(Host(Ui.Choice("tcg", [new("tcg", ""), new("whpx", "")], "tcg", _ => { }, allowCustom: false)));
        Assert.Equal(2, combo.ItemCount);
    }

    [AvaloniaFact]
    public void SystemSettingsUseDropdownsWithoutAnInstalledQemu()
    {
        var vm = VmConfig.Template("Linux"); using var manager = new VmManager();
        var window = new SettingsWindow(vm, CoreTests.Prefs(), manager); window.Show(); Open(window, "System");
        var labels = window.GetVisualDescendants().OfType<ComboBox>().SelectMany(c => c.Items.Select(Label)).ToArray();
        Assert.Contains("q35 (default)", labels);
        Assert.Contains("max (default)", labels);
        Assert.Contains("4 GB (default)", labels);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("built-in list", StringComparison.Ordinal) == true);
    }

    [AvaloniaFact]
    public void CpuFeaturesAreAddedAndRemovedAsChips()
    {
        var vm = VmConfig.Template("Linux"); vm.CpuFeatures = "-avx"; using var manager = new VmManager();
        var window = new SettingsWindow(vm, CoreTests.Prefs(), manager); window.Show(); Open(window, "System");
        var add = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "AddCpuFeature");
        add.SelectedItem = add.Items.OfType<ComboBoxItem>().Single(i => Label(i) == "+sse4.2");
        Assert.Equal("-avx,+sse4.2", vm.CpuFeatures); Settle(window);
        var remove = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RemoveCpuFeature-avx");
        remove.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("+sse4.2", vm.CpuFeatures);
    }

    [AvaloniaFact]
    public void NetworkDeviceMacAndTapAreDropdowns()
    {
        var vm = VmConfig.Template("Windows"); vm.MacAddress = "52:54:00:aa:bb:cc"; using var manager = new VmManager();
        var window = new SettingsWindow(vm, CoreTests.Prefs(), manager); window.Show(); Open(window, "Network");
        var labels = window.GetVisualDescendants().OfType<ComboBox>().SelectMany(c => c.Items.Select(Label)).ToArray();
        Assert.Contains("e1000e (default)", labels);
        Assert.Contains("52:54:00:aa:bb:cc (current)", labels);
        Assert.Contains("Generate a new random address", labels);
        Assert.Contains("None (default)", labels);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void ExistingUbuntuMachineOpensWithEverySavedValueSelectedAndUnchanged()
    {
        // The user's real Ubuntu Desktop 26.04.1 LTS machine, as stored in library.db.
        var vm = VmConfig.Template("Linux");
        vm.Name = "Ubuntu Desktop 26.04.1 LTS"; vm.Accelerator = "whpx"; vm.MemoryMiB = 8192; vm.Display = "sdl"; vm.Video = "virtio-vga-gl";
        vm.Audio = "intel-hda"; vm.MacAddress = "52:54:00:70:9a:b4"; vm.Drives.Add(new() { Path = @"C:\VMs\system.qcow2" });
        var before = System.Text.Json.JsonSerializer.Serialize(vm); using var manager = new VmManager();
        var window = new SettingsWindow(vm, CoreTests.Prefs(), manager); window.Show();
        var selected = new List<string>();
        foreach (var name in SettingsWindow.Sections)
        {
            Open(window, name);
            selected.AddRange(window.GetVisualDescendants().OfType<ComboBox>().Where(c => c.Name != "AddCpuFeature").Select(c => Label(c.SelectedItem)));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<ComboBox>(), c => Label(c.SelectedItem) == "Custom…");
        }
        foreach (var expected in new[] { "x86_64 (default)", "q35 (default)", "max (default)", "whpx", "8 GB", "4 (default)", "1 (default)", "sdl", "virtio-vga-gl", "intel-hda (default)",
                     "user (default)", "virtio-net-pci (default)", "52:54:00:70:9a:b4 (current)", "qcow2 (default)", "virtio (default)", "writeback (default)", "none (default)" })
            Assert.Contains(expected, selected);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(vm));
    }

    [AvaloniaFact]
    public void SavedUsbDevicesStayCheckedWhenUnplugged()
    {
        var vm = VmConfig.Template("Linux"); vm.UsbDevices = "dead:beef"; using var manager = new VmManager();
        var window = new SettingsWindow(vm, CoreTests.Prefs(), manager); window.Show(); Open(window, "USB & input");
        var saved = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content?.ToString()?.Contains("dead:beef", StringComparison.Ordinal) == true);
        Assert.True(saved.IsChecked);
        Assert.Contains("not connected", saved.Content!.ToString(), StringComparison.Ordinal);
        saved.IsChecked = false;
        Assert.Equal("", vm.UsbDevices);
    }
}
