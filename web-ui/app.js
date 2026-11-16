"use strict";

const byId = id => document.getElementById(id);
const element = (tag, className, text) => {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text) node.textContent = text;
  return node;
};
const translations = {
  en: {
    archiveRail: "Archive server",
    theme: "Toggle theme",
    lightTheme: "Switch to light theme",
    darkTheme: "Switch to dark theme",
    archive: "Archive",
    channelsAndThreads: "Channels and threads",
    channels: "Channels",
    history: "History",
    searchChannel: "Search this channel",
    search: "Search",
    messageHistory: "Message history",
    loadOlder: "Load older messages",
    loadingArchive: "Loading archive…",
    retry: "Retry",
    readOnlyArchive: "Read-only archive",
    language: "Language",
    missingFile: " · missing from archive",
    loadingMessages: "Loading messages…",
    noMatches: "No matches in this channel.",
    noMessages: "This channel has no archived messages.",
    messagesLoadFailed: "Could not load messages.",
    selectThread: "Select a thread from the channel list.",
    noThreads: "This channel has no archived threads.",
    incompleteSnapshot: "Incomplete snapshot",
    completeSnapshot: "Complete snapshot",
    noChannels: "The archive has no channels.",
    archiveLoadFailed: "Could not open the archive.",
    login: "Sign in with Discord",
    logout: "Sign out",
    loginRequired: "Sign in with Discord to open the archive.",
    loginDenied: "That Discord account was not granted access to this archive.",
    loginFailed: "Discord could not verify your account. Try signing in again.",
    adminsOnly: "Administrators only",
    restrictedArchive: "The archive is open to its administrators only.",
    deleteChannel: "Delete channel",
    deleteThread: "Delete thread",
    confirmDelete: name => `Permanently delete “${name}” with its messages and threads from this archive?`,
    accessSaved: "Access updated.",
    channelDeleted: "Removed from the archive.",
    actionFailed: "Could not complete the action.",
    accessList: "Manage access",
    users: "Users",
    guilds: "Guilds",
    administrator: "Administrator",
    saveAccess: "Add or update",
    close: "Close",
    removeAccess: "Remove",
    hideChannel: "Hide channel",
    showChannel: "Show channel",
    hiddenChannel: "Hidden from regular viewers",
  },
  ru: {
    archiveRail: "Сервер архива",
    theme: "Переключить тему",
    lightTheme: "Включить светлую тему",
    darkTheme: "Включить тёмную тему",
    archive: "Архив",
    channelsAndThreads: "Каналы и ветки",
    channels: "Каналы",
    history: "История",
    searchChannel: "Поиск по каналу",
    search: "Найти",
    messageHistory: "История сообщений",
    loadOlder: "Загрузить более ранние",
    loadingArchive: "Загружаю архив…",
    retry: "Повторить",
    readOnlyArchive: "Архив доступен для чтения",
    language: "Язык",
    missingFile: " · отсутствует в архиве",
    loadingMessages: "Загружаю сообщения…",
    noMatches: "Совпадений в этом канале нет.",
    noMessages: "В архиве этого канала нет сообщений.",
    messagesLoadFailed: "Не удалось загрузить сообщения.",
    selectThread: "Выберите ветку в списке каналов.",
    noThreads: "В архиве этого канала нет веток.",
    incompleteSnapshot: "Неполный снимок",
    completeSnapshot: "Полный снимок",
    noChannels: "В архиве нет каналов.",
    archiveLoadFailed: "Не удалось открыть архив.",
    login: "Войти через Discord",
    logout: "Выйти",
    loginRequired: "Войдите через Discord, чтобы открыть архив.",
    loginDenied: "Этому аккаунту Discord не предоставлен доступ к архиву.",
    loginFailed: "Discord не смог проверить ваш аккаунт. Попробуйте войти ещё раз.",
    adminsOnly: "Только для администраторов",
    restrictedArchive: "Архив открыт только для администраторов.",
    deleteChannel: "Удалить канал",
    deleteThread: "Удалить ветку",
    confirmDelete: name => `Удалить «${name}» и все его сообщения и ветки из архива без возможности восстановления?`,
    accessSaved: "Доступ обновлён.",
    channelDeleted: "Удалено из архива.",
    actionFailed: "Не удалось выполнить действие.",
    accessList: "Управление доступом",
    users: "Пользователи",
    guilds: "Гильдии",
    administrator: "Администратор",
    saveAccess: "Добавить или обновить",
    close: "Закрыть",
    removeAccess: "Удалить",
    hideChannel: "Скрыть канал",
    showChannel: "Показать канал",
    hiddenChannel: "Скрыт от обычных пользователей",
  },
};

