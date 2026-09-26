# Third-party mod redistribution

AutoModSync is a transport and synchronization tool. It does **not** grant, expand, or replace the license or redistribution permissions of any third-party mod or other file that a server operator chooses to synchronize.

## Server-operator responsibility

Before configuring AutoModSync to send a third-party mod or related asset to connecting clients, the server operator is responsible for determining that the applicable license, author permission, or other authorization permits that redistribution and for complying with any conditions that apply.

Those conditions may include attribution, preserving copyright or license notices, providing source code or source availability, limiting redistribution to particular contexts, or other requirements imposed by the rights holder.

A server being private, password-protected, limited to friends, or noncommercial does **not by itself** grant redistribution rights that are not otherwise provided by the mod's license or rights holder.

This is especially important for public servers, where synchronized files may be distributed to an unrestricted number of players. Mods whose redistribution rights are prohibited or unclear should not be served through AutoModSync unless the operator obtains appropriate permission from the rights holder.

## Scope of AutoModSync

AutoModSync itself does not bundle arbitrary third-party gameplay mods and does not fetch them from Nexus Mods, Thunderstore, GitHub, or other mod repositories on behalf of a server operator. The operator selects the local BepInEx files that the server advertises and transfers.

AutoModSync's MIT license applies only to AutoModSync-authored code. It does not relicense third-party mods that happen to be synchronized through AutoModSync.

AutoModSync does not attempt to determine whether a selected third-party file may legally be redistributed. License and permission review remains an operator responsibility.

## Recommended operator practice

- Keep a record of the source and license/permissions for every third-party mod the server is configured to distribute.
- Preserve required attribution and license notices.
- Exclude mods that prohibit redistribution or whose permissions are unclear unless you obtain permission.
- Use server-only classification for files clients do not need.
- Review the payload again before opening a server to the public or materially changing its mod set.

This document describes the AutoModSync project's distribution policy and is not legal advice.
