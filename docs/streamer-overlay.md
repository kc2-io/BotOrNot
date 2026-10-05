# Streamer browser overlay

BotOrNot can show library totals and recent matches in a browser source while you
stream. The overlay refreshes from the same replay folder and scan limit as the
Library. Keep BotOrNot running on the same computer as your streaming software.

## Set up the overlay

1. In the Library, select your Fortnite replay folder and run **Apply & Scan**.
2. Enable **Auto refresh library** and choose an interval. The default is ten
   minutes; use one minute for more frequent updates. Apply the interval.
3. Click **Enable browser overlay**.
4. Click **Open preview** to check the layout, then **Copy overlay URL**.
5. In your streaming software, add a browser source and paste the URL. Start with
   a viewport of **760 × 500** pixels, then position or scale it in your scene.

The page has a transparent outer background and a dark statistics panel. It
shows matches, wins, win rate, average kills, average lobby bot percentage, and
the newest three matches. Human and bot kill counts refer to the replay owner.
Unknown values appear as a dash. Names, replay filenames, and folder paths are
excluded from the overlay.

## Refresh and scope

The overlay covers the newest replay files up to the configured **Replay scan
limit**, which defaults to 50. These are library totals, not stream session
totals. Opponent filters in the Library do not change the overlay.

BotOrNot checks for new or changed replay files at the configured library
interval. The browser fetches the latest completed results every two seconds;
those requests do not start additional scans. The previous totals remain visible
while BotOrNot analyzes files. Refresh continues when you open match details,
as long as the overlay is enabled and library auto refresh is on.

Results become available after Fortnite writes a replay and BotOrNot scans it.
Files that are still being written or temporarily unreadable retry on a later
scan. Partial results show how many files were unavailable. A failed refresh
keeps the last completed results with an updates-delayed message. A successfully
scanned empty folder shows **Waiting for replays**. Selecting a different folder
clears the old overlay results.

## Port and lifecycle

The default URL is `http://127.0.0.1:17843/`. The port and enabled preference are
saved, so an enabled overlay starts again when BotOrNot launches. Closing the app
stops the server; a browser source that stays loaded retains its last results and
reconnects when the app starts again. A newly loaded source requires the app to
be running.

If the port is occupied, BotOrNot reports the conflict and leaves the overlay
disabled. Enter a different port, click **Apply port**, then enable the overlay
again. Ports must be between 1024 and 65535. Update your browser source URL if
you change the port. Disable the overlay before editing its port.

The server listens only on this computer. Streaming from a second computer is
outside this version's scope.

## Implementation

The app's existing `LibraryViewModel` remains the single scan and refresh owner.
Enabling the overlay keeps that owner's timer active during match navigation.
Both the Library and overlay use `LibraryStatistics` for aggregate calculations.
The library retains progressive loading; `StreamerOverlayState` publishes an
immutable, unfiltered broadcast snapshot after a scan drains successfully.

`StreamerOverlayServer` uses an embedded Kestrel HTTP host bound to IPv4 loopback.
It serves only bundled HTML, CSS, JavaScript, and a read-only `/api/snapshot`
endpoint. Requests never access UI collections or trigger scans. Assets are
embedded in the application assembly, including in the self-contained release;
there is no separate web frontend build or installation.
