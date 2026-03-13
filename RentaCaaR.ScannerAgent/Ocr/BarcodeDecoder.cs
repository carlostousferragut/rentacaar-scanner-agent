using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ZXing;
using ZXing.Common;

namespace RentaCaaR.ScannerAgent.Ocr;

public static class BarcodeDecoder
{
    public static DocumentFields? TryDecode(byte[] imageBytes)
    {
        try
        {
            using var ms = new MemoryStream(imageBytes);
            using var bitmap = new Bitmap(ms);

            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    PossibleFormats = new[] { BarcodeFormat.PDF_417 },
                    TryHarder = true,
                },
            };

            using var argbBitmap = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(argbBitmap))
            {
                g.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
            }

            var rect = new Rectangle(0, 0, argbBitmap.Width, argbBitmap.Height);
            var data = argbBitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int byteCount = Math.Abs(data.Stride) * data.Height;
                var pixelBytes = new byte[byteCount];
                Marshal.Copy(data.Scan0, pixelBytes, 0, byteCount);
                var result = reader.Decode(pixelBytes, data.Width, data.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
                if (result == null) return null;

                return ParseDniBarcode(result.Text);
            }
            finally
            {
                argbBitmap.UnlockBits(data);
            }
        }
        catch
        {
            return null;
        }
    }

    // Spanish DNI PDF417 format (varies by generation):
    // IDESP<NIF><<NOMBRE<APELLIDO1<APELLIDO2<DOB<SEX<EXPIRY
    private static DocumentFields? ParseDniBarcode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            // Format: IDESP<{NIF}<<{FIRST}<{LAST1}<{LAST2}<<{DOB}<{SEX}<{EXPIRY}
            // or newer: {NIF}<{LAST1}<{LAST2}<<{FIRST}<<{DOB}{SEX}{EXPIRY}
            var parts = raw.Split('<', StringSplitOptions.None);

            string docType = "DNI";
            string? nif = null, firstName = null, lastName1 = null, lastName2 = null;
            string? dob = null, expiry = null, sex = null;

            // Try to detect format
            if (raw.StartsWith("IDESP"))
            {
                // Format 1: IDESP<NIF<<FIRST<LAST1<LAST2<DOB<SEX<EXPIRY
                if (parts.Length >= 7)
                {
                    nif       = parts[0].Replace("IDESP", "").Trim();
                    firstName = Capitalize(parts[2]);
                    lastName1 = Capitalize(parts[3]);
                    lastName2 = parts.Length > 4 ? Capitalize(parts[4]) : null;
                    dob       = parts.Length > 5 ? ParseDate(parts[5]) : null;
                    sex       = parts.Length > 6 ? parts[6].Trim() : null;
                    expiry    = parts.Length > 7 ? ParseDate(parts[7]) : null;
                }
            }
            else
            {
                // Format 2 (newer DNI): NIF<LAST1<LAST2<<FIRST<<DOBSEXEXPIRY...
                nif       = parts[0].Trim();
                lastName1 = parts.Length > 1 ? Capitalize(parts[1]) : null;
                lastName2 = parts.Length > 2 ? Capitalize(parts[2]) : null;
                firstName = parts.Length > 4 ? Capitalize(parts[4]) : null;

                // Try to find date fields (6-digit sequences)
                foreach (var part in parts.Skip(5))
                {
                    var p = part.Trim();
                    if (p.Length == 6 && p.All(char.IsDigit))
                    {
                        if (dob == null) dob = ParseDate(p);
                        else expiry = ParseDate(p);
                    }
                    if (p == "M" || p == "F") sex = p;
                }
            }

            // Determine DNI vs NIE
            if (!string.IsNullOrEmpty(nif))
                docType = nif[0] is 'X' or 'Y' or 'Z' ? "NIE" : "DNI";

            return new DocumentFields
            {
                DocumentType   = docType,
                DocumentNumber = nif,
                FirstName      = firstName,
                LastName1      = lastName1,
                LastName2      = lastName2,
                DateOfBirth    = dob,
                ExpiryDate     = expiry,
                Nationality    = "ESP",
                Gender         = sex,
                Method         = "PDF417",
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseDate(string s)
    {
        s = s.Trim();
        if (s.Length < 6) return null;
        // YYMMDD → YYYY-MM-DD
        var yy = int.Parse(s[..2]);
        var mm = s[2..4];
        var dd = s[4..6];
        var yyyy = yy >= 0 && yy <= 30 ? 2000 + yy : 1900 + yy;
        return $"{yyyy}-{mm}-{dd}";
    }

    private static string? Capitalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLower());
    }
}
