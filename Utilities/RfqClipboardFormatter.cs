using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Procure.Models;

namespace Procure.Utilities
{
    public static class RfqClipboardFormatter
    {
        public static string GenerateHtmlTable(IEnumerable<RfqItem> items)
        {
            var sb = new StringBuilder();

            // Wrapper container to prevent email composers (Gmail / Outlook Web) from clipping outer left/top borders
            sb.AppendLine("<div style=\"margin: 6px 0; padding: 2px 0 2px 2px;\">");
            sb.AppendLine("<table border=\"1\" cellpadding=\"6\" cellspacing=\"0\" bordercolor=\"#000000\" style=\"border-collapse: collapse; width: 100%; border: 1px solid #000000; font-family: Calibri, 'Segoe UI', Arial, sans-serif; font-size: 11pt; color: #000000; background-color: #FFFFFF; mso-table-lspace: 0pt; mso-table-rspace: 0pt;\">");

            // Table Header Row: # | Item Description | Quantity | UoM
            sb.AppendLine("  <thead>");
            sb.AppendLine("    <tr style=\"background-color: #FFFFFF;\">");
            sb.AppendLine("      <th style=\"border: 1px solid #000000; border-top: 1px solid #000000; border-bottom: 1px solid #000000; border-left: 1px solid #000000; border-right: 1px solid #000000; padding: 8px 10px; text-align: center; font-weight: bold; width: 45px; vertical-align: middle;\">#</th>");
            sb.AppendLine("      <th style=\"border: 1px solid #000000; border-top: 1px solid #000000; border-bottom: 1px solid #000000; border-left: 1px solid #000000; border-right: 1px solid #000000; padding: 8px 10px; text-align: left; font-weight: bold; vertical-align: middle;\">Item Description</th>");
            sb.AppendLine("      <th style=\"border: 1px solid #000000; border-top: 1px solid #000000; border-bottom: 1px solid #000000; border-left: 1px solid #000000; border-right: 1px solid #000000; padding: 8px 10px; text-align: center; font-weight: bold; width: 95px; vertical-align: middle;\">Quantity</th>");
            sb.AppendLine("      <th style=\"border: 1px solid #000000; border-top: 1px solid #000000; border-bottom: 1px solid #000000; border-left: 1px solid #000000; border-right: 1px solid #000000; padding: 8px 10px; text-align: center; font-weight: bold; width: 95px; vertical-align: middle;\">UoM</th>");
            sb.AppendLine("    </tr>");
            sb.AppendLine("  </thead>");

            // Table Body
            sb.AppendLine("  <tbody>");
            int serialNumber = 1;
            foreach (var item in items)
            {
                var description = WebUtility.HtmlEncode(item.ItemName ?? string.Empty)
                    .Replace("\r\n", "<br/>")
                    .Replace("\n", "<br/>")
                    .Replace("\r", "<br/>");

                // "pcs" matches ClipboardItemParser's blank-unit default so the emitted table
                // round-trips without the unit silently changing.
                var unit = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(item.Unit) ? "pcs" : item.Unit.Trim());
                var qtyStr = item.Quantity.ToString("G29", CultureInfo.InvariantCulture);

                sb.AppendLine("    <tr>");
                sb.AppendLine($"      <td style=\"border: 1px solid #000000; border-top: 1px solid #000000; border-bottom: 1px solid #000000; border-left: 1px solid #000000; border-right: 1px solid #000000; padding: 8px 10px; text-align: center; vertical-align: middle;\">{serialNumber++}</td>");
                sb.AppendLine($"      <td style=\"border: 1px solid #000000; border-top: 1px solid #000000; border-bottom: 1px solid #000000; border-left: 1px solid #000000; border-right: 1px solid #000000; padding: 8px 10px; text-align: left; vertical-align: middle; line-height: 1.35;\">{description}</td>");
                sb.AppendLine($"      <td style=\"border: 1px solid #000000; border-top: 1px solid #000000; border-bottom: 1px solid #000000; border-left: 1px solid #000000; border-right: 1px solid #000000; padding: 8px 10px; text-align: center; vertical-align: middle;\">{qtyStr}</td>");
                sb.AppendLine($"      <td style=\"border: 1px solid #000000; border-top: 1px solid #000000; border-bottom: 1px solid #000000; border-left: 1px solid #000000; border-right: 1px solid #000000; padding: 8px 10px; text-align: center; vertical-align: middle;\">{unit}</td>");
                sb.AppendLine("    </tr>");
            }
            sb.AppendLine("  </tbody>");
            sb.AppendLine("</table>");
            sb.AppendLine("</div>");

            return sb.ToString();
        }

        public static string GeneratePlainTextTable(IEnumerable<RfqItem> items)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#\tItem Description\tQuantity\tUoM");

            int serialNumber = 1;
            foreach (var item in items)
            {
                var desc = (item.ItemName ?? string.Empty).Replace("\t", " ").Replace("\r\n", " ").Replace("\n", " ");
                var unit = string.IsNullOrWhiteSpace(item.Unit) ? "pcs" : item.Unit.Trim();
                var qtyStr = item.Quantity.ToString("G29", CultureInfo.InvariantCulture);

                sb.AppendLine($"{serialNumber++}\t{desc}\t{qtyStr}\t{unit}");
            }

            return sb.ToString();
        }

        public static async Task CopyToClipboardAsync(IEnumerable<RfqItem> items)
        {
            var html = GenerateHtmlTable(items);
            var plainText = GeneratePlainTextTable(items);

#if WINDOWS
            try
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetHtmlFormat(Windows.ApplicationModel.DataTransfer.HtmlFormatHelper.CreateHtmlFormat(html));
                dataPackage.SetText(plainText);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                return;
            }
            catch
            {
                // Fall back to MAUI clipboard if Windows DataPackage fails
            }
#endif
            await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.SetTextAsync(plainText);
        }
    }
}
