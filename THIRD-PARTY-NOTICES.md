# Third party notices

Currents itself is licensed under the AGPL-3.0, in [LICENSE](LICENSE). The components below are
redistributed with it in the binary release and keep their own licences.

## Browser libraries, in `wwwroot/vendor`

These are not in the source repository. `fetch-vendor.ps1` downloads them, and they are included in
the binary release so that the PDF reader works without an internet connection.

**pdf.js** (`pdf.min.mjs`, `pdf.worker.min.mjs`)
Copyright Mozilla Foundation and contributors.
Apache License 2.0. https://github.com/mozilla/pdf.js

**pdf-lib** version 1.17.1 (`pdf-lib.min.js`)
Copyright Andrew Dillon.
MIT License. https://github.com/Hopding/pdf-lib

## .NET

**.NET runtime and ASP.NET Core**
Copyright .NET Foundation and contributors.
MIT License. https://github.com/dotnet/runtime

**Microsoft.Data.Sqlite**
Copyright .NET Foundation and contributors.
MIT License. https://github.com/dotnet/efcore

**SQLitePCLRaw**
Copyright Eric Sink and contributors.
Apache License 2.0. https://github.com/ericsink/SQLitePCL.raw

**SQLite**
Public domain. https://www.sqlite.org/copyright.html

## Data sources

Currents reads from Crossref, PubMed (NCBI E-utilities), Unpaywall and Europe PMC. It does not
redistribute their data. Each has its own terms and its own rate limits, and the app sends a contact
address with its requests because all four ask for one.
