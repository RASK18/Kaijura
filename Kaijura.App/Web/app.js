const state = {
  activeView: "settings",
  columns: [],
  issues: [],
  config: {},
  connection: {},
  update: {}
};

const transitionMenu = {
  openIssueId: null,
  loadingIssueId: null,
  formTransitionId: null,
  submittingIssueId: null,
  submittingTransitionId: null,
  optionsByIssueId: {},
  errorByIssueId: {}
};

const views = {
  board: document.getElementById("boardView"),
  archive: document.getElementById("archiveView"),
  missing: document.getElementById("missingView"),
  settings: document.getElementById("settingsView")
};

window.chrome.webview.addEventListener("message", event => {
  if (!event.data || !event.data.type) {
    return;
  }

  switch (event.data.type) {
    case "state":
      Object.assign(state, event.data.payload);
      render();
      break;
    case "issueTransitions":
      receiveTransitions(event.data.payload);
      break;
    case "transitionError":
      receiveTransitionError(event.data.payload);
      break;
    case "transitionResult":
      receiveTransitionResult(event.data.payload);
      break;
  }
});

window.addEventListener("DOMContentLoaded", () => {
  bindChrome();
  post("ready");
});

function bindChrome() {
  document.getElementById("installUpdateButton").addEventListener("click", () => post("installUpdate"));
  document.getElementById("settingsForm").addEventListener("submit", event => {
    event.preventDefault();
    saveSettings();
  });
  document.addEventListener("click", closeTransitionMenu);
  document.addEventListener("keydown", event => {
    if (event.key === "Escape") {
      closeTransitionMenu();
    }
  });
}

function render() {
  renderChrome();
  renderSettings();
  renderBoard();
  renderArchive();
  renderMissing();
}

function renderChrome() {
  const activeView = state.activeView || "settings";
  const isRefreshing = state.connection.status === "refreshing";
  const statusMessage = isRefreshing ? "" : state.connection.message || "";
  Object.entries(views).forEach(([name, element]) => element.classList.toggle("hidden", name !== activeView));

  document.querySelector(".topbar").classList.toggle("hidden", !statusMessage);
  document.getElementById("statusMessage").textContent = statusMessage;

  const banner = document.getElementById("updateBanner");
  const installButton = document.getElementById("installUpdateButton");
  const showUpdate = ["available", "ready", "downloading", "failed"].includes(state.update.status);
  banner.classList.toggle("hidden", !showUpdate);
  document.getElementById("updateText").textContent = updateText(state.update);
  installButton.classList.toggle("hidden", !state.update.canInstall);
}

function renderSettings() {
  const config = state.config || {};
  setValue("jiraHost", config.jiraHost || "");
  setValue("userName", config.userName || "");
  setValue("token", "");
  document.getElementById("token").placeholder = config.hasToken ? "Token guardado" : "";
  setValue("jql", config.jql || "");
  setValue("taskIssueTypes", (config.taskIssueTypes || []).join(", "));
  setValue("incidentIssueTypes", (config.incidentIssueTypes || []).join(", "));
  setValue("ignoredCommentAuthors", (config.ignoredCommentAuthors || []).join(", "));
  setValue("refreshMinutes", config.refreshMinutes || 5);
  setValue("maxIssues", config.maxIssues || 1000);
  setValue("updateRepositoryUrl", config.updateRepositoryUrl || "");
}

function renderBoard() {
  renderLane("task", document.getElementById("taskBoard"));
  renderLane("incident", document.getElementById("incidentBoard"));
  renderList(document.getElementById("taskBacklog"), filterIssues("backlog", "task"), true);
  renderList(document.getElementById("incidentBacklog"), filterIssues("backlog", "incident"), true);

  const unmapped = state.issues.filter(issue => issue.kind === "unmapped" && !issue.isMissing && issue.section !== "archived");
  const block = document.getElementById("unmappedBlock");
  block.classList.toggle("hidden", unmapped.length === 0);
  renderList(document.getElementById("unmappedList"), unmapped, false);
}

