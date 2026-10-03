// Currents Saver - save the article you are reading to your own Currents server.
//
// WHY AN EXTENSION RATHER THAN A BROWSER INSIDE THE APP:
//   Currents is a web app reached over a private network, so the device you are reading on is
//   usually NOT the machine the server runs on. An embedded browser would open on the server's
//   desktop - in another room, headless - which is useless from a laptop or a phone. The extension
//   runs where you actually are.
//
// WHAT IT NEVER TOUCHES:
//   Your credentials. It does not render a login form and does not read one. It reuses the session
//   your browser ALREADY has, by fetching with `credentials: "include"`, exactly as clicking the
//   publisher's own download link would. The password never exists in this extension's world.
//
// WHAT IT DOES, only when you click the toolbar button:
//   1. reads the DOI and the publisher's declared PDF URL out of the current page
//   2. fetches that PDF using the page's own session
//   3. POSTs it to the Currents server you configured

const DEFAULTS = { server: "", token: "" };

async function cfg() {
  const s = await chrome.storage.sync.get(DEFAULTS);
  return { ...DEFAULTS, ...s };
}

function badge(tabId, text, color) {
  chrome.action.setBadgeText({ tabId, text });
  chrome.action.setBadgeBackgroundColor({ tabId, color });
  if (text) setTimeout(() => chrome.action.setBadgeText({ tabId, text: "" }), 6000);
}

// Runs IN the page. Publishers overwhelmingly emit citation_doi and citation_pdf_url - the Google
// Scholar convention - which is why this needs no per-publisher rules.
function scrapePage() {
  const meta = n => document.querySelector(`meta[name="${n}" i]`)?.content?.trim() || null;

  let doi = meta("citation_doi") || meta("DC.Identifier") || meta("dc.identifier") || null;
  if (doi) doi = doi.replace(/^(doi:|https?:\/\/(dx\.)?doi\.org\/)/i, "").trim();
  if (!doi) {
    const hit = (document.querySelector('link[rel="canonical"]')?.href || location.href)
      .match(/10\.\d{4,9}\/[^\s"'<>&?#]+/);
    if (hit) doi = hit[0];
  }

  let pdf = meta("citation_pdf_url");
  if (!pdf && /\.pdf(\?|$)/i.test(location.href)) pdf = location.href;
  if (!pdf) pdf = document.querySelector('a[type="application/pdf"]')?.href || null;
  if (pdf) pdf = new URL(pdf, location.href).href;

  return { doi, pdf, title: document.title };
}

chrome.action.onClicked.addListener(async (tab) => {
  const { server } = await cfg();
  if (!server) {
    badge(tab.id, "SET", "#b45309");
    chrome.runtime.openOptionsPage();
    return;
  }

  let found;
  try {
    const [res] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: scrapePage });
    found = res?.result;
  } catch (e) {
    badge(tab.id, "ERR", "#b91c1c");
    return notify("Could not read this page", e.message);
  }

  if (!found?.doi) {
    badge(tab.id, "?", "#b45309");
    return notify("No DOI found on this page", "Open the article's own page, not a search result or a table of contents.");
  }
  if (!found.pdf) {
    badge(tab.id, "?", "#b45309");
    return notify("No PDF link on this page",
      `Found DOI ${found.doi}, but the publisher does not declare a PDF URL here. Open the PDF itself, then click again.`);
  }

  badge(tab.id, "...", "#1d4ed8");
  try {
    // credentials:"include" is the whole trick - the browser attaches the session you already have
    // with the publisher, so an entitled reader gets the entitled file, and no password is involved.
    const r = await fetch(found.pdf, { credentials: "include" });
    if (!r.ok) throw new Error(`publisher returned HTTP ${r.status}`);
    const blob = await r.blob();

    // Guard against saving a login page or an interstitial as though it were the article.
    const head = new Uint8Array(await blob.slice(0, 5).arrayBuffer());
    if (String.fromCharCode(...head.slice(0, 4)) !== "%PDF")
      throw new Error("that link returned a web page, not a PDF - are you signed in?");

    const fd = new FormData();
    fd.append("file", blob, "paper.pdf");
    const up = await fetch(`${server.replace(/\/+$/, "")}/api/upload/${found.doi}`, { method: "POST", body: fd });
    if (!up.ok) {
      const msg = await up.text().catch(() => "");
      throw new Error(up.status === 404
        ? "Currents does not have this paper yet - let it fetch, then try again"
        : `Currents returned HTTP ${up.status} ${msg}`);
    }
    badge(tab.id, "OK", "#15803d");
    notify("Saved to Currents", found.title.slice(0, 120));
  } catch (e) {
    badge(tab.id, "ERR", "#b91c1c");
    notify("Could not save", e.message);
  }
});

function notify(title, message) {
  // notifications permission is deliberately not requested; fall back to the badge alone.
  try { console.info(`[Currents] ${title}: ${message}`); } catch { }
}
