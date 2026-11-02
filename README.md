# TaskbarSwitch

I wanted my taskbar hidden at the bottom on my laptop (OLED, so no static taskbar burning in),
but always visible on the left of my super-wide monitor when docked. Windows can't do that,
so I made this.

It sits in the tray and switches the taskbar between two setups depending on whether an
external monitor is plugged in. It also pulls windows back onto the laptop screen when you
undock, and puts them back when you dock again.

## Download

Grab [TaskbarSwitch.exe](https://github.com/Khaniyan1/TaskBarSwitch/releases/latest/download/TaskbarSwitch.exe)
and run it. It installs itself, starts with Windows and updates itself.

It isn't signed, so Windows may warn you the first time. Click "More info" and "Run anyway".

## Build it yourself

```
dotnet build -c Release
```

## License

Copyright (C) 2026 Jacob Engelbrecht. Free software under the GPL v3, see [LICENSE](LICENSE).