const languagePicker = byId("language");
const languageUrl = new URL(location.href);
function storedLanguage() {
  try { return localStorage.getItem("archive-language"); }
  catch { return null; }
}
const language = (languageUrl.searchParams.get("lang") ?? storedLanguage()) === "ru" ? "ru" : "en";
const translate = key => translations[language][key];
document.documentElement.lang = language;
languagePicker.value = language;
for (const node of document.querySelectorAll("[data-text]")) node.textContent = translate(node.dataset.text);
for (const node of document.querySelectorAll("[data-label]")) node.setAttribute("aria-label", translate(node.dataset.label));
for (const node of document.querySelectorAll("[data-placeholder]")) node.placeholder = translate(node.dataset.placeholder);
languagePicker.addEventListener("change", () => {
  const selected = languagePicker.value;
  try { localStorage.setItem("archive-language", selected); } catch { /* The URL retains the selection. */ }
  const url = new URL(location.href);
  url.searchParams.set("lang", selected);
  location.replace(url);
});

const threadTypes = new Set([10, 11, 12]);
const containerTypes = new Set([15, 16]);
const dateFormat = new Intl.DateTimeFormat(language, { dateStyle: "long" });
const timeFormat = new Intl.DateTimeFormat(language, { timeStyle: "short" });
const snapshotFormat = new Intl.DateTimeFormat(language, { dateStyle: "medium", timeStyle: "short" });
const timeline = byId("timeline");
const messages = byId("messages");
const older = byId("older");
const retry = byId("retry");
const search = byId("search");
const query = byId("query");
const adminsOnly = byId("admins-only");
const deleteButton = byId("delete-channel");
const hideButton = byId("hide-channel");
const accessDialog = byId("access-dialog");
const accessKind = byId("access-kind");
const accessId = byId("access-id");
const logout = byId("logout");
const loginPanel = byId("login-panel");
const channels = new Map();
const channelButtons = new Map();
let current = null;
let before = null;
let term = "";
let firstDay = null;
let requestNumber = 0;
let retryAction = null;
let session = null;

function storedTheme() {
  try { return localStorage.getItem("archive-theme"); }
  catch { return null; }
}

function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  const button = byId("theme");
  const label = theme === "dark" ? translate("lightTheme") : translate("darkTheme");
  button.textContent = theme === "dark" ? "☀" : "☾";
  button.title = label;
  button.setAttribute("aria-label", label);
}

const systemTheme = matchMedia("(prefers-color-scheme: dark)");
applyTheme(storedTheme() || (systemTheme.matches ? "dark" : "light"));
systemTheme.addEventListener("change", () => {
  if (!storedTheme()) applyTheme(systemTheme.matches ? "dark" : "light");
});
byId("theme").addEventListener("click", () => {
  const next = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
  applyTheme(next);
  try { localStorage.setItem("archive-theme", next); }
  catch { /* The theme still works when browser storage is unavailable. */ }
});

class ArchiveError extends Error {
  constructor(message, status) {
    super(message);
    this.status = status;
  }
}

async function api(path, body) {
  const headers = { "Accept-Language": language };
  const options = { headers };
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
    headers["X-CSRF-Token"] = session.csrf;
    options.method = "POST";
    options.body = JSON.stringify(body);
  }
  const response = await fetch(path, options);
  const data = await response.json();
  if (!response.ok) throw new ArchiveError(data.error, response.status);
  return data;
}

