// Currents - a self-hosted stroke/neurology literature inbox.
//
// PIPELINE, and why it is shaped this way:
//
//   1. DISCOVERY: Crossref REST API, by journal ISSN.
//      Not per-journal RSS. Crossref is uniform across every publisher, carries DOI/title/type/date,
//      needs no scraping, and does not break when a publisher moves their feed (JAMA Neurology's
//      documented RSS URL already 404s). It also surfaces papers within a day or two of publication.
//
//   2. ENRICHMENT: NCBI ID Converter (DOI -> PMID, 200 at a time) then PubMed efetch (200 at a time).
//      PubMed's *record* for a new paper appears within days, carrying the structured abstract -
//      it is only MEDLINE *indexing* (MeSH terms, publication types) that lags by weeks. Verified
//      2026-10-03: recent Stroke DOIs resolved to PMIDs with status Publisher/In-Process and full
//      BACKGROUND/METHODS/RESULTS/CONCLUSIONS abstracts.
//      NCBI allows 3 requests/sec without an API key (hit and confirmed during development), so all
//      calls go through a throttle and everything is batched.
//
//   3. TRIAGE: keyword scoring for stroke relevance + trial detection from text and registry IDs.
//      PubMed's "Randomized Controlled Trial[pt]" filter is not used: it only matches
//      MEDLINE-indexed records, so it would systematically hide the newest trials - the exact
//      opposite of what this tool is for.
//
//   4. TAKE-HOME: the CONCLUSIONS section is parsed out of the structured abstract. That is a real
//      take-home with no AI, no latency and no cost. An optional richer summary is produced by the
//      locally installed Claude Code CLI (MAX subscription, no API key) via a queue - see
//      /api/papers/{doi}/summarize and scripts\currents-summarize.ps1.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; });

var dataDir = Environment.GetEnvironmentVariable("CURRENTS_DATA") ?? @"C:\Currents\data";
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "currents.db");
var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();

var contactEmail = Environment.GetEnvironmentVariable("CURRENTS_EMAIL") ?? "currents@localhost";
var ncbiKey = Environment.GetEnvironmentVariable("CURRENTS_NCBI_KEY");   // optional: raises 3/s -> 10/s

var app = builder.Build();
var log = app.Logger;

// ---------------------------------------------------------------------------------------------
// schema
// ---------------------------------------------------------------------------------------------
void Exec(string sql)
{
    using var c = new SqliteConnection(connStr); c.Open();
    using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
}
// WAL so the summarizer task and the web app can touch the DB concurrently without locking.
Exec("PRAGMA journal_mode=WAL;");
Exec("""
CREATE TABLE IF NOT EXISTS papers (
  doi            TEXT PRIMARY KEY,
  pmid           TEXT,
  title          TEXT NOT NULL,
  journal        TEXT,
  issn           TEXT,
  tier           INTEGER DEFAULT 3,
  authors        TEXT,
  published      TEXT,
  discovered     TEXT NOT NULL,
  abstract       TEXT,
  conclusions    TEXT,
  pub_types      TEXT,
  nct            TEXT,
  score          INTEGER DEFAULT 0,
  is_trial       INTEGER DEFAULT 0,
  oa_url         TEXT,
  read_at        TEXT,
  starred        INTEGER DEFAULT 0,
  ai_summary     TEXT,
  ai_requested   INTEGER DEFAULT 0,
  ai_at          TEXT
);
""");
// Study-type columns, added idempotently so an existing database upgrades in place.
// SQLite has no ADD COLUMN IF NOT EXISTS, so check first.
using (var c0 = new SqliteConnection(connStr))
{
    c0.Open();
    var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    using (var q = c0.CreateCommand())
    {
        q.CommandText = "PRAGMA table_info(papers)";
        using var r = q.ExecuteReader();
        while (r.Read()) have.Add(r.GetString(1));
    }
    foreach (var col in new[] { "is_rct", "is_sr", "is_ma", "read_later" })
        if (!have.Contains(col))
        {
            using var add = c0.CreateCommand();
            add.CommandText = $"ALTER TABLE papers ADD COLUMN {col} INTEGER DEFAULT 0";
            add.ExecuteNonQuery();
        }
    // abstract_format records what the cleanup actually achieved for each paper:
    //   'sections' - split into labelled sections
    //   'plain'    - an abstract with no headings the splitter recognised
    //   'none'     - no abstract yet
    // Without it, a publisher whose heading style is not handled looks identical to a genuinely
    // unstructured abstract, and the gap is invisible until someone reads a bad one.
    // fingerprint catches the same article deposited under two DOIs. A publisher sometimes registers
    // an online-first record and an issue record separately, and JAMA did exactly that for
    // ATTENTION-LATE: 10.1001/jama.2026.15601 and ...15496, identical title and date, one carrying
    // the abstract and one not. DOI alone cannot see it; title + first author + date can.
    // dupe_of names the DOI this row duplicates; such rows are hidden from every list.
    foreach (var col in new[] { "oa_status", "pdf_file", "pdf_error", "abstract_sections",
                                "abstract_format", "fingerprint", "dupe_of" })
        if (!have.Contains(col))
        {
            using var add = c0.CreateCommand();
            add.CommandText = $"ALTER TABLE papers ADD COLUMN {col} TEXT";
            add.ExecuteNonQuery();
        }
}
var pdfDir = Path.Combine(dataDir, "pdf");
Directory.CreateDirectory(pdfDir);

// Highlights live in their own table, NOT inside the PDF. The stored file then stays byte-identical
// to what the publisher or the user supplied, highlights survive re-downloading it, and they can be
// read back without parsing a PDF. Burning them in is done on export, from these rows.
// Rectangles are stored normalised to the page (0-1), so they stay correct at any zoom and on any
// device - a phone and a desktop render the same page at very different pixel sizes.
Exec("""
CREATE TABLE IF NOT EXISTS highlights (
  id      INTEGER PRIMARY KEY AUTOINCREMENT,
  doi     TEXT NOT NULL,
  page    INTEGER NOT NULL,
  rects   TEXT NOT NULL,
  text    TEXT,
  color   TEXT,
  note    TEXT,
  created TEXT NOT NULL
);
""");
Exec("CREATE INDEX IF NOT EXISTS ix_hl_doi ON highlights(doi, page);");
Exec("CREATE INDEX IF NOT EXISTS ix_papers_discovered ON papers(discovered DESC);");
Exec("CREATE INDEX IF NOT EXISTS ix_papers_fingerprint ON papers(fingerprint);");

// Normalised title + publication date. The author is NOT part of the key, because it is precisely
// the field that differs between a publisher's two deposits of one article. JAMA's two records for
// ATTENTION-LATE carried "Rui Li" on one and no author at all on the other, so a key including the
// author put them in different buckets and matched nothing - which is how the first attempt failed.
// Title plus date is specific enough on its own: research titles are long, and corrections and
// correspondence (the realistic source of repeated short titles) are filtered out before this.
static string Fingerprint(string title, string? published)
{
    var t = Regex.Replace((title ?? "").ToLowerInvariant(), @"[^a-z0-9 ]", " ");
    t = Regex.Replace(t, @"\s+", " ").Trim();
    if (t.Length > 120) t = t[..120];
    return $"{t}|{published ?? ""}";
}
Exec("CREATE INDEX IF NOT EXISTS ix_papers_unread ON papers(read_at) WHERE read_at IS NULL;");
Exec("""
CREATE TABLE IF NOT EXISTS ingest_log (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  ts TEXT NOT NULL, journal TEXT, seen INTEGER, kept INTEGER, note TEXT
);
""");

// ---------------------------------------------------------------------------------------------
// config  (record types are declared at the very bottom - C# requires top-level statements first)
// ---------------------------------------------------------------------------------------------
const string AppVersion = "1.1.0";

// Config lives in the DATA directory, not the app directory. Two reasons: the service account has
// only read+execute on the app directory, so edits from the UI would fail there; and `dotnet
// publish` overwrites the app directory, which would silently discard the user's journal and
// keyword edits on the next deployment. The app copy is the seed, used once.
//
// Named config.json since 1.0: it carries journals, keywords, the library proxy and download
// policy, so the old journals.json name was misleading. An existing journals.json is migrated in
// place on first run.
var configPath = Path.Combine(dataDir, "config.json");
var legacyConfig = Path.Combine(dataDir, "journals.json");
if (!File.Exists(configPath) && File.Exists(legacyConfig))
{
    File.Move(legacyConfig, configPath);
    Console.WriteLine($"migrated {legacyConfig} -> {configPath}");
}
if (!File.Exists(configPath))
{
    var seed = Path.Combine(AppContext.BaseDirectory, "config.json");
    if (!File.Exists(seed)) seed = Path.Combine(AppContext.BaseDirectory, "journals.json");
    if (File.Exists(seed)) File.Copy(seed, configPath);
}

Config LoadConfig()
{
    var path = configPath;
    var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    var journals = root["journals"]!.AsArray()
        .Select(j => new Journal(j!["issn"]!.GetValue<string>(), j["name"]!.GetValue<string>(), j["tier"]!.GetValue<int>()))
        .ToList();
    return new Config(
        journals,
        // Filtering defaults OFF: a new install should show you the journals you subscribed to,
        // not an empty list because no keyword matched.
        root["filtering"]?["enabled"]?.GetValue<bool>() ?? false,
        root["minScore"]?.GetValue<int>() ?? 2,
        root["topicTerms"]!.AsArray().Select(t => t!.GetValue<string>().ToLowerInvariant()).ToArray(),
        root["trialTerms"]!.AsArray().Select(t => t!.GetValue<string>().ToLowerInvariant()).ToArray());
}