function renderLane(kind, host) {
  host.innerHTML = "";

  state.columns.forEach(column => {
    const wrapper = document.createElement("div");
    wrapper.className = "column";
    wrapper.innerHTML = `<div class="column-title">${escapeHtml(column.title)}</div>`;

    const list = document.createElement("div");
    list.className = "drop-list";
    list.dataset.section = "board";
    list.dataset.kind = kind;
    list.dataset.column = column.id;
    bindDropList(list);

    renderList(list, filterIssues("board", kind, column.id), true);
    wrapper.appendChild(list);
    host.appendChild(wrapper);
  });
}

function renderArchive() {
  renderList(
    document.getElementById("archiveList"),
    state.issues.filter(issue => issue.section === "archived"),
    false,
    "restore"
  );
}

function renderMissing() {
  renderList(
    document.getElementById("missingList"),
    state.issues.filter(issue => issue.section === "missing" || issue.isMissing),
    false
  );
}

function renderList(host, issues, draggable, action) {
  host.innerHTML = "";

  if (host.classList.contains("drop-list")) {
    bindDropList(host);
  }

  if (issues.length === 0) {
    const empty = document.createElement("div");
    empty.className = "empty";
    empty.textContent = "Sin tickets";
    host.appendChild(empty);
    return;
  }

  issues
    .slice()
    .sort((left, right) => left.sortOrder - right.sortOrder || left.key.localeCompare(right.key))
    .forEach(issue => host.appendChild(createCard(issue, draggable, action)));
}

function createCard(issue, draggable, action) {
  const card = document.createElement("article");
  card.className = "card";
  card.dataset.issueId = issue.id;
  card.draggable = draggable && issue.kind !== "unmapped";

  card.innerHTML = `
    <div class="card-top">
      <button class="issue-key card-action" type="button">${escapeHtml(issue.key)}</button>
      <button class="issue-status" type="button" title="${escapeHtml(issue.jiraStatus || "")}" aria-haspopup="menu" aria-expanded="${transitionMenu.openIssueId === issue.id ? "true" : "false"}">${escapeHtml(issue.jiraStatus || "Sin estado")}</button>
    </div>
    <div class="issue-title">${escapeHtml(issue.summary || "")}</div>
  `;

  if (issue.hasUnreadComment) {
    const unreadButton = document.createElement("button");
    unreadButton.className = "comment-alert";
    unreadButton.type = "button";
    unreadButton.title = unreadTitle(issue);
    unreadButton.setAttribute("aria-label", "Marcar comentario como leido");
    unreadButton.addEventListener("click", event => {
      event.stopPropagation();
      post("markCommentRead", { issueId: issue.id });
    });
    card.appendChild(unreadButton);
  }

  card.querySelector(".issue-key").addEventListener("click", event => {
    event.stopPropagation();
    post("openIssue", { issueId: issue.id });
  });

  card.querySelector(".issue-status").addEventListener("click", event => {
    event.stopPropagation();
    toggleTransitionMenu(issue.id);
  });

  if (draggable) {
    card.addEventListener("dragstart", event => {
      card.classList.add("dragging");
      event.dataTransfer.setData("text/plain", issue.id);
      event.dataTransfer.effectAllowed = "move";
    });
    card.addEventListener("dragend", () => card.classList.remove("dragging"));
  }

  if (issue.section === "board" && issue.column === "Ready") {
    const actions = document.createElement("div");
    actions.className = "card-actions";
    const button = document.createElement("button");
    button.className = "card-action";
    button.type = "button";
    button.textContent = "Archivar";
    button.addEventListener("click", () => post("archiveIssue", { issueId: issue.id }));
    actions.appendChild(button);
    card.appendChild(actions);
  }

  if (action === "restore") {
    const actions = document.createElement("div");
    actions.className = "card-actions";
    const button = document.createElement("button");
    button.className = "card-action";
    button.type = "button";
    button.textContent = "Restaurar";
    button.addEventListener("click", () => post("restoreIssue", { issueId: issue.id }));
    actions.appendChild(button);
    card.appendChild(actions);
  }

  if (transitionMenu.openIssueId === issue.id) {
    card.appendChild(createTransitionMenu(issue));
  }

  return card;
}

