using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Procure.Services.Export
{
    // Rasterizes the exporter's own PDF bytes into page images (PNG) for the preview modal and for
    // printing. Windows.Data.Pdf is a first-party renderer, so nothing else has to understand the
    // PDF byte format PcrPdfExporter hand-writes.
    public static class PcrPdfRasterizer
    {
        // Shared with PcrExportService.PrintPcrPdfAsync, which reconstructs each page's real
        // physical size from its rendered pixel dimensions - both must agree on the same DPI.
        public const double DefaultDpi = 130;

        public static async Task<List<byte[]>> RenderPagesAsync(byte[] pdfBytes, double dpi = DefaultDpi)
        {
            using var inputStream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(inputStream))
            {
                writer.WriteBytes(pdfBytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            inputStream.Seek(0);

            var document = await PdfDocument.LoadFromStreamAsync(inputStream);
            var images = new List<byte[]>((int)document.PageCount);
            var scale = dpi / 72.0;

            for (uint i = 0; i < document.PageCount; i++)
            {
                using var page = document.GetPage(i);

                using var pageStream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(pageStream, new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)(page.Size.Width * scale),
                    DestinationHeight = (uint)(page.Size.Height * scale)
                });

                pageStream.Seek(0);
                var reader = new DataReader(pageStream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)pageStream.Size);
                var bytes = new byte[pageStream.Size];
                reader.ReadBytes(bytes);
                images.Add(bytes);
            }

            return images;
        }
    }
}