// ---------------------------------------------------------------------------------------------
// http plumbing: one client, and a hard throttle for NCBI
// ---------------------------------------------------------------------------------------------
var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
{ Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.UserAgent.ParseAdd($"Currents/1.0 (mailto:{contactEmail})");

// NCBI: 3 req/s anonymous, 10 with a key. Exceeding it returns a JSON error body, not an HTTP error,
// which is easy to mistake for a parsing bug - so serialise and pace every NCBI call here.
var ncbiGate = new SemaphoreSlim(1, 1);
var ncbiSpacing = ncbiKey is null ? TimeSpan.FromMilliseconds(380) : TimeSpan.FromMilliseconds(120);
var lastNcbi = DateTime.MinValue;
// Retries on 429. The /pmc/utils/idconv endpoint rate-limits noticeably harder than eutils does -
// pacing alone at ~2.5 req/s still produced "429 Too Many Requests" and lost most of the
// enrichment. Backoff is what actually fixes it, so do not remove this in favour of a bigger delay.
async Task<string> NcbiGet(string url)
{
    if (ncbiKey is not null) url += (url.Contains('?') ? "&" : "?") + "api_key=" + ncbiKey;
    await ncbiGate.WaitAsync();
    try
    {
        for (int attempt = 0; ; attempt++)
        {
            var wait = ncbiSpacing - (DateTime.UtcNow - lastNcbi);
            if (wait > TimeSpan.Zero) await Task.Delay(wait);

            using var resp = await http.GetAsync(url);
            lastNcbi = DateTime.UtcNow;
            if (resp.StatusCode != HttpStatusCode.TooManyRequests)
            {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync();
            }
            if (attempt >= 4) resp.EnsureSuccessStatusCode();   // give up, surface the 429
            var backoff = TimeSpan.FromMilliseconds(800 * Math.Pow(2, attempt));
            log.LogInformation("NCBI 429 - backing off {Ms} ms (attempt {N})", backoff.TotalMilliseconds, attempt + 1);
            await Task.Delay(backoff);
        }
    }
    finally { ncbiGate.Release(); }
}

// ---------------------------------------------------------------------------------------------
// triage
// ---------------------------------------------------------------------------------------------
// Noise that Crossref returns alongside real articles. Verified present in live output:
// "Reader Response: ...", "... - Reply", "Correction to: ...".
var noiseStarts = new[] { "correction", "erratum", "retraction", "reader response", "editorial board",
                          "masthead", "highlights from", "this month in", "in this issue", "author response",
                          // Correspondence that QUOTES the article it replies to. These slipped
                          // through and were classified as randomised studies, because the quoted
                          // title carries the original paper's "Randomized, Controlled Study".
                          "response by", "letter by", "comment on", "in reply", "reply to" };
var noiseContains = new[] { "—reply", "- reply", "– reply",
                            "letter regarding article", "response to letter" };

bool IsNoise(string title)
{
    var t = title.Trim().ToLowerInvariant();
    return noiseStarts.Any(p => t.StartsWith(p)) || noiseContains.Any(p => t.Contains(p));
}

// WORD BOUNDARIES MATTER. A naive Contains() made the short terms match inside unrelated words -
// "tia" hits demen|tia|, ini|tia|l, par|tia|l, poten|tia|l - which is how a paediatric blood-pressure
// trial and a hearing-aid/dementia study both scored as stroke papers on the first run.
// Terms are matched on \b boundaries, with a trailing \w* so "recanalis" still catches
// "recanalisation" and "white matter hyperintensit" catches "...ies"/"...y".
var rxCache = new Dictionary<string, System.Text.RegularExpressions.Regex>();
System.Text.RegularExpressions.Regex TermRx(string term)
{
    if (!rxCache.TryGetValue(term, out var rx))
    {
        rx = new System.Text.RegularExpressions.Regex(
            @"\b" + System.Text.RegularExpressions.Regex.Escape(term) + @"\w*",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);
        rxCache[term] = rx;
    }
    return rx;
}

(int score, bool isTrial) Triage(string title, string? abstractText, Config cfg)
{
    var t = title ?? "";
    var a = abstractText ?? "";
    int score = 0;
    foreach (var term in cfg.TopicTerms)
    {
        var rx = TermRx(term);
        if (rx.IsMatch(t)) score += 2;          // the title is the strongest signal
        else if (rx.IsMatch(a)) score += 1;
    }
    var hay = t + " " + a;
    bool trial = cfg.TrialTerms.Any(term => TermRx(term).IsMatch(hay));
    return (score, trial);
}

// Reads a JSON value as text whether it arrived as a string or a number.
static string? AsText(JsonNode? n) => n?.GetValueKind() switch
{
    JsonValueKind.String => n.GetValue<string>(),
    JsonValueKind.Number => n.GetValue<long>().ToString(CultureInfo.InvariantCulture),
    null => null,
    _ => n.ToString()
};

// ABSTRACT CLEANUP, applied on ingest so what is STORED is already readable.
//
// Two shapes arrive and both read badly raw. PubMed structured abstracts come as labelled sections
// that, concatenated, become one wall of text. Crossref abstracts arrive as JATS fragments whose
// section headings survive tag-stripping as inline words - "BACKGROUND AND PURPOSE: ... METHODS:
// ..." all on a single line, with no break to read against.
//
// So sections are split out and stored separately as JSON, and the UI renders each with its heading.
// The flat `abstract` column is kept for searching and for the list snippet.
var sectionLabels = new[] {
    "BACKGROUND AND PURPOSE", "BACKGROUND AND AIMS", "BACKGROUND AND OBJECTIVES", "BACKGROUND",
    "IMPORTANCE", "OBJECTIVE", "OBJECTIVES", "AIM", "AIMS", "PURPOSE", "INTRODUCTION", "CONTEXT",
    "MATERIALS AND METHODS", "METHODS AND RESULTS", "METHODS", "DESIGN", "DESIGN, SETTING, AND PARTICIPANTS",
    "SETTING", "PARTICIPANTS", "PATIENTS", "PATIENTS AND METHODS", "INTERVENTION", "INTERVENTIONS",
    "EXPOSURES", "MAIN OUTCOMES AND MEASURES", "MAIN OUTCOME MEASURES", "OUTCOMES", "MEASUREMENTS",
    "RESULTS", "FINDINGS", "CONCLUSION", "CONCLUSIONS", "CONCLUSIONS AND RELEVANCE", "INTERPRETATION",
    "DISCUSSION", "LIMITATIONS", "SIGNIFICANCE", "RELEVANCE", "IMPLICATIONS",
    "TRIAL REGISTRATION", "CLINICAL TRIAL REGISTRATION", "REGISTRATION", "FUNDING", "KEY POINTS"
};
// Longest first, so "BACKGROUND AND PURPOSE" is matched before the shorter "BACKGROUND" swallows it.
var labelAlternation = string.Join("|", sectionLabels.OrderByDescending(s => s.Length).Select(Regex.Escape));

static string TitleCaseLabel(string s)
{
    s = s.Trim().TrimEnd(':').Trim();
    if (s.Length == 0) return s;
    // "BACKGROUND AND PURPOSE" -> "Background and Purpose": all-caps headings shout on screen.
    var small = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "and", "or", "of", "the", "in", "to", "for" };
    var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                 .Select((w, i) =>
                 {
                     var lower = w.ToLowerInvariant();
                     if (i > 0 && small.Contains(lower)) return lower;
                     return char.ToUpperInvariant(lower[0]) + lower[1..];
                 });
    return string.Join(' ', words);
}

// Splits a flat abstract on inline section headings. Returns null when it finds none, so an
// genuinely unstructured abstract is left as one block rather than chopped up arbitrarily.
List<(string? Label, string Text)>? SplitFlatAbstract(string text)
{
    // TWO heading styles, because publishers disagree:
    //   "BACKGROUND: text"   - a colon, matched anywhere.
    //   "Importance Whether" - NO COLON, the heading running straight into the sentence. This is
    //                          JAMA's house style and it defeated the colon-only pattern entirely:
    //                          the ATTENTION-LATE abstract scored 0 matches and stayed as prose.
    // The colon-less form is only accepted at a sentence boundary and when the next character is a
    // capital or digit, so an ordinary mid-sentence "results" cannot masquerade as a heading.
    var matches = Regex.Matches(text,
        @"(?i)\b(" + labelAlternation + @")\s*:\s+"
      + @"|(?i)(?:\A|(?<=[.!?;]\s))(" + labelAlternation + @")\s+(?=[A-Z0-9])");
    if (matches.Count < 2) return null;       // one heading is not a structured abstract

    var outp = new List<(string?, string)>();
    // Anything before the first heading is an unlabelled preamble; keep it rather than lose it.
    var lead = text[..matches[0].Index].Trim();
    if (lead.Length > 0) outp.Add((null, lead));
    for (int i = 0; i < matches.Count; i++)
    {
        var start = matches[i].Index + matches[i].Length;
        var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
        var body = text[start..end].Trim();
        // Group 1 is the colon form, group 2 the colon-less one; exactly one of them matched.
        var label = matches[i].Groups[1].Success ? matches[i].Groups[1].Value : matches[i].Groups[2].Value;
        if (body.Length > 0) outp.Add((TitleCaseLabel(label), body));
    }
    return outp.Count > 1 ? outp : null;
}

(string flat, string? json) CleanAbstract(List<(string? Label, string Text)>? sections, string? raw)
{
    // Prefer real sections from PubMed; otherwise try to recover them from a flat abstract.
    if (sections is null || sections.Count == 0)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", null);
        var collapsed = Regex.Replace(raw, @"\s+", " ").Trim();
        sections = SplitFlatAbstract(collapsed);
        if (sections is null) return (collapsed, null);
    }

    var clean = sections
        .Select(s => (Label: s.Label is null ? null : TitleCaseLabel(s.Label),
                      Text: Regex.Replace(s.Text, @"\s+", " ").Trim()))
        .Where(s => s.Text.Length > 0)
        .ToList();
    if (clean.Count == 0) return ("", null);

    var flat = string.Join("\n\n", clean.Select(s => s.Label is null ? s.Text : $"{s.Label}: {s.Text}"));
    var json = new JsonArray();
    foreach (var s in clean)
        json.Add(new JsonObject { ["label"] = s.Label, ["text"] = s.Text });
    return (flat, json.ToJsonString());
}

