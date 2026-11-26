# Launch an Amazon Luna game in Playnite

1. Select **Add Luna Game** in the desktop library. The form starts with [Epic's Fortnite Play Now link](https://www.fortnite.com/mobile/amazon-luna).
2. Enter a name and the HTTPS game link from Amazon Luna, then select **Save**. Create a separate entry for each experience you want in your library.
3. Select the game and press **Play** in desktop or fullscreen. Chrome opens the game link using its usual profile. Switch back to fullscreen Playnite to return to the selected game.

Select **Configure Launch** to edit a saved name or link. To browse these entries, choose **Amazon Luna** in the desktop source filter.

Playnite looks for `google-chrome-stable` or `google-chrome` in `/usr/bin`, `/usr/local/bin`, and `/bin`. For another Chrome installation, set `PLAYNITE_LUNA_CHROME` to the absolute executable path before starting Playnite.

## Behavior contract

Playnite stores each entry as an Amazon Luna library game with a v10-compatible URL play action. The game path identifies the experience and the action keeps the query string. See [v10 GenericGameController](../../legacy/source/Playnite/Controllers/GenericGameController.cs) and [v10 GameAction](../../legacy/source/PlayniteSDK/Models/GameAction.cs).

A successful launch message confirms that Playnite started the Chrome command. Luna handles login, eligibility, controller input in the browser, and starting the stream. Playnite does not track the browser or record Luna playtime.
