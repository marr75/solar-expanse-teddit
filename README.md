# Teddit

A YAML-driven modding framework for [Solar Expanse](https://store.steampowered.com/app/1369700/Solar_Expanse__Space_Exploration_Manager/).

Teddit lets you add and patch game content entirely through YAML files. Drop a mod folder into the `mods/` directory and the framework loads it automatically at runtime.

## How to Use
This mod is based around the use of YAML files to modify or create game content for Solar Expanse.

Every time the game is run, Teddit will generate a list of all patchable vanilla objects and their properties as YAML files. The YAML files found within the /dump/ folder contain the IDs and properties for every patchable object in the game.

By adding a new folder in the Teddit/mods folder, and adding the files for the objects you wish to patch (resource deposits, facilities, spacecraft, etc.), the entries will be added to the game. If you specify an ID that already exists in the base game, the objects will instead be modified.

An example mod, which includes examples of most patches Teddit is capable of doing, has been provided and is installed by default.

## Installation

This plugin uses BepInEx 5.4 to inject code into the Solar Expanse exe. The link to installation instructions for BepInEx is found here: https://docs.bepinex.dev/articles/user_guide/installation/index.html

Once BepInEx is installed, run it once to generate the /plugins/ folder. Move the contents of this repo's /plugins/ folder to Solar Expanse/BepInEx/plugins.
If installed correctly, a small text will appear in the lower right-hand corner that shows the current Teddit version.

