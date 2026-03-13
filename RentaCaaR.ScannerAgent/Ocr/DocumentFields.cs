namespace RentaCaaR.ScannerAgent.Ocr;

public record DocumentFields
{
    public string? DocumentType    { get; init; }  // "DNI" | "NIE" | "PASSPORT" | "UNKNOWN"
    public string? DocumentNumber  { get; init; }
    public string? FirstName       { get; init; }
    public string? LastName1       { get; init; }
    public string? LastName2       { get; init; }
    public string? DateOfBirth     { get; init; }  // "YYYY-MM-DD"
    public string? ExpiryDate      { get; init; }  // "YYYY-MM-DD"
    public string? Nationality     { get; init; }  // "ESP"
    public string? Gender          { get; init; }  // "M" | "F"
    public string? Method          { get; init; }  // "PDF417" | "MRZ" | "OCR"
}
