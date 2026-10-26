# Revit Text QC

> Rolling this out to a team? See the [team guide](docs/TEAM-GUIDE.md).

A Revit add-in that checks the text in a model before drawings go out: spelling, text types and sizes,
letter case, spacing, forbidden wording and sheet/view naming. It supports Revit 2024, 2025, 2026 and 2027
and keeps itself up to date from this repository's GitHub releases.

## What it checks

| Rule | What it finds | Automatic fix |
| --- | --- | --- |
| Spelling | Words not in the dictionary (en_US or en_GB) or your custom word list. Codes with digits (`M12`, `100mm`) and mixed-case names (`uPVC`) are ignored. | Yes, you choose the suggestion |
| Text type & size | Text note and dimension types whose name, font, size, width factor or bold/italic/underline is not in the approved list. Notes with manual formatting overrides. | No, use Show in Revit to select the elements |
| Letter case | Text that should be UPPERCASE, lowercase or Title Case. Set per kind of text. | Yes |
| Spacing | Double spaces, spaces at the ends, space before punctuation, empty text notes. | Yes |
| Forbidden term | Words or phrases your office does not use, with an optional preferred wording. | Yes, when a replacement is set |
| Naming | Sheet numbers, sheet names and view names that do not match your patterns; default names such as `Section 3` or `Level 1 Copy 1`. | No |

## What it scans

- Text notes and 3D model text
- Dimension text, including spot elevations, coordinates and slopes: prefix, suffix, above, below and overrides
- Room, space and area names, and any instance text parameters you list (default: `Comments`). Generic
  annotations and revision clouds are read through this list, because their label text is not exposed by Revit.
- Tags, including room, area and space tags (the displayed tag text)
- Sheet numbers, sheet names, view names, grid names and level names
- Title block text: the sheet's own fields (Drawn By, Issue Date...), the project information and the
  revision descriptions printed on the sheet. Names in "by" fields are not spell checked.
- Fixed text typed inside the title block family (labels such as "Address", "Phone", "Consultant"). Revit's
  API does not expose these words, so each scanned sheet is exported briefly to a temporary DXF file, the
  title block's layer is read, and the file is deleted. Up to 12 sheets are read at a time. The words are
  reported with their place on the sheet, and the sheet zooms to them; to correct one, open the title block
  with Edit Family. It needs the title block on a layer whose name contains TTLB, which is what Revit's
  standard layer settings use.
- Text drawn by families and symbols, read from each view itself: labels inside annotation families, section
  and elevation marks, keynote symbols. This reads up to 30 views at a time and is slower; switch it off in
  **QC Settings** if it is not needed. It reports the text and where it is, but the text itself has to be
  fixed in the family. Revit cannot read text inside DWG imports or linked models.

## Showing a finding on a sheet

When a sheet is open, clicking a finding or pressing **Show** stays on the sheet. Text inside a placed view
selects that view's frame and zooms the sheet to where the text is printed. Title block fields select the
title block and say which field it is, because Revit does not report where a title block prints each field.
- Schedule titles and column headings (cell values optional)
- Keynote text for keynotes assigned in the model (entire-project scans)

Tags, schedules and keynotes are reported but not edited by the add-in, because their text comes from
somewhere else (the tagged element, the schedule definition, the keynote file).

**What is in this view?** The first tab of **QC Settings** lists every kind of annotation in the active
view or sheet with a count, and says whether Text QC reads it. Anything marked *Not checked* (title block
fields, detail items and so on) is not looked at.

## Install

