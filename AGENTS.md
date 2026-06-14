# Teddit Project Notes

## Canonical Paths

- Repo root: `C:\Users\parft\Documents\SolarExpanseMods\solar-expanse-teddit`
- Source project: `C:\Users\parft\Documents\SolarExpanseMods\solar-expanse-teddit\Source\Teddit`
- Repo build payload: `C:\Users\parft\Documents\SolarExpanseMods\solar-expanse-teddit\Teddit`
- Live deployed plugin: `C:\Program Files (x86)\Steam\steamapps\common\Solar Expanse\BepInEx\plugins\Teddit`
- Release output folder: `C:\Users\parft\Documents\SolarExpanseMods\Releases`

## Teddit Release Layout

When creating a Teddit release in `C:\Users\parft\Documents\SolarExpanseMods\Releases`, use this layout:

- `example_mod` at the release root
- `Source\Teddit`
- `Teddit`

Notes:

- `example_mod` belongs at the release root, not inside `Teddit\mods`
- keep `Teddit\mods` empty in release packages unless explicitly requested otherwise
- exclude `bin` and `obj` from the `Source\Teddit` snapshot

## Build Notes

- Build with:
  - `dotnet build -c Release C:\Users\parft\Documents\SolarExpanseMods\solar-expanse-teddit\Source\Teddit\Teddit.csproj`
- The project deploys to both:
  - the live plugin folder
  - the repo payload folder
