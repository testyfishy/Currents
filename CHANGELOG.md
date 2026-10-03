# Changelog

## 1.1.0 — 2026-10-03

Renamed from StrokeLit. The tool stopped being stroke-specific once journals became a catalogue, and
"Currents" names the activity — *current awareness* — rather than the subject.

### Journals are a catalogue, not a list

- **53 journals across 7 categories**, tickable in settings with a search box. Every ISSN is
  resolved from Crossref by `build-catalogue.ps1` and then proved to return recent articles, because
  a wrong ISSN produces a journal that silently returns nothing forever.
- Five journals needed their ISSN pinned by hand: Crossref's journal search does not match titles
  containing `&` (JNNP, *Alzheimer's & Dementia*, *Muscle & Nerve*), and a bare "BMJ" query matches
  an obsolete print ISSN that deposits nothing.

### Specialty presets

- Four presets set **the journals and the vocabulary together** — stroke and vascular neurology,
  general neurology, neurocritical care, general internal medicine. The previous configuration is
  backed up before a preset replaces it.
- **Subscription and filtering are now separate concerns**, filtering off by default. Previously a
  topic score gated all ingestion, so a fresh install with no matching keywords showed an empty list.

### Deduplication

- The same article can be deposited under **two DOIs**: JAMA registered ATTENTION-LATE as both
  `10.1001/jama.2026.15601` and `...15496`, identical title and date, one carrying the abstract and
  one not.
- The fingerprint is **normalised title + publication date**. The author is deliberately excluded:
  it is exactly the field that differs between two deposits — one record carried "Rui Li" and the
  other no author at all — and a first attempt that included it put the two rows in different
  buckets and matched nothing.

### Abstracts

- **Colon-less headings now split.** JAMA writes `Importance Whether intravenous tenecteplase…`
  with no colon, so a colon-only pattern found zero headings and stored the abstract as prose. The
  colon-less form is accepted only at a sentence boundary followed by a capital, so an ordinary
  mid-sentence word cannot masquerade as a heading. Structured abstracts went from 63 to 76 of 92.
- **`abstract_format` records `sections` / `plain` / `none` per paper**, surfaced in the System
  panel. Without it, a publisher style the splitter cannot handle looks identical to a genuinely
  unstructured abstract, and the gap stays invisible until someone reads a bad one.

### Reading

- **Sort control**: unread-first, publication date, date added, relevance, journal, tier, title,
  with a direction caret. The sort key comes from a fixed map, never interpolated from the request.
- **Inbox keeps read papers.** It previously filtered to unread, so reading a paper made it vanish
  and it could not be found again. Read state is now a toggle beside the search.
- **PDF column follows the selected paper** or closes, instead of leaving the previous paper's PDF
  open beside a different article.
- **Right-click a highlight to remove it.** Hit-tested by coordinate rather than by making the
  overlays clickable, so text over an existing highlight can still be selected.
- Highlights list moved behind a toggle, with click-to-jump to the page.

### Packaging

- Config renamed `config.json` with `schemaVersion` and `publicUrl`; secrets moved to a separate
  `secrets.json` that is gitignored and never returned by the API.
- Service, scheduled tasks, paths, database filename and environment variables all renamed.

## 1.0.0 — 2026-10-03

First consolidated version. Everything below was built and measured against a live corpus of
current stroke literature.

### Pipeline

- **Discovery via Crossref by journal ISSN**, chosen over per-journal RSS after testing: JAMA
  Neurology's documented feed URL returned 404 and three of four feeds parsed to a single empty
  item, while Crossref returned recent work for all five journals tested.
- **Enrichment via PubMed `esearch` by DOI**, not the NCBI ID Converter. The ID Converter resolves
  against PMC, not PubMed, and answered `Identifier not found in PMC` for papers plainly in PubMed
  — it resolved 1 of 6 test DOIs and capped enrichment at 4 of 50 papers. `esearch` reaches 43 of 52.
- **Batching and backoff for NCBI.** Three requests per second anonymous; exceeding it returns a
  JSON error body rather than a transport failure, which reads like a parsing bug.
- **19 journals**, including four open access.

### Triage

- **Word-boundary term matching.** Substring matching made `tia` match inside *dementia*,
  *initial*, *partial*, *potential*, admitting a paediatric blood-pressure trial and a hearing-aid
  study as stroke papers. Fixing it cut 68 retained papers to 50 and removed every known false
  positive.
- **Study-type classification** into randomised / systematic review / meta-analysis, replacing a
  single loose trial flag. Evidence synthesis is judged on the **title alone**, because a
  meta-analysis of randomised trials uses "randomised" throughout its abstract. A synthesis is never
  also counted as randomised — verified zero overlap.
- **Noise filtering** for corrections, replies and reader responses, including correspondence that
  quotes the article it answers (which carried the original paper's study design into the title).
- **JATS markup stripped from titles**, which otherwise rendered as `<scp>LASTE</scp>`.
- Tier bonus applies only to papers that already match the topic.

### Abstracts

- **Cleaned on ingest, not at display time.** Sections are split out — PubMed labels where present,
  otherwise by matching ~45 known clinical headings in flat text — whitespace collapsed, labels
  title-cased, stored as JSON alongside flat text kept for search. Genuinely unstructured abstracts
  are left as one block rather than chopped at guessed boundaries.
- `POST /api/reclean` re-runs it over the back catalogue.

### Full text

- **Offline copies** from Unpaywall, falling back to the Europe PMC open-access subset (saved as
  readable HTML, which also reads better on a phone than a two-column PDF).
- **Manual attach** for paywalled papers, validated on magic bytes so a saved login page named
  `.pdf` is rejected.
- **Library proxy** supporting both EZproxy styles; the proxied link becomes the primary action.
- **Downloading is on request only**, with opt-in automatic fetch on favourite or read-later.

### Reading

- **Three-pane UI** — icon rail, list, preview — with the PDF as an optional fourth column. It was
  previously inline in the preview, which pushed the take-home and abstract out of reach.
- **In-app pdf.js viewer with highlighting.** Highlights are stored separately from the file, as
  page-normalised rectangles so they stay correct at any zoom and on any device; export burns them
  into a copy. pdf.js and pdf-lib are vendored locally — an offline copy that needs the internet to
  render defeats the point.
- **Per-row field picker** and independent density control.
- Right-click actions, read-later, favourites, keyboard navigation.
- **Take-homes** from the authors' own conclusions, with optional structured summaries via the
  local Claude Code CLI.

### Operations

- Loopback-only bind published privately over Tailscale; dedicated virtual service account.
- Five monitored invariants including an ingest-freshness assertion — a literature inbox that stops
  ingesting looks like a quiet week, which is worse than an obvious outage.
- GUI settings for journals, keywords, proxy and download policy, validated and backed up on write.

### Known limits

- Only open-access full text can be fetched automatically; publishers block automated download of
  their own OA PDFs.
- Triage is lexical, not semantic.
- Recall is unquantified; precision is directly observable, recall needs a reference standard.
- Single-user, no authentication.
