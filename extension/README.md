# Currents Saver

> **Status: shelved, desktop-only. Optional.**
>
> Mobile browsers make this a dead end for a phone-first reader. Chrome for Android supports no
> extensions at all; Safari on iOS requires shipping inside a native App Store container app.
> Firefox for Android works but is niche.
>
> **The supported path on every device is Currents's own attach button** — open the paper through
> your library proxy, save the PDF, then tap *Attach a PDF* in the preview pane. That works
> identically on iOS, Android, Windows and Linux, needs no install, and is one tap.
>
> This extension remains here because on a desktop it removes that round trip: one click on the
> article page and the PDF lands on the server. Use it if that is worth an unpacked install.

A browser extension that saves the article you are reading to your own Currents server.

## Why an extension and not a browser inside the app

Currents is a web app reached over a private network, so **the device you read on is usually not
the machine the server runs on**. An embedded browser would have to run on the server — a headless
box in another room — which is useless from a laptop or a phone. Three further problems:

- A web page cannot embed a publisher's site. Essentially every publisher sets `X-Frame-Options` or
  a frame-ancestors CSP, so the iframe would simply refuse to load.
- Entering university SSO credentials into a window an application controls is indistinguishable
  from a phishing pattern, and many institutions' acceptable-use policies forbid it outright.
- It would put the credential inside the app's trust boundary, which is the thing worth avoiding.

The extension runs where you already are, in the browser where you are already signed in.

## What it can and cannot see

- **It never handles your password.** It does not render a login form and does not read one. It
  fetches the PDF with `credentials: "include"`, which reuses the session your browser already holds
  with the publisher — the same thing that happens when you click their own download link.
- **It does nothing until you click.** There is no content script running in the background on any
  site. `activeTab` means it can read a page only in response to the toolbar button.
- **It sends files to one place:** the server address you configure, and nowhere else.
- It is a few hundred lines of readable JavaScript. Read `background.js` before trusting it.

`host_permissions` is `<all_urls>` because the PDF comes from whichever publisher you happen to be
reading and there is no fixed list. Narrow it to your own library's domains if you prefer.

## Install

Chrome, Edge or any Chromium browser:

1. `chrome://extensions` → enable **Developer mode**
2. **Load unpacked** → select this folder
3. Click the extension's **Details → Extension options** and enter your Currents address

Firefox: `about:debugging` → **This Firefox** → **Load Temporary Add-on** → pick `manifest.json`.

## Use

1. Open the paper through your library proxy and sign in as usual
2. Get to the article page (or the PDF itself)
3. Click the toolbar button

The badge reports the outcome: `OK` saved · `...` in progress · `?` nothing found on this page ·
`ERR` failed. Details go to the extension's service-worker console.

## Limits

- **The paper must already be in Currents.** The extension attaches a file to a known DOI; it does
  not create entries. A 404 means Currents has not ingested that paper yet.
- **It needs the publisher to declare the PDF.** Most emit `citation_pdf_url`, the Google Scholar
  convention. Where they do not, open the PDF itself and click again.
- **iOS is not covered.** Safari web extensions must ship through the App Store inside a container
  app. On a phone, use Currents's own drag-and-drop upload instead.
- If a download returns a login page rather than a PDF, the extension refuses it rather than storing
  it — checked on the file's magic bytes, not its name.
