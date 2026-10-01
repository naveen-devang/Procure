using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Procure.Models;

namespace Procure.Services.Export
{
    /// <summary>The Service Entry register as an .xlsx, laid out like the paper book. Written by hand
    /// like PcrExcelExporter (no spreadsheet library), and streamed so a long register does not build
    /// the whole sheet as one string.</summary>
    public static class ServiceEntryExcelExporter
    {
        private static readonly string[] Headers =
        {
            "Sr No", "PO No", "PO Amount", "Vendor Name", "Description", "Inv Date", "Inv Number", "Inv Amount",
            "Tech Handover", "Return - SAP SE", "Service Entry No", "Account Handover", "Status",
        };
        private static readonly double[] Widths = { 8, 18, 14, 32, 40, 12, 20, 14, 14, 15, 18, 16, 15 };

        // Style indexes into cellXfs below.
        private const int Bold = 1, Date = 2, Money = 3;

        public static byte[] Generate(IReadOnlyList<ServiceEntry> rows)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                Text(zip, "[Content_Types].xml", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
<Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
<Default Extension=""xml"" ContentType=""application/xml""/>
<Override PartName=""/xl/workbook.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml""/>
<Override PartName=""/xl/worksheets/sheet1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml""/>
<Override PartName=""/xl/styles.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml""/>
</Types>");
                Text(zip, "_rels/.rels", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
<Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""xl/workbook.xml""/>
</Relationships>");
                Text(zip, "xl/_rels/workbook.xml.rels", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
<Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"" Target=""worksheets/sheet1.xml""/>
<Relationship Id=""rId2"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"" Target=""styles.xml""/>
</Relationships>");
                Text(zip, "xl/workbook.xml", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<workbook xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"">
<sheets><sheet name=""Service Entries"" sheetId=""1"" r:id=""rId1""/></sheets>
<definedNames><definedName name=""_xlnm._FilterDatabase"" localSheetId=""0"" hidden=""1"">'Service Entries'!$A$1:$M$1</definedName></definedNames>
</workbook>");
                Text(zip, "xl/styles.xml", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<styleSheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">
<numFmts count=""1""><numFmt numFmtId=""164"" formatCode=""dd/mm/yyyy""/></numFmts>
<fonts count=""2""><font><sz val=""11""/><name val=""Calibri""/></font><font><b/><sz val=""11""/><name val=""Calibri""/></font></fonts>
<fills count=""3""><fill><patternFill patternType=""none""/></fill><fill><patternFill patternType=""gray125""/></fill>
<fill><patternFill patternType=""solid""><fgColor rgb=""FFF0EDE6""/></patternFill></fill></fills>
<borders count=""1""><border><left/><right/><top/><bottom/><diagonal/></border></borders>
<cellStyleXfs count=""1""><xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0""/></cellStyleXfs>
<cellXfs count=""4"">
<xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0"" xfId=""0""/>
<xf numFmtId=""0"" fontId=""1"" fillId=""2"" borderId=""0"" xfId=""0"" applyFont=""1"" applyFill=""1""/>
<xf numFmtId=""164"" fontId=""0"" fillId=""0"" borderId=""0"" xfId=""0"" applyNumberFormat=""1""/>
<xf numFmtId=""4"" fontId=""0"" fillId=""0"" borderId=""0"" xfId=""0"" applyNumberFormat=""1""/>
</cellXfs>
</styleSheet>");

                using var w = XmlWriter.Create(zip.CreateEntry("xl/worksheets/sheet1.xml").Open(),
                    new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
                w.WriteStartDocument(true);
                w.WriteStartElement("worksheet", Ns);

                // Header row stays put while scrolling.
                w.WriteStartElement("sheetViews", Ns);
                w.WriteStartElement("sheetView", Ns); w.WriteAttributeString("workbookViewId", "0");
                w.WriteStartElement("pane", Ns);
                w.WriteAttributeString("ySplit", "1"); w.WriteAttributeString("topLeftCell", "A2");
                w.WriteAttributeString("activePane", "bottomLeft"); w.WriteAttributeString("state", "frozen");
                w.WriteEndElement(); w.WriteEndElement(); w.WriteEndElement();

                w.WriteStartElement("cols", Ns);
                for (var i = 0; i < Widths.Length; i++)
                {
                    w.WriteStartElement("col", Ns);
                    w.WriteAttributeString("min", (i + 1).ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("max", (i + 1).ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("width", Widths[i].ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("customWidth", "1");
                    w.WriteEndElement();
                }
                w.WriteEndElement();

                w.WriteStartElement("sheetData", Ns);
                w.WriteStartElement("row", Ns);
                foreach (var h in Headers) Str(w, h, Bold);
                w.WriteEndElement();
                foreach (var e in rows)
                {
                    w.WriteStartElement("row", Ns);
                    Num(w, e.SrNo, 0);
                    Str(w, e.PoNo);
                    if (e.PoAmount is { } po) Num(w, po, Money); else Empty(w);
                    Str(w, e.Vendor);
                    Str(w, e.Description);
                    Day(w, e.InvoiceDate);
                    Str(w, e.InvoiceNo);
                    Num(w, e.InvoiceAmount, Money);
                    Day(w, e.TechHandoverDate);
                    Day(w, e.SapSeDate);
                    Str(w, e.ServiceEntryNo ?? "");
                    Day(w, e.AccountHandoverDate);
                    Str(w, e.StageName);
                    w.WriteEndElement();
                }
                w.WriteEndElement();

                w.WriteStartElement("autoFilter", Ns); w.WriteAttributeString("ref", "A1:M1"); w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndDocument();
            }
            return ms.ToArray();
        }

        private static void Text(ZipArchive zip, string name, string body)
        {
            using var s = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            s.Write(body);
        }

        private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        private static void Str(XmlWriter w, string v, int style = 0)
        {
            w.WriteStartElement("c", Ns);
            w.WriteAttributeString("t", "inlineStr");
            if (style != 0) w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
            w.WriteStartElement("is", Ns);
            w.WriteStartElement("t", Ns);
            w.WriteString(Clean(v));
            w.WriteEndElement(); w.WriteEndElement(); w.WriteEndElement();
        }

        private static void Num(XmlWriter w, decimal v, int style)
        {
            w.WriteStartElement("c", Ns);
            if (style != 0) w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
            w.WriteElementString("v", Ns, v.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }

        private static void Day(XmlWriter w, DateTime? d)
        {
            if (d is not { } x) { Empty(w); return; }
            Num(w, (decimal)x.Date.ToOADate(), Date);
        }

        // A cell with nothing in it keeps the columns in place without needing cell references.
        private static void Empty(XmlWriter w) { w.WriteStartElement("c", Ns); w.WriteEndElement(); }

        // XML 1.0 cannot carry most control characters; a pasted description sometimes has them.
        private static string Clean(string s)
        {
            foreach (var ch in s)
                if (ch < 0x20 && ch is not ('\t' or '\n' or '\r'))
                    return string.Concat(Array.FindAll(s.ToCharArray(), c => c >= 0x20 || c is '\t' or '\n' or '\r'));
            return s;
        }
    }
}
