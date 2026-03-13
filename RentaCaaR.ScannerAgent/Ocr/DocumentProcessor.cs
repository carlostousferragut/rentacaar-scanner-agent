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
        // 1. Try PDF417 barcode (Spanish DNI/NIE - most reliable)
        var barcode = BarcodeDecoder.TryDecode(imageBytes);
        if (barcode != null)
        {
            _logger.LogInformation("Document decoded via PDF417 barcode: {Type}", barcode.DocumentType);
            return barcode;
        }

        // 2. Try MRZ (passports, some EU ID cards)
        var mrz = MrzDecoder.TryDecode(imageBytes);
        if (mrz != null)
        {
            _logger.LogInformation("Document decoded via MRZ: {Type}", mrz.DocumentType);
            return mrz;
        }

        // 3. Return empty (no match - frontend will show manual form)
        _logger.LogWarning("Could not extract document fields from scan");
        return new DocumentFields { DocumentType = "UNKNOWN", Method = "NONE" };
    }
}
