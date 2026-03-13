using System.Drawing;
using System.Text.RegularExpressions;
using Tesseract;

namespace RentaCaaR.ScannerAgent.Ocr;

public static class MrzDecoder
{
    private static readonly string TessDataPath = Path.Combine(
        AppContext.BaseDirectory, "tessdata");

    public static DocumentFields? TryDecode(byte[] imageBytes)
    {
        if (!Directory.Exists(TessDataPath)) return null;

        try
        {
            using var ms = new MemoryStream(imageBytes);
            using var originalBitmap = new Bitmap(ms);

            // Crop bottom 20% of image where MRZ is located
            int cropHeight = originalBitmap.Height / 5;
            int cropY = originalBitmap.Height - cropHeight;
            var rect = new Rectangle(0, cropY, originalBitmap.Width, cropHeight);
            using var croppedBitmap = originalBitmap.Clone(rect, originalBitmap.PixelFormat);

            // Use mrz tessdata if available, fall back to eng
            string lang = File.Exists(Path.Combine(TessDataPath, "mrz.traineddata")) ? "mrz" : "eng";

            using var engine = new TesseractEngine(TessDataPath, lang, EngineMode.Default);
            engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789<");

            using var pix = PixConverter.ToPix(croppedBitmap);
            using var page = engine.Process(pix);
            var text = page.GetText() ?? "";

            return ParseMrz(text);
        }
        catch
        {
            return null;
        }
    }

    private static DocumentFields? ParseMrz(string text)
    {
        // Extract lines that look like MRZ (all caps, digits, <)
        var lines = text.Split('\n')
            .Select(l => Regex.Replace(l.Trim(), @"[^A-Z0-9<]", ""))
            .Where(l => l.Length >= 30)
            .ToList();

        if (lines.Count < 2) return null;

        // Try TD3 (passport): 2 lines × 44 chars
        var td3Lines = lines.Where(l => l.Length >= 44).ToList();
        if (td3Lines.Count >= 2)
            return ParseTD3(td3Lines[0][..44], td3Lines[1][..44]);

        // Try TD1 (ID card): 3 lines × 30 chars
        var td1Lines = lines.Where(l => l.Length >= 30).ToList();
        if (td1Lines.Count >= 3)
            return ParseTD1(td1Lines[0][..30], td1Lines[1][..30], td1Lines[2][..30]);

        return null;
    }

    // TD3 Passport MRZ
    private static DocumentFields? ParseTD3(string line1, string line2)
    {
        try
        {
            // Line 1: P<{COUNTRY}{SURNAME}<<{FIRSTNAME}<...
            // Line 2: {DOCNUM}{CHECK}{COUNTRY}{DOB}{CHECK}{SEX}{EXPIRY}{CHECK}{PERSONAL}{CHECK}{OVERALL}
            if (!line1.StartsWith("P")) return null;

            string country = line1[2..5];
            string nameField = line1[5..];
            var nameParts = nameField.Split(["<<"], StringSplitOptions.None);
            string lastName = nameParts.Length > 0 ? Capitalize(nameParts[0].Replace("<", " ").Trim()) : "";
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

    // TD1 ID Card MRZ (3 lines × 30)
    private static DocumentFields? ParseTD1(string line1, string line2, string line3)
    {
        try
        {
            string docNum = line1[5..14].TrimEnd('<');
            string dob    = ParseMrzDate(line2[0..6]);
            string sex    = line2[7..8];
            string expiry = ParseMrzDate(line2[8..14]);
            string country = line2[15..18];

            var nameParts = line3.Split(["<<"], StringSplitOptions.None);
            string lastName = nameParts.Length > 0 ? Capitalize(nameParts[0].Replace("<", " ").Trim()) : "";
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
