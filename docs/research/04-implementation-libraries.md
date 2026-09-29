_Research report produced 2026-09-28 by a Claude research agent for the Release Management App scope. Every claim cites a page that was actually opened; unreachable pages are flagged._

# Implementation Options Research: .NET 10 + SQLite + React/TS Internal App

Research date: 2026-09-28. Every version/license/price below comes from a page I opened (linked inline); page dates are noted where visible. Items I could not verify are marked **[UNVERIFIED]**. NuGet's JSON API and bundlephobia were blocked from this environment, so NuGet facts come from the nuget.org HTML pages and npm facts from the npm registry API (`registry.npmjs.org`, queried live).

---

## 1. PDF generation (server-side .NET)

| Library | Version (date) | License / cost | Layout model | Tables | Charts | .NET 10 | Maintenance | Key gotchas |
|---|---|---|---|---|---|---|---|---|
| **QuestPDF** | 2026.9.1 (2026-09-25) — [NuGet](https://www.nuget.org/packages/QuestPDF/) | Dual, **source-available, explicitly not MIT/OSI** (License v3.0, effective 2026-07-06). Community free if org < USD 1M annual gross revenue (consolidated), or individual/charity/academic/OSI-licensed OSS/transitive dependency. **Public-sector and publicly traded companies are ineligible regardless of revenue.** Professional $1,999+ (one legal entity, perpetual, 12 mo updates), Enterprise $4,999+ (affiliates, NBD support). Unlimited developers in all tiers. — [license guide](https://www.questpdf.com/license/guide.html), [community](https://www.questpdf.com/license/community.html), [pricing](https://www.questpdf.com/pricing.html), [LICENSE.md](https://github.com/QuestPDF/QuestPDF/blob/main/LICENSE.md) | Fluent, code-first C# | Native `Table` API: Relative/Constant columns, `Header()` rows repeat across pages, Row/ColumnSpan — [docs](https://www.questpdf.com/api-reference/table/basics.html) | None built in; official docs integrate **ScottPlot → `GetSvgXml()` → `.Svg()`** (vector) — [charts](https://www.questpdf.com/api-reference/charts.html), [SVG](https://www.questpdf.com/api-reference/image/svg.html) | Yes: net10.0 TFM listed; zero NuGet deps on .NET 5+ (bundles own Skia m154 + qpdf 12.4.1) | Monthly releases (2026.7.0 Jul 5 → 2026.8.0 Aug 24 → 2026.9.1 Sep 25). Native AOT/trimming since 2026.7.0. 2026.8.0 dropped XPS output. — [releases](https://github.com/QuestPDF/QuestPDF/releases) | Custom Skia native ~16 MB/platform; Linux needs GLIBC 2.32+ and libstdc++6, Alpine is hard — [discussion #622](https://github.com/QuestPDF/QuestPDF/discussions/622). Docker font issues: install `fontconfig`/`libfontconfig1`, register fonts via `FontManager` — [#700](https://github.com/QuestPDF/QuestPDF/issues/700), [#1406](https://github.com/QuestPDF/QuestPDF/issues/1406). SVG text uses the document's fonts, not system fonts unless `UseSystemFonts`. |
| **PDFsharp 6 + MigraDoc** | PDFsharp 6.2.4 (2026-01-06); PDFsharp-MigraDoc 6.2.4 (2026-01-06); 7.0.0-preview-1 (2026-03-24) — [PDFsharp NuGet](https://www.nuget.org/packages/PDFsharp/), [MigraDoc NuGet](https://www.nuget.org/packages/PDFsharp-MigraDoc/), [GitHub](https://github.com/empira/PDFsharp) | **MIT**, free | MigraDoc = high-level document object model (sections/paragraphs/tables, also RTF); PDFsharp = low-level `XGraphics` drawing | Yes (MigraDoc tables) | **Built-in**: `ChartType` = Line, Column2D, ColumnStacked2D, Area2D, Bar2D, BarStacked2D, Pie2D, PieExploded2D — [ChartType.cs](https://github.com/empira/PDFsharp/blob/master/src/foundation/src/MigraDoc/src/MigraDoc.DocumentObjectModel/DocumentObjectModel.Shapes.Charts/enums/ChartType.cs), [charts doc](https://docs.pdfsharp.net/MigraDoc/DOM/Contents/Charts.html) | Yes: net10.0 TFM listed | Slower cadence (6.2.4 Jan 2026; 7.0 in preview). 72.8M downloads. | No HTML, no SVG import **[UNVERIFIED — not stated on pages opened]**; MigraDoc charts are basic 2D and not restyleable to match a web theme; Linux font resolution requires `IFontResolver` setup **[UNVERIFIED on pages opened]**. |
| **IronPDF** | 2026.9.2 (2026-09-01) — [NuGet](https://www.nuget.org/packages/IronPdf/) | Commercial, perpetual + 1 yr updates. Lite $999 (1 dev/1 location/1 project), Plus $1,499 (3/3/3), Professional list $2,999 (shown $2,399), Unlimited list $5,999 (shown $4,799); 3/5-yr extended support extra — [licensing](https://ironpdf.com/licensing/) (discounts visible at fetch time; treat list prices as canonical) | HTML/CSS/JS → PDF via bundled Chrome engine | Via HTML | Via HTML/JS (could render ECharts) | Yes: .NET 10 listed | Actively released | Ships its own Chromium (IronPdf.Native.Chrome.Windows / .Linux). Linux needs libgtk2.0-0, libnss3, libgbm1, libasound2 etc.; supports Ubuntu 16–24, Debian 10/11, CentOS 8, Amazon Linux 2/2023; min 1.75 GB RAM, recommended 8 GB+ — [Linux guide](https://ironpdf.com/how-to/linux/) (page dated 2026-07-21). Download size not documented **[UNVERIFIED]**. |
| **Playwright for .NET** (headless Chromium) | Microsoft.Playwright 1.63.0 (2026-09-23) — [NuGet](https://www.nuget.org/packages/Microsoft.Playwright) | MIT | HTML/CSS → PDF (`Page.PdfAsync`) | Via HTML | Via HTML/JS — can render your actual React/ECharts components | netstandard2.0 lib, runs on .NET 10 | Very active (Microsoft) | Browser installed separately: `pwsh bin/Debug/net10.0/playwright.ps1 install --with-deps chromium` (or `--only-shell` for the lighter headless shell); Chromium ~281 MB on disk; needs PowerShell or programmatic install — [browsers](https://playwright.dev/dotnet/docs/browsers). OS support: Debian 12/13, Ubuntu 22.04/24.04/26.04 (x64/arm64), Windows 11+/Server 2019+, macOS 14+ — [intro](https://playwright.dev/dotnet/docs/intro). Embed fonts as WOFF in containers; offload to a background job — [hompus.nl 2025-08-18](https://blog.hompus.nl/2025/08/18/playwright-pdf-generation-in-dotnet/). PDF only works in headless Chromium. |
| PuppeteerSharp | 25.12.0 (2026-09-24), MIT, net8/net10/netstandard2.0 — [NuGet](https://www.nuget.org/packages/PuppeteerSharp/) | MIT | HTML → PDF | Via HTML | Via HTML | Yes | Active | Same Chromium-download and OS-deps weight as Playwright; Chromium-only. |
| iText (itext7 core) | 9.7.0 (2026-07-08) — [NuGet](https://www.nuget.org/packages/itext/) | **AGPL-3.0** or paid commercial | Programmatic | Yes | No native charts | netstandard2.0 (runs on .NET 10) | Active | AGPL is a non-starter for a proprietary internal app without a commercial license. |

**Recommendation: QuestPDF + SVG charts (ScottPlot server-side, or ECharts SVG posted from the client).** Rationale: code-first, deterministic, no browser process, native AOT-friendly, first-class multi-page tables with repeating headers, monthly releases, and an officially documented vector-chart path via `.Svg()`. The one decision gate is licensing: if the deploying organization is publicly traded or over USD 1M revenue, budget the $1,999 Professional license (unlimited devs, perpetual). If that is unacceptable, **PDFsharp/MigraDoc (MIT)** is the free fallback and even has built-in bar/line/pie charts, at the cost of an older, less fluent API and no SVG embedding. Choose **Playwright** only if pixel-identical reproduction of the React UI is a hard requirement and you accept a ~300 MB Chromium + OS-deps footprint in the deployment image.

---

## 2. CSV import/export

| Library | Version (date) | License | .NET 10 | Performance (Sep's benchmark, 50k rows, AMD 9950X3D) | Mapping / validation | Error reporting for bad rows | Notes |
|---|---|---|---|---|---|---|---|
| **CsvHelper** | 33.1.0 (**2025-06-02**) — [NuGet](https://www.nuget.org/packages/CsvHelper/), [GitHub](https://github.com/JoshClose/CsvHelper) | MS-PL **or** Apache-2.0 (dual) | TFMs are net8.0/net9.0/netstandard2.0/2.1/net462+ — **no net10 TFM**, runs via net9.0 asset | 24.66 ms, 1,184 MB/s, 19.95 KB alloc — [nietras.com/Sep](https://nietras.com/Sep/) | `ClassMap<T>`, attributes, `TypeConverter`s, `Validate()` with message expression (v30+) | Callbacks `BadDataFound`, `MissingFieldFound`, `ReadingExceptionOccurred` (return true to skip), `HeaderValidated`; exceptions carry row number/raw record (`ExceptionMessagesContainRawData`) — [error handling](https://deepwiki.com/JoshClose/CsvHelper/7.2-error-handling) | 699.7M downloads; 326 open issues; last release 15 months ago. Known rough edges: `BadDataFound` context lacks full line ([#1108](https://github.com/JoshClose/CsvHelper/issues/1108)), `MissingFieldFound` headerNames null ([#1110](https://github.com/JoshClose/CsvHelper/issues/1110)) — issue pages found via search, not opened **[UNVERIFIED]**. |
| **Sep** (nietras) | 0.17.1 (release date not shown on page) — [NuGet](https://www.nuget.org/packages/Sep/), [GitHub](https://github.com/nietras/Sep), [docs](https://nietras.com/Sep/) | MIT | **Yes**: net8.0/net9.0/net10.0 (site also lists net11.0) | 1.30 ms, 22,452 MB/s, 1.02 KB alloc (fastest) | No class-map; typed column access `row["Col"].Parse<T>()` (`ISpanParsable<T>`), header lookup, `Unescape`, `SepTrim`, `ParallelEnumerate`, writer | Column-count mismatch throws with `LineNumber`; row spans shown as `<ROWINDEX>:[<LINERANGE>]`; `DisableColCountCheck` to be lenient and validate yourself | Zero-alloc after warmup, SIMD, trimmable/AOT. Minimal API means you write an explicit row→DTO mapper — which is exactly where per-row validation and an error list naturally live. |
| **Sylvan.Data.Csv** | 1.4.4 (2026-04-07) — [NuGet](https://www.nuget.org/packages/Sylvan.Data.Csv/), [docs](https://github.com/MarkPflug/Sylvan/blob/main/docs/Csv/Sylvan.Data.Csv.md) | **[UNVERIFIED — license not shown on pages opened; believed MIT]** | net6.0+/netstandard2.0/2.1 (no net10 TFM) | 1.86 ms, 15,709 MB/s, 8.45 KB alloc | `DbDataReader`; schema via `ICsvSchemaProvider` (nullable/typed columns); `GetRecords<T>` binding via Sylvan.Data; string pooling; delimiter auto-detect | Missing fields → empty string; extra fields ignored; malformed quoting throws with **no recovery**; oversized records fail | Good fit if you want `SqlBulkCopy`-style `IDataReader` pipelines. |

**Recommendation: Sep** for both import and export. Rationale: MIT, explicit net10 TFM, active, fastest by a wide margin (irrelevant at 100k rows, but free), AOT/trim-safe, and its explicit row-parsing style makes a collectable `(row, column, message)` validation report straightforward instead of fighting callback semantics. CsvHelper is the conservative alternative if you want attribute/ClassMap binding, but note it has not shipped since June 2025 and has no net10 target.

---

## 3. Excel (.xlsx) export

| Library | Version (date) | License / cost | .NET 10 | Model | Styling/tables | Memory | Notes |
|---|---|---|---|---|---|---|---|
| **ClosedXML** | 0.105.1 (2026-07-25) — [NuGet](https://www.nuget.org/packages/closedxml/) | **MIT** | Yes (netstandard2.0; compat list includes .NET 10) | Full in-memory workbook DOM on Open XML SDK | Rich: styles, number formats, tables, freeze panes, autofilter, formulas | Whole workbook in memory | 225.9M downloads; deps DocumentFormat.OpenXml ≥3.1.1 <4, SixLabors.Fonts, ExcelNumberFormat, RBush, ClosedXML.Parser. Still pre-1.0 versioning. |
| **EPPlus** | 8.7.1 (2026-09-22) — [NuGet](https://www.nuget.org/packages/epplus/) | **Polyform Noncommercial 1.0.0** since v5; commercial required for business use. Subscription $569→$329/dev/yr (tiered), 2-yr $1,079→$624; perpetual $899→$809/dev (1 yr updates), $1,339→$1,209 (2 yr); 10–50 dev packs $6,395–$16,195. EULA last updated 2023-10-19 — [LicenseOverview](https://www.epplussoftware.com/en/LicenseOverview) | net8/9/10, netstandard2.0/2.1, net35/462 | Full DOM + charts, pivot tables | Richest feature set | In memory | Must call `License.SetCommercial()` / `SetNonCommercialPersonal/Organization()` at startup or it throws. An internal tool at a company is commercial use. |
| **MiniExcel** | 1.46.0 (2026-08-22) — [NuGet](https://www.nuget.org/packages/MiniExcel/), [GitHub](https://github.com/shps951023/MiniExcel) | **Apache-2.0** | Yes: net8/9/10 TFMs, no deps on modern .NET | Streaming row-by-row from `IEnumerable`/`DataTable`/`IDataReader`; templates | Basic table styling only; no charts (per README) | Very low (streams) | 13.5M downloads. Canonical repo appears to be `mini-software/MiniExcel` (the shps951023 page identified itself as a fork) **[repo location UNVERIFIED]**. |
| **Open XML SDK** | DocumentFormat.OpenXml 3.5.1 (2026-03-18) — [NuGet](https://www.nuget.org/packages/DocumentFormat.OpenXml/) | MIT | net8/net10/netstandard2.0/net35 | Low-level typed XML | Everything manual (shared strings, styles part) | Can stream via `OpenXmlWriter` | 442.5M downloads; verbose for report export. |

**Recommendation (export-only): ClosedXML.** Rationale: MIT, .NET 10, huge adoption, and the styling/table/freeze-pane features you want for a report-grade export with no license ceremony. Switch the large-dataset endpoints to **MiniExcel** if exports exceed a few hundred thousand rows and only need plain tables. Skip EPPlus unless you are buying per-developer licenses.

---

## 4. Analytics on SQLite (.NET 10 / EF Core 10)

**SQLite feature versions**

| Feature | Version | Source |
|---|---|---|
| Window functions (11 built-ins: row_number, rank, dense_rank, percent_rank, cume_dist, ntile, lag, lead, first_value, last_value, nth_value; any built-in aggregate usable with `OVER`) | 3.25.0 (2018-09-15); EXCLUDE/GROUPS/window chaining 3.28.0 (2019-04-16) | [sqlite.org/windowfunctions](https://sqlite.org/windowfunctions.html) |
| JSON functions built in by default, `->`/`->>` operators | 3.38.0 (2022-02-22) | [sqlite.org/json1](https://sqlite.org/json1.html) |
| JSONB binary storage (`jsonb()`, `jsonb_*`) | 3.45.0 (2024-01-15) | same |

**What .NET 10 actually ships**

| Package | Version (date) | Bundled SQLite |
|---|---|---|
| Microsoft.Data.Sqlite 10.0.12 (2026-09-08), MIT — [NuGet](https://www.nuget.org/packages/microsoft.data.sqlite) | depends on SQLitePCLRaw.bundle_e_sqlite3 ≥ 2.1.12 | 2.1.12 references SQLite **3.53.3** per [SQLitePCL.raw releases](https://github.com/ericsink/SQLitePCL.raw/releases) |
| Microsoft.EntityFrameworkCore.Sqlite 10.0.12 (2026-09-08), MIT, net10.0 — [NuGet](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Sqlite/) | same bundle ≥ 2.1.12 | 3.53.3 by default; bundle 2.1.13 (2026-08-13) and 3.0.5 (2026-07-27, SQLite 3.53.4) exist — [bundle NuGet](https://www.nuget.org/packages/sqlitepclraw.bundle_e_sqlite3/). SQLitePCLRaw 3.x renamed the native package to `SourceGear.sqlite3`; EF Core 10 still pins the 2.1.x line. |

So every window/JSON/JSONB feature above is available out of the box; you can pin a newer `SQLitePCLRaw.bundle_e_sqlite3` explicitly if you want 3.53.4.

**EF Core 10 SQLite provider limitations relevant to analytics** — [limitations page](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations) (updated 2026-04-16) and [What's new in EF Core 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew) (updated 2025-10-02):

- `DateTimeOffset`: comparison and ordering are client-evaluated. Store `DateTime` in UTC (or a `long` ticks/Unix-ms column with a value converter) for anything you filter or order on. EF Core 10 also changed Microsoft.Data.Sqlite's DateTime/DateTimeOffset UTC handling (#36195, listed as a breaking change).
- `decimal`: limitations page says comparison, ordering, and aggregates are client-side; use `double` or `.HasConversion<double>()`. EF Core 10 What's New says `MAX`/`MIN`/`ORDER BY` on decimal are now translated (#35606). **Discrepancy between the two pages — flag**; assume `SUM`/`AVG` on decimal still evaluate client-side and store metrics as `double`/`long`.
- `TimeSpan`, `ulong`: comparison/ordering client-side.
- No schemas, no sequences; many migration ops force table rebuilds.
- Window functions: no LINQ translation. Use `context.Database.SqlQuery<T>($"...")` (EF Core 8+, arbitrary unmapped result types, parameterized via interpolation) or `FromSql` for entity types; composable if the SQL starts with `SELECT` — [SQL queries doc](https://learn.microsoft.com/en-us/ef/core/querying/sql-queries) (updated 2026-09-03). Dapper on the same `SqliteConnection` is an equally good fit for rollup queries.
- Useful EF Core 10 additions: `LeftJoin`/`RightJoin` LINQ operators, named query filters, `ExecuteUpdate` with non-expression lambdas.

**Practical limits at ~100k rows** — SQLite's own guidance: "data analysis" is a listed appropriate use, unlimited readers, one writer at a time — [whentouse](https://www.sqlite.org/whentouse.html). At 100k event rows, live `GROUP BY` rollups with a covering index on `(release_id, occurred_at)` (plus `ANALYZE`, WAL mode) should be tens of milliseconds; full-table aggregates well under a few hundred ms **[engineering estimate, not measured]**. Do not pre-materialize for performance at this scale.

**Materialized daily snapshots vs live**: compute live by default; add a `metric_daily(day, release_id, metric, value)` table when (a) the audit evidence pack must reproduce the exact numbers reported at a point in time (this is the strongest reason: snapshots make evidence immutable), (b) history spans years and dashboards poll frequently, or (c) rollups need cross-table joins that get slow. Refresh via a background job (`INSERT ... ON CONFLICT DO UPDATE`) keyed by day; keep raw events as the source of truth.

**DuckDB via DuckDB.NET**: DuckDB.NET.Data.Full 1.5.5 (2026-07-26), MIT, net8/net10 — [NuGet](https://www.nuget.org/packages/DuckDB.NET.Data.Full); Bindings.Full 1.5.6 (2026-09-28) is a **104.81 MB** nupkg — [NuGet](https://www.nuget.org/packages/DuckDB.NET.Bindings.Full). The sqlite extension autoloads and reads SQLite in place: `ATTACH 'app.db' (TYPE sqlite)`; type-affinity mismatches throw unless `sqlite_all_varchar=true` — [DuckDB docs](https://duckdb.org/docs/lts/core_extensions/sqlite). Autoload fetches the extension from DuckDB's repository at first use (network access required unless pre-bundled) **[inference from "autoloaded from the official extension repository"]**. **Verdict: not worth it at 100k rows** — it adds ~100 MB native binaries and a second engine for no measurable gain. Revisit at tens of millions of rows or for ad-hoc analyst SQL.

**Recommendation:** Keep analytics in SQLite 3.53.x; express rollups as raw SQL with window functions through `SqlQuery<T>`/Dapper; store timestamps as UTC `DateTime` (or Unix ms) and metrics as `double`/`long`; add daily snapshot tables only for audit reproducibility.

---

## 5. React charting for dense internal dashboards

npm registry facts queried live 2026-09-28:

| Library | Latest (published) | License | Rendering | Bundle | SVG export (for PDF) | Dense-dashboard fit | Maintenance |
|---|---|---|---|---|---|---|---|
| **Apache ECharts** + echarts-for-react | echarts 6.1.0 (2026-05-19); echarts-for-react 3.0.6 (2026-01-21), peer `echarts ^3–^6`, `react >=16` — [echarts-for-react GitHub](https://github.com/hustcc/echarts-for-react) | Apache-2.0 / MIT | Canvas or SVG (choose per chart) | Tree-shakeable via `echarts/core` + `echarts.use([...])` registering only charts/components/renderer — [import guide](https://apache.github.io/echarts-handbook/en/basics/import/); exact min+gz size **[UNVERIFIED — bundlephobia blocked]** | **Best**: `renderToSVGString()` (SSR mode, since 5.3.0) and `getDataURL({type:'svg'})` with `SVGRenderer` — [SSR doc](https://echarts.apache.org/handbook/en/how-to/cross-platform/server/) | Excellent: dataZoom, brush, large-series Canvas mode, grid for small multiples — [LogRocket 2026-06-01](https://blog.logrocket.com/best-react-chart-libraries-2026/) | Active Apache project |
| **Recharts** | 3.10.1 (2026-07-25), peer React 16.8–19; deps include @reduxjs/toolkit, react-redux, immer, victory-vendor (d3) | MIT | SVG | ~370 KB per [PkgPulse 2026-03-09](https://www.pkgpulse.com/guides/recharts-vs-chartjs-vs-nivo-vs-visx-react-charting-2026) **[UNVERIFIED independently]**; "limited tree-shaking upside" (LogRocket) | Serialize the rendered `<svg>` DOM node; no built-in export API **[UNVERIFIED]** | Fine for a handful of charts; "not optimized for very large datasets" (LogRocket) | Active (v3 line shipping monthly) |
| **visx** | @visx/visx 4.0.0 (2026-06-11), peer React 18/19 | MIT | SVG primitives | Smallest for custom charts, but you write the chart code | Native SVG | Flexible but high effort per chart | Maintenance worries raised Apr 2025; maintainer confirmed May 2025; v4 with React 19 shipped Jun 2026 — [discussion #1908](https://github.com/airbnb/visx/discussions/1908) |
| **Nivo** | @nivo/core and @nivo/line 0.99.0 (**2025-05-23**) | MIT | SVG/Canvas/HTML | Per-chart packages; heavy deps (react-spring, lodash) | SVG variants | Polished defaults | **No release in 16 months** — [releases](https://github.com/plouc/nivo/releases) |
| **Observable Plot** | 0.6.17 (**2025-02-14**), depends on d3 | ISC | SVG | ~1.5 MB unpacked | Native SVG | Concise grammar, but imperative (render in `useEffect`), not React-native | **No release in 19 months**, pre-1.0 — [releases](https://github.com/observablehq/plot/releases) |

**Recommendation: Apache ECharts (echarts 6.1) with `echarts/core` selective imports, wrapped by echarts-for-react or a ~30-line `useEffect` hook.** Rationale: the strongest dense-dashboard toolkit (dataZoom, brush, large-series Canvas), Apache-2.0, actively released, and — decisive for you — it can emit the exact same chart as an SVG string (`renderToSVGString` / `getDataURL`) for embedding into the PDF. Recharts is the pragmatic runner-up if charts stay few and small. Avoid Nivo and Plot given their release gaps.

---

## 6. Server-rendered charts for PDF (matching on-screen)

| Option | Version (date) | License | Output | Match to on-screen ECharts | Deployment weight |
|---|---|---|---|---|---|
| **Client-supplied ECharts SVG → QuestPDF `.Svg()`** | echarts 6.1.0 | Apache-2.0 | Vector SVG | **Exact geometry** (same engine, same option object); set `animation:false`; fonts are resolved by QuestPDF's font manager, so register the same font family in both the web app and QuestPDF | Zero extra server deps |
| ECharts SSR in a Node sidecar (`renderToSVGString`) | echarts ≥5.3.0 | Apache-2.0 | SVG string, no DOM/browser needed — [SSR doc](https://echarts.apache.org/handbook/en/how-to/cross-platform/server/) | Exact | Small Node process; needed only for scheduled/no-browser exports |
| **ScottPlot 5** | 5.1.59 (2026-06-22), MIT, netstandard2.0/net8 (compat 9/10); deps SkiaSharp ≥3.119.0 + HarfBuzz + `SkiaSharp.NativeAssets.Linux.NoDependencies` — [NuGet](https://www.nuget.org/packages/ScottPlot/) | MIT | PNG or **SVG via `GetSvgXml()`**; QuestPDF docs use it — [QuestPDF charts](https://www.questpdf.com/api-reference/charts.html), [SVG feasibility #2704](https://github.com/ScottPlot/ScottPlot/issues/2704) | Approximate (different engine; style manually) | ~SkiaSharp natives; QuestPDF no longer depends on SkiaSharp so no version conflict ([#622](https://github.com/QuestPDF/QuestPDF/discussions/622)) |
| OxyPlot.SkiaSharp | 2.2.0 (**2024-09-03**), MIT, SkiaSharp ≥2.88.8 — [NuGet](https://www.nuget.org/packages/OxyPlot.SkiaSharp), [releases](https://github.com/oxyplot/oxyplot/releases) | MIT | PNG/PDF; SVG exporter in OxyPlot.Core **[UNVERIFIED]** | Approximate | Pins SkiaSharp 2.88.x — likely conflicts with ScottPlot's 3.x **[inference]**; no release in 2 years |
| LiveCharts2 (SkiaSharpView) | 2.0.5 (2026-06-18), MIT, SkiaSharp ≥2.88.9 — [NuGet](https://www.nuget.org/packages/LiveChartsCore.SkiaSharpView/) | MIT | `SKCartesianChart.SaveImage()` → PNG; SVG not documented — [sample](https://livecharts.dev/docs/eto/latest/samples.general.charttoimage) **[SVG UNVERIFIED]** | Approximate | UI-oriented; raster only as far as documented |
| Headless Chromium (Playwright/PuppeteerSharp) rendering the React page | see §1 | MIT | PDF/PNG | Pixel-exact | ~300 MB browser + OS deps |

**Recommendation: have the React app export each chart as SVG (`chart.renderToSVGString()` or `getDataURL({type:'svg'})`) and send it with the export request; embed via QuestPDF `.Svg()`.** Same engine, same data, vector output, no browser or Skia chart library on the server. For unattended/scheduled exports use ECharts' zero-dependency `renderToSVGString` in a tiny Node step, or fall back to ScottPlot with a shared palette. Register the same web font (e.g., the one used in the dashboard) in QuestPDF's `FontManager` so SVG text matches.

---

## 7. ICS calendar feed generation

| Library | Version (date) | License | .NET 10 | Deps | Status |
|---|---|---|---|---|---|
| **Ical.Net** | 5.2.3 (2026-06-23) — [NuGet](https://www.nuget.org/packages/ical.net/), [GitHub](https://github.com/ical-org/ical.net) | MIT | netstandard2.0/2.1, net6, net8 TFMs; compat list includes .NET 10 | NodaTime ≥ 3.2.2 (all TFMs); Portable.System.DateTimeOnly on netstandard | Active: moved to the `ical-org` organization Sept 2024; v5 is a full rewrite (~half the memory of v4) with a v4→v5 migration guide; 34.7M downloads |

**Recommendation: Ical.Net 5.x.** It is the de-facto RFC 5545 library for .NET, MIT, and actively maintained under a new org. Gotchas: v5 API differs from v4 samples floating around the web; NodaTime becomes a transitive dependency; serve feeds as `text/calendar` with stable `UID`s per release event so subscribers get updates instead of duplicates.

---

## Cross-cutting recommendation (one stack)

- **PDF:** QuestPDF (Professional license if the org is >$1M revenue or publicly traded; otherwise Community) with charts embedded as ECharts SVG from the client.
- **CSV:** Sep (import with explicit per-row validation report; export).
- **Excel:** ClosedXML; MiniExcel for very large plain exports.
- **Analytics:** SQLite 3.53.x as shipped by Microsoft.Data.Sqlite 10.0.12; raw SQL window functions via `SqlQuery<T>`/Dapper; UTC `DateTime` + `double`/`long` metrics; daily snapshot table only for audit reproducibility; no DuckDB.
- **Dashboards:** Apache ECharts 6 via `echarts/core`.
- **ICS:** Ical.Net 5.2.

## Items I could not verify (flagged above)

1. Exact min+gzip bundle sizes for ECharts/Recharts (bundlephobia blocked; PkgPulse's ~370 KB Recharts figure is third-party).
2. Sylvan.Data.Csv license text (not shown on the pages I opened).
3. Sep 0.17.1 release date.
4. IronPDF native Chrome package download size.
5. PDFsharp/MigraDoc Linux font-resolver requirement and lack of SVG import.
6. OxyPlot SVG exporter and SkiaSharp 2.88 vs 3.x conflict with ScottPlot; LiveCharts2 SVG output.
7. EF Core 10 decimal aggregate behavior (limitations page and What's New page disagree on ordering/MIN/MAX).
8. CsvHelper issue #1108/#1110 details (titles only from search results).
9. MiniExcel canonical repo location (mini-software vs shps951023).
10. DuckDB extension autoload network requirement (inferred from docs wording).
11. SQLite query timing at 100k rows (engineering estimate, not benchmarked).