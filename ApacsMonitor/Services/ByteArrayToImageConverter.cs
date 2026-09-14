using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ApacsMonitor.Services;

public sealed class ByteArrayToImageConverter : IValueConverter
{
    private static readonly ConditionalWeakTable<byte[], BitmapImage> Cache = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes || bytes.Length == 0)
            return null!;

        if (Cache.TryGetValue(bytes, out var cached))
            return cached;

        try
        {
            var image = Decode(bytes);
            if (image is null)
                return null!;

            Cache.Add(bytes, image);
            return image;
        }
        catch
        {
            return null!;
        }
    }

    private static BitmapImage? Decode(byte[] bytes)
    {
        // APACS can store an image blob with its own header around the actual image.
        // First try the whole field, then look for common embedded image signatures.
        var candidates = new List<(int Offset, int Length)>
        {
            (0, bytes.Length)
        };

        AddSignatureCandidates(bytes, new byte[] { 0xFF, 0xD8, 0xFF }, candidates); // JPEG
        AddSignatureCandidates(bytes, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, candidates); // PNG
        AddSignatureCandidates(bytes, new byte[] { 0x47, 0x49, 0x46, 0x38 }, candidates); // GIF
        AddSignatureCandidates(bytes, new byte[] { 0x42, 0x4D }, candidates); // BMP
        AddSignatureCandidates(bytes, new byte[] { 0x49, 0x49, 0x2A, 0x00 }, candidates); // TIFF LE
        AddSignatureCandidates(bytes, new byte[] { 0x4D, 0x4D, 0x00, 0x2A }, candidates); // TIFF BE

        foreach (var candidate in candidates.Distinct())
        {
            if (TryCreateBitmap(bytes, candidate.Offset, candidate.Length, out var image))
                return image;
        }

        return null;
    }

    private static void AddSignatureCandidates(byte[] bytes, byte[] signature, List<(int Offset, int Length)> candidates)
    {
        for (var i = 0; i <= bytes.Length - signature.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < signature.Length; j++)
            {
                if (bytes[i + j] != signature[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                candidates.Add((i, bytes.Length - i));
        }
    }

    private static bool TryCreateBitmap(byte[] bytes, int offset, int length, out BitmapImage? image)
    {
        image = null;

        try
        {
            using var stream = new MemoryStream(bytes, offset, length, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
