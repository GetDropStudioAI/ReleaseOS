# PDF/A spike (OI-8, REOS-50)

Question from `DECISIONS.md` open item 8: "PDF/A needed? Provisional default: PDF/A off; spike QuestPDF PDF/A support in M7 and report."
Time-boxed to about an hour. Written 2026-09-30 against **QuestPDF 2026.9.1** and **veraPDF 1.30.2**.

## Answer in one paragraph

QuestPDF can produce PDF/A. The setting exists and works: `DocumentSettings.PDFA_Conformance` (`PDFA_1A` to `PDFA_3U` are defined). It is wired behind the config key
`Pdf:PdfA` (unset or `false` = off, the OI-8 default; `true` or `2b` = PDF/A-2b; `3b` = PDF/A-3b). With it on, veraPDF validated **all four documents** (release report,
run sheet, evidence pack, scorecard) as **PDF/A-2b and PDF/A-3b: PASS**, and the same documents produced with it off **FAIL** PDF/A-2b, so the validator was discriminating.
The feature is off by default because nobody has said PDF/A is required, and this report does not say the pack is "PDF/A compliant" as a product claim (see "Not verified").

## What was tried

1. Read the installed package's API surface (`Documentation.xml`): `Document.WithSettings`, `DocumentSettings.PDFA_Conformance`, `PDFUA_Conformance`, `CompressDocument`,
   `ImageCompressionQuality`, `ImageRasterDpi`; `QuestPDF.Settings.UseSystemFonts`, `ThrowOnMissingFontFamilies`, `ThrowOnMissingTextGlyphs`.
2. `TrainDocument.GetSettings()` returns `PDFA_Conformance = PDFA_2B` or `PDFA_3B` when `Pdf:PdfA` asks for it (`Exports/Documents/PdfASettings.cs`).
   The value is validated when a job is queued and again when it renders: anything other than false, true, 2b, 3b fails readably (`Pdf:PdfA is '4z'...`), never a silent default.
3. Ran the whole export test suite (`PdfExportTests`, 21 tests: page 1 text markers, ZIP, hashes, roles) with `Pdf__PdfA=true` and again with `Pdf__PdfA=3b`. All passed, so text extraction,
   page size and the SHA-256 acceptance are unaffected by the switch.
4. Validated the generated files with **veraPDF 1.30.2** (`verapdf --flavour 2b|3b --format text`). It was not installed, so it was downloaded from `software.verapdf.org`
   (installer zip) and installed headless on the sandbox with an IzPack auto-install file. This needs internet access and Java 11+ (Java 21 was present); it is **not** part of CI.

| Input | Flavour checked | Result |
|---|---|---|
| Release report, run sheet, evidence pack (4 files), scorecard, `Pdf:PdfA=2b` | PDF/A-2b | **PASS** (9 of 9 files) |
| Same set, `Pdf:PdfA=3b` | PDF/A-3b | **PASS** (9 of 9 files) |
| Same documents, `Pdf:PdfA` off | PDF/A-2b | **FAIL** (9 of 9 files): the check is not vacuous |

What the switch changes in the file: an XMP metadata stream with `pdfaid:part` 2 (or 3) and `pdfaid:conformance` B, a `/OutputIntents` entry (`/S /GTS_PDFA1`), embedded fonts and
about 5 KB more size per document (a 48 KB report became 53 KB, an 88 KB pack 93 KB).

## Findings that matter for the design

* **Not byte-reproducible with PDF/A on.** With it off, the same data renders byte-identical files (tested for all four kinds). With it on, QuestPDF writes random
  `xmpMM:DocumentID` / `InstanceID` UUIDs and derives the trailer `/ID` from them, so two renders differ in about 110 bytes; masking those values makes them equal (also tested).
  This does not touch the acceptance: the stored SHA-256 is computed over the final file that is then served, so it always matches the download. It does mean "regenerate and
  compare hashes" cannot be used as a reproducibility check when PDF/A is on. The MetricSnapshots rows, not the PDF bytes, are what make the numbers reproducible.
* **Fonts.** PDF/A requires embedding every font used. QuestPDF 2026.9 no longer uses system fonts by default; the exports switch them on (`Pdf:UseSystemFonts`, default true) so
  the OS supplies the monospace and symbol glyphs. Body text is QuestPDF's bundled Lato (SIL OFL, safe to embed). The OS fonts that get embedded (subsets) are whatever the
  host has (Cascadia Mono or Consolas on Windows, Menlo or SF Mono on macOS, DejaVu / Liberation Mono on Linux): **check that their licences allow embedding** before
  turning PDF/A on in production on a host whose fonts are proprietary. Apple's SF fonts in particular restrict embedding.
* `PDFUA_Conformance` (accessible PDF) is a separate switch; QuestPDF documents it as making output non-reproducible and it was **not** evaluated.

## Not verified (do not claim)

* PDF/A-2a, 2u, 3a, 3u and PDF/A-1: not run. (2a and 3a need tagged structure; QuestPDF's tagging is the PDF/UA feature above.)
* Only the veraPDF profile rules were checked (its "PASS" means no rule of that profile failed). No Adobe Preflight run, no visual comparison between the archival and the normal file.
* Only the generated sample train (two gates, four runbook steps, two attachments). A very long pack (hundreds of audit rows, many pages) and a host with **no** symbol font
  (missing glyphs render as replacement marks with `ThrowOnMissingTextGlyphs=false`; whether veraPDF still passes is untested) were not tried.
* PDF/A-3 allows attaching the source files inside the PDF. QuestPDF's API for that was not investigated; the ZIP form of the pack remains the way files travel with the pack.
* Long-term validity across QuestPDF upgrades: the result is for 2026.9.1 only.

## Recommendation

1. Keep **`Pdf:PdfA` off** (the provisional default) until a requirement is stated. Retention is 7 years (D22), and the audit rows and the SHA-256 in the pack already give tamper evidence;
   PDF/A adds format longevity, which is a records-management decision, not an engineering one.
2. If governance wants it: set `Pdf:PdfA=2b`. Nothing else in the code changes.
3. Before saying "PDF/A" to an auditor: add an optional CI job that runs veraPDF against a generated pack (Java plus the installer as above, or the `verapdf` Docker image), pin the veraPDF and
   QuestPDF versions, and re-run it on every QuestPDF upgrade. Decide which host fonts are permitted to be embedded.
4. Decide 2b or 3b: 3b only matters if files are to be embedded in the PDF, which this code does not do.

## Open risks

* Font licensing for embedded OS fonts (above). Mitigation: register a licensed OFL monospace font with `FontManager` instead of using system fonts (would add a font file to the repo; a decision for the lead, rule 10 forbids only SF Pro).
* Output changes when QuestPDF changes its PDF/A writer (fixes are likely; regressions are possible). Mitigation: the CI job above.
* Non-reproducible bytes (above): fine for evidence, not for diffing.
