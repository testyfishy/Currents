# Changelog

Every change from v1.1.0-alpha.1 onwards is recorded here as it is made. See
[CONTRIBUTING.md](CONTRIBUTING.md) for what an entry has to say.

## Unreleased

### Added

- **Send a PDF to Currents from the page you are reading it on.** Settings, Library proxy now
  generates a bookmarklet holding your own server address. Open a paper through your library, and
  one click on the bookmarklet finds the PDF, fetches it using the session your browser already
  has, and posts it to Currents. Previously the only route was to save the file and attach it by
  hand. This works on iOS and Android, which the browser extension never could.
- The right-click menu has **Get PDF via library**, which opens the paper through your proxy and
  marks the row as waiting for a file, so it is obvious which paper the next upload belongs to.

- **Summaries are a setting now, under Settings, Summaries.** A toggle for whether they happen at
  all, a toggle for whether the full PDF is read, and how many papers one run of the scheduled task
  will do. Off by default: summaries need the Claude Code CLI installed and signed in, and they
  spend that subscription's allowance, so no fresh install should start using it unasked.
- With summaries off, the Summarise action is hidden in both the list and the right-click menu, the
  queue returns nothing, and `?action=summarize` is refused with a message. It used to be queued
  and then silently ignored, which is worse than being told no.

### Changed

- **The summary prompt was rewritten.** It used to return five loose fields from the abstract. It
  now returns nine: take-home, population, intervention, comparator, outcome, harms, limits,
  confounding and verdict. Population, intervention, comparator and outcome are asked for
  separately with numbers, because "reduced mortality" is not an answer and "14.2 percent versus
  19.8 percent, absolute difference 5.6 points" is. Limits asks only for what the authors do not
  already emphasise, and confounding asks what in the tables and figures could produce the result
  instead of the intervention, naming the table it is reading.
- **The summariser reads the PDF when there is an offline copy.** Only the abstract was used
  before, which cannot show an unbalanced baseline table or a confidence interval that crosses one.
  Measured on one paper: the abstract-only summary said "n not stated" and "no tables or figures
  were available"; the same paper with the PDF gave n 17, r 0.725, p 0.00099, and found that
  adjusted R squared falls when the third predictor block is added, while the abstract headlines
  the unadjusted figure.
- Summaries are rendered with their labels in bold on separate lines, the same as a structured
  abstract, instead of as one block of text.
- Summary text is forced to plain ASCII before it is stored. Typographic dashes, arrows and maths
  symbols are replaced with words or ASCII equivalents, because they do not render reliably.
- `POST /api/upload/{doi}` now returns `Access-Control-Allow-Origin: *`, so a bookmarklet running
  on a publisher's page can read the result and report whether it worked. This grants no new write
  access: a cross-origin form post already reached the endpoint, the browser simply hid the reply.
  No read endpoint sends the header, so no page can pull your library back out.

### Fixed

- **The summariser never actually read the PDF, in two separate ways, each of which failed
  silently.** The Claude Code CLI confines file access to its working directory, so the PDF needed
  `--add-dir` as well as `--allowedTools Read`; without it the file is simply unreadable and the
  model writes a perfectly reasonable abstract-only summary that mentions the missing full text in
  its limitations, which reads like an answer rather than a fault. Separately, Windows PowerShell
  5.1 hands a JSON array back from `Invoke-RestMethod` as one pipeline object, so `@(...)` wrapped
  it again and the queue's first element was the array rather than the paper. Field access such as
  `$p.doi` still worked, because PowerShell enumerates members of a one-element array, so the
  prompt looked correct while every property test against it returned false. The script now uses
  `Invoke-WebRequest` with `ConvertFrom-Json`, and logs whether each summary used the full text or
  the abstract alone, so this cannot fail quietly again.

## 1.1.0 (2026-10-03)

Renamed from StrokeLit. Once journals became a catalogue the tool was no longer stroke-specific, and
"Currents" names the activity, keeping up with current literature, rather than the subject.

### Journals are a catalogue now, not a fixed list

- 53 journals in 7 categories, tickable in settings with a search box. Every ISSN is resolved
  through Crossref by `build-catalogue.ps1` and then checked for recent articles, because a wrong
  ISSN gives you a journal that quietly returns nothing for ever.
- Five ISSNs had to be set by hand. Crossref's journal search does not match titles containing an
  ampersand, which covers JNNP, Alzheimer's and Dementia, and Muscle and Nerve. A plain "BMJ" search
  matches an old print ISSN that has nothing deposited against it.

### Specialty presets

- Four presets that set the journals and the topic terms together: stroke and vascular neurology,
  general neurology, neurocritical care, and general internal medicine. Your previous configuration
  is backed up before a preset replaces it.
- Following a journal and filtering its contents are now separate settings, and filtering is off by
  default. Before this a topic score gated everything, so a fresh install with no matching keywords
  showed an empty list.

### Duplicates

- The same paper can be registered under two DOIs. JAMA registered ATTENTION-LATE as both
  `10.1001/jama.2026.15601` and `10.1001/jama.2026.15496`, same title and date, one with the
  abstract and one without.
- Duplicates are matched on normalised title plus publication date. Author is left out on purpose:
  it is the field most likely to differ between two deposits. One of those records had an author of
  "Rui Li" and the other had none, and a first version that included the author put them in
  different buckets and found nothing.

### Abstracts