function toggleTransitionMenu(issueId) {
  if (transitionMenu.openIssueId === issueId) {
    closeTransitionMenu();
    return;
  }

  transitionMenu.openIssueId = issueId;
  transitionMenu.loadingIssueId = issueId;
  transitionMenu.formTransitionId = null;
  transitionMenu.submittingIssueId = null;
  transitionMenu.submittingTransitionId = null;
  delete transitionMenu.optionsByIssueId[issueId];
  delete transitionMenu.errorByIssueId[issueId];
  render();
  post("loadTransitions", { issueId });
}

function closeTransitionMenu() {
  if (!transitionMenu.openIssueId) {
    return;
  }

  transitionMenu.openIssueId = null;
  transitionMenu.loadingIssueId = null;
  transitionMenu.formTransitionId = null;
  transitionMenu.submittingIssueId = null;
  transitionMenu.submittingTransitionId = null;
  render();
}

function createTransitionMenu(issue) {
  const menu = document.createElement("div");
  menu.className = "status-menu";
  menu.setAttribute("role", "menu");
  menu.addEventListener("click", event => event.stopPropagation());

  const error = transitionMenu.errorByIssueId[issue.id];
  if (error) {
    const message = document.createElement("div");
    message.className = "status-menu-message error";
    message.textContent = error;
    menu.appendChild(message);
  }

  if (transitionMenu.loadingIssueId === issue.id) {
    const loading = document.createElement("div");
    loading.className = "status-menu-message";
    loading.textContent = "Cargando transiciones...";
    menu.appendChild(loading);
    return menu;
  }

  const options = transitionMenu.optionsByIssueId[issue.id];
  if (!options) {
    const loading = document.createElement("div");
    loading.className = "status-menu-message";
    loading.textContent = "Preparando...";
    menu.appendChild(loading);
    return menu;
  }

  if (options.length === 0) {
    const empty = document.createElement("div");
    empty.className = "status-menu-message";
    empty.textContent = "Sin transiciones disponibles";
    menu.appendChild(empty);
    return menu;
  }

  options.forEach(option => {
    menu.appendChild(createTransitionOption(issue, option));

    if (transitionMenu.formTransitionId === option.id) {
      menu.appendChild(createTransitionForm(issue, option));
    }
  });

  return menu;
}

function createTransitionOption(issue, option) {
  if (!option.isEnabled) {
    const disabled = document.createElement("div");
    disabled.className = "status-option disabled";
    disabled.setAttribute("role", "menuitem");
    disabled.setAttribute("aria-disabled", "true");
    disabled.title = option.disabledReason || "Transicion no disponible";
    disabled.textContent = option.label;
    return disabled;
  }

  const button = document.createElement("button");
  button.className = "status-option";
  button.type = "button";
  button.setAttribute("role", "menuitem");
  button.disabled = transitionMenu.submittingIssueId === issue.id;
  button.textContent = option.label;
  button.addEventListener("click", () => {
    if (option.requiresForm) {
      transitionMenu.formTransitionId = transitionMenu.formTransitionId === option.id ? null : option.id;
      delete transitionMenu.errorByIssueId[issue.id];
      render();
      return;
    }

    submitTransition(issue.id, option, {});
  });
  return button;
}

