# FishySteamworks source pin

Source: https://github.com/FirstGearGames/FishySteamworks/tree/0320459da446a95fc109aef66d42e1337e3b5fff/FishNet/Plugins/FishySteamworks

Revision: `0320459da446a95fc109aef66d42e1337e3b5fff`.

Only C# transport sources and the upstream MIT license are vendored. Steamworks.NET comes from the existing Unity package. The optional upstream SteamManager is omitted; Two Birds owns the Steam lifecycle.

Local adaptations:

- P2P enabled by default; seven remote slots by default. `SetMaximumClients` persists the value used by server startup.
- Connection timeout belongs to `SessionController`; the upstream timeout worker and `Thread.Abort` path are removed. Native shutdown runs through the Unity lifecycle instead of a finalizer.
- Server stop closes remote connections and clears queued host packets. Incoming host packet arrays return to the pool after delivery.
- Steam connection callbacks are scoped to the current client socket or server listener.

Preserve these adaptations when updating the pin. `GameSteamTransport` supplies development payload accounting without changing FishNet's cart motion extension.
