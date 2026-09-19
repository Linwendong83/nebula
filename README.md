# Nebula Multiplayer Mod [![Build - Win x64](https://github.com/Linwendong83/nebula/actions/workflows/build-winx64.yml/badge.svg)](https://github.com/Linwendong83/nebula/actions/workflows/build-winx64.yml) [![QQ Group: 587702629](https://img.shields.io/badge/QQ_Group-587702629-12B7F5?logo=qq&logoColor=white)](https://qun.qq.com/universal-share/share?ac=1&authKey=aqcKfS15Bl5nLEWhfmkiskMj65QyEsdWPCsMGYducyVdZhqNPrGeilcYSgaENhs%2F&busi_data=eyJncm91cENvZGUiOiI1ODc3MDI2MjkiLCJ0b2tlbiI6Ik1HbXppTWRHYmNlVUlSSCtaN1BkaUVXWU9ud3ZMOElJdGplaitmL21OWkorNllYaCtBbXduc2hKdGlzUnlrVksiLCJ1aW4iOiI1NDU1NTg1MzgifQ%3D%3D&data=ZYdt-PQo-JrmmYsIyy4D3s8XTbPjqzGZTrjxiTlbr9uAivSvyvqc_KyXatfb7YTjGqF5O7FONbKh1QpxuEen5A&svctype=4&tempid=h5_group_info)

[English](README.md) | [简体中文](README_zh-CN.md)

