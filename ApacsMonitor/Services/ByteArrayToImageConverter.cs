using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ApacsMonitor.Services;

public sealed class ByteArrayToImageConverter : IValueConverter
{
    private sealed record DecodeResult(BitmapImage? Image);

    // byte[] uses reference equality here. SqlService reuses the same cached byte[]
    // for the same employee, so a failed decode is also cached and never repeated.
    private static readonly ConcurrentDictionary<byte[], DecodeResult> Cache = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes || bytes.Length == 0)
            return null!;

        var result = Cache.GetOrAdd(bytes, static data => new DecodeResult(Decode(data)));
        return result.Image!;
    }

    private static BitmapImage? Decode(byte[] bytes)
    {
        // APACS may store an image blob with its own header around the actual image.
        var candidates = new List<(int Offset, int Length)> { (0, bytes.Length) };

        AddSignatureCandidate(bytes, new byte[] { 0xFF, 0xD8, 0xFF }, candidates); // JPEG
        AddSignatureCandidate(bytes, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, candidates); // PNG
        AddSignatureCandidate(bytes, new byte[] { 0x47, 0x49, 0x46, 0x38 }, candidates); // GIF
        AddSignatureCandidate(bytes, new byte[] { 0x42, 0x4D }, candidates); // BMP
        AddSignatureCandidate(bytes, new byte[] { 0x49, 0x49, 0x2A, 0x00 }, candidates); // TIFF LE
        AddSignatureCandidate(bytes, new byte[] { 0x4D, 0x4D, 0x00, 0x2A }, candidates); // TIFF BE

        foreach (var candidate in candidates)
        {
            if (TryCreateBitmap(bytes, candidate.Offset, candidate.Length, out var image))
                return image;
        }

        return null;
    }

    private static void AddSignatureCandidate(byte[] bytes, byte[] signature, List<(int Offset, int Length)> candidates)
    {
        var index = IndexOf(bytes, signature);
        if (index >= 0)
            candidates.Add((index, bytes.Length - index));
    }

    private static int IndexOf(byte[] bytes, byte[] pattern)
    {
        for (var i = 0; i <= bytes.Length - pattern.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (bytes[i + j] != pattern[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return i;
        }

        return -1;
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
            bitmap.DecodePixelWidth = 64;
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
