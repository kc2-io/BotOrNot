"use strict";
const byId = (id) => document.getElementById(id);
const number = (value, digits = 0, suffix = "") => value == null ? "—" : `${Number(value).toFixed(digits)}${suffix}`;
let renderedSnapshot;

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  node.textContent = text;
  return node;
}

function render(payload) {
  const snapshot = payload.snapshot;
  byId("stats").hidden = !snapshot || !snapshot.statistics.matches;
  byId("recent-section").hidden = !snapshot || !snapshot.recentMatches.length;
  byId("empty").hidden = !!snapshot?.statistics.matches;
  byId("scope").textContent = snapshot ? `LATEST ${snapshot.scanLimit} REPLAYS` : "REPLAY LIBRARY";

  if (!payload.hasDirectory) byId("empty").textContent = "Select a replay folder in BotOrNot";
  else if (!snapshot && payload.updatesDelayed) byId("empty").textContent = "Waiting for library updates";
  else if (!snapshot) byId("empty").textContent = "Loading library…";
  else byId("empty").textContent = "Waiting for replays";

  if (snapshot && snapshot.updatedAt !== renderedSnapshot) {
    const stats = snapshot.statistics;
    byId("matches").textContent = number(stats.matches);
    byId("wins").textContent = number(stats.wins);
    byId("win-rate").textContent = number(stats.winRate, 1, "%");
    byId("avg-kills").textContent = number(stats.averageKills, 1);
    byId("avg-bots").textContent = number(stats.averageBotPercent, 1, "%");
    byId("recent").replaceChildren(...snapshot.recentMatches.map(match => {
      const row = element("div", "match", "");
      const mode = element("div", "mode", match.gameMode || "Unknown mode");
      mode.title = match.gameMode;
      const details = match.analysisStatus === "Complete" ? match.region : `${match.region} · ${match.analysisStatus}`;
      mode.append(element("span", "detail", details));
      const split = element("div", "kills-split", `${number(match.playerKills)} / `);
      split.append(element("span", "bot-kills", number(match.botKills)));
      row.append(mode,
        element("div", match.placement === "1" ? "place win" : "place", match.placement ? `#${match.placement}` : "—"),
        element("div", "", number(match.kills)), split,
        element("div", "percent", number(match.botPercent, 1, "%")));
      return row;
    }));
  }
  renderedSnapshot = snapshot?.updatedAt;
  byId("updated").textContent = snapshot
    ? `Updated ${new Date(snapshot.updatedAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}` : "";
  let status = payload.autoRefreshEnabled ? `Auto refresh · every ${payload.autoRefreshMinutes} min` : "Auto refresh paused";
  if (payload.isScanning) status = "Refreshing library…";
  if (snapshot?.failedCount) status += ` · ${snapshot.failedCount} replay files unavailable`;
  if (payload.updatesDelayed) status = "Updates delayed · keeping last results";
  byId("status").textContent = status;
}

async function poll() {
  try {
    const response = await fetch("/api/snapshot", { cache: "no-store", signal: AbortSignal.timeout(3500) });
    if (!response.ok) throw new Error("Overlay unavailable");
    render(await response.json());
  } catch {
    // Keep the last rendered results through app restarts and transient disconnects.
    byId("status").textContent = "Updates delayed · reconnecting to BotOrNot…";
  } finally {
    setTimeout(poll, 2000);
  }
}
poll();
