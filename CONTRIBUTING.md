# Contributing

## Every change gets catalogued

From v1.1.0-alpha.1 onwards, nothing is merged without a changelog entry. The changelog is written
as the change is made, not reconstructed from git history later, because by then the reason for a
change is gone and only the diff is left.

### How to record a change

Add a bullet under `## Unreleased` in [CHANGELOG.md](CHANGELOG.md), under one of these headings.
Create the heading if it is not there yet.

| Heading | For |
|---|---|
| `Added` | New features |
| `Changed` | Changes to how something already worked |
| `Fixed` | Bug fixes |
| `Removed` | Features taken out |
| `Security` | Anything affecting what is exposed, stored or trusted |

### What an entry has to say

Say what changed, and say why it was wrong before. One or two sentences. If a number proves the
point, give the number.

Good:

> Headings without a colon are now split correctly. JAMA writes `Importance Whether intravenous
> tenecteplase...` with no colon, so a pattern that required one found no headings at all and stored
> the abstract as prose. Structured abstracts went from 63 to 76 out of 92.

Not enough:

> Fixed abstract parsing.

The second one is true and useless. In six months nobody can tell whether a later regression is the
same bug coming back.

Three specific rules, each of which exists because the opposite caused a real problem here:

1. **Record the measurement if you took one.** "68 kept papers down to 50" is checkable. "Improved
   filtering" is not.
2. **Record what you ruled out and why.** The PubMed publication-type filter, the NCBI ID Converter
   and per-journal RSS were all tried and rejected for concrete reasons. Without that written down,
   the next person reintroduces them.
3. **Say when a change moves or renames data.** Anything touching the database, the data folder, the
   config format or an environment variable gets called out, because someone upgrading needs to know
   before they upgrade, not after.

### Things that do not need an entry

Typos, comment rewording, formatting, and changes to the build that nobody running the app would
notice. If a user or an operator cannot observe it, leave it out.

## Releasing

1. Move everything under `## Unreleased` into a new `## x.y.z (YYYY-MM-DD)` section and leave
   `## Unreleased` empty above it.
2. Update the version in three places, which must agree:
   - `AppVersion` in `src/Program.cs`
   - `<Version>` in `src/Currents.csproj`
   - `<Version>` in `tray/CurrentsTray.csproj`

   `AssemblyVersion` and `FileVersion` stay numeric, without the prerelease suffix, because Windows
   file metadata cannot hold one.
3. Commit, then tag `vx.y.z`.
4. Build:
   ```powershell
   dotnet publish src  -c Release -r win-x64 --self-contained true -o out
   dotnet publish tray -c Release -r win-x64 --self-contained true -o out
   ```
   Both go to the same folder so they share one copy of the runtime. Include `LICENSE`,
   `THIRD-PARTY-NOTICES.md` and a `START-HERE.txt`, then zip it.
5. Before publishing, unzip it somewhere that is not the build directory and run it. Check that
   `/api/health` reports the version you expect and that the database is created beside the
   executable. A build that only works in place is a build that has not been tested.
6. Publish with the SHA-256 in the release notes. Mark alpha and beta builds as prereleases.

## Commit messages

A subject line in the imperative, under about 70 characters, then a blank line, then why. The body
matters more than the subject. If the change is not obvious from the diff, the body is where you
explain it.

Do not skip hooks, and do not force-push a branch anyone else may have pulled.

## Code

Match what is already there. The codebase comments the reasoning behind decisions that look odd,
and those comments are load-bearing: they are what stops someone "simplifying" a workaround back
into the bug it was working around. If you remove one, say why in the changelog.

Write plainly, in both code and documentation. No em dashes, no arrows, no dot separators.

## A note on contributions

Currents is dual licensed, so merged contributions need a Contributor Licence Agreement. The project
cannot offer a commercial licence covering code it does not control. Please open an issue before
starting anything substantial, so nobody writes something that cannot be taken.
