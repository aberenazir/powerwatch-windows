# Power Watch for Windows

A Windows version of the Power Watch menu bar app. It shows how much power your laptop is using, which apps (including hidden background ones) are using it, and how long the battery will last. It also lets you quit apps.

- **Icon by the clock:** shows the live watts on battery, coloured green, orange or red by how heavy the use is. Hover over it for the time left; click it to open the panel.
- **Now:**
  - an advice card: is this fine, the time left with a likely range, and accuracy checks;
  - the power use over the last 30 minutes;
  - every app with its share of power, CPU and memory, advice, and Quit / Force quit;
  - Focus, which quits everything except the app you're using;
  - what Windows itself is doing.
- **Modes & tips:** battery time in each Windows power mode (Best power efficiency, Balanced, Best performance) and the best mode for what you're doing. It also has tips you can act on.
- **Checkup:** covers restarts, memory, sleep settings, browser tabs, search indexing, Windows Update and virus-scan activity, the power mode on battery, startup apps and battery health. It also shows what it has learned about you: the estimate tested on your own history, and what each app costs.

Windows only measures the laptop's power use while it's on battery, and it doesn't measure power per app. Each app's share is therefore estimated from its processor and graphics-chip use.

## Install

Download **PowerWatch-Setup.exe** from the [latest release](https://github.com/aberenazir/powerwatch-windows/releases/latest) and double-click it. It installs for your user account, adds a Start menu entry, starts with Windows and updates itself.
