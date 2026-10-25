# CS2-Bot-Randomizer
CS2-Bot-Randomizer is a plugin based on CounterStrikeSharp that allows each bot to have their own knife, gloves, weapon skins, agent model, music kit, stickers, charms and avatar.
# Features
1. Assigns each bot a knife, a pair of gloves, a set of weapon skins, stickers and charms

2. Assigns each bot their own T/CT agent model and avatar

3. Allocates an MVP music kit to each bot

<img width="960" height="576" alt="Skins_1" src="https://github.com/user-attachments/assets/f096b9d6-5800-4942-baed-d1aa7d4461ec" />

![pic2](https://github.com/user-attachments/assets/f45ee14b-e994-4037-8ac8-caf36f8056a3)
![random_2](https://github.com/user-attachments/assets/e13ab9b5-1abc-4f1e-9b11-178c4dd3f420)

# Installation
1. Download the latest **BotRandomizer.zip** from [Releases](https://github.com/ed0ard/CS2-Bot-Randomizer/releases)

2. Decompress it and upload the folder to `game/csgo/addons/counterstrikesharp/plugins` on your server

3. Navigate to `addons/counterstrikesharp/configs/core.json` and set `FollowCS2ServerGuidelines` to `false`

3. Restart your server

## Human player music kits

Human music overrides are off by default and never affect bots. In the server console, or the host's console on a listen server:

- `br_player_music_kits` lists catalog kit IDs and English names. Older catalogs without names list kit IDs only.
- `br_player_music_kit 33` selects a kit ID (33 is Blitz Kids, The Good Youth) for all human players.
- `br_player_music_kit -1` shares one random kit across human players for the map.
- `br_player_music_reroll` chooses a different random kit and applies it immediately when the mode is `-1`.
- `br_player_music_kit 0` restores each human player's music fields captured before the override.

Convar changes apply immediately through `FakeConVar.ValueChanged`. The plugin also reapplies on spawn, team join, round start and before the MVP event, including its anthem fields. An unknown kit ID is ignored and logged once per value for the plugin's lifetime; the listen-server host's console rejects it with a reply.

# Credits
[CS2-Lib](https://github.com/ianlucas/cs2-lib)  
[CS2-CSS-Inventory-Simulator ](https://github.com/ianlucas/cs2-css-inventory-simulator)  
## If you find the project useful then please take the time to star⭐ the repository