Nothing else needs to be installed first: the release contains everything and Revit brings its own .NET.
The easiest way is to download [RevitTextQC-Setup.exe](https://github.com/Surajprem7/RevitTextQC/releases/latest/download/RevitTextQC-Setup.exe)
and run it with Revit closed (Windows may warn that it is unsigned: More info > Run anyway). It can be removed from
Windows Apps > Installed apps. Or double-click [Install-TextQC.bat](https://github.com/Surajprem7/RevitTextQC/releases/latest/download/Install-TextQC.bat),
or run this in PowerShell with Revit closed. Both download the latest release and install it
for every Revit version on the PC:

```powershell
irm https://github.com/Surajprem7/RevitTextQC/releases/latest/download/install.ps1 -OutFile install.ps1; ./install.ps1
```

Or download `RevitTextQC-<year>.zip` from the [releases page](https://github.com/Surajprem7/RevitTextQC/releases)
and extract it into `%APPDATA%\Autodesk\Revit\Addins\<year>\`.

The first time Revit loads the add-in it asks whether to trust it; choose **Always Load**.

## Use

1. Open the **Text QC** tab and press **Run Text QC**.
2. Choose **Active view / sheet**, or **Selected views / sheets** (press **Choose...** and tick them; a sheet
   includes the views placed on it and its title block), then press **Scan**.
3. Click a finding to highlight it in Revit. Text on the open sheet is highlighted without leaving the
   sheet; double-click (or **Show in Revit**) to open its view and zoom to it. **Group by** arranges the
   findings by rule, view/sheet, kind of text or level.
4. For a fixable finding, review the corrected text, edit it if needed and press **Apply fix**.
   **Fix all selected** applies the first suggestion to every selected finding that has one.
   Each batch is one Revit undo step.
5. **Add word to dictionary** stops a word being reported again.
6. **Export CSV** saves the visible findings for a QC record.

## Find text by type, size, font or style

The **Find text** tab lists what the scanned view or project actually uses. Choose **Find by** (text type,
text size, font, or style such as bold/italic), and whether to look in text notes, dimensions or both. Each
value shows how many elements use it and whether it is approved in your standards. Select a value to list
the elements, click one to highlight it, or press **Select all in Revit** and change them with the Type Selector.

## Set your office standards

**QC Settings** controls what is scanned and what counts as correct.

- **Text type & size**: list the approved type names (wildcards allowed), fonts and sizes in mm, separately for
  text notes and dimensions. An empty list means that property is not checked. **Fill from current model**
  lists everything in use in the open model so you can delete what is not standard.
- **Case & wording**: required case per kind of text, and forbidden terms written as `approx => approximately`.
  Unit symbols (`mm`, `kN`, `c/c`) and anything containing a digit (`100mm`, `M12`) are never re-cased; the
  list of exceptions is editable.
- **Spelling**: a built-in list of common construction abbreviations (`FFL`, `DPC`, `TYP`, ...) and of Gulf and
  Australian place names and local terms (`Jeddah`, `Jumeirah`, `majlis`, `Sydney`, ...) is accepted on top of
  the dictionary and your own custom words.
- **Spelling language** (QC Settings > Spelling): English (United Kingdom), English (Australia) or English
  (United States). Spellings differ: `colour`, `metre`, `centre` and `aluminium` are mistakes in US English,
  while `color`, `center` and `aluminum` are mistakes in the other two. Australian English also treats
  `programme` as a mistake. A short list of American *words* that drawings use correctly, such as `elevator`,
  `sidewalk` and `faucet`, is accepted in every mode; American *spellings* are reported in the United Kingdom
  and Australia modes.
  Use United Kingdom for most UAE and Saudi clients and Australia for Australian ones.
  The language is stored in the settings, so keep one settings file per client (**Export** to save it,
  **Import** to load it).
- **Naming**: regular expressions for sheet numbers, sheet names and view names. The whole value must match.

Settings are stored per user in `%APPDATA%\RevitTextQC\settings.json`. Use **Export** and **Import** to share
one standard across a team.

## Updates

Every time Revit starts, the add-in asks GitHub whether a newer release exists. If so, it
downloads the build for the running Revit version, verifies its checksum and installs it when Revit closes.
Both behaviours can be switched off in **QC Settings > Updates**, and **Check for Updates** on the ribbon
checks on demand.

Updates only work when the add-in is installed in the per-user add-ins folder (`%APPDATA%`), where no
administrator rights are needed, and while this repository's releases are public.

## Build from source

Requires the .NET 10 SDK. Revit does not need to be installed; the Revit API comes from NuGet.

```powershell
./build.ps1                 # all versions -> artifacts\<year>\
./build.ps1 -Install        # also copy into your Revit add-ins folders (close Revit first)
dotnet test tests/RevitTextQC.Tests
```

| Revit | .NET target |
| --- | --- |
| 2024 | .NET Framework 4.8 |
| 2025, 2026 | .NET 8 |
| 2027 | .NET 10 |

Layout:

- `src/RevitTextQC.Core` - rules, settings, spell checking and release parsing. No Revit dependency, unit tested.
- `src/RevitTextQC` - the add-in: collectors that read text from the model, the windows, and the updater.
- `tests/RevitTextQC.Tests` - unit tests for the core.

## Publish a release

Push a version tag. GitHub Actions builds every Revit version, runs the tests and publishes the release;
installed add-ins pick it up on their next start.

```powershell
git tag v0.6.5
git push origin v0.6.5
```

## Licence

Copyright (c) 2026 Suraj. All rights reserved. This is proprietary software: see [LICENSE](LICENSE).
It may be installed and used by the people it has been given to; copying or reusing the code needs permission.

## Third-party components

- [WeCantSpell.Hunspell](https://github.com/aarondandy/WeCantSpell.Hunspell) spell checking engine.
- English dictionaries from the [LibreOffice dictionaries](https://github.com/LibreOffice/dictionaries) project;
  their licences are in `src/RevitTextQC/Dictionaries/README_en_*.txt`.
