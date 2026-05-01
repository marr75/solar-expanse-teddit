# Teddit for Solar Expanse
Teddit is a BepInEx plugin for Solar Expanse that allows you to create mods for Solar Exapanse that modify or create facilities, spacecraft, research, resource deposits, and many other parts of Solar Expanse.
## How to Use
This mod is based around the use of YAML files to modify or create objects for Solar Expanse.

Every time the game is run, Teddit will generate a list of all patchable vanilla objects and their properties as YAML files. The YAML files found within the /dump/ folder contain the IDs and properties for every patchable object in the game.

By adding a new folder in the Teddit/mods folder, and adding the files for the objects you wish to patch (resource deposits, facilities, spacecraft, etc.), the entries will be added to the game. If you specify an ID that already exists in the base game, the objects will instead be modified. Currently, adding new entries is only supported for facilities, though functionality for spacecraft and research is planend.

An example mod, which includes a new facility with a custom icon, has been provided and is installed by default.

## Installation

This plugin uses BepInEx 5.4 to inject code into the Solar Expanse exe. The link to installation instructions for BepInEx is found here: https://docs.bepinex.dev/articles/user_guide/installation/index.html

Once BepInEx is installed, run it once to generate the /plugins/ folder. Move the contents of this repo's /plugins/ folder to Solar Expanse/BepInEx/plugins.

By default, this mod comes with a mod (example_mod in the mods directory) configured to add a new building -- the Cat Habitat -- that is unlocked by default. If this building is visible under habitats, the mod is working.