function createTransitionForm(issue, option) {
  const form = document.createElement("form");
  form.className = "status-form";

  if (option.requiresComment) {
    form.appendChild(createTextareaField("comment", "Comentario", true));
  }

  if (option.requiresWorklog) {
    form.appendChild(createInputField("worklogTimeSpent", "Tiempo", "30m, 1h", true));
    form.appendChild(createTextareaField("worklogComment", "Comentario de trabajo", false));
  }

  const textFields = option.requiredTextFields || [];
  textFields.forEach((field, index) => {
    form.appendChild(createTextareaField(textFieldControlName(index), field.name || field.id, true));
  });

  const actions = document.createElement("div");
  actions.className = "status-form-actions";

  const submit = document.createElement("button");
  submit.className = "status-form-submit";
  submit.type = "submit";
  submit.disabled = transitionMenu.submittingIssueId === issue.id;
  submit.textContent = transitionMenu.submittingTransitionId === option.id ? "Cambiando..." : "Cambiar";

  const cancel = document.createElement("button");
  cancel.className = "status-form-cancel";
  cancel.type = "button";
  cancel.textContent = "Cancelar";
  cancel.addEventListener("click", () => {
    transitionMenu.formTransitionId = null;
    delete transitionMenu.errorByIssueId[issue.id];
    render();
  });

  actions.appendChild(cancel);
  actions.appendChild(submit);
  form.appendChild(actions);

  form.addEventListener("submit", event => {
    event.preventDefault();
    if (!form.reportValidity()) {
      return;
    }

    const submittedTextFields = {};
    textFields.forEach((field, index) => {
      submittedTextFields[field.id] = form.elements[textFieldControlName(index)]?.value || "";
    });

    submitTransition(issue.id, option, {
      comment: form.elements.comment?.value || "",
      worklogTimeSpent: form.elements.worklogTimeSpent?.value || "",
      worklogComment: form.elements.worklogComment?.value || "",
      textFields: submittedTextFields
    });
  });

  return form;
}

function createTextareaField(name, labelText, required) {
  const label = document.createElement("label");
  label.className = "status-field";
  label.textContent = labelText;

  const textarea = document.createElement("textarea");
  textarea.name = name;
  textarea.required = required;
  textarea.rows = 3;
  label.appendChild(textarea);
  return label;
}

function textFieldControlName(index) {
  return `textField${index}`;
}

function createInputField(name, labelText, placeholder, required) {
  const label = document.createElement("label");
  label.className = "status-field";
  label.textContent = labelText;

  const input = document.createElement("input");
  input.name = name;
  input.placeholder = placeholder;
  input.required = required;
  input.autocomplete = "off";
  label.appendChild(input);
  return label;
}

function submitTransition(issueId, option, values) {
  transitionMenu.submittingIssueId = issueId;
  transitionMenu.submittingTransitionId = option.id;
  delete transitionMenu.errorByIssueId[issueId];
  render();

  post("changeIssueStatus", {
    issueId,
    transitionId: option.id,
    comment: values.comment || "",
    worklogTimeSpent: values.worklogTimeSpent || "",
    worklogComment: values.worklogComment || "",
    textFields: values.textFields || {}
  });
}

function receiveTransitions(payload) {
  if (!payload || !payload.issueId) {
    return;
  }

  transitionMenu.loadingIssueId = null;
  transitionMenu.optionsByIssueId[payload.issueId] = payload.transitions || [];
  delete transitionMenu.errorByIssueId[payload.issueId];
  render();
}

function receiveTransitionError(payload) {
  if (!payload || !payload.issueId) {
    return;
  }

  transitionMenu.loadingIssueId = null;
  transitionMenu.submittingIssueId = null;
  transitionMenu.submittingTransitionId = null;
  transitionMenu.errorByIssueId[payload.issueId] = payload.message || "Accion no completada";
  render();
}

function receiveTransitionResult(payload) {
  if (!payload || !payload.issueId) {
    return;
  }

  delete transitionMenu.optionsByIssueId[payload.issueId];
  delete transitionMenu.errorByIssueId[payload.issueId];

  if (transitionMenu.openIssueId === payload.issueId) {
    transitionMenu.openIssueId = null;
    transitionMenu.formTransitionId = null;
  }

  transitionMenu.loadingIssueId = null;
  transitionMenu.submittingIssueId = null;
  transitionMenu.submittingTransitionId = null;
  render();
}