// STUDY TYPE. Two sources, in priority order:
//   1. PubMed publication types - authoritative, but absent until MEDLINE indexes the paper, which
//      takes weeks. So it can confirm a type, never rule one out.
//   2. The title and abstract - available from day one.
//
// The decisive rule is that EVIDENCE SYNTHESIS IS JUDGED ON THE TITLE ALONE. A meta-analysis of
// randomised trials uses the word "randomised" throughout its abstract; classifying on abstract
// text would therefore label every meta-analysis an RCT. The live example that forced this:
// "Updated Meta-Analysis of Left Atrial Appendage Closure Versus Oral Anticoagulation" was
// previously flagged simply as a Trial.
static (bool rct, bool sr, bool ma) ClassifyStudy(string title, string? abstractText, string? pubTypes)
{
    const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    var t = title ?? "";
    var a = abstractText ?? "";
    var pt = pubTypes ?? "";
    bool PT(string s) => pt.Contains(s, StringComparison.OrdinalIgnoreCase);

    bool ma = PT("Meta-Analysis")     || Regex.IsMatch(t, @"\bmeta[-\s]?analys", IC);
    bool sr = PT("Systematic Review") || Regex.IsMatch(t, @"\bsystematic review\b", IC)
                                     || Regex.IsMatch(t, @"\bsystematic literature review\b", IC);

    bool rct;
    if (ma || sr)
    {
        // A synthesis OF randomised trials is not itself a randomised study, even when PubMed also
        // tags it RCT (it does, for some pooled analyses). Keeping these out of the randomised
        // filter is the whole point of separating the two.
        rct = false;
    }
    else if (PT("Randomized Controlled Trial"))
    {
        rct = true;
    }
    else
    {
        // Title evidence is strong; abstract evidence needs a phrase that only a primary trial
        // report uses about itself. "randomised trials" alone appears in any discussion section.
        rct = Regex.IsMatch(t, @"\brandomi[sz]ed\b|\brandomised\b|\bRCT\b", IC)
           || Regex.IsMatch(a, @"\brandomly assigned\b|\brandomi[sz]ed (?:1:1|2:1|in a|to receive|to )", IC)
           || (Regex.IsMatch(t, @"\btrial\b", IC) && Regex.IsMatch(a, @"\brandomi[sz]ed\b", IC));
    }
    return (rct, sr, ma);
}

static string? ExtractNct(string? s) =>
    s is null ? null
    : System.Text.RegularExpressions.Regex.Matches(s, @"NCT\d{8}")
        .Select(m => m.Value).Distinct().FirstOrDefault();

// ---------------------------------------------------------------------------------------------
// ingestion
// ---------------------------------------------------------------------------------------------
async Task<(int seen, int kept)> IngestJournal(Journal j, int days, Config cfg)
{
    var since = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");
    var url = $"https://api.crossref.org/journals/{j.Issn}/works"
            + $"?filter=from-created-date:{since},type:journal-article"
            + $"&rows=200&select=DOI,title,author,created,abstract,container-title"
            + $"&mailto={Uri.EscapeDataString(contactEmail)}";

    JsonNode? root;
    try { root = JsonNode.Parse(await http.GetStringAsync(url)); }
    catch (Exception ex) { log.LogWarning("crossref {Journal}: {Err}", j.Name, ex.Message); return (0, 0); }

    var items = root?["message"]?["items"]?.AsArray();
    if (items is null) return (0, 0);

    var candidates = new List<(string doi, string title, string? authors, string? published, string? abs)>();
    foreach (var it in items)
    {
        var doi = it?["DOI"]?.GetValue<string>();
        var title = it?["title"]?.AsArray().FirstOrDefault()?.GetValue<string>();
        // Crossref titles can carry JATS markup - a live example rendered as
        // "...Transfer: A Post Hoc Analysis of the <scp>LASTE</scp> Trial". Strip tags and collapse
        // whitespace, or the markup shows verbatim in the list.
        if (title is not null)
            title = Regex.Replace(Regex.Replace(title, "<.*?>", " "), @"\s+", " ").Trim();
        if (doi is null || string.IsNullOrWhiteSpace(title) || IsNoise(title)) continue;

        var authors = it?["author"]?.AsArray()
            .Select(a => $"{a?["given"]?.GetValue<string>()} {a?["family"]?.GetValue<string>()}".Trim())
            .Where(s => s.Length > 0).Take(6).ToArray();
        var authorStr = authors is { Length: > 0 }
            ? string.Join(", ", authors) + (it!["author"]!.AsArray().Count > 6 ? ", et al." : "")
            : null;

        string? pub = null;
        var parts = it?["created"]?["date-parts"]?.AsArray().FirstOrDefault()?.AsArray();
        if (parts is { Count: >= 1 })
        {
            int y = parts[0]!.GetValue<int>();
            int m = parts.Count > 1 ? parts[1]!.GetValue<int>() : 1;
            int d = parts.Count > 2 ? parts[2]!.GetValue<int>() : 1;
            pub = new DateTime(y, m, d).ToString("yyyy-MM-dd");
        }

        // Crossref abstracts arrive as JATS XML fragments; strip tags for a usable fallback.
        var rawAbs = it?["abstract"]?.GetValue<string>();
        if (rawAbs is not null)
            rawAbs = System.Text.RegularExpressions.Regex.Replace(rawAbs, "<.*?>", " ")
                     .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
        if (rawAbs is not null) rawAbs = System.Text.RegularExpressions.Regex.Replace(rawAbs, @"\s+", " ").Trim();

        candidates.Add((doi.ToLowerInvariant(), title.Trim(), authorStr, pub, rawAbs));
    }

    // Ask PubMed about anything still missing a PMID or a parsed conclusion - NOT merely anything
    // missing an abstract. Crossref often supplies a flat abstract, which previously satisfied the
    // check and meant the paper never got PubMed's STRUCTURED abstract - the only place
    // CONCLUSIONS can be parsed from. That capped conclusions at 6 of 68 on the first run.
    var needEnrich = new List<string>();
    using (var c = new SqliteConnection(connStr))
    {
        c.Open();
        foreach (var cand in candidates)
        {
            using var q = c.CreateCommand();
            q.CommandText = "SELECT (pmid IS NOT NULL AND conclusions IS NOT NULL) FROM papers WHERE doi=$d";
            q.Parameters.AddWithValue("$d", cand.doi);
            var done = q.ExecuteScalar();
            if (done is null || Convert.ToInt64(done) == 0) needEnrich.Add(cand.doi);
        }
    }

    var enrich = await EnrichFromPubMed(needEnrich);

    int kept = 0;
    using (var c = new SqliteConnection(connStr))
    {
        c.Open();
        using var tx = c.BeginTransaction();
        foreach (var cand in candidates)
        {
            enrich.TryGetValue(cand.doi, out var pm);
            // PubMed's structured abstract wins; a Crossref one is cleaned the same way so that a
            // paper not yet in PubMed still reads properly rather than as a single run-on line.
            string? absText; string? absJson;
            if (!string.IsNullOrWhiteSpace(pm?.Abstract)) { absText = pm!.Abstract; absJson = pm.AbstractJson; }
            else { var (flatAbs, jsonAbs) = CleanAbstract(null, cand.abs); absText = flatAbs.Length > 0 ? flatAbs : null; absJson = jsonAbs; }
            var (score, isTrial) = Triage(cand.title, absText, cfg);
            // Tier-1 nudge, but ONLY for a paper that already matched the topic at all. Applying it
            // unconditionally let pure off-topic tier-1 papers over the threshold - a paediatric
            // blood-pressure trial and a hearing-aid/MCI study both slipped in on the first run.
            // The bonus is meant to rescue a borderline on-topic paper, not to admit everything in NEJM.
            if (j.Tier == 1 && score > 0) score += 1;
            // SUBSCRIPTION AND FILTERING ARE SEPARATE CONCERNS. With filtering off - the default -
            // everything from a subscribed journal is kept, and the app is simply a reader for the
            // journals you follow. Topic scoring is an opt-in layer on top, for narrowing to a
            // subspecialty. Noise (corrections, replies) is still dropped either way.
            if (cfg.FilterEnabled && score < cfg.MinScore) continue;

            using var up = c.CreateCommand();
            up.Transaction = tx;
            // Never overwrite read_at / starred / ai_summary on re-ingest.
            var (isRct, isSr, isMa) = ClassifyStudy(cand.title, absText, pm?.PubTypes);

            // Is this the same article already stored under a different DOI? The row that carries an
            // abstract is kept as canonical, because the bare duplicate is the useless one.
            var fp = Fingerprint(cand.title, cand.published);
            string? dupeOf = null;
            using (var dq = c.CreateCommand())
            {
                dq.Transaction = tx;
                dq.CommandText = """
                  SELECT doi, abstract IS NOT NULL AND length(abstract)>0
                  FROM papers WHERE fingerprint=$f AND doi<>$doi AND dupe_of IS NULL LIMIT 1
                  """;
                dq.Parameters.AddWithValue("$f", fp);
                dq.Parameters.AddWithValue("$doi", cand.doi);
                using var dr = dq.ExecuteReader();
                if (dr.Read())
                {
                    var otherDoi = dr.GetString(0);
                    var otherHasAbs = dr.GetInt64(1) == 1;
                    var thisHasAbs = !string.IsNullOrWhiteSpace(absText);
                    if (otherHasAbs || !thisHasAbs)
                    {
                        dupeOf = otherDoi;      // the existing row wins
                    }
                    else
                    {
                        // This one has the abstract and the stored one does not: flip them.
                        dr.Close();
                        using var flip = c.CreateCommand();
                        flip.Transaction = tx;
                        flip.CommandText = "UPDATE papers SET dupe_of=$keep WHERE doi=$drop";
                        flip.Parameters.AddWithValue("$keep", cand.doi);
                        flip.Parameters.AddWithValue("$drop", otherDoi);
                        flip.ExecuteNonQuery();
                    }
                }
            }
            up.CommandText = """
            INSERT INTO papers (doi,pmid,title,journal,issn,tier,authors,published,discovered,
                                abstract,abstract_sections,abstract_format,conclusions,pub_types,nct,score,is_trial,is_rct,is_sr,is_ma,
                                fingerprint,dupe_of)
            VALUES ($doi,$pmid,$title,$journal,$issn,$tier,$authors,$published,$now,
                    $abs,$absj,$absf,$conc,$ptypes,$nct,$score,$trial,$rct,$sr,$ma,
                    $fp,$dupe)
            ON CONFLICT(doi) DO UPDATE SET
              pmid        = COALESCE(excluded.pmid, papers.pmid),
              abstract    = COALESCE(NULLIF(excluded.abstract,''), papers.abstract),
              abstract_sections = COALESCE(NULLIF(excluded.abstract_sections,''), papers.abstract_sections),
              abstract_format   = COALESCE(NULLIF(excluded.abstract_format,''), papers.abstract_format),
              conclusions = COALESCE(NULLIF(excluded.conclusions,''), papers.conclusions),
              pub_types   = COALESCE(NULLIF(excluded.pub_types,''), papers.pub_types),
              nct         = COALESCE(excluded.nct, papers.nct),
              score       = excluded.score,
              is_trial    = excluded.is_trial,
              -- Re-classified on every ingest: publication types arrive weeks later, so a paper
              -- that looked like a plain trial can be confirmed, or corrected, once indexed.
              is_rct      = excluded.is_rct,
              is_sr       = excluded.is_sr,
              is_ma       = excluded.is_ma,
              fingerprint = excluded.fingerprint,
              -- never resurrect a row already marked duplicate
              dupe_of     = COALESCE(papers.dupe_of, excluded.dupe_of);
            """;
            up.Parameters.AddWithValue("$doi", cand.doi);
            up.Parameters.AddWithValue("$pmid", (object?)pm?.Pmid ?? DBNull.Value);
            up.Parameters.AddWithValue("$title", cand.title);
            up.Parameters.AddWithValue("$journal", j.Name);
            up.Parameters.AddWithValue("$issn", j.Issn);
            up.Parameters.AddWithValue("$tier", j.Tier);
            up.Parameters.AddWithValue("$authors", (object?)cand.authors ?? DBNull.Value);
            up.Parameters.AddWithValue("$published", (object?)cand.published ?? DBNull.Value);
            up.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            up.Parameters.AddWithValue("$abs", (object?)absText ?? DBNull.Value);
            up.Parameters.AddWithValue("$absj", (object?)absJson ?? DBNull.Value);
            up.Parameters.AddWithValue("$absf",
                absJson is not null ? "sections" : string.IsNullOrWhiteSpace(absText) ? "none" : "plain");
            up.Parameters.AddWithValue("$conc", (object?)pm?.Conclusions ?? DBNull.Value);
            up.Parameters.AddWithValue("$ptypes", (object?)pm?.PubTypes ?? DBNull.Value);
            up.Parameters.AddWithValue("$nct", (object?)(ExtractNct(absText) ?? pm?.Nct) ?? DBNull.Value);
            up.Parameters.AddWithValue("$score", score);
            up.Parameters.AddWithValue("$trial", isTrial ? 1 : 0);
            up.Parameters.AddWithValue("$rct", isRct ? 1 : 0);
            up.Parameters.AddWithValue("$sr", isSr ? 1 : 0);
            up.Parameters.AddWithValue("$ma", isMa ? 1 : 0);
            up.Parameters.AddWithValue("$fp", fp);
            up.Parameters.AddWithValue("$dupe", (object?)dupeOf ?? DBNull.Value);
            up.ExecuteNonQuery();
            kept++;
        }
        using (var lg = c.CreateCommand())
        {
            lg.Transaction = tx;
            lg.CommandText = "INSERT INTO ingest_log (ts,journal,seen,kept) VALUES ($t,$j,$s,$k)";
            lg.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            lg.Parameters.AddWithValue("$j", j.Name);
            lg.Parameters.AddWithValue("$s", candidates.Count);
            lg.Parameters.AddWithValue("$k", kept);
            lg.ExecuteNonQuery();
        }
        tx.Commit();
    }
    return (candidates.Count, kept);
}

