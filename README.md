# Currents

Currents watches the journals you choose and gives you one list of new papers. It tracks what you
have read, pulls out the authors' conclusions, fetches open access copies, and links paywalled
papers through your library proxy. It runs on your own machine and you reach it from any device on
your private network.

Version 1.1.0

## What it does

1. Checks Crossref for new papers in the journals you follow, looked up by ISSN.
2. Looks each paper up in PubMed for the structured abstract and other metadata.
3. Optionally scores papers against your topic terms, and tags randomised trials, systematic
   reviews and meta-analyses.
4. Shows the result as a list with a preview pane. PDFs open in a column beside it.

Discovery uses Crossref rather than each journal's RSS feed. One API covers every publisher, there
is nothing to scrape, and it does not break when a publisher moves a feed. JAMA Neurology's
documented feed URL returns 404, which is the sort of problem RSS keeps producing.

PubMed supplies metadata only, never the list of candidate papers. A PubMed record appears within
days of publication. It is MEDLINE indexing that takes weeks, and that distinction matters below.

## Why study type is read from the text

PubMed's "Randomized Controlled Trial" publication type filter only matches records MEDLINE has
already indexed. New papers stay at Publisher or In-Process status for weeks, so filtering on
publication type hides the newest trials. For a tool meant to show you what just came out, that is
backwards.

Currents works out study type from the title, the abstract and trial registry numbers instead.
Publication types are used to confirm a classification once they eventually arrive.

## Specialty presets

A preset sets the journals and the topic terms together, so the feed points the right way on the
first fetch.

| Preset | Journals | Topic filtering |
|---|---|---|
| Stroke and vascular neurology | 28 | on |
| General neurology | 27 | off |
| Neurocritical care | 15 | on |
| General internal medicine | 6 | off |

Everything a preset sets stays editable afterwards, and your previous setup is backed up before it
is replaced.

The stroke preset includes NEJM, the Lancet and JAMA, because practice-changing stroke trials
usually appear there rather than in a stroke journal. That is also why topic filtering is on for
that preset and off for the general ones.

Following a journal and filtering its contents are separate settings. With filtering off, which is
the default, you get everything your journals publish and Currents is simply a reader for them.
Topic terms are there if you want to narrow to a subspecialty.

## Features

- A catalogue of 53 journals you can tick on and off. Add any other journal by ISSN.
- Filters for randomised trials, systematic reviews and meta-analyses, read and unread, major
  journals, favourites, read later, and papers with an offline copy.
- Sorting by unread first, publication date, date added, relevance, journal, tier or title.
- Take-home points taken from the authors' own conclusions in the structured abstract. No AI
  involved, and nothing to wait for.
- Optional longer summaries through the Claude Code CLI if you have it installed. Uses your
  existing subscription, no API key.
- Open access full text from Unpaywall and Europe PMC. Anything else you attach by hand.
- A built-in PDF reader with a highlighter. Right-click a highlight to remove it. Export writes the
  highlights into a copy of the file.
- Library proxy support, both the login URL and hostname rewrite styles.
- Abstracts split into labelled sections when they arrive, handling publishers that write headings
  without a colon.
- Duplicate detection, for when the same paper is registered under two DOIs.
- Binds to localhost only and is published over a private network.

## Requirements

- .NET 10, the SDK to build or the runtime to run.
- Windows for the installer scripts and the tray app. The service itself is portable .NET.
- Tailscale, or some other private way to reach the machine. There is no login screen, so do not
  expose it to the internet. The network is the access control.

## Install

```powershell
powershell -ExecutionPolicy Bypass -File fetch-vendor.ps1   # pdf.js and pdf-lib, once
cd src
dotnet publish -c Release -o ..\app
..\app\Currents.exe                                          # http://127.0.0.1:8789
```

To run it as a Windows service with a Tailscale address and scheduled fetches, see
`scripts/setup-currents.ps1` in the HTPC-Tuning repository.

### Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `CURRENTS_DATA` | a `data` folder beside the executable | Database, config and saved PDFs |
| `CURRENTS_URL` | `http://127.0.0.1:8789` | Address to bind. Keep it on localhost. |
| `CURRENTS_EMAIL` | none | Contact address sent to Crossref, NCBI and Unpaywall, which they ask for |
| `CURRENTS_NCBI_KEY` | none | Optional. Raises NCBI's rate limit from 3 to 10 requests a second. |

## Configuration

`config.json` inside the data folder is read again on every fetch and can be edited from the
settings page:

- `journals`, each with an ISSN, a name and a tier. Tier 1 means a journal where practice changes.
- `filtering.enabled`, whether topic terms are applied at all.
- `topicTerms`, `trialTerms` and `minScore`, the vocabulary and the cutoff.
- `proxy`, your library proxy, with `mode` set to `qurl` or `hostname`.
- `autoSave`, whether marking a paper as a favourite or read later also fetches a copy.
- `publicUrl`, the address other devices should use.

Secrets go in `data/secrets.json`, not in the config file. The config holds journals and the proxy,
which you might reasonably paste into a bug report. The API only reports whether a key is set, never
its value.

`build-catalogue.ps1` rebuilds the journal catalogue. It resolves every ISSN through Crossref and
then checks that it actually returns recent articles, because a wrong ISSN gives you a journal that
quietly returns nothing for ever.

## API

| Method | Route | |
|---|---|---|
| GET | `/api/papers?filter=&types=&state=&sort=&dir=&q=` | The list |
| GET | `/api/stats`, `/api/coverage`, `/api/system` | Counts, metadata coverage, service state |
| GET, POST | `/api/profiles`, `/api/profiles/{id}` | List presets, apply one |
| GET | `/api/catalogue` | The journal catalogue |
| POST | `/api/papers/{doi}?action=` | `read`, `unread`, `star`, `later`, `save`, `unsave`, `summarize` |
| POST | `/api/upload/{doi}` | Attach a PDF. Checked on magic bytes. |
| GET | `/api/pdf/{doi}` | Serve a saved copy, with range request support |
| GET, POST | `/api/highlights/{doi}` | Read and add. `?del=id` removes one. |
| POST | `/api/ingest`, `/api/save-all`, `/api/reclean`, `/api/dedupe` | Maintenance |
| GET, PUT | `/api/config`, `/api/secrets` | Settings. Validated and backed up on write. |

## What it will not do

It does not get round paywalls. Only open access full text is fetched. Publishers block automated
downloads even of their own open access PDFs: Wiley and neurology.org return 403 to any client that
is not a browser, with or without a user agent string, while Europe PMC returns 200. Currents
respects that rather than working around it.

It does not store your university login. Replaying a library login from a script is what
systematic-download detection looks for, and the usual result is that the whole institution loses
access. Paywalled papers open through the proxy in your browser and you attach the PDF yourself.

It does not use a language model to decide what is relevant. That approach suits sifting a large
noisy pile. This tool is curated at the source instead, so a good preset does the same job with
nothing to tune or second-guess.

It has no login screen. It is built for one person, and the private network is the boundary.

## Not a clinical decision tool

Currents shows you literature and, if you turn it on, machine-generated summaries of abstracts. It
does not interpret evidence, and a summary is not a substitute for reading the paper. Check anything
that would change how you treat a patient against the source. Automated extraction can be wrong
while reading perfectly plausibly.

## Licence

Copyright 2026 Arankesh Mahadevan

GNU Affero General Public License v3.0. See [LICENSE](LICENSE). This is open source, approved by the
OSI.

You can use, change and share it. The AGPL's network clause means that if you run a modified version
as a service other people can use, you have to make your modified source available to them.

### Commercial licensing

The copyright holder keeps copyright and offers Currents under a second licence as well. If the
AGPL's terms do not work for you, for instance if you want to run a modified version as a hosted
service without publishing your changes, a commercial licence is available. Open an issue to ask.

### Contributing

Because of the second licence, contributions need a Contributor Licence Agreement before they can be
merged. The project cannot offer a commercial licence covering code it does not control. Please open
an issue before starting anything substantial.
