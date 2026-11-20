# Homepage

A simple launcher for Windows. Keep your files, folders, apps and websites on one page and open them with a double-click.

One EXE, no install.

## What it does

- Pages, groups and tabs to organize your shortcuts
- Show shortcuts as icons or rows, normal or compact
- Drag files and folders in from Explorer
- Import bookmarks from Edge, Chrome, Firefox, Brave and Opera
- Website names and icons are filled in for you
- Ctrl+click several shortcuts and press Enter to open them all
- Right-click a group tab and pick **Open all** to open the whole tab
- Undo with Ctrl+Z

## How to use it

- **Add things:** click **+**, right-click the page, or drop files onto it.
- **Open:** double-click a shortcut.
- **Rearrange:** click the pencil (or Ctrl+E), then drag shortcuts and groups around.
- **Move the window:** drag the empty part of the top bar.

Everything is saved in a `Homepage.data` folder next to the EXE. Move that folder with the EXE to keep your layout.

Homepage only links to your files. It never copies, moves or deletes them.

## Build

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```powershell
dotnet publish -c Release -o dist
```

The app ends up at `dist\Homepage.exe`.

## License

Apache-2.0. Third-party notices are in `THIRD-PARTY-NOTICES.txt`.