An open-source, multiplayer mod for the game [Dyson Sphere Program](https://store.steampowered.com/app/1366540/Dyson_Sphere_Program/).

## Releases & Downloads

- You can download the latest builds from this repository's [Releases](https://github.com/Linwendong83/nebula/releases) or [Actions](https://github.com/Linwendong83/nebula/actions).
- Stable builds of the original mod can also be found on [Thunderstore](https://dsp.thunderstore.io/package/nebula/NebulaMultiplayerMod/).
- For manual installation instructions, please refer to the [Installation Guide](https://github.com/NebulaModTeam/nebula/wiki/Installation#manual-installation).

## FAQ

### Where can I get mod support or report issues?

Please open an issue on our [GitHub Issues](https://github.com/Linwendong83/nebula/issues) page.  
The mod often becomes incompatible after game updates. A temporary version rollback may be needed.  
Some mods are not compatible with multiplayer. Check the [NebulaCompatibilityAssist](https://thunderstore.io/c/dyson-sphere-program/p/starfi5h/NebulaCompatibilityAssist/) mod page to learn more.  

### How can I play this mod?

Please do keep in mind that this mod is still in active development to keep up with game changes, it may still contain bugs.

- Pre-release and development builds can be downloaded from [Releases](https://github.com/Linwendong83/nebula/releases).
- For step-by-step setup, see the [Installation Guide](https://github.com/NebulaModTeam/nebula/wiki/Installation#manual-installation).
- To connect, check the [Hosting and Joining Guide](https://github.com/NebulaModTeam/nebula/wiki/Hosting-and-Joining). The mod uses TCP for direct connections, with the default port set to `8469`.

### Chat 

The chat window can be opened/closed using `Alt + Backtick` (configurable in Settings - Multiplayer - Chat). Also in settings is an option to disable the chat window from automatically opening when a message is received.  
Type `/help` to view all commands, or view the [Chat Commands](https://github.com/NebulaModTeam/nebula/wiki/Chat-Commands) wiki page for more info.  

### Dedicated Server

The mod supports running the server in a non-GPU environment. Check [the wiki page](https://github.com/NebulaModTeam/nebula/wiki/Setup-Headless-Server) to learn how to set it up and view available command-line arguments.  

### What is the current status?

Check the [Wiki](https://github.com/NebulaModTeam/nebula/wiki/About-Nebula) for an overview of features.  

The multiplayer mod currently supports the Dark Fog combat mode in the latest game version (0.10.34.x).  
Most battle aspects are synchronized, with only a few features still work in progress.  

<details>
<summary>List of peace mode syncing features (click to expand)</summary>

- [x] Server / Client communication
- [x] Custom Multiplayer menu in-game
- [x] Player Movement syncing on Planet
- [x] Player Movement syncing in Space
- [x] Player VFX syncing (jetpack, torch, ...)
- [x] Player SFX syncing (footsteps sound, torch sound, ...)
- [x] Players appearances syncing
- [x] Game Time (UPS) syncing
- [x] Universe settings syncing
- [x] Client planet loading from server
- [x] Planet vegetation mining syncing
- [x] Planet resources syncing
- [x] Build preview syncing
- [x] Entity creation syncing
- [x] Entity desctruction syncing
- [x] Entity upgrade syncing
- [x] Dyson spheres syncing
- [x] Researches syncing
- [x] Factories statistics syncing (some new extra info is not sync)
- [x] Containers inventory syncing
- [x] Building Interaction syncing
- [x] Belts interaction syncing (pickup, putdown)
- [x] Trash (dropped items) syncing
- [x] Interstellar Station syncing
- [x] Drones events syncing
- [x] Foundation syncing (terrain deformation)
- [x] Server state persistence
- [x] Power network syncing (request power from dyson sphere)
- [x] Warning alarm syncing
- [x] Broadcast notification syncing (events with guide icon)
- [x] Logistics Control Panel (I) syncing (entry list and detail panel)
- [x] Planet Memo syncing
- [ ] Goal system (currently not available in client)
- [x] Custom dashboard (persisted across reconnects and star system warps)
- [x] Tutorial and advisor tips syncing (progress preserved across reconnects)
- [x] Wireless charge tower (power will not sync when mecha is charging)

</details>


<details>
<summary>List of combat mode syncing features (click to expand)</summary>

- [x] Sync settings of new building (BAB, turrets)
- [x] Sync combat settings
- [x] Sync DF ground enemy create/destroy events (factory.enemyPool)
- [x] Sync DF ground units activate/deactivate event 
- [x] Sync DF space enemy create/destroy events (spaceSector.enemyPool)
- [x] Sync DF space units activate/deactivate events
- [x] Sync DF planet base exp level and threat
- [x] Sync DF space hive exp level and threat
- [x] Sync loot and loot filter table
- [x] Sync mecha shooting weapons
- [x] Sync mecha bombing
- [x] Sync mecha death and respawn animation
- [x] Sync mecha personal shield to block projectiles
- [x] Sync DF base awake events (player lock with weapon, player nearby, under attack)
- [x] Sync DF base threat and launch assault event
- [x] Patch DF unit to search for nearest alive mecha (sensor range)
- [x] Patch DF turret to search for nearest alive mecha (attack when within attack range or counterattack)
- [x] Sync the hatred targets changes so DF units are attacking the same target
- [x] Sync building repair drone (imperfect)
- [x] Sync building kill event (server fully authorized)
- [x] Sync building reconstruct event
- [x] Sync DFRelay ArriveBase/ArriveDock/LeaveBase/LeaveDock events
- [x] Sync Remove base pit event
- [x] Sync TryCreateNewHive, DispatchFromHive events
- [x] Sync hive realize and open/close preview events
- [x] Sync DF hive awake events (player lock with weapon, player nearby, under attack)
- [x] Sync DF hive threat level and launch assault event
- [x] Patch DF unit to search for nearest alive mecha (sensor range)
- [x] Patch DF turret to search for nearest alive mecha (attack when within attack range or counterattack)
- [x] Show base/hive/relay invasion events in chat
- [x] Sync Dark Fog communicator (aggressiveness and truce)
- [ ] Sync kill stats
- [ ] Show remote mecha combat drone fleet animation
- [ ] Show remote mecha spacecraft fleet animation
- [ ] Show ground-to-space attacks animation on client for remote planets (missile turrets, plasma cannon)
- [ ] Show space-to-ground attacks animation for remote planets (lancers invading with sweep laser and bomber)

</details>

### API Documentation

This mod has an API that makes it easier for other mod developers to make their mods compatible with Nebula. If you are a mod developer and you want your mods to be compatible, follow the instructions [here](https://github.com/NebulaModTeam/nebula/wiki/Nebula-mod-API).

### How can I contribute?

Contributions are welcome! Please feel free to open an issue or submit a pull request. Contribution documentation can be found here: [Wiki](https://github.com/NebulaModTeam/nebula/wiki/Setting-up-a-development-environment).
