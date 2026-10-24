# Revit Text QC: team guide

For whoever rolls the add-in out, and for the people using it.

## What it is

A Revit add-in built for Revit 2024, 2025, 2026 and 2027 (it has been tested inside Revit 2027; try it on one PC
with 2024, 2025 or 2026 before the whole team) that checks the text on a sheet or view: spelling, text type and size,
letter case, spacing, forbidden words, and sheet/view naming. It reads text notes, dimensions, tags, room names,
schedules, the title block (its fields, its revision text and the words typed inside it) and the lettering that
families draw. Findings are listed with the error, a suggestion, the element id and a **Show** button that zooms
to the text, on the sheet if a sheet is open.

## Install (about two minutes per person)

1. Close Revit.
2. Open **PowerShell** and run (all Revit versions found on the PC are installed):

   ```powershell
   irm https://github.com/Surajprem7/RevitTextQC/releases/latest/download/install.ps1 -OutFile install.ps1; ./install.ps1
   ```

   If PowerShell blocks the script, run `powershell -ExecutionPolicy Bypass -File .\install.ps1` instead.
3. Start Revit. The first time, Revit asks whether to trust the add-in: choose **Always Load**. (Revit asks for every
   add-in that is not signed with a paid code-signing certificate.)
4. A **Text QC** tab appears. Press **Run Text QC**, choose **Active view / sheet** or **Selected views / sheets**,
   and press **Scan**.

Nothing needs administrator rights: everything is installed under the user's own `%APPDATA%`.

## Set it up once per client

Open **QC Settings** and set:

- **Spelling > Spelling language:** English (United Kingdom) for most UAE and Saudi clients, English (Australia)
  for Australian clients, English (United States) for American ones. The language is **remembered per project**.
- **Text type & size:** the approved type names, fonts and sizes. **Fill from current model** lists what the open
  model uses so you can delete what is not standard.
- **Case & wording, Naming:** required letter case, forbidden words, sheet and view naming patterns.
- **Add word to dictionary** (in the results window) for project names, consultants and local terms.

To give everyone the same standard, set it up on one PC, press **Export**, send the file, and let the others press
**Import**. Keep one file per client.

## Updates

Every time Revit starts, the add-in checks GitHub for a newer release. If there is one it is downloaded, a message
says so, and it is installed when Revit closes (however Revit ends). People can switch this off in
**QC Settings > Updates**, or press **Check for Updates** at any time.

**Testing a change before the team gets it:** publish it on GitHub as a *pre-release*. The add-in only follows the
latest *full* release, so a pre-release reaches nobody until it is promoted. Install it on a pilot PC by hand.

**If a release turns out to be bad:** publish a fixed release with a higher version number; every PC picks it up on
its next start. (A release can also be deleted on GitHub; PCs that already installed it keep it until the next one.)

## What it cannot do

- Fixed text typed inside a title block family can be found and shown, but must be corrected by opening the title
  block with **Edit Family**. The same goes for labels inside annotation families.
- Text inside imported DWG files and linked models is not read.
- Real words used wrongly (their / there) are not caught; a dictionary does not know. A word that is wrong but
  valid in the chosen language, such as "Phon" for "Phone" in United Kingdom mode, is missed too. Add such words to
  **Forbidden words** (`phon => Phone`) if they matter.
- The title block's fixed text is found by the layer Revit puts title blocks on (its name contains `TTLB`, which is
  Revit's standard). If an office uses a different layer standard, the scan says so.

## Speed

A scan of one sheet takes about 5 seconds. Reading a sheet's title block text exports the sheet briefly (about 4
seconds each, up to 12 sheets per scan), and reading drawn text replays each view (up to 30 views, stopping after
25 seconds). Both can be switched off in **QC Settings > What to scan**. Revit is busy while a scan runs.

## Privacy

Nothing from a model is sent anywhere. The only network traffic is the update check, which asks github.com for the
newest version number, and the download of a new release. A temporary file used for the title block text is
written to the user's `%TEMP%\RevitTextQC` folder and deleted straight away.

## When something does not work

1. Look at the line at the bottom of the Text QC window; it says what could not be read.
2. The log is `%LOCALAPPDATA%\RevitTextQC\textqc.log`. Send it with a description.
3. **Check for Updates** to make sure the PC has the latest version.

## Remove it

Close Revit, then delete `%APPDATA%\Autodesk\Revit\Addins\<year>\RevitTextQC.addin` and the
`RevitTextQC` folder next to it. Settings live in `%APPDATA%\RevitTextQC` and can be deleted too.

## Releasing (for the maintainer)

Push a version tag; GitHub Actions builds every Revit version, runs the tests and publishes the release.

```powershell
git tag v0.6.0
git push origin v0.6.0
```

Protect the GitHub account: the add-in installs whatever the latest release contains on every PC, so turn on
two-factor authentication and, for the repository, branch and tag protection.
