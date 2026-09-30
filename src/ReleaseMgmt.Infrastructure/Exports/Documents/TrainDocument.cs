using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ReleaseMgmt.Infrastructure.Exports.Documents;

/// <summary>One table cell: plain text (optionally monospaced / quiet / bold) or a rich text callback.</summary>
public sealed class Cell
{
    public string? Text { get; init; }
    public bool Mono { get; init; }
    public bool Quiet { get; init; }
    public bool Bold { get; init; }
    public string? Color { get; init; }
    public Action<TextDescriptor>? Rich { get; init; }
    public float? Size { get; init; }

    public static implicit operator Cell(string? text) => new() { Text = text ?? "" };
    public static Cell M(string? text) => new() { Text = text ?? "", Mono = true };
    public static Cell Q(string? text) => new() { Text = text ?? "", Quiet = true };
    public static Cell B(string? text) => new() { Text = text ?? "", Bold = true };
    public static Cell R(Action<TextDescriptor> rich) => new() { Rich = rich };

    public void Render(IContainer c)
    {
        c.Text(t =>
        {
            t.DefaultTextStyle(x => Size is float s ? x.FontSize(s) : x);
            if (Rich is not null) { Rich(t); return; }
            var span = t.Span(Text ?? "");
            if (Mono) span.FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize);
            if (Quiet) span.FontColor(PdfStyle.Quiet);
            if (Color is not null) span.FontColor(Color);
            if (Bold) span.SemiBold();
        });
    }
}

public sealed record Col(string Head, float Width, bool Fixed = false);

/// <summary>
/// Common frame of the four PDFs: Letter, deterministic metadata (creation date = the export's clock, so the same data renders byte-identical PDFs),
/// header (document name left, job reference and generation stamp right, 2-pt rule) and a footer with generated-at, generated-by, train Version and page n of m
/// (PROJECT_SCOPE 9). The evidence pack overrides the footer of its first page to match the mockup.
/// </summary>
public abstract class TrainDocument(ExportModel model, ExportOptions options) : IDocument
{
    protected ExportModel M { get; } = model;
    protected PdfFmt F { get; } = new(model.Clock);
    protected abstract string DocumentName { get; }

    public DocumentMetadata GetMetadata() => new()
    {
        Title = $"{DocumentName}: {M.Train.Title}",
        Author = M.GeneratedBy,
        Subject = $"{M.JobRef} for train {M.Train.Title} (version {M.Train.Version})",
        Creator = "ReleaseOS",
        Producer = "ReleaseOS (QuestPDF)",
        Language = "en-US",
        CreationDate = new DateTimeOffset(DateTime.SpecifyKind(M.GeneratedAt, DateTimeKind.Utc)),
        ModifiedDate = new DateTimeOffset(DateTime.SpecifyKind(M.GeneratedAt, DateTimeKind.Utc)),
    };

    public DocumentSettings GetSettings() => options.TryPdfA(out var level) is null && level is not null ? PdfASettings.Apply(new DocumentSettings(), level) : DocumentSettings.Default;

    public void Compose(IDocumentContainer container) =>
        container.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.MarginTop(39); page.MarginBottom(30); page.MarginHorizontal(42);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(x => x.FontFamily(PdfStyle.SansFamilies).FontSize(PdfStyle.Body).FontColor(PdfStyle.Ink).LineHeight(1.4f));
            page.Header().PaddingBottom(10.5f).Element(Header);
            page.Content().Element(c => c.Column(col => { col.Spacing(10.5f); Body(col); }));
            page.Footer().Element(Footer);
        });

    protected abstract void Body(ColumnDescriptor col);

    protected virtual void Header(IContainer c) =>
        c.BorderBottom(1.5f).BorderColor(PdfStyle.Ink).PaddingBottom(4.5f).Row(r =>
        {
            r.RelativeItem().Text(DocumentName).FontSize(PdfStyle.Body).SemiBold();
            r.AutoItem().Text($"{M.JobRef} · generated {F.Stamp(M.GeneratedAt)} by {M.GeneratedBy}").FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize).FontColor(PdfStyle.Quiet);
        });

    protected virtual void Footer(IContainer c) =>
        c.BorderTop(0.75f).BorderColor(PdfStyle.Hair).PaddingTop(4.5f).Row(r =>
        {
            r.RelativeItem().Text($"{M.JobRef} · generated {F.Stamp(M.GeneratedAt)} by {M.GeneratedBy} · train {M.Train.Title} version {M.Train.Version}")
                .FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize).FontColor(PdfStyle.Quiet);
            r.AutoItem().Element(PageNumber);
        });

    protected static void PageNumber(IContainer c) =>
        c.Text(t =>
        {
            t.DefaultTextStyle(x => x.FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize).FontColor(PdfStyle.Quiet));
            t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages();
        });

    // ---- building blocks ------------------------------------------------------------------------------------------------------------------

    protected static void Grid(IContainer c, Col[] cols, IEnumerable<Cell[]> rows)
    {
        c.Table(t =>
        {
            t.ColumnsDefinition(cd => { foreach (var col in cols) { if (col.Fixed) cd.ConstantColumn(col.Width); else cd.RelativeColumn(col.Width); } });
            t.Header(h => { foreach (var col in cols) h.Cell().HeadCell().Text(col.Head); });
            foreach (var row in rows)
                foreach (var cell in row)
                    cell.Render(t.Cell().BodyCell());
        });
    }

    /// <summary>Label / value rows (no header): 150 px = 112.5 pt label column as in the mockup.</summary>
    protected static void KeyVal(IContainer c, IEnumerable<(string Key, Cell Value)> rows)
    {
        c.Table(t =>
        {
            t.ColumnsDefinition(cd => { cd.ConstantColumn(112.5f); cd.RelativeColumn(); });
            foreach (var (k, v) in rows)
            {
                t.Cell().BodyCell().Text(k).FontColor(PdfStyle.Quiet);
                v.Render(t.Cell().BodyCell());
            }
        });
    }

    protected static void Section(ColumnDescriptor col, string title, Action<IContainer> content, string? note = null)
    {
        col.Item().Column(s =>
        {
            s.Item().SectionTitle(title);
            s.Item().Element(content);
            if (note is not null) s.Item().PaddingTop(3).Text(note).FontColor(PdfStyle.Quiet);
        });
    }

    protected static void Empty(IContainer c, string text) => c.Text(text).FontColor(PdfStyle.Quiet);

    public static string Clip(string? text, int max)
    {
        var s = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    }

    protected static string Or(string? text, string fallback = "—") => string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();

    protected string OwnerOrDash(string? name) => string.IsNullOrEmpty(name) ? "—" : name;
}
