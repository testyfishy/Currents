# Currents

**A literature inbox for clinicians.** Follow the journals in your field and get one list — read and
unread, with take-homes, offline copies and a library-proxy link — reachable privately from any
device.

*Current awareness* is the library-science term for keeping up with what is published in your field.
That is all this does, and it is named for the activity rather than for any one specialty.

Version 1.1.0

---

## Why it exists

Discovery is solved many times over — PubMed alerts, journal RSS, table-of-contents services. What
is not solved anywhere in one place is **a focused feed with a structured take-home and a durable
read/unread library you can reach from a phone** — without waiting for a paper to become popular on
social media first.

## How it works

```
Crossref (by ISSN)  ──┐
                      ├──> triage ──> SQLite ──> web UI ──> private network
PubMed (by DOI)     ──┘
```

1. **Discovery — Crossref, by journal ISSN.** Not per-journal RSS. One uniform API across every
   publisher, no scraping, and it does not break when a publisher moves their feed. Other trackers
   independently reach the same conclusion: RSS where it is stable, Crossref where it is not — and
   for JAMA it is not, its documented feed URL returns 404.
2. **Enrichment — PubMed.** A PubMed *record* appears within days and carries the structured
   abstract; only MEDLINE *indexing* lags by weeks. PubMed supplies metadata, never the candidate
   list.
3. **Triage.** Optional topic scoring on word boundaries, plus study-type classification into
   randomised / systematic review / meta-analysis.
4. **Presentation.** Three panes — icon rail, list, preview — with the PDF as an optional fourth.

### The constraint that shapes everything

> **PubMed's `Randomized Controlled Trial[Publication Type]` filter matches only MEDLINE-indexed
> records.** New papers sit at `Publisher`/`In-Process` status for weeks, so a publication-type
> filter systematically excludes the newest trials — the exact opposite of what a current-awareness
> tool is for.

Study type is detected from text and registry identifiers, with publication types used only to
confirm once they arrive.

## Specialty presets

Pick a specialty and it sets **the journals and the vocabulary together**, so the feed is aimed
correctly from the first fetch:

| Preset | Journals | Topic filtering |
|---|---|---|
| Stroke and vascular neurology | 28 | on |
| General neurology | 27 | off |
| Neurocritical care | 15 | on |
| General internal medicine | 6 | off |

A preset is a starting point, not a lock-in — everything it sets stays editable, and the previous
setup is backed up before it is replaced. The stroke preset deliberately includes NEJM, Lancet and
JAMA: practice-changing stroke trials usually appear there rather than in a stroke journal, which is
also why filtering is on for that profile and off for the general ones.

**Subscription and filtering are separate concerns.** With filtering off — the default — everything
your journals publish arrives, and the app is simply a reader for the journals you follow. Topic
terms are an opt-in layer for narrowing to a subspecialty.

## Features

| | |
|---|---|
| **Journals** | A catalogue of 53 verified journals, tickable; add any other by ISSN |
| **Filters** | Randomised · systematic review / meta-analysis · unread / read · major journals · favourites · read later · offline |
| **Sorting** | Unread-first, publication date, date added, relevance, journal, tier, title |
| **Take-homes** | The authors' own conclusions, parsed from the structured abstract — free, instant, no AI |
| **Summaries** | Optional structured briefs via the local Claude Code CLI (subscription, no API key) |
| **Offline copies** | Open-access full text from Unpaywall and Europe PMC; attach anything else by hand |
| **PDF reader** | In-app pdf.js viewer with highlighting; right-click a highlight to remove it; export burns them into a copy |
| **Library proxy** | EZproxy support, both starting-point URL and hostname-rewrite styles |
| **Abstracts** | Split into labelled sections on ingest, handling both colon and colon-less heading styles |
| **Deduplication** | The same article deposited under two DOIs is detected and hidden |
| **Access** | Loopback-only bind, published privately over a tailnet |

## Requirements

- .NET 10 SDK (to build) or runtime (to run)
- Windows for the installer scripts and tray app; the service itself is portable .NET
- Tailscale, or another private way to reach the host. **Do not expose it publicly** — there is no
  authentication; the network boundary is the access control.

## Install

```powershell
powershell -ExecutionPolicy Bypass -File fetch-vendor.ps1   # pdf.js + pdf-lib, once
cd src
dotnet publish -c Release -o ..\app
..\app\Currents.exe                                          # http://127.0.0.1:8789
```