// DOI -> PMID in bulk via the NCBI ID Converter (200 per call), then one efetch per 200 PMIDs.
// Doing this per-paper is what trips the 3/sec limit.
async Task<Dictionary<string, PmRecord>> EnrichFromPubMed(List<string> dois)
{
    var result = new Dictionary<string, PmRecord>(StringComparer.OrdinalIgnoreCase);
    if (dois.Count == 0) return result;

    // DOI -> PMID via esearch, NOT the /pmc/utils/idconv ID Converter.
    // idconv is PMC-centric: for papers that are in PubMed but not in PMC it answers
    // "Identifier not found in PMC" and returns no pmid. Measured 2026-10-03 on six current
    // papers - idconv resolved 1 of 6, esearch by [DOI] resolved them correctly. Using idconv
    // capped enrichment at 4 of 50 papers.
    var pmids = new List<string>();
    foreach (var chunk in dois.Chunk(20))
    {
        var term = string.Join(" OR ", chunk.Select(d => $"\"{d}\"[DOI]"));
        var url = "https://eutils.ncbi.nlm.nih.gov/entrez/eutils/esearch.fcgi"
                + "?db=pubmed&retmode=json&retmax=200&term=" + Uri.EscapeDataString(term);
        try
        {
            var node = JsonNode.Parse(await NcbiGet(url));
            foreach (var id in node?["esearchresult"]?["idlist"]?.AsArray() ?? new JsonArray())
            {
                var s = AsText(id);
                if (s is not null) pmids.Add(s);
            }
        }
        catch (Exception ex) { log.LogWarning("esearch: {Err}", ex.Message); }
    }
    if (pmids.Count == 0) return result;

    // esearch returns PMIDs without saying which DOI each came from, so the DOI is read back out
    // of each efetch record's ArticleIdList and used to key the result.
    foreach (var chunk in pmids.Distinct().Chunk(180))
    {
        var url = "https://eutils.ncbi.nlm.nih.gov/entrez/eutils/efetch.fcgi"
                + "?db=pubmed&retmode=xml&id=" + string.Join(",", chunk);
        try
        {
            var xml = XDocument.Parse(await NcbiGet(url));
            foreach (var art in xml.Descendants("PubmedArticle"))
            {
                var pmid = art.Descendants("PMID").FirstOrDefault()?.Value;
                // Map back by the DOI carried in the record itself.
                var doi = art.Descendants("ArticleId")
                             .FirstOrDefault(a => (string?)a.Attribute("IdType") == "doi")?.Value
                             ?.Trim().ToLowerInvariant();
                if (pmid is null || doi is null) continue;

                // Structured abstracts carry a Label per section. Keep the labels - they are what
                // makes CONCLUSIONS findable, and they read well in the UI.
                var texts = art.Descendants("AbstractText").ToList();
                var parts = new List<(string? Label, string Text)>();
                string? conclusions = null;
                foreach (var tx in texts)
                {
                    var label = tx.Attribute("Label")?.Value ?? tx.Attribute("NlmCategory")?.Value;
                    var body = string.Concat(tx.Nodes().Select(n => n is XText t ? t.Value : ((XElement)n).Value)).Trim();
                    if (body.Length == 0) continue;
                    parts.Add((label, body));
                    if (label is not null &&
                        (label.Contains("CONCLUSION", StringComparison.OrdinalIgnoreCase) ||
                         label.Contains("INTERPRETATION", StringComparison.OrdinalIgnoreCase)))
                        conclusions = body;
                }
                var (absText, absJson) = CleanAbstract(parts, null);
                // Unstructured abstract: fall back to the last two sentences, which for a clinical
                // abstract is almost always the conclusion.
                if (conclusions is null && absText.Length > 0 && texts.Count == 1)
                {
                    var sentences = Regex.Split(absText, @"(?<=[.!?])\s+");
                    if (sentences.Length >= 2) conclusions = string.Join(" ", sentences.TakeLast(2));
                }

                var ptypes = string.Join(", ", art.Descendants("PublicationType").Select(p => p.Value).Distinct());
                var nct = art.Descendants("AccessionNumber").Select(a => a.Value)
                             .FirstOrDefault(v => v.StartsWith("NCT", StringComparison.OrdinalIgnoreCase));

                result[doi] = new PmRecord(pmid, absText.Length > 0 ? absText : null, absJson, conclusions, ptypes, nct);
            }
        }
        catch (Exception ex) { log.LogWarning("efetch: {Err}", ex.Message); }
    }
    return result;
}

// ---------------------------------------------------------------------------------------------
// offline copies - OPEN ACCESS ONLY
// ---------------------------------------------------------------------------------------------
// Legal full text comes from Unpaywall, which indexes author manuscripts, publisher OA copies and
// repository deposits. If Unpaywall says a paper is not open access there is NO fallback: the
// system does not attempt to obtain paywalled full text by any route. That is a design boundary,
// not a missing feature - the DOI link hands those papers to the reader's own entitlement.
static string PdfName(string doi) =>
    string.Join("_", doi.Split(Path.GetInvalidFileNameChars())).Replace('/', '_') + ".pdf";

async Task<(string? url, string? status)> ResolveOa(string doi)
{
    var u = $"https://api.unpaywall.org/v2/{Uri.EscapeDataString(doi)}?email={Uri.EscapeDataString(contactEmail)}";
    try
    {
        var node = JsonNode.Parse(await http.GetStringAsync(u));
        var status = AsText(node?["oa_status"]) ?? "unknown";
        if (node?["is_oa"]?.GetValue<bool>() != true) return (null, status);
        // url_for_pdf is a direct PDF; url_for_landing_page is not, so only the former is useful.
        var best = node?["best_oa_location"];
        var pdf = AsText(best?["url_for_pdf"]);
        if (pdf is null)
            foreach (var loc in node?["oa_locations"]?.AsArray() ?? new JsonArray())
            {
                pdf = AsText(loc?["url_for_pdf"]);
                if (pdf is not null) break;
            }
        return (pdf, status);
    }
    catch (Exception ex) { log.LogWarning("unpaywall {Doi}: {Err}", doi, ex.Message); return (null, null); }
}