function notice(message, action = null) {
  byId("notice").textContent = message;
  retryAction = action;
  retry.hidden = !action;
}

function symbol(channel) {
  if (threadTypes.has(channel.type)) return "↳";
  if (containerTypes.has(channel.type)) return "▤";
  return "#";
}

function setMenu(open) {
  byId("sidebar").classList.toggle("open", open);
  byId("mobile-menu").setAttribute("aria-expanded", String(open));
}

function assetUrl(id) {
  return "/media/" + encodeURIComponent(id);
}

function avatar(target, id) {
  if (!id) return;
  const image = element("img");
  image.alt = "";
  image.addEventListener("load", () => target.replaceChildren(image), { once: true });
  image.src = assetUrl(id);
}

function renderChannels(data) {
  channels.clear();
  channelButtons.clear();
  for (const channel of data.channels) channels.set(channel.id, channel);
  const groups = new Map(data.categories.map(category => [category.id, { name: category.name, items: [] }]));
  const uncategorized = { name: translate("channels"), items: [] };
  const children = new Map();
  for (const channel of data.channels) {
    if (threadTypes.has(channel.type) && channels.has(channel.parent_id)) {
      if (!children.has(channel.parent_id)) children.set(channel.parent_id, []);
      children.get(channel.parent_id).push(channel);
    } else {
      (groups.get(channel.parent_id) || uncategorized).items.push(channel);
    }
  }

  function renderChannel(channel, parent) {
    const button = element("button", threadTypes.has(channel.type) ? "channel thread" : "channel");
    button.type = "button";
    button.title = channel.hidden ? `${channel.name} · ${translate("hiddenChannel")}` : channel.name;
    button.classList.toggle("hidden-channel", channel.hidden === true);
    const icon = element("span", "symbol", symbol(channel));
    icon.setAttribute("aria-hidden", "true");
    button.append(icon, element("span", "", channel.name));
    button.addEventListener("click", () => {
      if (location.hash !== "#" + channel.id) location.hash = channel.id;
      else openChannel(channel);
    });
    channelButtons.set(channel.id, button);
    parent.append(button);
    for (const thread of children.get(channel.id) || []) renderChannel(thread, parent);
  }

  const nav = byId("channels");
  nav.replaceChildren();
  for (const group of [uncategorized, ...groups.values()]) {
    if (!group.items.length) continue;
    const section = element("section", "group");
    section.append(element("h2", "group-title", group.name));
    for (const channel of group.items) renderChannel(channel, section);
    nav.append(section);
  }
}

function renderAttachment(file) {
  const card = element("div", "attachment");
  if (!file.id) {
    card.append(element("span", "missing", file.name + translate("missingFile")));
    return card;
  }
  const url = assetUrl(file.id);
  if (file.type.startsWith("image/")) {
    const image = element("img");
    image.alt = file.name;
    image.loading = "lazy";
    image.src = url;
    card.append(image);
  } else if (file.type.startsWith("video/") || file.type.startsWith("audio/")) {
    const player = element(file.type.split("/")[0]);
    player.controls = true;
    player.preload = "metadata";
    player.src = url;
    card.append(player);
  }
  const link = element("a", "", file.name);
  link.href = url;
  link.download = file.inline ? "" : file.name;
  card.append(link);
  return card;
}

const emojiSegments = new Intl.Segmenter(undefined, { granularity: "grapheme" });
const unicodeEmoji = /\p{Extended_Pictographic}|\p{Emoji_Presentation}|\u20e3/u;
const customEmoji = /<(a?):([^:<>]+):([0-9]{1,20})>/g;

function emojiImage(name, urls) {
  const image = element("img", "emoji");
  image.alt = image.title = name;
  image.loading = "lazy";
  image.addEventListener("error", () => {
    const next = urls.shift();
    if (next) image.src = next;
    else image.replaceWith(document.createTextNode(name));
  });
  image.src = urls.shift();
  return image;
}

