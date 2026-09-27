using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Qemik.Desktop;

// The wave artwork is shared by the app, PNG exports and Windows icon frames.
public sealed class Brand : Control
{
    private static readonly Lazy<Bitmap> Artwork = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://Qemik/Assets/qemik-artwork.png"));
        return new Bitmap(stream);
    });
    public Brand() { Width = 48; Height = 48; }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var destination = new Rect(Bounds.Size);
        using var clip = context.PushClip(new RoundedRect(destination, Bounds.Width * .22));
        context.DrawImage(Artwork.Value, new Rect(Artwork.Value.Size), destination);
    }
}
