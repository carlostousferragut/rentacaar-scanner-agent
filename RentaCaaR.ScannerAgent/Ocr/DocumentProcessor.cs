using System.Drawing;
using System.Drawing.Imaging;

namespace RentaCaaR.ScannerAgent.Ocr;

public class DocumentProcessor
{
    private readonly ILogger<DocumentProcessor> _logger;

    public DocumentProcessor(ILogger<DocumentProcessor> logger)
    {
        _logger = logger;
    }

    public DocumentFields Process(byte[] imageBytes)
    {
        // 1. Try PDF417 barcode — ZXing has AutoRotate internally
        var barcode = BarcodeDecoder.TryDecode(imageBytes);
        if (barcode != null)
        {
            _logger.LogInformation("Document decoded via PDF417 barcode: {Type}", barcode.DocumentType);
            return barcode;
        }

        // 2. Try MRZ on all 4 rotations (phone photos can be in any orientation)
        foreach (var (rotated, angle) in GetRotations(imageBytes))
        {
            var mrz = MrzDecoder.TryDecode(rotated, _logger);
            if (mrz != null)
            {
                _logger.LogInformation("Document decoded via MRZ at {Angle}°: {Type}", angle, mrz.DocumentType);
                return mrz;
            }
        }

        // 3. Return empty (no match - frontend will show manual form)
        _logger.LogWarning("Could not extract document fields from scan");
        return new DocumentFields { DocumentType = "UNKNOWN", Method = "NONE" };
    }

    private static IEnumerable<(byte[] bytes, int angle)> GetRotations(byte[] imageBytes)
    {
        yield return (imageBytes, 0);

        var angles = new[] { (RotateFlipType.Rotate90FlipNone, 90), (RotateFlipType.Rotate180FlipNone, 180), (RotateFlipType.Rotate270FlipNone, 270) };
        foreach (var (flip, angle) in angles)
        {
            using var ms = new MemoryStream(imageBytes);
            using var bmp = new Bitmap(ms);
            bmp.RotateFlip(flip);
            using var outMs = new MemoryStream();
            bmp.Save(outMs, ImageFormat.Png);
            yield return (outMs.ToArray(), angle);
        }
    }
}
