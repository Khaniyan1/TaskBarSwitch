# TaskbarSwitch

A small Windows tray app that switches your taskbar settings depending on whether an
external monitor is connected, so you can have one setup on the laptop screen alone and
another when docked. Handy when your laptop is OLED and your monitor super-wide.

## Build

```
dotnet build -c Release
```

Output: `bin\Release\net48\TaskbarSwitch.exe` (.NET Framework 4.8, no extra runtime needed on Windows 11).

## License

Copyright (C) 2026 Jacob Engelbrecht

This program is free software: you can redistribute it and/or modify it under the terms
of the GNU General Public License as published by the Free Software Foundation, either
version 3 of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
See the [LICENSE](LICENSE) file for details.
