# Teddit for Solar Expanse
Teddit is a BepInEx plugin for Solar Expanse that allows you to edit spacecraft, resource, research, launch vehicle, and facility properties via JSON files.
## How to Use
The JSON files found within the /dump/ folder contain the entries for every patchable object in the game. The values there can be used as reference. This list is updated every time the game is ran.

Patches are made by adding edits to the deposits, facilities, launch_vehicles, research, and spacecraft files (example entries and documentation included in each).
Note that the plugin is currently only able to edit entries, not add new ones. This is a planned feature.

## Installation

This plugin uses BepInEx 5.4 to inject code into the Solar Expanse exe. The link to installation instructions for BepInEx is found here: https://docs.bepinex.dev/articles/user_guide/installation/index.html

Once BepInEx is installed, run it once to generate the /plugins/ folder. Move the contents of this repo's /plugins/ folder to Solar Expanse/BepInEx/plugins.

By default, this mod is configured to set the Mobile Fuel Refinery to require 100 humans to operate. This is a temporary method to show whether the plugin is working -- you can then edit the files to reverse this change.