function appendUnicodeEmoji(target, text) {
  let start = 0;
  for (const { segment, index } of emojiSegments.segment(text)) {
    if (segment.includes("\uFE0E") || !unicodeEmoji.test(segment)) continue;
    const code = Array.from(segment, character => character.codePointAt(0).toString(16)).join("-");
    const names = new Set([code, code.replaceAll("-fe0f", "")]);
    target.append(text.slice(start, index), emojiImage(segment, Array.from(names, name => `/emoji/${name}.png`)));
    start = index + segment.length;
  }
  target.append(text.slice(start));
}

function renderMessageText(message) {
  const text = element("p", "message-text");
  let start = 0;
  for (const match of message.content.matchAll(customEmoji)) {
    appendUnicodeEmoji(text, message.content.slice(start, match.index));
    const name = `:${match[2]}:`;
    const asset = message.emojiAssets[match[3] + (match[1] ? ":a" : "")];
    text.append(asset ? emojiImage(name, [assetUrl(asset)]) : name);
    start = match.index + match[0].length;
  }
  appendUnicodeEmoji(text, message.content.slice(start));
  return text;
}

function renderReactions(reactions) {
  const group = element("ul", "reactions");
  const counts = new Intl.NumberFormat(document.documentElement.lang);
  for (const reaction of reactions) {
    const badge = element("li", "reaction");
    if (reaction.asset) badge.append(emojiImage(reaction.name, [assetUrl(reaction.asset)]));
    else appendUnicodeEmoji(badge, reaction.name);
    badge.append(element("span", "", counts.format(reaction.count)));
    group.append(badge);
  }
  return group;
}

function renderMessage(message, date) {
  const article = element("article", "message");
  const picture = element("div", "avatar", Array.from(message.author)[0].toLocaleUpperCase());
  picture.setAttribute("aria-hidden", "true");
  avatar(picture, message.avatar);
  const content = element("div");
  const byline = element("div", "byline");
  const time = element("time", "", timeFormat.format(date));
  time.dateTime = message.timestamp;
  time.title = snapshotFormat.format(date);
  const author = element("strong");
  if (/^[1-9][0-9]{0,19}$/.test(message.authorId ?? "")) {
    const profile = element("a", "", message.author);
    profile.href = `https://discord.com/users/${message.authorId}`;
    profile.target = "_blank";
    profile.rel = "noopener noreferrer";
    author.append(profile);
  } else author.textContent = message.author;
  byline.append(author, time);
  content.append(byline);
  if (message.content) content.append(renderMessageText(message));
  if (message.attachments.length) {
    const attachments = element("div", "attachments");
    for (const file of message.attachments) attachments.append(renderAttachment(file));
    content.append(attachments);
  }
  if (message.reactions.length) content.append(renderReactions(message.reactions));
  article.append(picture, content);
  return article;
}

function prependMessages(page) {
  const fragment = document.createDocumentFragment();
  let previousDay = null;
  let pageFirstDay = null;
  for (const message of page) {
    const date = new Date(message.timestamp);
    const day = dateFormat.format(date);
    pageFirstDay ??= day;
    if (day !== previousDay) fragment.append(element("div", "date-divider", day));
    fragment.append(renderMessage(message, date));
    previousDay = day;
  }
  if (previousDay && previousDay === firstDay) messages.firstElementChild.remove();
  firstDay = pageFirstDay || firstDay;
  messages.prepend(fragment);
}

async function loadMessages(append = false) {
  const request = ++requestNumber;
  const params = new URLSearchParams({ channel: current.id, q: term });
  if (append && before) params.set("before", before);
  older.disabled = true;
  notice(append ? "" : translate("loadingMessages"));
  try {
    const page = await api("/api/messages?" + params);
    if (request !== requestNumber) return;
    const height = timeline.scrollHeight;
    const offset = timeline.scrollTop;
    prependMessages(page.messages);
    before = page.before;
    older.hidden = !before;
    notice(page.messages.length || append ? "" : term ? translate("noMatches") : translate("noMessages"));
    timeline.scrollTop = append ? offset + timeline.scrollHeight - height : timeline.scrollHeight;
  } catch (error) {
    if (request !== requestNumber) return;
    if (session?.enabled && (error.status === 401 || error.status === 403)) {
      await loadArchive();
      return;
    }
    notice(error instanceof ArchiveError && error.message ? error.message : translate("messagesLoadFailed"), () => loadMessages(append));
    if (append) timeline.scrollTop = timeline.scrollHeight;
  } finally {
    if (request === requestNumber) older.disabled = false;
  }
}

