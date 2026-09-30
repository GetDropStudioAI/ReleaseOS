using QuestPDF.Infrastructure;

namespace ReleaseMgmt.Infrastructure.Exports.Documents;

/// <summary>OI-8 spike: the PDF/A switch (config Pdf:PdfA, default off). See docs/PDFA_SPIKE.md for what was and was not verified.</summary>
public static class PdfASettings
{
    public static DocumentSettings Apply(DocumentSettings s, string level)
    {
        s.PDFA_Conformance = level == "3b" ? PDFA_Conformance.PDFA_3B : PDFA_Conformance.PDFA_2B;
        return s;
    }
}