// Second legitimate source. Unpaywall's "green" status often means only that a repository holds a
// record - for current clinical journals that record is frequently just the PubMed abstract page,
// with url_for_pdf null. Europe PMC, by contrast, serves the actual full text for articles in the
// open-access subset. Saved as XML and rendered as readable HTML, which also reads better on a
// phone than a two-column PDF.
async Task<(string? url, string kind)> ResolveEuropePmc(string doi)
{
    var q = $"https://www.ebi.ac.uk/europepmc/webservices/rest/search?query=DOI:%22{Uri.EscapeDataString(doi)}%22"
          + "&resultType=core&format=json&pageSize=1";
    try
    {
        var node = JsonNode.Parse(await http.GetStringAsync(q));
        var hit = node?["resultList"]?["result"]?.AsArray()?.FirstOrDefault();
        if (hit is null) return (null, "");
        var isOa = AsText(hit["isOpenAccess"]);
        var pmcid = AsText(hit["pmcid"]);
        if (!string.Equals(isOa, "Y", StringComparison.OrdinalIgnoreCase) || pmcid is null) return (null, "");
        return ($"https://www.ebi.ac.uk/europepmc/webservices/rest/{pmcid}/fullTextXML", "xml");
    }
    catch (Exception ex) { log.LogWarning("europepmc {Doi}: {Err}", doi, ex.Message); return (null, ""); }
}

// JATS full text -> plain readable HTML. Only headings and paragraphs are kept: this is for
// reading on a phone, not for reproducing the publisher's typesetting.
static string JatsToHtml(string xml, string title)
{
    string body;
    try
    {
        var doc = XDocument.Parse(xml);
        var sb = new StringBuilder();
        var root = doc.Descendants("body").FirstOrDefault() ?? doc.Root;
        foreach (var el in root?.Descendants() ?? Enumerable.Empty<XElement>())
        {
            if (el.Name.LocalName == "title")
                sb.Append("<h2>").Append(WebUtility.HtmlEncode(el.Value.Trim())).Append("</h2>\n");
            else if (el.Name.LocalName == "p" && el.Ancestors().All(a => a.Name.LocalName != "table-wrap"))
                sb.Append("<p>").Append(WebUtility.HtmlEncode(el.Value.Trim())).Append("</p>\n");
        }
        body = sb.ToString();
    }
    catch { body = "<p>Could not parse the full text.</p>"; }
    if (body.Length < 200) body = "<p>The open-access record contained no readable body text.</p>";
    // $$ raw string: with a single $, "{{" does NOT escape a brace - braces are literal only when
    // the interpolation delimiter is longer than one. With $$ the delimiter is {{ }}, so the CSS
    // braces below need no escaping at all.
    return $$"""
      <!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
      <title>{{WebUtility.HtmlEncode(title)}}</title>
      <style>body{max-width:44em;margin:0 auto;padding:18px;font:16px/1.6 -apple-system,Segoe UI,Roboto,sans-serif;
      color:#17181b;background:#fff}h1{font-size:20px}h2{font-size:15px;margin-top:1.6em;color:#444}
      @media(prefers-color-scheme:dark){body{background:#14161a;color:#e3e5ea}h2{color:#9aa3af} }</style>
      <h1>{{WebUtility.HtmlEncode(title)}}</h1>
      {{body}}
      """;
}

async Task<(bool ok, string detail)> DownloadPdf(string doi)
{
    string? url = null, haveFile = null;
    using (var c = new SqliteConnection(connStr))
    {
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT oa_url, pdf_file FROM papers WHERE doi=$d";
        q.Parameters.AddWithValue("$d", doi);
        using var r = q.ExecuteReader();
        if (!r.Read()) return (false, "unknown paper");
        url = r.IsDBNull(0) ? null : r.GetString(0);
        haveFile = r.IsDBNull(1) ? null : r.GetString(1);
    }
    if (haveFile is not null && File.Exists(Path.Combine(pdfDir, haveFile))) return (true, "already saved");

    var kind = "pdf";
    if (url is null)
    {
        var (u2, st) = await ResolveOa(doi);
        url = u2;
        if (url is null)
        {
            var (u3, k3) = await ResolveEuropePmc(doi);
            if (u3 is not null) { url = u3; kind = k3; }
        }
        using var c = new SqliteConnection(connStr); c.Open();
        using var up = c.CreateCommand();
        up.CommandText = "UPDATE papers SET oa_url=$u, oa_status=$s WHERE doi=$d";
        up.Parameters.AddWithValue("$u", (object?)url ?? DBNull.Value);
        up.Parameters.AddWithValue("$s", (object?)st ?? DBNull.Value);
        up.Parameters.AddWithValue("$d", doi);
        up.ExecuteNonQuery();
    }
    else if (url.Contains("europepmc", StringComparison.OrdinalIgnoreCase)) kind = "xml";
    if (url is null) return (false, "no open-access full text");

    try
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
        var len = resp.Content.Headers.ContentLength ?? 0;
        if (len > 80 * 1024 * 1024) return (false, "file larger than 80 MB");

        string name, path;
        if (kind == "xml")
        {
            var xml = await resp.Content.ReadAsStringAsync();
            string title;
            using (var c2 = new SqliteConnection(connStr))
            {
                c2.Open();
                using var tq = c2.CreateCommand();
                tq.CommandText = "SELECT title FROM papers WHERE doi=$d";
                tq.Parameters.AddWithValue("$d", doi);
                title = tq.ExecuteScalar() as string ?? doi;
            }
            name = PdfName(doi).Replace(".pdf", ".html");
            path = Path.Combine(pdfDir, name);
            await File.WriteAllTextAsync(path, JatsToHtml(xml, title));
        }
        else
        {
            // Publishers frequently answer a PDF URL with an HTML interstitial or a login page.
            // Storing that as a .pdf would produce a file that opens to nothing, so check the type.
            if (!ct.Contains("pdf", StringComparison.OrdinalIgnoreCase))
                return (false, $"not a PDF (server sent {(ct.Length > 0 ? ct : "no content type")})");
            name = PdfName(doi);
            path = Path.Combine(pdfDir, name);
            await using var fs = File.Create(path);
            await resp.Content.CopyToAsync(fs);
        }

        var size = new FileInfo(path).Length;
        if (size < 1024) { File.Delete(path); return (false, "file was empty"); }
        _ = kind;

        using var c = new SqliteConnection(connStr); c.Open();
        using var up = c.CreateCommand();
        up.CommandText = "UPDATE papers SET pdf_file=$f, pdf_error=NULL WHERE doi=$d";
        up.Parameters.AddWithValue("$f", name);
        up.Parameters.AddWithValue("$d", doi);
        up.ExecuteNonQuery();
        return (true, $"{Math.Round(size / 1024.0)} KB");
    }
    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
    {
        // Measured 2026-10-03 on Wiley and neurology.org: a legitimately open-access PDF URL
        // returns 403 to any non-browser client, with or without a User-Agent or Accept header.
        // This is publisher bot-management, not a paywall. The system does NOT disguise itself as
        // a browser to get around it - the DOI link still opens the paper normally.
        return (false, "publisher blocked automated download - open the DOI link instead");
    }
    catch (Exception ex) { return (false, ex.Message); }
}

