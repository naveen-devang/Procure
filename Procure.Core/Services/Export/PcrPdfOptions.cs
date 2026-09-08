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

        /// <summary>0-based pages to write out; null or empty means all of them. Layout and the
        /// "Page N of M" labels are still computed over the full document, so a 3-page subset of a
        /// 5-page sheet reads "Page 3 of 5" exactly as Acrobat's own page-range print does. Used by
        /// the print path for PDF/XPS-writer "printers", which save a file rather than print.</summary>
        public System.Collections.Generic.IReadOnlyList<int>? PagesToEmit { get; init; }
    }
}
