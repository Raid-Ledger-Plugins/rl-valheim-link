# Third-party sources: terms check (ROK-1834 acceptance)

Item names and icons that Raid Ledger hosts come **only** from this mod's own
ObjectDB export (`kind: "items"`), produced in-game from the installed build.
Nothing is copied from third-party sites. The two sites named in the story are
used as **reference and link-out targets only**. Checked 2026-10-18.

| Site | What was checked | Result | What we do |
|---|---|---|---|
| https://valheim.weirdgloop.org/ (Valheim Wiki, hosted by Weird Gloop) | Footer licence notice; https://meta.weirdgloop.org/w/Licensing; https://weirdgloop.org/terms | Text is **CC BY-NC-SA 3.0** (non-commercial, share-alike, attribution). Media is **not** under the text licence: "should not be assumed to be available under the same license as the text"; each file's description page governs (game art is Iron Gate's). The Terms of Use bar automated use "without prior consent from the Company". | **No scraping, no bulk copy, no icon copy.** Raid Ledger may link out to `https://valheim.weirdgloop.org/w/<Item name>` for an item page. |
| https://www.valheim.tools/items | Attempted on 2026-10-18 with the built-in browser and with curl. | The site sits behind a bot-check interstitial ("Performing security verification") and did not serve any page or terms to automated access. No terms text could be read. | **Nothing copied**, no automated access attempted beyond the first page load. Link-out only (`https://www.valheim.tools/items`). If the operator wants anything more than a link, read the site's terms in a normal browser first and record them here. |

Game assets (icons, names) are Iron Gate AB's copyright. The icons in the
mod's atlas are rendered from the player's own installed game files by the
player's own client and uploaded to the operator's private Raid Ledger
instance for the group's use; they are not redistributed by this repository
(no icon, atlas or game file is committed).
