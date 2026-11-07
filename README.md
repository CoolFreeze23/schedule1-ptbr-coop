# Schedule I em Português (Brasil) + S1 Co-op Talk

Play [Schedule I](https://store.steampowered.com/app/3164500/Schedule_I/) in Brazilian Portuguese, with better co-op. Unofficial fan mods, not affiliated with TVGS.

## Instalar / Install

Abra o **PowerShell** (tecla Windows, digite `powershell`, Enter), cole este comando e aperte Enter:

Open **PowerShell** (Windows key, type `powershell`, Enter), paste this command and press Enter:

```powershell
irm https://github.com/CoolFreeze23/schedule1-ptbr-coop/releases/latest/download/install.ps1 | iex
```

Ele acha o jogo, instala o que falta e abre o jogo sozinho. A primeira abertura demora alguns minutos e parece travada: é normal, não feche. Para atualizar, rode o mesmo comando. Todos os jogadores precisam instalar.

It finds the game, installs what is missing and starts the game. The first launch takes a few minutes and looks frozen: this is normal, do not close it. Run the same command to update. Every player needs it installed.

Seus saves nunca são alterados. Your saves are never changed.

## O que vem no pacote / What you get

**Portuguese translation**
- Menus, dialogue, items, mixes and hints, written to feel native, with the jokes adapted
- Repainted signs, labels and textures, and rebuilt 3D signs
- Short NPC voice lines in Brazilian Portuguese, synthesized with Chatterbox from invented character voices
- R$, 24-hour clock and metric units
- A language option in Ajustes > Tela, which switches everything back to English live

**Co-op**
- See what your partner hears on calls and in NPC conversations
- A colored icon for each player on the compass and side panel, with distance, area and activity
- `Y` chat
- `F7` ping, `Shift+F7` call for help, `F8` conversation history
- `/grana 500` (or `/cash 500`) gives money to the nearest player
- Alerts for players waiting at the bed, police chases, arrests, knockouts, joins and leaves
- An end-of-day sales scoreboard
- Repairs for missing or moved items and stuck management menus, automatically or with `/sync`

Keys can be changed in `UserData\MelonPreferences.cfg`.

## Problemas / Troubleshooting

- **Antivirus warning:** MelonLoader works by adding `version.dll` to the game folder, which some antivirus programs flag. It is required for the mods to load.
- **Some text is in English after a game update:** the translation was built for game version 0.4.7f12. New text stays in English until the mod is updated.
- **Co-op problems:** send `UserData\S1CoopTalk_sync.log` from the game folder when reporting an issue.

## Remover / Uninstall

```powershell
& ([scriptblock]::Create((irm https://github.com/CoolFreeze23/schedule1-ptbr-coop/releases/latest/download/install.ps1))) -Uninstall
```

## Building from source

Needs the .NET SDK and the game with MelonLoader installed and launched once. `shared/Game.props` assumes the default Steam path; change `GameDir` if yours differs.

```powershell
dotnet build S1PortuguesBR -c Release
dotnet build S1CoopTalk -c Release
```

## Credits

Built on [MelonLoader](https://github.com/LavaGang/MelonLoader) (Apache-2.0). Voices made with [Chatterbox](https://github.com/resemble-ai/chatterbox) (MIT). Schedule I and its assets belong to TVGS.