function resetMessages() {
  ++requestNumber;
  before = firstDay = null;
  messages.replaceChildren();
  older.hidden = true;
}

function openChannel(channel) {
  if (current) channelButtons.get(current.id)?.removeAttribute("aria-current");
  current = channel;
  channelButtons.get(channel.id).setAttribute("aria-current", "true");
  byId("channel-name").textContent = channel.name;
  byId("channel-symbol").textContent = symbol(channel);
  setMenu(false);
  term = query.value = "";
  resetMessages();
  const container = containerTypes.has(channel.type);
  deleteButton.hidden = !session?.admin;
  hideButton.hidden = !session?.admin;
  hideButton.textContent = translate(channel.hiddenDirectly ? "showChannel" : "hideChannel");
  deleteButton.textContent = translate(threadTypes.has(channel.type) ? "deleteThread" : "deleteChannel");
  search.hidden = container;
  byId("channel-footer").hidden = container || session?.admin === true;
  if (container) {
    const hasThreads = [...channels.values()].some(child => child.parent_id === channel.id);
    notice(hasThreads ? translate("selectThread") : translate("noThreads"));
  } else {
    loadMessages();
  }
}

function followHash() {
  const target = channels.get(location.hash.slice(1));
  if (target) openChannel(target);
}

async function loadArchive() {
  notice(translate("loadingArchive"));
  resetMessages();
  renderChannels({ categories: [], channels: [] });
  current = null;
  byId("server-name").textContent = translate("archive");
  byId("server-icon").textContent = "AS";
  byId("snapshot-date").removeAttribute("datetime");
  byId("channel-name").textContent = translate("history");
  byId("channel-symbol").textContent = "#";
  search.hidden = byId("channel-footer").hidden = deleteButton.hidden = hideButton.hidden = true;
  loginPanel.hidden = logout.hidden = byId("admin-tools").hidden = true;
  try {
    session = await api("/api/session");
    logout.hidden = !session.authenticated;
    if (session.enabled && !session.authenticated) {
      const result = languageUrl.searchParams.get("auth");
      notice(translate(result === "denied" ? "loginDenied" : result === "failed" ? "loginFailed" : "loginRequired"));
      loginPanel.hidden = false;
      return;
    }
    if (session.adminsOnly && !session.admin) {
      notice(translate("restrictedArchive"));
      return;
    }
    const data = await api("/api/snapshot");
    document.title = data.projectName || data.name;
    byId("admin-tools").hidden = !session.admin;
    adminsOnly.checked = session.adminsOnly;
    byId("server-name").textContent = data.name;
    const icon = byId("server-icon");
    icon.textContent = data.name.trim().split(/\s+/).slice(0, 2).map(word => Array.from(word)[0]).join("").toLocaleUpperCase() || "AS";
    avatar(icon, data.icon);
    byId("snapshot-status").textContent = data.incomplete ? translate("incompleteSnapshot") : translate("completeSnapshot");
    const date = byId("snapshot-date");
    date.dateTime = data.startedAt;
    date.textContent = snapshotFormat.format(new Date(data.startedAt));
    renderChannels(data);
    const defaultChannel = channels.get(location.hash.slice(1)) ||
      data.channels.find(channel => !containerTypes.has(channel.type) && !threadTypes.has(channel.type)) ||
      data.channels[0];
    if (defaultChannel) {
      if (!channels.has(location.hash.slice(1))) {
        history.replaceState(null, "", "#" + defaultChannel.id);
      }
      openChannel(defaultChannel);
    } else notice(translate("noChannels"));
  } catch (error) {
    notice(error instanceof ArchiveError && error.message ? error.message : translate("archiveLoadFailed"), loadArchive);
  }
}

