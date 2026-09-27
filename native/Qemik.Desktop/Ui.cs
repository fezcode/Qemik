using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Qemik.Desktop;
public static class Ui
{
    public static TextBlock Text(string text, double size = 13, string? color = null) => new() { Text = text, FontSize = size, Foreground = Brush.Parse(color ?? "#ECF2EE"), TextWrapping = TextWrapping.Wrap };
    public static TextBlock Heading(string text, double size = 28) { var t = Text(text, size); t.FontWeight = FontWeight.SemiBold; t.FontFamily = new FontFamily("avares://Qemik/Assets/Fonts#Manrope"); return t; }
    public static TextBlock Muted(string text) => Text(text, 13, "#92988D");
    public static TextBlock Eyebrow(string text) { var t = Text(text, 11, "#92988D"); t.LetterSpacing = 2; t.FontWeight = FontWeight.SemiBold; return t; }
    public static StackPanel Stack(params Control[] children) { var s = new StackPanel { Spacing = 14 }; foreach (var c in children) s.Children.Add(c); return s; }
    public static StackPanel Row(params Control[] children) { var s = Stack(children); s.Orientation = Orientation.Horizontal; s.Spacing = 10; foreach (var child in children) child.VerticalAlignment = VerticalAlignment.Center; return s; }
    public static Button Button(string text, Action action, string? cls = null)
    {
        var b = new Button { Content = text }; if (cls is not null) b.Classes.Add(cls); b.Click += (_, _) => action(); return b;
    }
    public static Button AsyncButton(string text, Func<Task> action, Action<Exception> error, string? cls = null)
    {
        var b = new Button { Content = text }; if (cls is not null) b.Classes.Add(cls);
        b.Click += async (_, _) => { b.IsEnabled = false; try { await action(); } catch (Exception ex) { error(ex); } finally { b.IsEnabled = true; } }; return b;
    }
    public static Border Card(Control content, string background = "#191E17") => new() { Background = Brush.Parse(background), BorderBrush = Brush.Parse("#30362C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(22), Child = content };
    public static Control Field(string title, Control input, string? hint = null)
    {
        var s = Stack(Heading(title, 13), input); s.Spacing = 7; if (hint is not null) s.Children.Add(Muted(hint)); return s;
    }
    public static TextBox Input(string value, Action<string> changed, bool multiline = false)
    {
        var t = new TextBox { Text = value, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 90 : 37 };
        t.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) changed(t.Text ?? ""); }; return t;
    }
    public static ComboBox Select(string value, IEnumerable<string> options, Action<string> changed)
    {
        var items = options.Append(value).Distinct().ToArray(); var c = new ComboBox { ItemsSource = items, SelectedItem = value };
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is string s) changed(s); }; return c;
    }
    public static NumericUpDown Number(int value, int min, int max, Action<int> changed)
    {
        var n = new NumericUpDown { Value = value, Minimum = min, Maximum = max, Increment = 1, FormatString = "0" };
        n.ValueChanged += (_, _) => { if (n.Value.HasValue) changed((int)n.Value); }; return n;
    }
    public static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Content = text, IsChecked = value }; c.IsCheckedChanged += (_, _) => changed(c.IsChecked == true); return c;
    }
    public static Grid Columns(Control left, Control right, string widths = "*,*")
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(widths), ColumnSpacing = 18 }; grid.Children.Add(left); Grid.SetColumn(right, 1); grid.Children.Add(right); return grid;
    }
    public static ScrollViewer Scroll(Control content) => new() { Content = new Border { Padding = new Thickness(0, 0, 12, 4), Child = content }, AllowAutoHide = false, ClipToBounds = true, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    public static Avalonia.Controls.Shapes.Path Glyph(string data, double size = 10) => new() { Data = Geometry.Parse(data), Width = size, Height = size, Stretch = size == 10 ? Stretch.None : Stretch.Uniform, Stroke = Brush.Parse("#92988D"), StrokeThickness = 1.2, StrokeLineCap = PenLineCap.Round, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    public static Button NavigationButton(string title, string icon, Action action)
    {
        var b = Button("", action, "nav");
        var glyph = Glyph(icon, 17); glyph.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Avalonia.Data.Binding("Foreground") { Source = b });
        var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*"), ColumnSpacing = 11, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(glyph); Grid.SetColumn(label, 1); content.Children.Add(label); b.Content = content;
        Avalonia.Automation.AutomationProperties.SetName(b, title); ToolTip.SetTip(b, title); return b;
    }
    public static Button WithIcon(Button button, string title, string icon)
    {
        var glyph = Glyph(icon, 16); glyph.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Avalonia.Data.Binding("Foreground") { Source = button });
        var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("16,Auto"), ColumnSpacing = 7, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(glyph); Grid.SetColumn(label, 1); content.Children.Add(label); button.Content = content;
        Avalonia.Automation.AutomationProperties.SetName(button, title); return button;
    }
    public static TextBox Code(string text) => new() { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono,Consolas"), FontSize = 12, MinHeight = 160 };
}