// ---------------------------------------------------------------------------------------------
// api
// ---------------------------------------------------------------------------------------------
app.MapGet("/api/papers", (string? filter, string? q, string? types, string? state,
                           string? sort, string? dir, int? limit) =>
{
    var where = filter switch
    {
        "inbox"   => "1=1",        // the inbox is EVERY paper; read state is a separate toggle
        "unread"  => "read_at IS NULL",
        "starred" => "starred = 1",
        "later"   => "read_later = 1",
        "offline" => "pdf_file IS NOT NULL",
        "tier1"   => "tier = 1",
        _         => "1=1"
    };

    // Study-type filters OR together (asking for randomised AND meta-analysis at once would return
    // nothing, since the classifier makes them mutually exclusive) and AND with the active view.
    var want = (types ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var clauses = new List<string>();
    if (want.Contains("rct"))  clauses.Add("is_rct = 1");
    if (want.Contains("srma")) clauses.Add("(is_sr = 1 OR is_ma = 1)");
    if (want.Contains("sr"))   clauses.Add("is_sr = 1");
    if (want.Contains("ma"))   clauses.Add("is_ma = 1");
    var typeSql = clauses.Count > 0 ? $"AND ({string.Join(" OR ", clauses)})" : "";

    // Read state is its own filter, not a view. Reading a paper must never make it vanish from the
    // list - losing a paper you just read is far more jarring than scrolling past it.
    var want2 = (state ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var stateSql = (want2.Contains("unread"), want2.Contains("read")) switch
    {
        (true, false) => "AND read_at IS NULL",
        (false, true) => "AND read_at IS NOT NULL",
        _             => ""     // neither or both selected = everything
    };

    // Sort is chosen from a fixed map, never interpolated from the request, so no caller-supplied
    // text ever reaches the ORDER BY clause.
    var descending = !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
    var dirSql = descending ? "DESC" : "ASC";
    var orderSql = sort switch
    {
        // Explicit single-key sorts. The secondary key is always the date, so ties are stable and
        // the newest of an equal group comes first.
        "published" => $"COALESCE(published,discovered) {dirSql}, title ASC",
        "added"     => $"discovered {dirSql}, COALESCE(published,discovered) DESC",
        "score"     => $"score {dirSql}, COALESCE(published,discovered) DESC",
        "journal"   => $"journal COLLATE NOCASE {dirSql}, COALESCE(published,discovered) DESC",
        "title"     => $"title COLLATE NOCASE {dirSql}",
        "tier"      => $"tier {dirSql}, COALESCE(published,discovered) DESC",
        // Default: the triage order - unread first, then major journals, then study type, then date.
        _           => $"(read_at IS NULL) DESC, tier ASC, is_rct DESC, (is_sr OR is_ma) DESC, "
                     + $"COALESCE(published,discovered) {dirSql}"
    };

    var sql = $"""
      SELECT doi,pmid,title,journal,tier,authors,published,discovered,abstract,abstract_sections,
             abstract_format,conclusions,pub_types,nct,score,is_trial,is_rct,is_sr,is_ma,read_at,starred,read_later,
             oa_status,pdf_file,pdf_error,ai_summary,ai_requested
      FROM papers WHERE dupe_of IS NULL AND {where} {typeSql} {stateSql}
      {(string.IsNullOrWhiteSpace(q) ? "" : "AND (lower(title) LIKE $q OR lower(abstract) LIKE $q)")}
      ORDER BY {orderSql}
      LIMIT $lim
      """;
    using var c = new SqliteConnection(connStr); c.Open();
    using var cmd = c.CreateCommand(); cmd.CommandText = sql;
    if (!string.IsNullOrWhiteSpace(q)) cmd.Parameters.AddWithValue("$q", "%" + q.ToLowerInvariant() + "%");
    cmd.Parameters.AddWithValue("$lim", Math.Clamp(limit ?? 300, 1, 1000));
    var rows = new List<Dictionary<string, object?>>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        var d = new Dictionary<string, object?>();
        for (int i = 0; i < r.FieldCount; i++) d[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
        rows.Add(d);
    }
    return Results.Json(rows);
});

app.MapGet("/api/stats", () =>
{
    using var c = new SqliteConnection(connStr); c.Open();
    object? One(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }
    return Results.Json(new
    {
        total    = Convert.ToInt64(One("SELECT COUNT(*) FROM papers") ?? 0),
        unread   = Convert.ToInt64(One("SELECT COUNT(*) FROM papers WHERE read_at IS NULL") ?? 0),
        // is_trial is the pre-1.0 loose flag. It is still computed and stored as a broad signal,
        // but nothing filters on it any more - study type is expressed by is_rct / is_sr / is_ma.
        trials   = Convert.ToInt64(One("SELECT COUNT(*) FROM papers WHERE is_trial=1") ?? 0),
        rct      = Convert.ToInt64(One("SELECT COUNT(*) FROM papers WHERE is_rct=1") ?? 0),
        srma     = Convert.ToInt64(One("SELECT COUNT(*) FROM papers WHERE is_sr=1 OR is_ma=1") ?? 0),
        starred  = Convert.ToInt64(One("SELECT COUNT(*) FROM papers WHERE starred=1") ?? 0),
        later    = Convert.ToInt64(One("SELECT COUNT(*) FROM papers WHERE read_later=1") ?? 0),
        offline  = Convert.ToInt64(One("SELECT COUNT(*) FROM papers WHERE pdf_file IS NOT NULL") ?? 0),
        lastIngest = One("SELECT MAX(ts) FROM ingest_log") as string
    });
});

static IResult Flip(string connStr, string doi, string sql)
{
    using var c = new SqliteConnection(connStr); c.Open();
    using var cmd = c.CreateCommand(); cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("$doi", doi.ToLowerInvariant());
    return cmd.ExecuteNonQuery() > 0 ? Results.Ok() : Results.NotFound();
}

app.MapPost("/api/papers/{*doi}", async (string doi, string? action) =>
{
    if (action == "save")
    {
        var (ok, detail) = await DownloadPdf(doi.ToLowerInvariant());
        if (!ok)
        {
            using var c = new SqliteConnection(connStr); c.Open();
            using var up = c.CreateCommand();
            up.CommandText = "UPDATE papers SET pdf_error=$e WHERE doi=$d";
            up.Parameters.AddWithValue("$e", detail);
            up.Parameters.AddWithValue("$d", doi.ToLowerInvariant());
            up.ExecuteNonQuery();
        }
        return Results.Json(new { ok, detail });
    }
    if (action == "unsave")
    {
        // Detach and delete the stored file - for a wrong attachment, or to reclaim disk.
        using var c = new SqliteConnection(connStr); c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT pdf_file FROM papers WHERE doi=$d";
        q.Parameters.AddWithValue("$d", doi.ToLowerInvariant());
        if (q.ExecuteScalar() is string f)
        {
            var p = Path.Combine(pdfDir, f);
            if (File.Exists(p)) File.Delete(p);
        }
        using var up = c.CreateCommand();
        up.CommandText = "UPDATE papers SET pdf_file=NULL, pdf_error=NULL WHERE doi=$d";
        up.Parameters.AddWithValue("$d", doi.ToLowerInvariant());
        return up.ExecuteNonQuery() > 0 ? Results.Json(new { ok = true }) : Results.NotFound();
    }
    // Downloading is on request only. The one exception is opt-in: if the user has asked for it,
    // marking a paper favourite or read-later also tries to fetch an open-access copy, because
    // those are exactly the papers they intend to read away from a desk.
    if (action is "star" or "later")
    {
        var col = action == "star" ? "starred" : "read_later";
        var res = Flip(connStr, doi, $"UPDATE papers SET {col}=1-COALESCE({col},0) WHERE doi=$doi");
        bool nowSet, hasPdf;
        using (var c = new SqliteConnection(connStr))
        {
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = $"SELECT COALESCE({col},0), pdf_file IS NOT NULL FROM papers WHERE doi=$d";
            q.Parameters.AddWithValue("$d", doi.ToLowerInvariant());
            using var r = q.ExecuteReader();
            if (!r.Read()) return res;
            nowSet = r.GetInt64(0) == 1;
            hasPdf = r.GetInt64(1) == 1;
        }
        if (nowSet && !hasPdf)
        {
            var cfgNow = JsonNode.Parse(await File.ReadAllTextAsync(configPath))?["autoSave"];
            var key = action == "star" ? "onFavourite" : "onReadLater";
            if (cfgNow?[key]?.GetValue<bool>() == true)
                // Fire and forget: the click must not wait on a publisher's server.
                _ = Task.Run(async () =>
                {
                    var (good, detail) = await DownloadPdf(doi.ToLowerInvariant());
                    if (!good)
                    {
                        using var c2 = new SqliteConnection(connStr); c2.Open();
                        using var up = c2.CreateCommand();
                        up.CommandText = "UPDATE papers SET pdf_error=$e WHERE doi=$d";
                        up.Parameters.AddWithValue("$e", detail);
                        up.Parameters.AddWithValue("$d", doi.ToLowerInvariant());
                        up.ExecuteNonQuery();
                    }
                });
        }
        return res;
    }

    return action switch
    {
        "read"     => Flip(connStr, doi, "UPDATE papers SET read_at=datetime('now') WHERE doi=$doi"),
        "unread"   => Flip(connStr, doi, "UPDATE papers SET read_at=NULL WHERE doi=$doi"),
    // Queued, not synchronous: the Claude Code CLI runs under the interactive user's credentials,
    // which this service, running under its own low-privilege account, cannot reach. The scheduled
    // task scripts\currents-summarize.ps1 picks these up and posts the result back.
        "summarize" => Flip(connStr, doi, "UPDATE papers SET ai_requested=1 WHERE doi=$doi"),
        _           => Results.BadRequest(new { error = "action must be read|unread|star|later|save|summarize" })
    };
});

// Serve a saved PDF. Separate route prefix because /api/papers/{*doi} is a catch-all and would
// otherwise swallow a trailing /pdf segment.
app.MapGet("/api/pdf/{*doi}", (string doi) =>
{
    using var c = new SqliteConnection(connStr); c.Open();
    using var q = c.CreateCommand();
    q.CommandText = "SELECT pdf_file FROM papers WHERE doi=$d";
    q.Parameters.AddWithValue("$d", doi.ToLowerInvariant());
    var name = q.ExecuteScalar() as string;
    if (name is null) return Results.NotFound();
    var path = Path.Combine(pdfDir, name);
    if (!File.Exists(path)) return Results.NotFound();
    // inline so the browser renders it in the preview pane rather than downloading it
    var mime = name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "text/html" : "application/pdf";
    return Results.File(path, mime, enableRangeProcessing: true);
});

// Attach a PDF the user downloaded themselves.
//
// This is the supported route for PAYWALLED papers, and it exists instead of storing institutional
// credentials and fetching on the user's behalf. Replaying a library login from a script is what
// publishers' systematic-download detection is built to catch, and the standard consequence is
// suspension of the whole institution's access, not just one account. So the human does the
// authenticated download in their browser, and the server only stores the result - after which it
// is readable from every device on the tailnet, which was the actual goal.
app.MapPost("/api/upload/{*doi}", async (string doi, HttpRequest req) =>
{
    doi = doi.ToLowerInvariant();
    if (!req.HasFormContentType) return Results.BadRequest(new { error = "expected a file upload" });
    var form = await req.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "no file received" });
    if (file.Length > 80 * 1024 * 1024) return Results.BadRequest(new { error = "file larger than 80 MB" });

    using (var c = new SqliteConnection(connStr))
    {
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM papers WHERE doi=$d";
        q.Parameters.AddWithValue("$d", doi);
        if (Convert.ToInt64(q.ExecuteScalar() ?? 0L) == 0) return Results.NotFound(new { error = "unknown paper" });
    }

    // Check the magic bytes rather than trusting the extension: a saved login page named .pdf
    // would otherwise be stored as though it were the article.
    await using var src = file.OpenReadStream();
    var head = new byte[5];
    var read = await src.ReadAtLeastAsync(head, 5, throwOnEndOfStream: false);
    if (read < 5 || Encoding.ASCII.GetString(head, 0, 4) != "%PDF")
        return Results.BadRequest(new { error = "that file is not a PDF" });

    var name = PdfName(doi);
    var path = Path.Combine(pdfDir, name);
    await using (var fs = File.Create(path))
    {
        await fs.WriteAsync(head.AsMemory(0, read));
        await src.CopyToAsync(fs);
    }

    using (var c = new SqliteConnection(connStr))
    {
        c.Open();
        using var up = c.CreateCommand();
        up.CommandText = "UPDATE papers SET pdf_file=$f, pdf_error=NULL WHERE doi=$d";
        up.Parameters.AddWithValue("$f", name);
        up.Parameters.AddWithValue("$d", doi);
        up.ExecuteNonQuery();
    }
    var kb = Math.Round(new FileInfo(path).Length / 1024.0);
    log.LogInformation("attached PDF for {Doi} ({Kb} KB)", doi, kb);
    return Results.Json(new { ok = true, detail = $"{kb} KB attached" });
});