- Headings without a colon are now split correctly. JAMA writes `Importance Whether intravenous
  tenecteplase...` with no colon, so a pattern that required one found no headings at all and stored
  the abstract as a block of prose. The colon-less form is only accepted at the start of a sentence
  and when followed by a capital letter, so an ordinary word mid-sentence cannot pass as a heading.
  Structured abstracts went from 63 to 76 out of 92.
- `abstract_format` now records `sections`, `plain` or `none` for each paper and shows it in the
  System panel. Without that, a publisher style the splitter cannot handle looks exactly like an
  abstract that was never structured, and the problem stays hidden until someone reads a bad one.

### Reading

- A sort control: unread first, publication date, date added, relevance, journal, tier or title,
  with a direction arrow. The sort column comes from a fixed list and is never taken from the
  request.
- The inbox keeps papers you have read. It used to show only unread papers, so reading one made it
  disappear with no way back to it. Read state is a toggle beside the search box now.
- The PDF column follows whichever paper is selected, or closes. It used to leave the previous
  paper's PDF open next to a different article.
- Right-click a highlight to remove it. Hit detection works on coordinates rather than by making the
  highlights clickable, so you can still select text that sits on top of one.
- The highlights list is behind a toggle, and clicking an entry jumps to that page.

### Packaging

- The config file is now `config.json`, with a `schemaVersion` and a `publicUrl`. Secrets moved to
  `secrets.json`, which is gitignored and never returned by the API.
- The service, scheduled tasks, paths, database filename and environment variables were all renamed.
- The data folder now defaults to a `data` directory beside the executable, instead of a hardcoded
  `C:\Currents\data`. A copy unzipped anywhere else used to write its database and settings to a
  path the user had never heard of. Set `CURRENTS_DATA` to override it, which is what the service
  installer does.
- A self-contained Windows build, so running it needs no .NET install.

## 1.0.0 (2026-10-03)

First consolidated version. Everything below was built and tested against a live set of current
stroke papers.

### Pipeline

- Discovery through Crossref by ISSN, chosen over per-journal RSS after testing both. JAMA
  Neurology's documented feed URL returned 404, and three of four feeds parsed to a single empty
  item. Crossref returned recent work for all five journals tested.
- Metadata through PubMed's `esearch` by DOI, not the NCBI ID Converter. The ID Converter resolves
  against PMC rather than PubMed, and answered "Identifier not found in PMC" for papers that are
  plainly in PubMed. It resolved 1 of 6 test DOIs and held metadata coverage at 4 papers out of 50.
  `esearch` reaches 43 of 52.
- Batching and backoff for NCBI. Three requests a second without a key, and going over returns a
  JSON error body rather than a failed connection, which looks like a parsing bug if you are not
  expecting it.
- 19 journals, four of them open access.

### Triage

- Term matching on word boundaries. Substring matching made "tia" match inside dementia, initial,
  partial and potential, which let in a paediatric blood pressure trial and a hearing aid study as
  stroke papers. Fixing it took 68 kept papers down to 50 and removed every false positive we knew
  about.
- Study type classification into randomised, systematic review and meta-analysis, replacing a single
  loose trial flag. Reviews and meta-analyses are judged on the title alone, because a meta-analysis
  of randomised trials says "randomised" throughout its abstract. A review is never also counted as
  randomised, and there is no overlap between the two.
- Filtering out corrections, replies and reader responses, including correspondence that quotes the
  article it is answering and so carried the original paper's study design into its own title.
- JATS markup stripped from titles, which otherwise showed up as `<scp>LASTE</scp>`.
- The tier bonus only applies to papers that already match on topic.

### Abstracts

- Cleaned when the paper arrives rather than when it is displayed. Sections are split out using
  PubMed's labels where they exist and about 45 known clinical headings otherwise, whitespace is
  collapsed, labels are title-cased, and the result is stored as JSON next to the flat text kept for
  searching. Abstracts that really are unstructured are left as one block rather than cut at guessed
  boundaries.
- `POST /api/reclean` runs it again over everything already stored.

### Full text

- Open access copies from Unpaywall, falling back to the Europe PMC open access set. Those are saved
  as HTML, which also reads better on a phone than a two column PDF.
- Manual attach for paywalled papers, checked on magic bytes so a saved login page renamed to `.pdf`
  is rejected.
- Library proxy support for both EZproxy styles, with the proxied link as the main action on a paper.
- Downloads only happen when you ask, with an option to fetch automatically when you favourite a
  paper or mark it to read later.

### Reading

- A three pane layout: icon bar, list, preview, with the PDF as an optional fourth column. The PDF
  used to sit inside the preview, which pushed the take-home points and the abstract off screen.
- A built-in pdf.js reader with a highlighter. Highlights are stored separately from the file as
  rectangles relative to the page size, so they stay in the right place at any zoom and on any
  device. Export writes them into a copy. pdf.js and pdf-lib are stored locally, since an offline
  copy that needs the internet to display is not much use.
- A per-row field picker and a separate density control.
- Right-click actions, read later, favourites and keyboard navigation.
- Take-home points from the authors' own conclusions, with optional longer summaries through the
  Claude Code CLI.

### Running it

- Binds to localhost only and is published over Tailscale, under its own virtual service account.
- Five monitored checks, including one that the last fetch is recent. A literature inbox that has
  stopped fetching looks like a quiet week, which is worse than an outage you can see.
- Settings pages for journals, keywords, proxy and download policy, validated and backed up when
  saved.

### Known limits

- Only open access full text can be fetched automatically, because publishers block automated
  downloads even of their own open access PDFs.
- Triage matches words, it does not understand meaning.
- Precision is easy to see; recall is not measured, because that would need a reference set to
  compare against.
- One user, no login.
