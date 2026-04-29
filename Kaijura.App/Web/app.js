const state = {
  activeView: "settings",
  columns: [],
  issues: [],
  activeTracker: null,
  config: {},
  connection: {},
  update: {}
};

const automationUi = {
  rules: [],
  sourceSignature: "",
  isDirty: false,
  expandedRuleId: null,
  templatesOpen: false,
  simulation: null
};

const automationTriggers = [
  { id: "TicketNew", label: "Ticket nuevo" },
  { id: "JiraStatusChanged", label: "Cambio de estado Jira" },
  { id: "IssueClassificationChanged", label: "Cambio de tipo/clasificacion" },
  { id: "RelevantCommentChanged", label: "Comentario relevante detectado" },
  { id: "TemporalCheck", label: "Chequeo temporal" }
];

const automationScopes = [
  { id: "All", label: "Todos" },
  { id: "Task", label: "Tareas" },
  { id: "Incident", label: "Incidencias" },
  { id: "Unmapped", label: "Sin mapear" }
];

const automationLocations = [
  { id: "Any", label: "Cualquier ubicacion" },
  { id: "Backlog", label: "Backlog" },
  { id: "ToDo", label: "Por hacer" },
  { id: "Progress", label: "En progreso" },
  { id: "PendingQa", label: "Pendiente QA" },
  { id: "ValidatedQa", label: "Validado QA" }
];

const automationDestinations = [
  { id: "Backlog", label: "Backlog" },
  { id: "ToDo", label: "Por hacer" },
  { id: "Progress", label: "En progreso" },
  { id: "PendingQa", label: "Pendiente QA" },
  { id: "ValidatedQa", label: "Validado QA" },
  { id: "Archived", label: "Archivar" }
];

const automationConditionFields = [
  { id: "JiraStatus", label: "Estado Jira", type: "values" },
  { id: "IssueType", label: "Issue type", type: "values" },
  { id: "HasUnreadComment", label: "Comentario sin leer", type: "bool" },
  { id: "JiraUpdatedMoreThanDaysAgo", label: "Ultima actualizacion Jira", type: "days" },
  { id: "FirstSeenMoreThanDaysAgo", label: "Visto por primera vez", type: "days" }
];

const automationTemplates = [
  { id: "status", label: "Mover por estado Jira" },
  { id: "new", label: "Mover tickets nuevos" },
  { id: "archive", label: "Archivar por estado" },
  { id: "comment", label: "Mover por comentario sin leer" },
  { id: "blank", label: "Regla en blanco" }
];

const transitionMenu = {
  openIssueId: null,
  loadingIssueId: null,
  formTransitionId: null,
  submittingIssueId: null,
  submittingTransitionId: null,
  optionsByIssueId: {},
  errorByIssueId: {}
};

const trackerUi = {
  busyIssueId: null,
  errorByIssueId: {}
};

const confirmDialog = {
  isOpen: false,
  isBusy: false,
  onAccept: null,
  onCancel: null,
  previousFocus: null
};

const dragAutoScroll = {
  active: false,
  velocity: 0,
  frameId: null
};

const dragAutoScrollEdgeSize = 150;
const dragAutoScrollMaxSpeed = 24;

const views = {
  board: document.getElementById("boardView"),
  archive: document.getElementById("archiveView"),
  missing: document.getElementById("missingView"),
  unmapped: document.getElementById("unmappedView"),
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
    case "trackerError":
      receiveTrackerError(event.data.payload);
      break;
    case "trackerResult":
      receiveTrackerResult(event.data.payload);
      break;
    case "showCloseTrackerConfirmation":
      showCloseTrackerConfirmation(event.data.payload);
      break;
    case "closeTrackerError":
      receiveCloseTrackerError(event.data.payload);
      break;
    case "showPendingTrackerConfirmation":
      showPendingTrackerConfirmation(event.data.payload);
      break;
    case "pendingTrackerError":
      receivePendingTrackerError(event.data.payload);
      break;
    case "pendingTrackerResult":
      closeConfirmDialog();
      break;
    case "automationSimulation":
      automationUi.simulation = event.data.payload || null;
      renderAutomationSimulation();
      break;
  }
});

window.addEventListener("DOMContentLoaded", () => {
  bindChrome();
  setInterval(updateTrackerElapsed, 1000);
  post("ready");
});