// One-off pass over rows stored before fingerprinting existed. Groups by fingerprint and keeps the
// row with an abstract (falling back to the earliest DOI) as canonical.
app.MapPost("/api/dedupe", () =>
{
    using var c = new SqliteConnection(connStr); c.Open();
    using (var fill = c.CreateCommand())
    {
        // Recompute for EVERY row, not just the empty ones: the fingerprint rule itself can change,
        // and a stale key silently matches nothing while looking like it worked.
        fill.CommandText = "SELECT doi,title,authors,published FROM papers";
        var rows = new List<(string Doi, string Fp)>();
        using (var r = fill.ExecuteReader())
            while (r.Read())
                rows.Add((r.GetString(0), Fingerprint(r.GetString(1), r.IsDBNull(3) ? null : r.GetString(3))));
        using var tx = c.BeginTransaction();
        foreach (var (doi, fp) in rows)
        {
            using var up = c.CreateCommand();
            up.Transaction = tx;
            up.CommandText = "UPDATE papers SET fingerprint=$f WHERE doi=$d";
            up.Parameters.AddWithValue("$f", fp);
            up.Parameters.AddWithValue("$d", doi);
            up.ExecuteNonQuery();
        }
        tx.Commit();
    }

    using var mark = c.CreateCommand();
    mark.CommandText = """
      UPDATE papers SET dupe_of = (
        SELECT k.doi FROM papers k
        WHERE k.fingerprint = papers.fingerprint AND k.doi <> papers.doi AND k.dupe_of IS NULL
        ORDER BY (k.abstract IS NOT NULL AND length(k.abstract)>0) DESC, k.doi ASC LIMIT 1)
      WHERE dupe_of IS NULL
        AND EXISTS (
          SELECT 1 FROM papers o
          WHERE o.fingerprint = papers.fingerprint AND o.doi <> papers.doi AND o.dupe_of IS NULL
            AND ( (o.abstract IS NOT NULL AND length(o.abstract)>0)
                   > (papers.abstract IS NOT NULL AND length(papers.abstract)>0)
                  OR ( ((o.abstract IS NOT NULL AND length(o.abstract)>0)
                        = (papers.abstract IS NOT NULL AND length(papers.abstract)>0))
                       AND o.doi < papers.doi ) ) );
      """;
    var n = mark.ExecuteNonQuery();
    using var left = c.CreateCommand();
    left.CommandText = "SELECT COUNT(*) FROM papers WHERE dupe_of IS NOT NULL";
    return Results.Json(new { marked = n, totalDuplicates = Convert.ToInt64(left.ExecuteScalar() ?? 0L) });
});

// --- journal catalogue --------------------------------------------------------------------------
// The menu of journals offered in settings. Generated by build-catalogue.ps1, which resolves every
// ISSN from Crossref and proves it returns recent articles, so a subscribed journal is never a
// silent dead end. Subscribing copies the entry into config; this file is read-only.
app.MapGet("/api/catalogue", () =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "catalogue.json");
    if (!File.Exists(path)) return Results.Json(new { journals = Array.Empty<object>() });
    return Results.Text(File.ReadAllText(path), "application/json");
});

// Specialty presets: one choice sets the journals AND the vocabulary, so a clinician gets a feed
// aimed at their field without tuning anything first. A starting point, not a lock-in - everything
// a preset sets stays editable afterwards.
app.MapGet("/api/profiles", () =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "profiles.json");
    if (!File.Exists(path)) return Results.Json(new { profiles = Array.Empty<object>() });
    return Results.Text(File.ReadAllText(path), "application/json");
});

app.MapPost("/api/profiles/{id}", (string id) =>
{
    var pPath = Path.Combine(AppContext.BaseDirectory, "profiles.json");
    var cPath = Path.Combine(AppContext.BaseDirectory, "catalogue.json");
    if (!File.Exists(pPath)) return Results.NotFound(new { error = "no profiles installed" });

    var prof = JsonNode.Parse(File.ReadAllText(pPath))?["profiles"]?.AsArray()
                 .FirstOrDefault(p => AsText(p?["id"]) == id);
    if (prof is null) return Results.NotFound(new { error = $"no profile '{id}'" });

    // Journal names come from the catalogue so the saved config stays readable, not a list of numbers.
    var cat = File.Exists(cPath) ? JsonNode.Parse(File.ReadAllText(cPath))?["journals"]?.AsArray() : null;
    var wanted = (prof["issns"]?.AsArray() ?? new JsonArray())
                    .Select(AsText).Where(s => s is not null).Distinct().ToList();

    var cfg = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
    var journals = new JsonArray();
    foreach (var issn in wanted)
    {
        var hit = cat?.FirstOrDefault(c => AsText(c?["issn"]) == issn);
        journals.Add(new JsonObject
        {
            ["issn"] = issn,
            ["name"] = AsText(hit?["name"]) ?? issn,
            ["tier"] = hit?["tier"]?.GetValue<int>() ?? 2
        });
    }
    cfg["journals"] = journals;
    cfg["topicTerms"] = prof["topicTerms"]?.DeepClone() ?? new JsonArray();
    cfg["filtering"] = new JsonObject { ["enabled"] = prof["filtering"]?.GetValue<bool>() ?? false };
    cfg["activeProfile"] = id;

    // The previous setup is kept: applying a preset replaces a hand-tuned list wholesale.
    var backup = Path.Combine(dataDir, $"config.{DateTime.Now:yyyyMMdd-HHmmss}.bak.json");
    File.Copy(configPath, backup, true);
    File.WriteAllText(configPath, cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    log.LogInformation("applied profile {Id}: {N} journals", id, journals.Count);
    return Results.Json(new { ok = true, journals = journals.Count, backup = Path.GetFileName(backup) });
});

// --- secrets ------------------------------------------------------------------------------------
// Kept OUT of config.json on purpose. config.json holds journals, keywords and the proxy - things a
// user might reasonably paste into an issue or commit as an example. A key must never ride along
// with that, so it lives in its own file which .gitignore excludes and the installer ACLs.
var secretsPath = Path.Combine(dataDir, "secrets.json");

app.MapGet("/api/secrets", () =>
{
    // Never returns the value. The UI only needs to know whether one is set.
    var set = false;
    try
    {
        if (File.Exists(secretsPath))
            set = !string.IsNullOrWhiteSpace(AsText(JsonNode.Parse(File.ReadAllText(secretsPath))?["anthropicApiKey"]));
    }
    catch { }
    return Results.Json(new { anthropicApiKeySet = set });
});

app.MapPut("/api/secrets", async (HttpRequest req) =>
{
    using var sr = new StreamReader(req.Body);
    var body = JsonNode.Parse(await sr.ReadToEndAsync());
    var key = AsText(body?["anthropicApiKey"]);
    var obj = new JsonObject();
    if (!string.IsNullOrWhiteSpace(key)) obj["anthropicApiKey"] = key;
    await File.WriteAllTextAsync(secretsPath, obj.ToJsonString());
    log.LogInformation("secrets updated ({State})", string.IsNullOrWhiteSpace(key) ? "cleared" : "key set");
    return Results.Json(new { ok = true });
});

// Re-run the abstract cleanup over everything already stored. New papers are cleaned on arrival;
// this is for the back catalogue, and for after the section-label list is extended.
app.MapPost("/api/reclean", () =>
{
    var rows = new List<(string Doi, string Abs)>();
    using (var c = new SqliteConnection(connStr))
    {
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT doi, abstract FROM papers WHERE abstract IS NOT NULL AND length(abstract)>0";
        using var r = q.ExecuteReader();
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
    }
    int changed = 0;
    using (var c = new SqliteConnection(connStr))
    {
        c.Open();
        using var tx = c.BeginTransaction();
        foreach (var (doi, abs) in rows)
        {
            var (flat, json) = CleanAbstract(null, abs);
            if (flat.Length == 0) continue;
            using var up = c.CreateCommand();
            up.Transaction = tx;
            up.CommandText = "UPDATE papers SET abstract=$a, abstract_sections=$j, abstract_format=$f WHERE doi=$d";
            up.Parameters.AddWithValue("$a", flat);
            up.Parameters.AddWithValue("$j", (object?)json ?? DBNull.Value);
            up.Parameters.AddWithValue("$f", json is not null ? "sections" : "plain");
            up.Parameters.AddWithValue("$d", doi);
            up.ExecuteNonQuery();
            if (json is not null) changed++;
        }
        tx.Commit();
    }
    return Results.Json(new { scanned = rows.Count, structured = changed });
});

// --- highlights ---------------------------------------------------------------------------------
app.MapGet("/api/highlights/{*doi}", (string doi) =>
{
    using var c = new SqliteConnection(connStr); c.Open();
    using var q = c.CreateCommand();
    q.CommandText = "SELECT id,page,rects,text,color,note,created FROM highlights WHERE doi=$d ORDER BY page, id";
    q.Parameters.AddWithValue("$d", doi.ToLowerInvariant());
    var rows = new List<object>();
    using var r = q.ExecuteReader();
    while (r.Read())
        rows.Add(new
        {
            id = r.GetInt64(0),
            page = r.GetInt32(1),
            rects = JsonNode.Parse(r.GetString(2)),
            text = r.IsDBNull(3) ? null : r.GetString(3),
            color = r.IsDBNull(4) ? "yellow" : r.GetString(4),
            note = r.IsDBNull(5) ? null : r.GetString(5),
            created = r.GetString(6)
        });
    return Results.Json(rows);
});

app.MapPost("/api/highlights/{*doi}", async (string doi, long? del, HttpRequest req) =>
{
    doi = doi.ToLowerInvariant();
    using var c = new SqliteConnection(connStr); c.Open();

    if (del is not null)
    {
        using var d = c.CreateCommand();
        d.CommandText = "DELETE FROM highlights WHERE id=$i AND doi=$d";
        d.Parameters.AddWithValue("$i", del.Value);
        d.Parameters.AddWithValue("$d", doi);
        return d.ExecuteNonQuery() > 0 ? Results.Ok() : Results.NotFound();
    }

    using var sr = new StreamReader(req.Body);
    var body = JsonNode.Parse(await sr.ReadToEndAsync());
    var rects = body?["rects"];
    if (rects is not JsonArray arr || arr.Count == 0) return Results.BadRequest(new { error = "rects required" });

    using var ins = c.CreateCommand();
    ins.CommandText = """
      INSERT INTO highlights (doi,page,rects,text,color,note,created)
      VALUES ($d,$p,$r,$t,$c,$n,datetime('now'));
      SELECT last_insert_rowid();
      """;
    ins.Parameters.AddWithValue("$d", doi);
    ins.Parameters.AddWithValue("$p", body?["page"]?.GetValue<int>() ?? 1);
    ins.Parameters.AddWithValue("$r", rects.ToJsonString());
    ins.Parameters.AddWithValue("$t", (object?)AsText(body?["text"]) ?? DBNull.Value);
    ins.Parameters.AddWithValue("$c", AsText(body?["color"]) ?? "yellow");
    ins.Parameters.AddWithValue("$n", (object?)AsText(body?["note"]) ?? DBNull.Value);
    return Results.Json(new { id = Convert.ToInt64(ins.ExecuteScalar() ?? 0L) });
});

// Bulk save for everything currently open access and not yet stored.
app.MapPost("/api/save-all", async (int? max) =>
{
    var todo = new List<string>();
    using (var c = new SqliteConnection(connStr))
    {
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT doi FROM papers WHERE pdf_file IS NULL AND (pdf_error IS NULL OR pdf_error='') LIMIT $m";
        q.Parameters.AddWithValue("$m", Math.Clamp(max ?? 25, 1, 200));
        using var r = q.ExecuteReader();
        while (r.Read()) todo.Add(r.GetString(0));
    }
    int ok = 0;
    foreach (var d in todo)
    {
        var (good, detail) = await DownloadPdf(d);
        if (good) ok++;
        else
        {
            using var c = new SqliteConnection(connStr); c.Open();
            using var up = c.CreateCommand();
            up.CommandText = "UPDATE papers SET pdf_error=$e WHERE doi=$d";
            up.Parameters.AddWithValue("$e", detail);
            up.Parameters.AddWithValue("$d", d);
            up.ExecuteNonQuery();
        }
        await Task.Delay(400);   // polite to Unpaywall and to publisher servers
    }
    return Results.Json(new { attempted = todo.Count, saved = ok });
});

// Used only by the local summarizer task (loopback-only binding is the access control).
app.MapPut("/api/papers/{*doi}", async (string doi, HttpRequest req) =>
{
    using var sr = new StreamReader(req.Body);
    var body = JsonNode.Parse(await sr.ReadToEndAsync());
    var text = body?["summary"]?.GetValue<string>();
    if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { error = "summary required" });
    using var c = new SqliteConnection(connStr); c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "UPDATE papers SET ai_summary=$s, ai_at=datetime('now'), ai_requested=0 WHERE doi=$doi";
    cmd.Parameters.AddWithValue("$s", text);
    cmd.Parameters.AddWithValue("$doi", doi.ToLowerInvariant());
    return cmd.ExecuteNonQuery() > 0 ? Results.Ok() : Results.NotFound();
});

