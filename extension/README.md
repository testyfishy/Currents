# Currents Saver

**Status: shelved, desktop only, and entirely optional.**

Mobile browsers make an extension a dead end for a phone-first reader. Chrome for Android supports
no extensions at all, and Safari on iOS needs the extension shipped inside a native App Store app.
Firefox for Android works, but few people use it.

The path that works everywhere is Currents's own attach button. Open the paper through your library
proxy, save the PDF, then tap "Attach a PDF" in the preview pane. That behaves the same on iOS,
Android, Windows and Linux, needs nothing installed, and is one tap.

This extension is still here because on a desktop it saves that round trip. One click on the article
page and the PDF lands on the server. Use it if that is worth an unpacked install to you.

It is unfinished and not actively maintained. Please do not file bugs against it.

## Why an extension rather than a browser inside the app

Currents is a web app you reach over a private network, so the device you read on is usually not the
machine running the server. A browser built into the app would run on the server, which is a box in
another room, and that is no use from a laptop or a phone. Three other problems:

- A web page cannot embed a publisher's site. Nearly all of them set `X-Frame-Options` or a
  frame-ancestors CSP, so the iframe would refuse to load.
- Typing university login details into a window an application controls looks exactly like
  phishing, and plenty of institutions forbid it outright.
- It would put your credentials inside the app's trust boundary, which is the thing worth avoiding.

An extension runs where you already are, in the browser you are already signed in to.

## What it can and cannot see

- It never handles your password. It does not show a login form and does not read one. It fetches
  the PDF with `credentials: "include"`, which reuses the session your browser already has with the
  publisher. That is the same thing that happens when you click their own download link.
- It does nothing until you click. There is no script running in the background on any site.
  `activeTab` means it can only read a page in response to the toolbar button.
- It sends files to one place, the server address you configure, and nowhere else.
- It is a few hundred lines of plain JavaScript. Read `background.js` before you trust it.

`host_permissions` is `<all_urls>` because the PDF comes from whichever publisher you happen to be
reading, and there is no fixed list of those. Narrow it to your own library's domains if you prefer.

## Install

Chrome, Edge or any Chromium browser:

1. Go to `chrome://extensions` and turn on Developer mode.
2. Click "Load unpacked" and select this folder.
3. Open the extension's Details, then Extension options, and enter your Currents address.

Firefox: go to `about:debugging`, then This Firefox, then Load Temporary Add-on, and pick
`manifest.json`.

## Use

1. Open the paper through your library proxy and sign in as normal.
2. Get to the article page, or to the PDF itself.
3. Click the toolbar button.

The badge shows what happened. `OK` means saved, `...` means in progress, `?` means nothing was found
on this page, and `ERR` means it failed. The details go to the extension's service worker console.

## Limits

- The paper has to be in Currents already. The extension attaches a file to a DOI it knows about; it
  does not create new entries. A 404 means Currents has not fetched that paper yet.
- The publisher has to declare where the PDF is. Most of them publish `citation_pdf_url`, which is
  the Google Scholar convention. Where they do not, open the PDF itself and click again.
- iOS is not covered, because Safari web extensions have to ship through the App Store inside a
  container app. On a phone, drag and drop the file into Currents instead.
- If a download returns a login page instead of a PDF, the extension refuses it rather than saving
  it. That is checked on the file's magic bytes, not its name.
