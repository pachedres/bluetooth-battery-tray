// Generates the app icon (multi-size .ico + preview .png) using WPF rendering.
// Usage: dotnet run tools/make-icon.cs -- <output-dir>
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:property PublishTrimmed=false

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);

// WPF visuals must be created on an STA thread.
var thread = new Thread(() =>
{
    int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    var images = sizes.Select(Render).ToList();
    File.WriteAllBytes(Path.Combine(outDir, "app.ico"), BuildIco(sizes, images));
    File.WriteAllBytes(Path.Combine(outDir, "app.png"), Render(512));
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
Console.WriteLine($"Icon written to {Path.GetFullPath(outDir)}");

static byte[] Render(int size)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
        var small = size <= 32;
        var white = Brushes.White;

        // Background: rounded square with a blue -> violet gradient.
        var bg = new LinearGradientBrush(Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x7C, 0x3A, 0xED), new Point(0, 0), new Point(1, 1));
        var margin = small ? 6.0 : 16.0;
        var radius = small ? 44.0 : 56.0;
        dc.DrawRoundedRectangle(bg, null, new Rect(margin, margin, 256 - 2 * margin, 256 - 2 * margin), radius, radius);

        if (small)
        {
            // Bigger, bolder battery so it reads at 16-32 px.
            dc.DrawRoundedRectangle(null, new Pen(white, 22), new Rect(36, 80, 164, 96), 16, 16);
            dc.DrawRoundedRectangle(white, null, new Rect(206, 106, 18, 44), 6, 6);
            dc.DrawRoundedRectangle(white, null, new Rect(60, 104, 90, 48), 6, 6);
        }
        else
        {
            // Battery, ~70% charged.
            dc.DrawRoundedRectangle(null, new Pen(white, 14), new Rect(44, 64, 148, 84), 18, 18);
            dc.DrawRoundedRectangle(white, null, new Rect(198, 89, 16, 34), 6, 6);
            dc.DrawRoundedRectangle(white, null, new Rect(62, 82, 86, 48), 8, 8);

            // "Now playing" badge with a music note.
            var center = new Point(182, 182);
            var ring = new SolidColorBrush(Color.FromRgb(0x6A, 0x4F, 0xEE));
            dc.DrawEllipse(white, new Pen(ring, 12), center, 50, 50);

            var note = new FormattedText("", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                54, ring, 1.0);
            dc.DrawText(note, new Point(center.X - note.Width / 2, center.Y - note.Height / 2));
        }
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var ms = new MemoryStream();
    encoder.Save(ms);
    return ms.ToArray();
}

// ICO file with PNG-compressed entries (supported since Windows Vista).
static byte[] BuildIco(int[] sizes, List<byte[]> images)
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((short)0);            // reserved
    w.Write((short)1);            // type: icon
    w.Write((short)sizes.Length); // image count

    var offset = 6 + 16 * sizes.Length;
    for (var i = 0; i < sizes.Length; i++)
    {
        var dim = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
        w.Write(dim);              // width
        w.Write(dim);              // height
        w.Write((byte)0);          // palette
        w.Write((byte)0);          // reserved
        w.Write((short)1);         // color planes
        w.Write((short)32);        // bits per pixel
        w.Write(images[i].Length); // data size
        w.Write(offset);           // data offset
        offset += images[i].Length;
    }
    foreach (var image in images) w.Write(image);
    return ms.ToArray();
}