// What the summarizer task asks for.
app.MapGet("/api/queue", () =>
{
    using var c = new SqliteConnection(connStr); c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT doi,title,journal,abstract FROM papers WHERE ai_requested=1 AND abstract IS NOT NULL LIMIT 10";
    var rows = new List<object>();
    using var r = cmd.ExecuteReader();
    while (r.Read()) rows.Add(new { doi = r.GetString(0), title = r.GetString(1), journal = r.IsDBNull(2) ? null : r.GetString(2), abstractText = r.GetString(3) });
    return Results.Json(rows);
});

app.MapPost("/api/ingest", async (int? days) =>
{
    var cfg = LoadConfig();
    int seen = 0, kept = 0;
    foreach (var j in cfg.Journals)
    {
        var (s, k) = await IngestJournal(j, Math.Clamp(days ?? 14, 1, 365), cfg);
        seen += s; kept += k;
        log.LogInformation("{Journal}: {Seen} seen, {Kept} kept", j.Name, s, k);
    }
    return Results.Json(new { journals = cfg.Journals.Count, seen, kept });
});

app.MapGet("/api/health", () => Results.Json(new { ok = true, db = dbPath, version = AppVersion }));

// --- configuration, editable from the UI --------------------------------------------------------
app.MapGet("/api/config", () => Results.Text(File.ReadAllText(configPath), "application/json"));

app.MapPut("/api/config", async (HttpRequest req) =>
{
    using var sr = new StreamReader(req.Body);
    var text = await sr.ReadToEndAsync();

    // Validate BEFORE touching the file. A malformed config would otherwise break every future
    // ingest, and the failure would surface hours later as "no new papers" rather than as an error.
    JsonNode? parsed;
    try { parsed = JsonNode.Parse(text); }
    catch (Exception ex) { return Results.BadRequest(new { error = $"not valid JSON: {ex.Message}" }); }
    var obj = parsed?.AsObject();
    if (obj is null) return Results.BadRequest(new { error = "top level must be an object" });
    foreach (var key in new[] { "journals", "topicTerms", "trialTerms" })
        if (obj[key] is not JsonArray) return Results.BadRequest(new { error = $"'{key}' must be an array" });
    if (obj["journals"]!.AsArray().Count == 0) return Results.BadRequest(new { error = "at least one journal is required" });
    foreach (var j in obj["journals"]!.AsArray())
        if (j?["issn"] is null || j["name"] is null)
            return Results.BadRequest(new { error = "every journal needs issn and name" });

    // Keep the previous version. Config is hand-tuned over time and is the most valuable
    // non-reproducible state in the system.
    var backup = Path.Combine(dataDir, $"journals.{DateTime.Now:yyyyMMdd-HHmmss}.bak.json");
    File.Copy(configPath, backup, true);
    await File.WriteAllTextAsync(configPath, text);
    log.LogInformation("config updated; previous version kept at {Backup}", backup);
    return Results.Json(new { ok = true, backup = Path.GetFileName(backup) });
});

// --- system panel -------------------------------------------------------------------------------
app.MapGet("/api/system", () =>
{
    var cfg = LoadConfig();
    long dbSize = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;

    var recent = new List<object>();
    using (var c = new SqliteConnection(connStr))
    {
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts, journal, seen, kept FROM ingest_log ORDER BY id DESC LIMIT 60";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            recent.Add(new { ts = r.GetString(0), journal = r.IsDBNull(1) ? "" : r.GetString(1), seen = r.GetInt32(2), kept = r.GetInt32(3) });
    }

    // publicUrl is read from config rather than compiled in, so the tray app and any future client
    // need no knowledge of this particular tailnet - the one piece that was host-specific.
    string? publicUrl = null;
    try { publicUrl = JsonNode.Parse(File.ReadAllText(configPath))?["publicUrl"]?.GetValue<string>(); } catch { }

    return Results.Json(new
    {
        version = AppVersion,
        publicUrl,
        dbPath,
        dbSizeBytes = dbSize,
        pdfDir,
        pdfCount = Directory.Exists(pdfDir) ? Directory.GetFiles(pdfDir).Length : 0,
        pdfBytes = Directory.Exists(pdfDir) ? Directory.GetFiles(pdfDir).Sum(f => new FileInfo(f).Length) : 0,
        configPath,
        journalCount = cfg.Journals.Count,
        topicTermCount = cfg.TopicTerms.Length,
        minScore = cfg.MinScore,
        // `url` is declared below, at app.Run - read the same environment variable instead of
        // reaching forward to it (CS0841).
        bind = Environment.GetEnvironmentVariable("CURRENTS_URL") ?? "http://127.0.0.1:8789",
        process = new
        {
            memoryMb = Math.Round(Environment.WorkingSet / 1024.0 / 1024.0, 1),
            startedUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o"),
            dotnet = Environment.Version.ToString()
        },
        recentIngests = recent
    });
});

// Coverage counters, so the UI can show whether enrichment is actually working rather than
// assuming it. These are the numbers that exposed every ingestion bug during development.
app.MapGet("/api/coverage", () =>
{
    using var c = new SqliteConnection(connStr); c.Open();
    long Q(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L); }
    return Results.Json(new
    {
        total       = Q("SELECT COUNT(*) FROM papers"),
        withPmid    = Q("SELECT COUNT(*) FROM papers WHERE pmid IS NOT NULL"),
        withAbstract= Q("SELECT COUNT(*) FROM papers WHERE abstract IS NOT NULL AND length(abstract)>0"),
        withConcl   = Q("SELECT COUNT(*) FROM papers WHERE conclusions IS NOT NULL AND length(conclusions)>0"),
        absSections = Q("SELECT COUNT(*) FROM papers WHERE abstract_format='sections'"),
        absPlain    = Q("SELECT COUNT(*) FROM papers WHERE abstract_format='plain'"),
        withNct     = Q("SELECT COUNT(*) FROM papers WHERE nct IS NOT NULL"),
        summarised  = Q("SELECT COUNT(*) FROM papers WHERE ai_summary IS NOT NULL"),
        queued      = Q("SELECT COUNT(*) FROM papers WHERE ai_requested=1")
    });
});

// Per-journal yield, so a journal that contributes nothing can be spotted and dropped.
app.MapGet("/api/journals/stats", () =>
{
    using var c = new SqliteConnection(connStr); c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = """
      SELECT journal, tier, COUNT(*) AS kept, SUM(is_trial) AS trials,
             SUM(CASE WHEN read_at IS NULL THEN 1 ELSE 0 END) AS unread
      FROM papers GROUP BY journal, tier ORDER BY kept DESC
      """;
    var rows = new List<object>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
        rows.Add(new { journal = r.GetString(0), tier = r.GetInt32(1), kept = r.GetInt32(2), trials = r.GetInt32(3), unread = r.GetInt32(4) });
    return Results.Json(rows);
});

app.UseDefaultFiles();
app.UseStaticFiles();

var url = Environment.GetEnvironmentVariable("CURRENTS_URL") ?? "http://127.0.0.1:8789";
log.LogInformation("Currents starting on {Url} (db {Db})", url, dbPath);
app.Run(url);

// ---------------------------------------------------------------------------------------------
// types - must come after every top-level statement (C# CS8803)
// ---------------------------------------------------------------------------------------------
record Journal(string Issn, string Name, int Tier);
record Config(List<Journal> Journals, bool FilterEnabled, int MinScore, string[] TopicTerms, string[] TrialTerms);
record PmRecord(string? Pmid, string? Abstract, string? AbstractJson, string? Conclusions, string? PubTypes, string? Nct);
