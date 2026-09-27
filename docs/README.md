# SqlVitals documentation

**Start at [Home](Home.md).** The [sidebar](_Sidebar.md) lists every page.

These are the pages of the [SqlVitals wiki](https://github.com/aselasampath/sqlvitals/wiki), kept here while the
repository is private: GitHub Free has no wiki for a private repository. When the repository is public again, they move
back to the wiki ([#106](https://github.com/aselasampath/sqlvitals/issues/106)).

## While they're here

- **Update these pages, not the wiki.** Wherever a page or the contribution workflow says "the wiki", read `docs/`.
- File names match the wiki's, so moving back is a copy. `Home.md`, `_Sidebar.md` and `_Footer.md` are the wiki's
  front page, sidebar and footer.
- Links between pages end in `.md` so they work here, e.g. `[Alerts](Alerts.md#rules)`. The wiki writes them without
  it, `[Alerts](Alerts#rules)`; the script below converts them back. Keep new links in the same form.

## Moving them back to the wiki

Make the repository public first: the wiki can't be cloned or pushed while it's hidden.

Then run this from the repository root in PowerShell 7. It clones the wiki next to the repository, replaces its pages
with the ones here (so pages added, renamed or removed here are too), and points the README's documentation links back
at the wiki.

```powershell
git clone https://github.com/aselasampath/sqlvitals.wiki.git ..\sqlvitals.wiki
$docs  = (Resolve-Path docs).Path
$wiki  = (Resolve-Path ..\sqlvitals.wiki).Path
$utf8  = New-Object Text.UTF8Encoding($false)
$pages = (Get-ChildItem "$docs\*.md" -Exclude README.md).BaseName
$names = ($pages | ForEach-Object { [regex]::Escape($_) }) -join '|'

# Pages: (Page.md#anchor) -> (Page#anchor)
Remove-Item "$wiki\*.md"
foreach ($f in Get-ChildItem "$docs\*.md" -Exclude README.md) {
    $text = [IO.File]::ReadAllText($f.FullName)
    $text = [regex]::Replace($text, "\]\(($names)\.md(#[^)]*)?\)", ']($1$2)')
    [IO.File]::WriteAllText((Join-Path $wiki $f.Name), $text, $utf8)
}

# README: docs/Home.md -> the wiki's home page, docs/Page.md -> the wiki page
$readme = (Resolve-Path README.md).Path
$r = [IO.File]::ReadAllText($readme)
$r = [regex]::Replace($r, 'docs/Home\.md', 'https://github.com/aselasampath/sqlvitals/wiki')
$r = [regex]::Replace($r, "docs/($names)\.md", 'https://github.com/aselasampath/sqlvitals/wiki/$1')
[IO.File]::WriteAllText($readme, $r, $utf8)
```

Then:

1. Check `git -C ..\sqlvitals.wiki status` shows the changes you expect, then commit and push the wiki.
2. In a pull request, delete `docs/` and commit the README change.
