namespace Procure.Services.Export
{
    public enum PdfLayoutMode
    {
        AsGenerated,
        ShrinkToFit
    }

    public enum PdfPaperSize
    {
        A0,
        A1,
        A2,
        A3,
        A4,
        A5,
        A6,
        Letter,
        Legal,
        Tabloid,
        Executive
    }

    public enum PdfOrientation
    {
        Landscape,
        Portrait
    }

    public enum PdfMarginPreset
    {
        Normal,
        Narrow,
        Wide
    }

    // Defaults match today's behavior exactly, so a caller that doesn't opt in sees no change.
    public record PcrPdfOptions
    {
        public PdfLayoutMode LayoutMode { get; init; } = PdfLayoutMode.AsGenerated;
        public PdfPaperSize PaperSize { get; init; } = PdfPaperSize.A4;
        public PdfOrientation Orientation { get; init; } = PdfOrientation.Landscape;
        public PdfMarginPreset MarginPreset { get; init; } = PdfMarginPreset.Normal;
        public bool IncludeSignatureBoxes { get; init; } = true;
    }
}
