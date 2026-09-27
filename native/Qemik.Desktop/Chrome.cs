using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Qemik.Desktop;

// Qemik draws its own window frame. The client area is extended over the system decorations, so
// the platform still owns the invisible resize border, the drop shadow and the snap gestures while
// everything visible above the content belongs to the app. Element roles tell the platform which
// of our controls stand in for the caption; on Win32 they map to HTCAPTION/HTMINBUTTON/HTMAXBUTTON
// /HTCLOSE, which is also what keeps the Windows 11 snap layouts flyout working on Maximize.
internal static class Chrome
{
    public const double Height = 38;
    private const string MinimizeGlyph = "M0 5 L10 5";
    private const string MaximizeGlyph = "M0.5 0.5 L9.5 0.5 L9.5 9.5 L0.5 9.5 Z";
    private const string RestoreGlyph = "M2.5 2.5 L2.5 0.5 L9.5 0.5 L9.5 7.5 L7.5 7.5 M0.5 2.5 L7.5 2.5 L7.5 9.5 L0.5 9.5 Z";
    public const string CloseGlyph = "M0.5 0.5 L9.5 9.5 M9.5 0.5 L0.5 9.5";
    public static void Frame(Window window, Control? captionAction = null)
    {
        var content = (Control)window.Content!;
        window.Content = null;
        Extend(window);
        var frame = new Grid { RowDefinitions = new RowDefinitions("38,*") };
        var title = Ui.Text(window.Title ?? "Qemik", 11);
        title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        frame.Children.Add(TitleBar(window, title, window.CanResize, captionAction));
        Grid.SetRow(content, 1); frame.Children.Add(content); window.Content = frame;
        window.Height += Height; KeepInsideScreen(window, frame);
    }

    public static void Extend(Window window)
    {
        // BorderOnly keeps the platform border — resize edges, snap and shadow — and drops the
        // title bar, so Avalonia never draws its own caption over the one Qemik builds below.
        window.WindowDecorations = WindowDecorations.BorderOnly;
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = -1;
    }

    // Windows hangs a maximized window's resize border off the screen. Content has to step inside
    // that margin, or its outer edges are cut away on every side.
    public static void KeepInsideScreen(Window window, Layoutable content)
    {
        void Apply() => content.Margin = window.OffScreenMargin;
        window.PropertyChanged += (_, e) => { if (e.Property == Window.WindowStateProperty) Apply(); };
        window.Resized += (_, _) => Apply();
        Apply();
    }

    // Marks a region outside the title bar strip as caption, so it drags the window too.
    public static void Draggable(Control control) =>
        WindowDecorationProperties.SetElementRole(control, WindowDecorationsElementRole.TitleBar);

    public static void ToggleMaximized(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    public static Border TitleBar(Window window, Control? lead, bool resizable, Control? captionAction = null)
    {
        var strip = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        if (lead != null)
        {
            lead.HorizontalAlignment = HorizontalAlignment.Left; lead.VerticalAlignment = VerticalAlignment.Center;
            lead.Margin = new Thickness(18, 0, 0, 0); strip.Children.Add(lead);
        }
        var buttons = new StackPanel { Name = "WindowButtons", Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (captionAction is not null)
        {
            WindowDecorationProperties.SetElementRole(captionAction, WindowDecorationsElementRole.User);
            captionAction.DoubleTapped += (_, e) => e.Handled = true;
            buttons.Children.Add(captionAction);
        }
        if (resizable)
        {
            buttons.Children.Add(Caption(MinimizeGlyph, "Minimize", WindowDecorationsElementRole.MinimizeButton, () => window.WindowState = WindowState.Minimized));
            var maximize = Caption(MaximizeGlyph, "Maximize", WindowDecorationsElementRole.MaximizeButton, () => ToggleMaximized(window));
            var glyph = (Path)maximize.Content!;
            void Sync()
            {
                var maximized = window.WindowState == WindowState.Maximized;
                glyph.Data = Geometry.Parse(maximized ? RestoreGlyph : MaximizeGlyph);
                var name = maximized ? "Restore" : "Maximize";
                ToolTip.SetTip(maximize, name); Avalonia.Automation.AutomationProperties.SetName(maximize, name);
            }
            window.PropertyChanged += (_, e) => { if (e.Property == Window.WindowStateProperty) Sync(); };
            Sync(); buttons.Children.Add(maximize);
        }
        buttons.Children.Add(Caption(CloseGlyph, "Close", WindowDecorationsElementRole.CloseButton, () => window.Close(), "close"));
        Grid.SetColumn(buttons, 1); strip.Children.Add(buttons);
        var border = new Border { Name = "WindowChrome", Height = Height, Background = Brushes.Transparent, Child = strip };
        Draggable(border);
        // Win32 resolves the double click on the caption itself; this covers backends that pass it through.
        if (resizable) border.DoubleTapped += (_, _) => ToggleMaximized(window);
        return border;
    }

    private static Button Caption(string glyph, string name, WindowDecorationsElementRole role, Action action, string style = "")
    {
        var button = Ui.Button("", action);
        button.Classes.Add("chrome"); if (style.Length > 0) button.Classes.Add(style);
        var path = Ui.Glyph(glyph);
        path.Bind(Shape.StrokeProperty, new Avalonia.Data.Binding("Foreground") { Source = button });
        button.Content = path;
        WindowDecorationProperties.SetElementRole(button, role);
        ToolTip.SetTip(button, name); Avalonia.Automation.AutomationProperties.SetName(button, name);
        return button;
    }
}