async function adminAction(path, body, success, onSuccess) {
  const status = byId("admin-status");
  adminsOnly.disabled = deleteButton.disabled = hideButton.disabled = true;
  status.textContent = "";
  try {
    await api(path, body);
    if (onSuccess) await onSuccess();
    status.textContent = translate(success);
  } catch (error) {
    adminsOnly.checked = session.adminsOnly;
    if (error.status === 401) await loadArchive();
    status.textContent = error.message || translate("actionFailed");
  } finally {
    adminsOnly.disabled = deleteButton.disabled = hideButton.disabled = false;
  }
}

adminsOnly.addEventListener("change", () => {
  const restricted = adminsOnly.checked;
  adminAction("/api/admin/access", { adminsOnly: restricted }, "accessSaved", () => {
    session.adminsOnly = restricted;
  });
});
deleteButton.addEventListener("click", () => {
  const selected = current;
  if (selected && confirm(translate("confirmDelete")(selected.name))) {
    adminAction("/api/admin/channel", { channel: selected.id }, "channelDeleted", loadArchive);
  }
});
hideButton.addEventListener("click", () => {
  if (current) adminAction("/api/admin/visibility", { channel: current.id, hidden: !current.hiddenDirectly }, "accessSaved", loadArchive);
});

async function updateAccess(change) {
  const status = byId("access-status");
  const form = byId("access-form");
  const list = byId("access-items");
  form.inert = list.inert = true;
  status.textContent = "";
  try {
    if (change) await api("/api/admin/members", change);
    const access = await api("/api/admin/members");
    const kind = accessKind.value;
    const entries = kind === "user" ? access.users : access.guilds.map(id => ({ id }));
    list.replaceChildren();
    for (const entry of entries) {
      const item = element("li");
      const remove = element("button", "secondary", translate("removeAccess"));
      remove.type = "button";
      remove.setAttribute("aria-label", `${translate("removeAccess")} ${entry.id}`);
      remove.addEventListener("click", () => updateAccess({ kind, id: entry.id, remove: true }));
      item.append(element("span", "", entry.admin ? `${entry.id} · ${translate("administrator")}` : entry.id), remove);
      list.append(item);
    }
    if (change) {
      accessId.value = "";
      status.textContent = translate("accessSaved");
    }
  } catch (error) {
    if (error.status === 401 || error.status === 403) {
      accessDialog.close();
      await loadArchive();
    } else status.textContent = error.message || translate("actionFailed");
  } finally {
    form.inert = list.inert = false;
  }
}

byId("access-list").addEventListener("click", () => {
  accessDialog.showModal();
  updateAccess();
});
byId("close-access").addEventListener("click", () => accessDialog.close());
accessKind.addEventListener("change", () => {
  byId("access-admin-label").hidden = accessKind.value !== "user";
  updateAccess();
});
byId("access-form").addEventListener("submit", event => {
  event.preventDefault();
  updateAccess({ kind: accessKind.value, id: accessId.value.trim(), admin: byId("access-admin").checked, remove: false });
});
logout.addEventListener("click", async () => {
  try {
    await api("/auth/logout", {});
    location.replace("/");
  } catch (error) {
    if (error.status === 401) await loadArchive();
    else notice(error.message || translate("actionFailed"));
  }
});

search.addEventListener("submit", event => {
  event.preventDefault();
  term = query.value.trim();
  resetMessages();
  loadMessages();
});
query.addEventListener("search", () => {
  if (!query.value && term) search.requestSubmit();
});
older.addEventListener("click", () => loadMessages(true));
retry.addEventListener("click", () => retryAction?.());
byId("mobile-menu").addEventListener("click", () => setMenu(byId("mobile-menu").getAttribute("aria-expanded") !== "true"));
document.addEventListener("keydown", event => {
  if (event.key === "Escape" && byId("sidebar").classList.contains("open")) {
    setMenu(false);
    byId("mobile-menu").focus();
  }
});
window.addEventListener("hashchange", followHash);
loadArchive();
