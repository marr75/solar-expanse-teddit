# Teddit for Solar Expanse
Teddit is a YAML modding framework for Solar Expanse that allows you to create mods that modify or create facilities, spacecraft, research, resource deposits, and many other parts of Solar Expanse.

## Editable Objects
Teddit can currently edit and create:
* Facilities
* Research
* Resources
* Resource Deposits
* Companies
* Bodies (here only for removing orbits from existing bodies)
* Spacecraft
* Launch Vehicles
* Life Support

## Gameplay Patches

Teddit also extends several existing gameplay features, for use by mods. All features are togglable in ```gameplay_patches.yaml``` in the root folder.

```constructionRespectsEfficiency```: Allows constrruction modules to take crew, power, and resources in order to run.
```energyModuleConsumeFuel```
```energyThrottleable``` (Set per-facility in Facilities YAML): In stock, energy facilities run at full efficiency regardless of energy deficit. This boolean, when set to true, allows a facility to throttle down production to meet demand (when batteries are full), so as to not waste fuel.

## How to Use
This mod is based around the use of YAML files to modify or create objects for Solar Expanse.

Every time the game is run, Teddit will generate a list of all patchable vanilla objects and their properties as YAML files. The YAML files found within the /dump/ folder contain the IDs and properties for every patchable object in the game.

By adding a new folder in the Teddit/mods folder, and adding the files for the objects you wish to patch (resource deposits, facilities, spacecraft, etc.), the entries will be added to the game. If you specify an ID that already exists in the base game, the objects will instead be modified.

## Installation

This plugin uses BepInEx 5.4 to inject code into the Solar Expanse exe. The link to installation instructions for BepInEx is found here: https://docs.bepinex.dev/articles/user_guide/installation/index.html

1. Once BepInEx is installed, run it once to generate the ```BepInEx/plugins/``` folder.
2. Install the latest Release from the Releases Page.
3. Move the ```Teddit``` Folder into ```Solar Expanse\BepInEx\plugins```
4. If installation was succesful, a badge with the version number should briefly appear in the bottom right after launching the game.