function bindChrome() {
  document.getElementById("installUpdateButton").addEventListener("click", () => post("installUpdate"));
  document.getElementById("newAutomationRuleButton").addEventListener("click", () => {
    automationUi.templatesOpen = !automationUi.templatesOpen;
    renderAutomation();
  });
  document.getElementById("testAutomationRulesButton").addEventListener("click", () => {
    post("simulateAutomationRules", { automationRules: cleanAutomationRulesForSave(automationUi.rules) });
  });
  document.getElementById("confirmAcceptButton").addEventListener("click", acceptConfirmDialog);
  document.getElementById("confirmCancelButton").addEventListener("click", cancelConfirmDialog);
  document.getElementById("confirmOverlay").addEventListener("click", event => {
    if (event.target === event.currentTarget) {
      cancelConfirmDialog();
    }
  });
  document.getElementById("settingsForm").addEventListener("submit", event => {
    event.preventDefault();
    saveSettings();
  });
  document.addEventListener("dragover", updateDragAutoScroll, true);
  document.addEventListener("drop", stopDragAutoScroll, true);
  document.addEventListener("dragend", stopDragAutoScroll, true);
  document.addEventListener("click", closeTransitionMenu);
  document.addEventListener("keydown", event => {
    if (event.key === "Escape") {
      if (confirmDialog.isOpen) {
        cancelConfirmDialog();
        return;
      }

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
  renderUnmapped();
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
  syncAutomationDraft(config.automationRules || []);
  renderAutomation();
}

function syncAutomationDraft(rules) {
  const signature = JSON.stringify(rules || []);
  if (!automationUi.isDirty && automationUi.sourceSignature !== signature) {
    automationUi.rules = normalizeAutomationRules(rules || []);
    automationUi.sourceSignature = signature;
    if (!automationUi.rules.some(rule => rule.id === automationUi.expandedRuleId)) {
      automationUi.expandedRuleId = automationUi.rules[0]?.id || null;
    }
  }
}

function renderAutomation() {
  renderAutomationSuggestions();
  renderAutomationTemplatePicker();
  renderAutomationWarnings();
  renderAutomationSimulation();

  const host = document.getElementById("automationRules");
  host.innerHTML = "";

  if (automationUi.rules.length === 0) {
    const empty = document.createElement("div");
    empty.className = "automation-empty";
    empty.textContent = "Sin reglas";
    host.appendChild(empty);
    return;
  }

  automationUi.rules.forEach((rule, index) => {
    host.appendChild(createAutomationRuleElement(rule, index));
  });
}

function renderAutomationTemplatePicker() {
  const host = document.getElementById("automationTemplatePicker");
  host.classList.toggle("hidden", !automationUi.templatesOpen);
  host.innerHTML = "";

  if (!automationUi.templatesOpen) {
    return;
  }

  automationTemplates.forEach(template => {
    const button = document.createElement("button");
    button.className = "button compact";
    button.type = "button";
    button.textContent = template.label;
    button.addEventListener("click", () => addAutomationRule(template.id));
    host.appendChild(button);
  });
}

function renderAutomationWarnings() {
  const host = document.getElementById("automationWarnings");
  const warnings = findAutomationConflicts(automationUi.rules);
  host.classList.toggle("hidden", warnings.length === 0);
  host.innerHTML = "";

  warnings.slice(0, 4).forEach(warning => {
    const item = document.createElement("div");
    item.textContent = warning;
    host.appendChild(item);
  });
}

function renderAutomationSimulation() {
  const host = document.getElementById("automationSimulation");
  if (!host) {
    return;
  }

  const simulation = automationUi.simulation;
  host.classList.toggle("hidden", !simulation);
  host.innerHTML = "";

  if (!simulation) {
    return;
  }

  const total = simulation.total || 0;
  const title = document.createElement("div");
  title.className = "automation-simulation-title";
  title.textContent = total === 0 ? "La prueba no moveria tickets" : `${total} cambios posibles`;
  host.appendChild(title);

  const applications = simulation.applications || [];
  if (applications.length > 0) {
    const list = document.createElement("div");
    list.className = "automation-simulation-list";
    applications.forEach(application => {
      const row = document.createElement("div");
      row.textContent = `${application.issueKey}: ${automationDestinationLabel(application.from)} -> ${automationDestinationLabel(application.to)} por "${application.ruleName}"`;
      list.appendChild(row);
    });
    host.appendChild(list);
  }

  if (total > applications.length) {
    const more = document.createElement("div");
    more.className = "automation-simulation-more";
    more.textContent = `Y ${total - applications.length} cambios mas`;
    host.appendChild(more);
  }
}

function createAutomationRuleElement(rule, index) {
  const article = document.createElement("article");
  article.className = "automation-rule";
  article.classList.toggle("disabled", !rule.isEnabled);

  const summary = document.createElement("div");
  summary.className = "automation-rule-summary";

  const enabledLabel = document.createElement("label");
  enabledLabel.className = "automation-toggle";
  const enabledInput = document.createElement("input");
  enabledInput.type = "checkbox";
  enabledInput.checked = rule.isEnabled;
  enabledInput.addEventListener("change", () => {
    rule.isEnabled = enabledInput.checked;
    markAutomationDirty(true);
  });
  enabledLabel.appendChild(enabledInput);
  enabledLabel.appendChild(document.createTextNode("Activa"));

  const main = document.createElement("button");
  main.className = "automation-rule-main";
  main.type = "button";
  main.addEventListener("click", () => {
    automationUi.expandedRuleId = automationUi.expandedRuleId === rule.id ? null : rule.id;
    renderAutomation();
  });

  const title = document.createElement("span");
  title.className = "automation-rule-name";
  title.textContent = rule.name || "Regla sin nombre";
  const meta = document.createElement("span");
  meta.className = "automation-rule-meta";
  meta.textContent = describeAutomationRule(rule);
  main.append(title, meta);

  const actions = document.createElement("div");
  actions.className = "automation-rule-actions";
  actions.append(
    createAutomationActionButton("Arriba", () => moveAutomationRule(index, -1), index === 0),
    createAutomationActionButton("Abajo", () => moveAutomationRule(index, 1), index === automationUi.rules.length - 1),
    createAutomationActionButton("Eliminar", () => removeAutomationRule(rule.id), false)
  );

  summary.append(enabledLabel, main, actions);
  article.appendChild(summary);

  if (automationUi.expandedRuleId === rule.id) {
    article.appendChild(createAutomationRuleEditor(rule));
  }

  return article;
}

function createAutomationRuleEditor(rule) {
  const editor = document.createElement("div");
  editor.className = "automation-editor";

  const nameLabel = createTextInput("Nombre", rule.name, value => {
    rule.name = value;
    markAutomationDirty();
  });
  editor.appendChild(nameLabel);

  const grid = document.createElement("div");
  grid.className = "automation-editor-grid";
  grid.append(
    createSelectInput("Cuando", automationTriggers, rule.trigger, value => {
      rule.trigger = value;
      if (value === "TemporalCheck" && !rule.conditions.some(condition => isTemporalCondition(condition))) {
        rule.conditions.push(defaultAutomationCondition("JiraUpdatedMoreThanDaysAgo"));
      }
      markAutomationDirty(true);
    }),
    createSelectInput("Tipo", automationScopes, rule.issueScope, value => {
      rule.issueScope = value;
      markAutomationDirty(true);
    }),
    createSelectInput("Ubicacion actual", automationLocations, rule.currentLocation, value => {
      rule.currentLocation = value;
      markAutomationDirty(true);
    }),
    createSelectInput("Entonces", automationDestinations, rule.action.destination, value => {
      rule.action.destination = value;
      markAutomationDirty(true);
    })
  );
  editor.appendChild(grid);

  const conditions = document.createElement("div");
  conditions.className = "automation-conditions";
  const conditionHeader = document.createElement("div");
  conditionHeader.className = "automation-subheader";
  conditionHeader.textContent = "Condiciones";
  conditions.appendChild(conditionHeader);

  if (rule.conditions.length === 0) {
    const empty = document.createElement("div");
    empty.className = "automation-condition-empty";
    empty.textContent = "Sin condiciones adicionales";
    conditions.appendChild(empty);
  } else {
    rule.conditions.forEach((condition, index) => {
      conditions.appendChild(createAutomationConditionRow(rule, condition, index));
    });
  }

  const conditionActions = document.createElement("div");
  conditionActions.className = "automation-condition-actions";
  const addCondition = document.createElement("button");
  addCondition.className = "button compact";
  addCondition.type = "button";
  addCondition.textContent = "Anadir condicion";
  addCondition.addEventListener("click", () => {
    rule.conditions.push(defaultAutomationCondition("JiraStatus"));
    markAutomationDirty(true);
  });
  conditionActions.appendChild(addCondition);
  conditions.appendChild(conditionActions);
  editor.appendChild(conditions);

  const options = document.createElement("label");
  options.className = "automation-stop";
  const stop = document.createElement("input");
  stop.type = "checkbox";
  stop.checked = rule.stopProcessing;
  stop.addEventListener("change", () => {
    rule.stopProcessing = stop.checked;
    markAutomationDirty();
  });
  options.appendChild(stop);
  options.appendChild(document.createTextNode("Detener evaluacion si esta regla se aplica"));
  editor.appendChild(options);

  return editor;
}

function createAutomationConditionRow(rule, condition, index) {
  const row = document.createElement("div");
  row.className = "automation-condition-row";

  const fieldSelect = createBareSelect(automationConditionFields, condition.field);
  fieldSelect.addEventListener("change", () => {
    const next = defaultAutomationCondition(fieldSelect.value);
    Object.assign(condition, next);
    markAutomationDirty(true);
  });

  row.appendChild(fieldSelect);

  const field = automationConditionFields.find(candidate => candidate.id === condition.field) || automationConditionFields[0];
  if (field.type === "values") {
    const operatorOptions = [
      { id: "IsAnyOf", label: "es uno de" },
      { id: "IsNotAnyOf", label: "no es uno de" }
    ];
    const operator = createBareSelect(operatorOptions, condition.operator);
    operator.addEventListener("change", () => {
      condition.operator = operator.value;
      markAutomationDirty();
    });
    row.appendChild(operator);

    const input = document.createElement("input");
    input.value = (condition.values || []).join(", ");
    input.placeholder = field.id === "JiraStatus" ? "Abierta, In Progress" : "Task, Bug";
    input.setAttribute("list", field.id === "JiraStatus" ? "jiraStatusSuggestions" : "issueTypeSuggestions");
    input.addEventListener("input", () => {
      condition.values = splitList(input.value);
      markAutomationDirty();
    });
    row.appendChild(input);
  } else if (field.type === "bool") {
    const operatorOptions = [
      { id: "Is", label: "es" },
      { id: "IsNot", label: "no es" }
    ];
    const boolOptions = [
      { id: "true", label: "Si" },
      { id: "false", label: "No" }
    ];
    const operator = createBareSelect(operatorOptions, condition.operator);
    operator.addEventListener("change", () => {
      condition.operator = operator.value;
      markAutomationDirty();
    });
    const boolValue = createBareSelect(boolOptions, String(Boolean(condition.boolValue)));
    boolValue.addEventListener("change", () => {
      condition.boolValue = boolValue.value === "true";
      markAutomationDirty();
    });
    row.append(operator, boolValue);
  } else {
    const text = document.createElement("span");
    text.className = "automation-condition-text";
    text.textContent = "hace mas de";
    const input = document.createElement("input");
    input.type = "number";
    input.min = "1";
    input.value = condition.days || 1;
    input.addEventListener("input", () => {
      condition.days = Number(input.value) || 1;
      markAutomationDirty();
    });
    const suffix = document.createElement("span");
    suffix.className = "automation-condition-text";
    suffix.textContent = "dias";
    row.append(text, input, suffix);
  }

  const remove = document.createElement("button");
  remove.className = "button compact";
  remove.type = "button";
  remove.textContent = "Eliminar";
  remove.addEventListener("click", () => {
    rule.conditions.splice(index, 1);
    markAutomationDirty(true);
  });
  row.appendChild(remove);

  return row;
}

function createTextInput(label, currentValue, onInput) {
  const wrapper = document.createElement("label");
  wrapper.textContent = label;
  const input = document.createElement("input");
  input.value = currentValue || "";
  input.addEventListener("input", () => onInput(input.value.trim()));
  wrapper.appendChild(input);
  return wrapper;
}

function createSelectInput(label, options, currentValue, onChange) {
  const wrapper = document.createElement("label");
  wrapper.textContent = label;
  const select = createBareSelect(options, currentValue);
  select.addEventListener("change", () => onChange(select.value));
  wrapper.appendChild(select);
  return wrapper;
}

function createBareSelect(options, currentValue) {
  const select = document.createElement("select");
  options.forEach(option => {
    const element = document.createElement("option");
    element.value = option.id;
    element.textContent = option.label;
    select.appendChild(element);
  });
  select.value = currentValue;
  return select;
}

function createAutomationActionButton(label, onClick, disabled) {
  const button = document.createElement("button");
  button.className = "button compact";
  button.type = "button";
  button.textContent = label;
  button.disabled = disabled;
  button.addEventListener("click", onClick);
  return button;
}

function addAutomationRule(templateId) {
  const rule = createAutomationRuleFromTemplate(templateId);
  automationUi.rules.push(rule);
  automationUi.expandedRuleId = rule.id;
  automationUi.templatesOpen = false;
  markAutomationDirty(true);
}

function moveAutomationRule(index, offset) {
  const target = index + offset;
  if (target < 0 || target >= automationUi.rules.length) {
    return;
  }

  const [rule] = automationUi.rules.splice(index, 1);
  automationUi.rules.splice(target, 0, rule);
  markAutomationDirty(true);
}

function removeAutomationRule(ruleId) {
  automationUi.rules = automationUi.rules.filter(rule => rule.id !== ruleId);
  if (automationUi.expandedRuleId === ruleId) {
    automationUi.expandedRuleId = automationUi.rules[0]?.id || null;
  }
  markAutomationDirty(true);
}

function markAutomationDirty(shouldRender = false) {
  automationUi.isDirty = true;
  automationUi.simulation = null;
  if (shouldRender) {
    renderAutomation();
    return;
  }

  renderAutomationWarnings();
  renderAutomationSimulation();
}

function normalizeAutomationRules(rules) {
  return (rules || []).map(rule => ({
    id: rule.id || createAutomationId(),
    name: rule.name || "Regla sin nombre",
    isEnabled: Boolean(rule.isEnabled),
    trigger: rule.trigger || "TicketNew",
    issueScope: rule.issueScope || "All",
    currentLocation: rule.currentLocation || "Any",
    conditions: (rule.conditions || []).map(normalizeAutomationCondition),
    action: {
      destination: rule.action?.destination || "ToDo"
    },
    stopProcessing: rule.stopProcessing !== false
  }));
}

function normalizeAutomationCondition(condition) {
  const field = condition.field || "JiraStatus";
  const defaultCondition = defaultAutomationCondition(field);
  return {
    ...defaultCondition,
    operator: condition.operator || defaultCondition.operator,
    values: condition.values || [],
    boolValue: Boolean(condition.boolValue),
    days: Number(condition.days) || defaultCondition.days
  };
}

function cleanAutomationRulesForSave(rules) {
  return normalizeAutomationRules(rules).map(rule => ({
    id: rule.id || createAutomationId(),
    name: rule.name || "Regla sin nombre",
    isEnabled: Boolean(rule.isEnabled),
    trigger: rule.trigger,
    issueScope: rule.issueScope,
    currentLocation: rule.currentLocation,
    conditions: rule.conditions
      .map(cleanAutomationConditionForSave)
      .filter(Boolean),
    action: {
      destination: rule.action.destination
    },
    stopProcessing: rule.stopProcessing !== false
  }));
}

function cleanAutomationConditionForSave(condition) {
  const field = automationConditionFields.find(candidate => candidate.id === condition.field) || automationConditionFields[0];
  if (field.type === "values") {
    const values = (condition.values || []).map(value => value.trim()).filter(Boolean);
    if (values.length === 0) {
      return null;
    }

    return {
      field: field.id,
      operator: condition.operator === "IsNotAnyOf" ? "IsNotAnyOf" : "IsAnyOf",
      values,
      boolValue: false,
      days: 0
    };
  }

  if (field.type === "bool") {
    return {
      field: field.id,
      operator: condition.operator === "IsNot" ? "IsNot" : "Is",
      values: [],
      boolValue: Boolean(condition.boolValue),
      days: 0
    };
  }

  const days = Number(condition.days) || 0;
  if (days <= 0) {
    return null;
  }

  return {
    field: field.id,
    operator: "MoreThanDaysAgo",
    values: [],
    boolValue: false,
    days
  };
}

function defaultAutomationCondition(field) {
  const definition = automationConditionFields.find(candidate => candidate.id === field) || automationConditionFields[0];
  if (definition.type === "bool") {
    return { field: definition.id, operator: "Is", values: [], boolValue: true, days: 0 };
  }

  if (definition.type === "days") {
    return { field: definition.id, operator: "MoreThanDaysAgo", values: [], boolValue: false, days: 7 };
  }

  return { field: definition.id, operator: "IsAnyOf", values: [], boolValue: false, days: 0 };
}

function createAutomationRuleFromTemplate(templateId) {
  const rule = {
    id: createAutomationId(),
    name: "Regla sin nombre",
    isEnabled: false,
    trigger: "TicketNew",
    issueScope: "All",
    currentLocation: "Any",
    conditions: [],
    action: { destination: "ToDo" },
    stopProcessing: true
  };

  if (templateId === "status") {
    rule.name = "Mover por estado Jira";
    rule.trigger = "JiraStatusChanged";
    rule.currentLocation = "Backlog";
    rule.conditions = [defaultAutomationCondition("JiraStatus")];
  } else if (templateId === "new") {
    rule.name = "Mover tickets nuevos";
    rule.trigger = "TicketNew";
    rule.currentLocation = "Backlog";
  } else if (templateId === "archive") {
    rule.name = "Archivar por estado";
    rule.trigger = "JiraStatusChanged";
    rule.conditions = [defaultAutomationCondition("JiraStatus")];
    rule.action.destination = "Archived";
  } else if (templateId === "comment") {
    rule.name = "Mover por comentario sin leer";
    rule.trigger = "RelevantCommentChanged";
    rule.conditions = [defaultAutomationCondition("HasUnreadComment")];
    rule.action.destination = "Progress";
  }

  return rule;
}

function describeAutomationRule(rule) {
  const parts = [
    `Cuando ${automationTriggerLabel(rule.trigger)}`,
    `si ${describeAutomationScope(rule)}`,
    `entonces ${automationActionLabel(rule.action.destination)}`
  ];
  return parts.join(" - ");
}

function describeAutomationScope(rule) {
  const scope = rule.issueScope === "All" ? "todos" : `tipo ${automationScopeLabel(rule.issueScope)}`;
  const location = rule.currentLocation === "Any" ? "cualquier ubicacion" : `ubicacion ${automationLocationLabel(rule.currentLocation)}`;
  const conditions = rule.conditions.map(describeAutomationCondition).filter(Boolean);
  return [scope, location, ...conditions].join(", ");
}

function describeAutomationCondition(condition) {
  const field = automationConditionFields.find(candidate => candidate.id === condition.field);
  if (!field) {
    return "";
  }

  if (field.type === "values") {
    const values = (condition.values || []).join(", ");
    const operator = condition.operator === "IsNotAnyOf" ? "no es" : "es";
    return values ? `${field.label} ${operator} ${values}` : "";
  }

  if (field.type === "bool") {
    const value = condition.boolValue ? "si" : "no";
    const operator = condition.operator === "IsNot" ? "no es" : "es";
    return `${field.label} ${operator} ${value}`;
  }

  return `${field.label} hace mas de ${condition.days || 1} dias`;
}

function automationTriggerLabel(value) {
  return labelFrom(automationTriggers, value);
}

function automationScopeLabel(value) {
  return labelFrom(automationScopes, value);
}

function automationLocationLabel(value) {
  return labelFrom(automationLocations, value);
}

function automationDestinationLabel(value) {
  return labelFrom(automationDestinations, value);
}

function automationActionLabel(destination) {
  return destination === "Archived"
    ? "archivar"
    : `mover a ${automationDestinationLabel(destination)}`;
}

function labelFrom(options, value) {
  return options.find(option => option.id === value)?.label || value;
}

function isTemporalCondition(condition) {
  return condition.field === "JiraUpdatedMoreThanDaysAgo" || condition.field === "FirstSeenMoreThanDaysAgo";
}

function createAutomationId() {
  if (window.crypto?.randomUUID) {
    return window.crypto.randomUUID();
  }

  return `rule-${Date.now()}-${Math.round(Math.random() * 100000)}`;
}

function renderAutomationSuggestions() {
  renderDatalist("jiraStatusSuggestions", distinctValues(state.issues.map(issue => issue.jiraStatus)));
  renderDatalist("issueTypeSuggestions", distinctValues(state.issues.map(issue => issue.issueType)));
}

function renderDatalist(id, values) {
  const host = document.getElementById(id);
  host.innerHTML = "";
  values.forEach(value => {
    const option = document.createElement("option");
    option.value = value;
    host.appendChild(option);
  });
}

function distinctValues(values) {
  return values
    .map(value => (value || "").trim())
    .filter(Boolean)
    .filter((value, index, all) => all.findIndex(candidate => candidate.toLowerCase() === value.toLowerCase()) === index)
    .sort((left, right) => left.localeCompare(right));
}

function findAutomationConflicts(rules) {
  const warnings = [];
  const activeRules = rules.filter(rule => rule.isEnabled);
  for (let leftIndex = 0; leftIndex < activeRules.length; leftIndex++) {
    for (let rightIndex = leftIndex + 1; rightIndex < activeRules.length; rightIndex++) {
      const left = activeRules[leftIndex];
      const right = activeRules[rightIndex];
      if (left.trigger !== right.trigger
        || left.action.destination === right.action.destination
        || !automationScopesOverlap(left.issueScope, right.issueScope)
        || !automationLocationsOverlap(left.currentLocation, right.currentLocation)
        || !automationStatusConditionsOverlap(left, right)) {
        continue;
      }

      warnings.push(`"${left.name}" puede coincidir con "${right.name}". Se ejecutara primero la regla que este mas arriba.`);
    }
  }

  return warnings;
}

function automationScopesOverlap(left, right) {
  return left === "All" || right === "All" || left === right;
}

function automationLocationsOverlap(left, right) {
  return left === "Any" || right === "Any" || left === right;
}

function automationStatusConditionsOverlap(left, right) {
  const leftValues = automationPositiveStatusValues(left);
  const rightValues = automationPositiveStatusValues(right);
  if (!leftValues || !rightValues) {
    return true;
  }

  return leftValues.some(value => rightValues.includes(value));
}

function automationPositiveStatusValues(rule) {
  const statusConditions = rule.conditions.filter(condition => condition.field === "JiraStatus");
  if (statusConditions.length !== 1 || statusConditions[0].operator !== "IsAnyOf") {
    return null;
  }

  const values = statusConditions[0].values.map(value => value.trim().toLowerCase()).filter(Boolean);
  return values.length > 0 ? values : null;
}

function renderBoard() {
  const taskBoardIssues = filterIssues("board", "task");
  const incidentBoardIssues = filterIssues("board", "incident");
  const taskBacklogIssues = filterIssues("backlog", "task");
  const incidentBacklogIssues = filterIssues("backlog", "incident");

  setCount("taskBoardCount", taskBoardIssues.length);
  setCount("incidentBoardCount", incidentBoardIssues.length);
  setCount("taskBacklogCount", taskBacklogIssues.length);
  setCount("incidentBacklogCount", incidentBacklogIssues.length);

  renderLane("task", document.getElementById("taskBoard"));
  renderLane("incident", document.getElementById("incidentBoard"));
  renderList(document.getElementById("taskBacklog"), taskBacklogIssues, true);
  renderList(document.getElementById("incidentBacklog"), incidentBacklogIssues, true);
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
  const archived = state.issues.filter(issue => issue.section === "archived");

  renderList(document.getElementById("taskArchiveList"), archived.filter(issue => issue.kind === "task"), false, "restore");
  renderList(document.getElementById("incidentArchiveList"), archived.filter(issue => issue.kind === "incident"), false, "restore");
}

function renderMissing() {
  const missing = state.issues.filter(issue => issue.section === "missing" || issue.isMissing);

  renderList(document.getElementById("taskMissingList"), missing.filter(issue => issue.kind === "task"), false);
  renderList(document.getElementById("incidentMissingList"), missing.filter(issue => issue.kind === "incident"), false);
}

function renderUnmapped() {
  const active = state.issues.filter(issue =>
    issue.kind === "unmapped"
    && !issue.isMissing
    && issue.section !== "archived"
    && issue.section !== "missing");

  setCount("activeUnmappedCount", active.length);
  renderList(document.getElementById("activeUnmappedList"), active, false);
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
  card.classList.toggle("tracker-active", isTrackerActive(issue));
  card.classList.toggle("has-unread-comment", issue.hasUnreadComment);

  card.innerHTML = `
    <div class="card-top">
      <button class="issue-key card-action" type="button">${escapeHtml(issue.key)}</button>
      <div class="card-top-actions">
        <button class="issue-status" type="button" title="${escapeHtml(issue.jiraStatus || "")}" aria-haspopup="menu" aria-expanded="${transitionMenu.openIssueId === issue.id ? "true" : "false"}">${escapeHtml(issue.jiraStatus || "Sin estado")}</button>
      </div>
    </div>
    <div class="issue-title">${escapeHtml(issue.summary || "")}</div>
  `;

  const footer = document.createElement("div");
  footer.className = "card-footer";
  const footerStart = document.createElement("div");
  footerStart.className = "card-footer-start";
  const footerEnd = document.createElement("div");
  footerEnd.className = "card-footer-end";
  footer.append(footerStart, footerEnd);

  if (issue.hasUnreadComment) {
    const unreadButton = document.createElement("button");
    unreadButton.className = "comment-alert";
    unreadButton.type = "button";
    unreadButton.title = unreadTitle(issue);
    unreadButton.setAttribute("aria-label", "Marcar comentario como leido");
    unreadButton.innerHTML = `<span class="comment-alert-dot" aria-hidden="true"></span><span>Sin leer</span>`;
    unreadButton.addEventListener("click", event => {
      event.stopPropagation();
      post("markCommentRead", { issueId: issue.id });
    });
    footerStart.appendChild(unreadButton);
  }

  card.querySelector(".issue-key").addEventListener("click", event => {
    event.stopPropagation();
    post("openIssue", { issueId: issue.id });
  });

  card.querySelector(".issue-status").addEventListener("click", event => {
    event.stopPropagation();
    toggleTransitionMenu(issue.id);
  });

  if (issue.section === "board" && issue.column === "Progress") {
    const trackerRow = document.createElement("div");
    trackerRow.className = "tracker-row";
    trackerRow.appendChild(createTrackerButton(issue));

    if (isTrackerActive(issue)) {
      const elapsed = document.createElement("span");
      elapsed.className = "tracker-elapsed";
      elapsed.dataset.trackerStartedAt = state.activeTracker.startedAt;
      elapsed.textContent = formatElapsed(state.activeTracker.startedAt);
      trackerRow.appendChild(elapsed);
    }

    footerEnd.appendChild(trackerRow);
  }

  if (draggable) {
    card.addEventListener("dragstart", event => {
      card.classList.add("dragging");
      event.dataTransfer.setData("text/plain", issue.id);
      event.dataTransfer.effectAllowed = "move";
      startDragAutoScroll(event);
    });
    card.addEventListener("dragend", () => {
      card.classList.remove("dragging");
      stopDragAutoScroll();
    });
  }

  if (issue.section === "board" && issue.column === "ValidatedQa") {
    const actions = document.createElement("div");
    actions.className = "card-actions";
    const button = document.createElement("button");
    button.className = "card-action";
    button.type = "button";
    button.textContent = "Archivar";
    button.addEventListener("click", () => post("archiveIssue", { issueId: issue.id }));
    actions.appendChild(button);
    footerEnd.appendChild(actions);
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
    footerEnd.appendChild(actions);
  }

  if (footerStart.hasChildNodes() || footerEnd.hasChildNodes()) {
    card.appendChild(footer);
  }

  const trackerError = trackerUi.errorByIssueId[issue.id];
  if (trackerError) {
    const error = document.createElement("div");
    error.className = "tracker-error";
    error.textContent = trackerError;
    card.appendChild(error);
  }

  if (transitionMenu.openIssueId === issue.id) {
    card.appendChild(createTransitionMenu(issue));
  }

  return card;
}

function createTrackerButton(issue) {
  const active = isTrackerActive(issue);
  const busy = trackerUi.busyIssueId === issue.id;
  const button = document.createElement("button");
  button.className = `tracker-button${active ? " active" : ""}`;
  button.type = "button";
  button.title = active ? "Parar tracker" : "Iniciar tracker";
  button.setAttribute("aria-label", active ? "Parar tracker" : "Iniciar tracker");
  button.disabled = busy;
  button.innerHTML = `<span class="tracker-icon ${active ? "stop" : "play"}"></span>`;
  button.addEventListener("click", event => {
    event.stopPropagation();
    toggleTracker(issue);
  });
  return button;
}

function toggleTracker(issue) {
  delete trackerUi.errorByIssueId[issue.id];

  if (isTrackerActive(issue)) {
    trackerUi.busyIssueId = issue.id;
    render();
    post("stopTracker", { issueId: issue.id });
    return;
  }

  const hasOtherTracker = state.activeTracker && state.activeTracker.issueId !== issue.id;
  if (hasOtherTracker) {
    const activeKey = state.activeTracker.issueKey || "otro ticket";
    showConfirmDialog({
      title: "Tracker activo",
      message: `Hay un tracker iniciado en ${activeKey}. Se parara y se registrara en Jira antes de iniciar ${issue.key}.`,
      acceptLabel: "Aceptar",
      cancelLabel: "Cancelar",
      onAccept: () => {
        closeConfirmDialog();
        startTracker(issue, true);
      }
    });
    return;
  }

  startTracker(issue, false);
}

function startTracker(issue, replaceActive) {
  trackerUi.busyIssueId = issue.id;
  render();
  post("startTracker", {
    issueId: issue.id,
    replaceActive
  });
}

function isTrackerActive(issue) {
  return !!state.activeTracker && state.activeTracker.issueId === issue.id;
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

  const selectFields = option.requiredSelectFields || [];
  selectFields.forEach((field, index) => {
    form.appendChild(createSelectField(selectFieldControlName(index), field.name || field.id, field.options || []));
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
    const submittedSelectFields = {};
    selectFields.forEach((field, index) => {
      const selectedIndex = Number(form.elements[selectFieldControlName(index)]?.value ?? -1);
      submittedSelectFields[field.id] = field.options?.[selectedIndex] || {};
    });

    submitTransition(issue.id, option, {
      comment: form.elements.comment?.value || "",
      worklogTimeSpent: form.elements.worklogTimeSpent?.value || "",
      worklogComment: form.elements.worklogComment?.value || "",
      textFields: submittedTextFields,
      selectFields: submittedSelectFields
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

function selectFieldControlName(index) {
  return `selectField${index}`;
}

function createSelectField(name, labelText, options) {
  const label = document.createElement("label");
  label.className = "status-field";
  label.textContent = labelText;

  const select = document.createElement("select");
  select.name = name;
  select.required = true;

  const placeholder = document.createElement("option");
  placeholder.value = "";
  placeholder.textContent = "Seleccionar...";
  placeholder.disabled = true;
  placeholder.selected = true;
  select.appendChild(placeholder);

  options.forEach((option, index) => {
    const item = document.createElement("option");
    item.value = String(index);
    item.textContent = optionLabel(option);
    select.appendChild(item);
  });

  label.appendChild(select);
  return label;
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
    textFields: values.textFields || {},
    selectFields: values.selectFields || {}
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

function receiveTrackerError(payload) {
  if (!payload || !payload.issueId) {
    trackerUi.busyIssueId = null;
    render();
    return;
  }

  trackerUi.busyIssueId = null;
  trackerUi.errorByIssueId[payload.issueId] = payload.message || "Tracker no actualizado";
  render();
}

function receiveTrackerResult(payload) {
  if (payload && payload.issueId) {
    delete trackerUi.errorByIssueId[payload.issueId];
  }

  trackerUi.busyIssueId = null;
  render();
}

function showCloseTrackerConfirmation(payload) {
  const issueKey = payload?.issueKey || "el ticket activo";
  showConfirmDialog({
    title: "Tracker activo",
    message: `Hay un tracker iniciado en ${issueKey}. Antes de cerrar se parara y se registrara en Jira.`,
    acceptLabel: "Aceptar",
    cancelLabel: "Cancelar",
    onAccept: () => {
      setConfirmBusy(true);
      post("confirmCloseWithTracker");
    },
    onCancel: () => post("cancelCloseWithTracker")
  });
}

function receiveCloseTrackerError(payload) {
  showConfirmError(payload?.message || "No se pudo registrar el tiempo en Jira.");
}

function showPendingTrackerConfirmation(payload) {
  const issueKey = payload?.issueKey || "el ticket pendiente";
  showConfirmDialog({
    title: "Tracker pendiente",
    message: `Hay un tracker pendiente en ${issueKey}. Puedes registrarlo ahora en Jira o descartarlo.`,
    acceptLabel: "Registrar",
    cancelLabel: "Descartar",
    onAccept: () => {
      setConfirmBusy(true);
      post("registerPendingTracker");
    },
    onCancel: () => post("discardPendingTracker")
  });
}

function receivePendingTrackerError(payload) {
  showConfirmError(payload?.message || "No se pudo registrar el tracker pendiente.");
}

function showConfirmDialog(options) {
  confirmDialog.previousFocus = document.activeElement;
  confirmDialog.isOpen = true;
  confirmDialog.onAccept = options.onAccept || null;
  confirmDialog.onCancel = options.onCancel || null;

  document.getElementById("confirmTitle").textContent = options.title || "Confirmar";
  document.getElementById("confirmMessage").textContent = options.message || "";
  document.getElementById("confirmAcceptButton").textContent = options.acceptLabel || "Aceptar";
  document.getElementById("confirmCancelButton").textContent = options.cancelLabel || "Cancelar";
  document.getElementById("confirmError").classList.add("hidden");
  document.getElementById("confirmError").textContent = "";
  document.getElementById("confirmOverlay").classList.remove("hidden");
  setConfirmBusy(false);
  document.getElementById("confirmAcceptButton").focus();
}

function acceptConfirmDialog() {
  if (confirmDialog.isBusy) {
    return;
  }

  confirmDialog.onAccept?.();
}

function cancelConfirmDialog() {
  if (!confirmDialog.isOpen || confirmDialog.isBusy) {
    return;
  }

  const onCancel = confirmDialog.onCancel;
  closeConfirmDialog();
  onCancel?.();
}

function closeConfirmDialog() {
  document.getElementById("confirmOverlay").classList.add("hidden");
  setConfirmBusy(false);
  confirmDialog.isOpen = false;
  confirmDialog.onAccept = null;
  confirmDialog.onCancel = null;

  if (confirmDialog.previousFocus && typeof confirmDialog.previousFocus.focus === "function") {
    confirmDialog.previousFocus.focus();
  }

  confirmDialog.previousFocus = null;
}

function setConfirmBusy(isBusy) {
  confirmDialog.isBusy = isBusy;
  document.getElementById("confirmAcceptButton").disabled = isBusy;
  document.getElementById("confirmCancelButton").disabled = isBusy;
}

function showConfirmError(message) {
  const error = document.getElementById("confirmError");
  error.textContent = message;
  error.classList.remove("hidden");
  setConfirmBusy(false);
}

function startDragAutoScroll(event) {
  dragAutoScroll.active = true;
  updateDragAutoScroll(event);

  if (dragAutoScroll.frameId === null) {
    dragAutoScroll.frameId = requestAnimationFrame(runDragAutoScroll);
  }
}

function stopDragAutoScroll() {
  dragAutoScroll.active = false;
  dragAutoScroll.velocity = 0;

  if (dragAutoScroll.frameId !== null) {
    cancelAnimationFrame(dragAutoScroll.frameId);
    dragAutoScroll.frameId = null;
  }
}

function updateDragAutoScroll(event) {
  if (!dragAutoScroll.active) {
    return;
  }

  const viewportHeight = window.innerHeight || document.documentElement.clientHeight;
  const y = event.clientY;

  if (!Number.isFinite(y) || viewportHeight <= 0) {
    dragAutoScroll.velocity = 0;
    return;
  }

  if (y < dragAutoScrollEdgeSize) {
    dragAutoScroll.velocity = -dragAutoScrollSpeed(dragAutoScrollEdgeSize - y);
    return;
  }

  const bottomDistance = viewportHeight - y;
  if (bottomDistance < dragAutoScrollEdgeSize) {
    dragAutoScroll.velocity = dragAutoScrollSpeed(dragAutoScrollEdgeSize - bottomDistance);
    return;
  }

  dragAutoScroll.velocity = 0;
}

function dragAutoScrollSpeed(edgeOverlap) {
  const ratio = Math.min(1, Math.max(0, edgeOverlap / dragAutoScrollEdgeSize));
  return Math.max(2, Math.round(ratio * dragAutoScrollMaxSpeed));
}

function runDragAutoScroll() {
  if (!dragAutoScroll.active) {
    dragAutoScroll.frameId = null;
    return;
  }

  if (dragAutoScroll.velocity !== 0) {
    window.scrollBy(0, dragAutoScroll.velocity);
  }

  dragAutoScroll.frameId = requestAnimationFrame(runDragAutoScroll);
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
  const automationRules = cleanAutomationRulesForSave(automationUi.rules);
  automationUi.rules = automationRules;
  automationUi.isDirty = false;
  automationUi.sourceSignature = JSON.stringify(automationRules);
  post("saveSettings", {
    jiraHost: value("jiraHost"),
    userName: value("userName"),
    token: value("token"),
    jql: value("jql"),
    taskIssueTypes: splitList(value("taskIssueTypes")),
    incidentIssueTypes: splitList(value("incidentIssueTypes")),
    ignoredCommentAuthors: splitList(value("ignoredCommentAuthors")),
    automationRules,
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

function setCount(id, value) {
  const element = document.getElementById(id);
  if (element) {
    element.textContent = String(value);
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

function updateTrackerElapsed() {
  document.querySelectorAll("[data-tracker-started-at]").forEach(element => {
    element.textContent = formatElapsed(element.dataset.trackerStartedAt);
  });
}

function formatElapsed(value) {
  const started = new Date(value);
  const elapsed = Math.max(0, Date.now() - started.getTime());
  const totalSeconds = Math.floor(elapsed / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  if (hours > 0) {
    return `${hours}:${pad2(minutes)}:${pad2(seconds)}`;
  }

  return `${minutes}:${pad2(seconds)}`;
}

function pad2(value) {
  return String(value).padStart(2, "0");
}

function optionLabel(option) {
  return option?.name || option?.value || option?.id || "Opcion";
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
