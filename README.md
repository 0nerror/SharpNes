================================================================================
                              SharpNes Emulator
                         A Nintendo Entertainment System Emulator
================================================================================

KEYBOARD CONTROLS
-----------------

Game Controls:
  Arrow Keys      - D-Pad (Up, Down, Left, Right)
  Z               - B Button
  X               - A Button
  Enter           - Start
  Right Shift     - Select

Emulator Controls:
  F1              - Open ROM file dialog
  F2              - Reset game
  F3              - Stop/unload ROM
  F4              - Save state
  F5              - Load state
  Escape          - Quit application


CONTROLLER SUPPORT
------------------

Xbox 360 / Xbox One controllers are supported:
  D-Pad           - D-Pad
  A Button        - A Button
  B Button        - B Button
  X Button        - B Button (alternate)
  Start           - Start
  Back/View       - Select


SAVE STATES
-----------

Save states allow you to save your exact progress at any point in a game
and restore it later.

  - Press F4 to save your current state
  - Press F5 to load a previously saved state

Save state files are stored in the same directory as the ROM file with
a ".state" extension (e.g., "game.nes.state").

Note: You must have the same ROM loaded to restore a save state.


RUNNING THE EMULATOR
--------------------

You can run the emulator in two ways:

1. Without arguments (select ROM via file dialog):
   dotnet run --project src/SharpNes.App/SharpNes.App.csproj

2. With a ROM file argument:
   dotnet run --project src/SharpNes.App/SharpNes.App.csproj "path/to/game.nes"


SUPPORTED MAPPERS
-----------------

The following NES mappers are currently supported:

  Mapper 0  (NROM)   - Super Mario Bros, Donkey Kong, etc.
  Mapper 1  (MMC1)   - Legend of Zelda, Metroid, etc.
  Mapper 2  (UxROM)  - Mega Man, Castlevania, Duck Tales, etc.
  Mapper 3  (CNROM)  - Gradius, Paperboy, etc.
  Mapper 4  (MMC3)   - Super Mario Bros 2/3, Kirby's Adventure, etc.
  Mapper 7  (AxROM)  - Battletoads, A Nightmare on Elm Street, etc.
  Mapper 9  (MMC2)   - Punch-Out!!
  Mapper 66 (GxROM)  - Super Mario Bros + Duck Hunt


BUILDING FROM SOURCE
--------------------

Requirements:
  - .NET 8.0 SDK or later
  - SDL2 library

Build command:
  dotnet build

Run command:
  dotnet run --project src/SharpNes.App/SharpNes.App.csproj


================================================================================
