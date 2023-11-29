# WyzeSense
Project to interface with Wyze V1 and V2 sensors/Keypad using the V1 Bridge. 

It is currently in development and should not be used in a production environment until you understand all of the potential issues that are actively being resolved. 

------
## Roadmap
- Re-factor and enable web interface
- Add MQTT output formatting tool
- Add MQTT configuration via web interface

------
#### WyzeSenseCore

The heart of the project. 
Based on work from [HclX](https://github.com/HclX). Additional functionality found using [Ghidra](https://github.com/NationalSecurityAgency/ghidra).
Certaintly needs some refactoring but that isn't nearly as fun as uncovering new features.
1. [HclX/WyzeSensePy](https://github.com/HclX/WyzeSensePy)
2. [AKSnowman/WyzeSense](https://github.com/AK5nowman/WyzeSense)
   
------
#### WyzeSenseUpgrade

Console application to flash the hms cc1310 firmware to the v1 bridge. Use this at own risk - not all checks added.
1. [deeplyembeddedWP/cc2640r2f-sbl-linux](https://github.com/deeplyembeddedWP/cc2640r2f-sbl-linux)
2. [CC1310 Technical Reference Manual](https://www.ti.com/lit/ug/swcu117i/swcu117i.pdf?ts=1619413277495)

------
#### WyzeSenseApp

Console application to test WyzeSenseCore functionality

------
#### WyzesenseBlazor

Test project to bridge Wyze Sense to MQTT in a Blazor server app. 

------
#### License

This project is licensed under the Apache-2.0 license.