function bindDropList(list) {
  if (list.dataset.bound === "true") {
    return;
  }

  list.dataset.bound = "true";

  list.addEventListener("dragover", event => {
    event.preventDefault();
    list.classList.add("drag-over");
  });

  list.addEventListener("dragleave", () => list.classList.remove("drag-over"));

  list.addEventListener("drop", event => {
    event.preventDefault();
    list.classList.remove("drag-over");

    const issueId = event.dataTransfer.getData("text/plain");
    const issue = state.issues.find(candidate => candidate.id === issueId);
    if (!issue || issue.kind !== list.dataset.kind) {
      return;
    }

    const dragging = document.querySelector(`.card[data-issue-id="${cssEscape(issueId)}"]`);
    if (dragging) {
      const after = findInsertTarget(list, event.clientY);
      if (after) {
        list.insertBefore(dragging, after);
      } else {
        list.appendChild(dragging);
      }
    }

    const orderedIssueIds = [...list.querySelectorAll(".card")]
      .map(card => card.dataset.issueId)
      .filter(Boolean);

    post("moveIssue", {
      issueId,
      section: list.dataset.section,
      column: list.dataset.column,
      orderedIssueIds
    });
  });
}

function findInsertTarget(list, y) {
  const cards = [...list.querySelectorAll(".card:not(.dragging)")];
  return cards.find(card => {
    const box = card.getBoundingClientRect();
    return y < box.top + box.height / 2;
  });
}

function filterIssues(section, kind, column) {
  return state.issues.filter(issue => {
    if (issue.section !== section || issue.kind !== kind || issue.isMissing) {
      return false;
    }

    return column ? issue.column === column : true;
  });
}

function saveSettings() {
  post("saveSettings", {
    jiraHost: value("jiraHost"),
    userName: value("userName"),
    token: value("token"),
    jql: value("jql"),
    taskIssueTypes: splitList(value("taskIssueTypes")),
    incidentIssueTypes: splitList(value("incidentIssueTypes")),
    ignoredCommentAuthors: splitList(value("ignoredCommentAuthors")),
    refreshMinutes: Number(value("refreshMinutes")) || 5,
    maxIssues: Number(value("maxIssues")) || 1000,
    updateRepositoryUrl: value("updateRepositoryUrl")
  });
}

function post(type, payload = {}) {
  window.chrome.webview.postMessage({ type, payload });
}

function setValue(id, value) {
  const input = document.getElementById(id);
  if (document.activeElement !== input) {
    input.value = value;
  }
}

function value(id) {
  return document.getElementById(id).value.trim();
}

function splitList(value) {
  return value.split(",").map(item => item.trim()).filter(Boolean);
}

function connectionText(status) {
  switch (status) {
    case "connected": return "Conectado";
    case "refreshing": return "Sincronizando";
    case "blocked": return "Bloqueado";
    case "idle": return "Preparado";
    default: return "Sin configurar";
  }
}

function updateText(update) {
  if (!update) {
    return "";
  }

  const version = update.version ? ` ${update.version}` : "";
  const progress = update.status === "downloading" ? ` ${update.progress || 0}%` : "";
  return `${update.message || ""}${version}${progress}`;
}

function unreadTitle(issue) {
  const author = issue.lastRelevantCommentAuthor ? ` de ${issue.lastRelevantCommentAuthor}` : "";
  const date = issue.lastRelevantCommentAt ? ` (${formatDate(issue.lastRelevantCommentAt)})` : "";
  return `Comentario sin leer${author}${date}. Marcar como leido`;
}

function formatDate(value) {
  return new Date(value).toLocaleString();
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function cssEscape(value) {
  if (window.CSS && CSS.escape) {
    return CSS.escape(value);
  }

  return String(value).replaceAll('"', '\\"');
}
