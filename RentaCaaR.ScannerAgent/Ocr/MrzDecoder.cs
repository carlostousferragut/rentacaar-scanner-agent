using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using Tesseract;

namespace RentaCaaR.ScannerAgent.Ocr;

public static class MrzDecoder
{
    private static readonly string TessDataPath = Path.Combine(
        AppContext.BaseDirectory, "tessdata");

    public static DocumentFields? TryDecode(byte[] imageBytes, ILogger? logger = null)
    {
        if (!Directory.Exists(TessDataPath)) return null;

        try
        {
            using var ms = new MemoryStream(imageBytes);
            using var originalBitmap = new Bitmap(ms);

            string lang = File.Exists(Path.Combine(TessDataPath, "mrz.traineddata")) ? "mrz" : "eng";
            using var engine = new TesseractEngine(TessDataPath, lang, EngineMode.Default);
            engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789<");

            // Try 1: bottom 30% crop, scaled 2x
            var cropped = TryWithCrop(engine, originalBitmap, 0.30, logger);
            if (cropped != null) return cropped;

            // Try 2: bottom 50% crop (wider), scaled 2x
            cropped = TryWithCrop(engine, originalBitmap, 0.50, logger);
            if (cropped != null) return cropped;

            // Try 3: full image scaled 2x
            return TryWithFullImage(engine, originalBitmap, logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning("MrzDecoder exception: {Msg}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Returns the raw OCR text without parsing — useful for debugging.
    /// </summary>
    public static string GetRawOcr(byte[] imageBytes)
    {
        if (!Directory.Exists(TessDataPath)) return "(tessdata not found)";
        try
        {
            using var ms = new MemoryStream(imageBytes);
            using var bmp = new Bitmap(ms);

            string lang = File.Exists(Path.Combine(TessDataPath, "mrz.traineddata")) ? "mrz" : "eng";
            using var engine = new TesseractEngine(TessDataPath, lang, EngineMode.Default);
            engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789<");

            using var scaled = ScaleUp(bmp, 2);
            using var gray   = ToGrayscale(scaled);
            using var outMs  = new MemoryStream();
            gray.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
            using var pix  = Pix.LoadFromMemory(outMs.ToArray());
            using var page = engine.Process(pix);
            return page.GetText() ?? "";
        }
        catch (Exception ex) { return $"ERROR: {ex.Message}"; }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static DocumentFields? TryWithCrop(TesseractEngine engine, Bitmap bitmap, double fraction, ILogger? logger)
    {
        try
        {
            int cropHeight = (int)(bitmap.Height * fraction);
            int cropY = bitmap.Height - cropHeight;
            var rect = new Rectangle(0, cropY, bitmap.Width, cropHeight);
            using var cropped  = bitmap.Clone(rect, bitmap.PixelFormat);
            using var scaled   = ScaleUp(cropped, 2);
            using var gray     = ToGrayscale(scaled);
            using var outMs    = new MemoryStream();
            gray.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
            using var pix  = Pix.LoadFromMemory(outMs.ToArray());
            using var page = engine.Process(pix);
            var text = page.GetText() ?? "";
            logger?.LogDebug("MRZ crop {Pct}% raw: {Text}", (int)(fraction * 100), text.Replace("\n", " | "));
            return ParseMrz(text);
        }
        catch { return null; }
    }

    private static DocumentFields? TryWithFullImage(TesseractEngine engine, Bitmap bitmap, ILogger? logger)
    {
        try
        {
            using var scaled = ScaleUp(bitmap, 2);
            using var gray   = ToGrayscale(scaled);
            using var outMs  = new MemoryStream();
            gray.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
            using var pix  = Pix.LoadFromMemory(outMs.ToArray());
            using var page = engine.Process(pix);
            var text = page.GetText() ?? "";
            logger?.LogDebug("MRZ full image raw: {Text}", text.Replace("\n", " | "));
            return ParseMrz(text);
        }
        catch { return null; }
    }

    private static Bitmap ScaleUp(Bitmap src, int factor)
    {
        var dst = new Bitmap(src.Width * factor, src.Height * factor);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        return dst;
    }

    private static Bitmap ToGrayscale(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        var cm = new ColorMatrix(new float[][]
        {
            new[] { 0.299f, 0.299f, 0.299f, 0, 0 },
            new[] { 0.587f, 0.587f, 0.587f, 0, 0 },
            new[] { 0.114f, 0.114f, 0.114f, 0, 0 },
            new[] { 0f, 0f, 0f, 1f, 0 },
            new[] { 0f, 0f, 0f, 0f, 1f },
        });
        var attr = new ImageAttributes();
        attr.SetColorMatrix(cm);
        g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height),
                    0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attr);
        return dst;
    }

    // ── MRZ Parsing ──────────────────────────────────────────────────────────

    private static DocumentFields? ParseMrz(string text)
    {
        var lines = text.Split('\n')
            .Select(l => Regex.Replace(l.Trim(), @"[^A-Z0-9<]", ""))
            .Where(l => l.Length >= 30)
            .ToList();

        if (lines.Count < 2) return null;

        // TD3 (passport): 2 lines × 44 chars
        var td3Lines = lines.Where(l => l.Length >= 44).ToList();
        if (td3Lines.Count >= 2)
            return ParseTD3(td3Lines[0][..44], td3Lines[1][..44]);

        // TD1 (ID card): 3 lines × 30 chars
        var td1Lines = lines.Where(l => l.Length >= 30).ToList();
        if (td1Lines.Count >= 3)
            return ParseTD1(td1Lines[0][..30], td1Lines[1][..30], td1Lines[2][..30]);

        return null;
    }

    private static DocumentFields? ParseTD3(string line1, string line2)
    {
        try
        {
            if (!line1.StartsWith("P")) return null;
            string country = line1[2..5];
            string nameField = line1[5..];
            var nameParts = nameField.Split(["<<"], StringSplitOptions.None);
            string lastName  = nameParts.Length > 0 ? Capitalize(nameParts[0].Replace("<", " ").Trim()) : "";
            string firstName = nameParts.Length > 1 ? Capitalize(nameParts[1].Replace("<", " ").Trim()) : "";
            string docNum = line2[0..9].TrimEnd('<');
            string dob    = ParseMrzDate(line2[13..19]);
            string sex    = line2[20..21];
            string expiry = ParseMrzDate(line2[21..27]);
            return new DocumentFields
            {
                DocumentType   = "PASSPORT",
                DocumentNumber = docNum,
                FirstName      = firstName,
                LastName1      = lastName.Split(' ').FirstOrDefault(),
                LastName2      = lastName.Split(' ').Skip(1).FirstOrDefault(),
                DateOfBirth    = dob,
                ExpiryDate     = expiry,
                Nationality    = country,
                Gender         = sex == "M" ? "M" : sex == "F" ? "F" : null,
                Method         = "MRZ",
            };
        }
        catch { return null; }
    }

    private static DocumentFields? ParseTD1(string line1, string line2, string line3)
    {
        try
        {
            string docNum  = line1[5..14].TrimEnd('<');
            string dob     = ParseMrzDate(line2[0..6]);
            string sex     = line2[7..8];
            string expiry  = ParseMrzDate(line2[8..14]);
            string country = line2[15..18];
            var nameParts  = line3.Split(["<<"], StringSplitOptions.None);
            string lastName  = nameParts.Length > 0 ? Capitalize(nameParts[0].Replace("<", " ").Trim()) : "";
            string firstName = nameParts.Length > 1 ? Capitalize(nameParts[1].Replace("<", " ").Trim()) : "";
            return new DocumentFields
            {
                DocumentType   = "DNI",
                DocumentNumber = docNum,
                FirstName      = firstName,
                LastName1      = lastName.Split(' ').FirstOrDefault(),
                LastName2      = lastName.Split(' ').Skip(1).FirstOrDefault(),
                DateOfBirth    = dob,
                ExpiryDate     = expiry,
                Nationality    = country,
                Gender         = sex == "M" ? "M" : sex == "F" ? "F" : null,
                Method         = "MRZ",
            };
        }
        catch { return null; }
    }

    private static string ParseMrzDate(string yymmdd)
    {
        if (yymmdd.Length < 6) return "";
        int yy = int.Parse(yymmdd[..2]);
        int yyyy = yy <= 30 ? 2000 + yy : 1900 + yy;
        return $"{yyyy}-{yymmdd[2..4]}-{yymmdd[4..6]}";
    }

    private static string Capitalize(string? s) =>
        string.IsNullOrEmpty(s) ? "" :
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLower());
}