For the Windows service, the Tailscale proxy and the schedules, see `scripts/setup-currents.ps1` in
the HTPC-Tuning repository.

### Environment

| Variable | Default | Purpose |
|---|---|---|
| `CURRENTS_DATA` | `C:\Currents\data` | Database, config, PDFs |
| `CURRENTS_URL` | `http://127.0.0.1:8789` | Bind address — keep it on loopback |
| `CURRENTS_EMAIL` | — | Contact address sent to Crossref, NCBI and Unpaywall, as their etiquette asks |
| `CURRENTS_NCBI_KEY` | — | Optional; raises NCBI's rate limit from 3/s to 10/s |

## Configuration

`data/config.json`, re-read on each ingest and editable from the UI:

- `journals` — ISSN, name, tier (1 = practice-changing venue)
- `filtering.enabled` — whether topic terms gate ingestion at all
- `topicTerms`, `trialTerms`, `minScore` — the vocabulary and threshold
- `proxy` — library proxy (`mode`: `qurl` or `hostname`)
- `autoSave` — opt-in offline copies on favourite / read-later
- `publicUrl` — the address clients should link to

Secrets live in `data/secrets.json`, never in config: config holds journals and the proxy, which
someone might reasonably paste into an issue. The API reports only *whether* a key is set.

Regenerate the journal catalogue with `build-catalogue.ps1`. It resolves every ISSN from Crossref and
then proves it returns recent articles, because a wrong ISSN produces a journal that silently returns
nothing forever.

## API

| Method | Route | |
|---|---|---|
| GET | `/api/papers?filter=&types=&state=&sort=&dir=&q=` | List |
| GET | `/api/stats`, `/api/coverage`, `/api/system` | Counts, enrichment coverage, service state |
| GET/POST | `/api/profiles`, `/api/profiles/{id}` | List presets, apply one |
| GET | `/api/catalogue` | The journal menu |
| POST | `/api/papers/{doi}?action=` | `read`, `unread`, `star`, `later`, `save`, `unsave`, `summarize` |
| POST | `/api/upload/{doi}` | Attach a PDF (multipart; validated on magic bytes) |
| GET | `/api/pdf/{doi}` | Serve an offline copy (range requests supported) |
| GET/POST | `/api/highlights/{doi}` | Read, add (`?del=id` removes) |
| POST | `/api/ingest`, `/api/save-all`, `/api/reclean`, `/api/dedupe` | Maintenance |
| GET/PUT | `/api/config`, `/api/secrets` | Configuration (validated, backed up) |

## What it deliberately does not do

- **No paywall circumvention.** Only open-access full text is fetched. Publishers block automated
  download even of their own OA PDFs — measured: Wiley and neurology.org return HTTP 403 to any
  non-browser client, with or without a User-Agent, while Europe PMC returns 200. That block is
  respected, not defeated.
- **No stored institutional credentials.** Replaying a library login from a script is what
  systematic-download detection targets, and the usual consequence is suspension of the whole
  institution's access. Paywalled papers are opened through the proxy and attached by hand.
- **No LLM relevance screening.** That suits sifting a noisy pile; this tool is curated at the
  source, so a good preset does the same job with no classifier to tune or trust.
- **No authentication.** Single-user by construction. The private network is the boundary.

## Not a clinical decision tool

Currents surfaces literature and, optionally, machine-generated summaries of abstracts. It does not
interpret evidence, and a summary is not a substitute for reading the paper. Verify anything that
would change patient care against the source. Automated extraction can be wrong in ways that read
perfectly plausibly.

## Licence

Copyright © 2026 Arankesh Mahadevan

**GNU Affero General Public License v3.0** — see [LICENSE](LICENSE). Open source, OSI-approved.

You may use, modify and redistribute it. AGPL's network clause means that if you run a **modified**
version as a service others can reach, you must make your modified source available to them.

### Commercial licensing

The copyright holder retains copyright and offers Currents under **dual licence**. If AGPL's terms
do not suit your use — for example, running a modified version as a hosted service without
publishing your changes — a separate commercial licence is available. Open an issue to enquire.

### Contributing

Because of the dual licence, contributions need a Contributor Licence Agreement before they can be
merged: the project cannot offer a commercial licence covering code it does not control. Please open
an issue before starting substantial work.
