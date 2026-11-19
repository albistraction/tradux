// Editor do tradutor de XML (Fase 22, ADR-0025). Tela inicial: os templates do
// serviço. Editor: XSD de origem → XSD de destino → DE-PARA → teste → publicar.
// As versões são do serviço (cada publicação é a próxima versão; rollback
// reativa uma anterior). O navegador só guarda a edição em andamento, para não
// perdê-la ao recarregar a página — nunca como versão.
//
// Este módulo guarda o estado, executa as ações e liga os painéis (ui/) aos
// módulos puros (model/).

import * as api from './api.js';
import { h, replaceChildren } from './ui/dom.js';
import { sourceTree, destinationTree } from './ui/trees.js';
import { rulePanel } from './ui/rulePanel.js';
import { testControls, testPanel } from './ui/testPanel.js';
import { qualityPanel } from './ui/qualityPanel.js';
import { publishPanel } from './ui/publishPanel.js';
import { homeView } from './ui/home.js';
import { tablesPanel } from './ui/tablesPanel.js';
import { schemaPicker, emptyPicker, entryPointOf } from './ui/schemaPicker.js';
import { schemaChangesPanel } from './ui/schemaChangesPanel.js';
import { buildPublication, sampleScenario, TEMPLATE_NAME } from './model/publish.js';
import { buildMappingTemplate, mappingsFromTemplate } from './model/mappingTemplate.js';
import { field } from './model/ruleNodes.js';
import { scopeNode, findNode, valueFields, collectionFields, withRootFields } from './model/sourceTree.js';
import { rowsFromApi, tablesForApi, duplicateProblems } from './model/lookupTables.js';
import { criticalByPath } from './model/diagnostics.js';
import { saveLocalDraft, readLocalDraft, discardLocalDraft } from './model/localDraft.js';
import { readFileAsBase64 } from './model/files.js';
import {
  applySchemaChanges, moveOrphan, discardOrphan, changeMarks, accepts, mergeOrphans, inactiveMappings,
} from './model/schemaChanges.js';
import {
  computeStatus, statusCounts, scopeChain, findDestination, validateDraft, pruneMappings, isRepeatable,
  removeSubtree, summarize, withCollection,
} from './model/draft.js';

const freshEditor = () => ({
  schema: null, // { files: [{ name, contentBase64 }], entryPoint, rootElementName }
  tree: null, // resposta de /v1/design/schema
  sourceSchema: null, // mesma forma do schema de destino
  sourceTree: null, // resposta de /v1/design/source-schema
  // Escolha de arquivos de cada XSD (ui/schemaPicker.js).
  pickers: { source: emptyPicker(), destination: emptyPicker() },
  sampleXml: '',
  mappings: {},
  // Troca do XSD de destino (ADR-0017): { report, orphans } até fechar o relatório.
  schemaChanges: null,
  schemaChangesError: null,
  lookupTables: {},
  selectedPath: null,
  expectedXml: '',
  testResult: null,
  testError: null,
  qualityResult: null,
  qualityError: null,
  qualityStale: false,
  publishProblems: [],
  publishResult: null,
  publishError: null,
  localProblems: [],
  versions: [],
  versionsError: null,
});

const state = {
  operators: [],
  apiError: null,
  view: 'home',
  templates: null, // GET /v1/templates
  templatesError: null,
  newTemplateName: '',
  homeError: null,
  // Template em edição: { templateId, isNew, baseVersion } (baseVersion = versão aberta).
  current: null,
  busy: false,
  notice: null,
  ...freshEditor(),
};

const $ = (id) => document.getElementById(id);

const save = () => saveLocalDraft(state);

const sourceRoot = () => state.sourceTree?.root ?? null;

function restore() {
  try {
    const stored = readLocalDraft();
    if (stored) Object.assign(state, freshEditor(), stored);
  } catch (e) {
    discardLocalDraft();
    state.notice = `A edição salva no navegador era inválida e foi descartada (${e.message}).`;
  }
}

// ---------------------------------------------------------------- tela inicial

async function loadTemplates() {
  state.templatesError = null;
  if (!api.hasCredential()) {
    state.templates = null;
    render();
    return;
  }

  try {
    state.templates = await api.listTemplates();
  } catch (e) {
    state.templates = null;
    state.templatesError = e.message;
  }

  render();
}

function goHome() {
  state.view = 'home';
  state.notice = null;
  render();
  loadTemplates();
}

function newTemplate() {
  const name = state.newTemplateName.trim();
  state.homeError = null;
  if (!TEMPLATE_NAME.test(name)) {
    state.homeError = 'Nome do template: use letras, dígitos, ".", "-" ou "_" (ex.: dps-municipio).';
    render();
    return;
  }

  if (state.templates?.some((t) => t.templateId === name)) {
    state.homeError = `Já existe o template "${name}": abra-o para publicar uma nova versão.`;
    render();
    return;
  }

  if (state.current && !window.confirm(`Descartar a edição em andamento de "${state.current.templateId}"?`)) return;
  Object.assign(state, freshEditor(), { view: 'editor', current: { templateId: name, isNew: true, baseVersion: null }, newTemplateName: '', notice: null });
  save();
  render();
}

/** Abre uma versão publicada (a ativa, se não informada) — o ponto de partida da próxima versão. */
async function openVersion(templateId, version) {
  if (state.current && state.view === 'home' && state.current.templateId !== templateId
    && !window.confirm(`Descartar a edição em andamento de "${state.current.templateId}"?`)) return;

  state.busy = true;
  state.homeError = null;
  render();
  try {
    const target = version ?? state.templates?.find((t) => t.templateId === templateId)?.activeVersion;
    if (!target) throw new Error(`O template "${templateId}" não tem versão publicada.`);
    const v = await api.getVersion(templateId, target);
    const [tree, sourceTreeResult] = await Promise.all([
      api.describeSchema(v.schema),
      v.sourceSchema ? api.describeSourceSchema(v.sourceSchema) : Promise.resolve(null),
    ]);
    const { mappings, problems } = mappingsFromTemplate(tree.root, v.mapping);
    const { sampleXml, expectedXml } = sampleScenario(v.scenarios);
    Object.assign(state, freshEditor(), {
      view: 'editor',
      current: { templateId, isNew: false, baseVersion: v.version },
      schema: v.schema,
      tree,
      sourceSchema: v.sourceSchema ?? null,
      sourceTree: sourceTreeResult,
      mappings,
      lookupTables: rowsFromApi(v.lookupTables),
      sampleXml,
      expectedXml,
      selectedPath: tree.root.path,
      notice: problems.length > 0
        ? `Versão ${v.version} aberta, mas ${problems.length} regra(s) não puderam ser representadas no editor e não iriam na próxima versão: ${problems.join(' ')}`
        : `Versão ${v.version} de "${templateId}" aberta.`,
    });
    if (!v.sourceSchema) state.notice += ' Esta versão não tem XSD de origem: carregue-o para publicar a próxima.';
    save();
    await loadVersions();
  } catch (e) {
    if (state.view === 'home') state.homeError = e.message;
    else state.publishError = e.message;
  } finally {
    state.busy = false;
    render();
  }
}

async function loadVersions() {
  state.versionsError = null;
  if (!state.current || state.current.isNew) {
    state.versions = [];
    return;
  }

  try {
    state.versions = await api.listVersions(state.current.templateId);
  } catch (e) {
    state.versionsError = e.message;
  }
}

async function runRollback(version) {
  if (!window.confirm(`Reativar a versão ${version} de "${state.current.templateId}"? O microsserviço passa a converter com ela.`)) return;
  state.busy = true;
  state.publishError = null;
  render();
  try {
    const result = await api.rollback(state.current.templateId, version);
    if (result?.success) state.notice = `Versão ${result.activeVersion} reativada. A edição atual não mudou.`;
    else state.publishError = result?.error ?? 'Rollback recusado.';
    await loadVersions();
  } catch (e) {
    state.publishError = e.message;
  } finally {
    state.busy = false;
    render();
  }
}

// ---------------------------------------------------------------- ações do editor

/** Muda o DE-PARA: o último teste fica desatualizado e a edição é guardada no navegador. */
function update(mutator) {
  mutator(state);
  state.testResult = state.testResult && { ...state.testResult, stale: true };
  state.qualityStale = state.qualityResult !== null;
  save();
  render();
}

/** Carrega o XSD escolhido de um dos lados ('source' | 'destination'). */
async function loadXsd(side) {
  const picker = state.pickers[side];
  if (picker.files.length === 0) {
    picker.error = SCHEMA_PANELS[side].noFiles;
    render();
    return;
  }

  try {
    const upload = {
      files: await Promise.all(picker.files.map(async (f) => ({ name: f.name, contentBase64: await readFileAsBase64(f) }))),
      entryPoint: entryPointOf(picker),
      rootElementName: picker.rootDraft.trim() || null,
    };
    if (side === 'source') await useSourceSchema(upload);
    else await useDestinationSchema(upload);
  } catch (e) {
    picker.error = e.message;
    render();
  }
}

async function useSourceSchema(sourceSchema) {
  const tree = await api.describeSourceSchema(sourceSchema);
  update((s) => {
    s.sourceSchema = sourceSchema;
    s.sourceTree = tree;
    s.pickers.source = { ...s.pickers.source, error: null, open: false };
    s.testResult = null;
  });
}

async function useDestinationSchema(schema) {
  // XSD novo sobre um DE-PARA existente (ex.: nova versão da prefeitura): compara antes de trocar,
  // para mostrar o que mudou e não perder mapeamentos em silêncio (ADR-0017).
  const previousRoot = state.tree && state.schema ? state.tree.root : null;
  const report = previousRoot
    ? await api.compareSchemas({ before: state.schema, after: schema, mapping: buildMappingTemplate(previousRoot, state.mappings) })
    : null;
  const tree = await api.describeSchema(schema);
  update((s) => {
    s.schema = schema;
    s.tree = tree;
    s.pickers.destination = { ...s.pickers.destination, error: null, open: false };
    s.schemaChangesError = null;
    if (report && !report.identical) {
      const applied = applySchemaChanges(tree.root, s.mappings, report, previousRoot);
      s.mappings = applied.mappings;
      // Pendentes de antes que continuem sem destino também entram.
      s.schemaChanges = { report, orphans: mergeOrphans(s.schemaChanges?.orphans, applied.orphans, tree.root, applied.mappings) };
    } else {
      s.mappings = pruneMappings(tree.root, s.mappings);
      if (report?.identical) s.notice = 'O XSD novo é idêntico ao atual: nada mudou.';
    }
    s.selectedPath = tree.root.path;
    s.testResult = null;
  });
}

async function runTest() {
  state.testError = null;
  state.localProblems = [];
  if (!state.tree) {
    state.testError = 'Carregue o XSD de destino primeiro (passo 2).';
    render();
    return;
  }

  const inputProblems = [
    !state.sourceSchema && 'Carregue o XSD de origem (passo 1).',
    !state.sampleXml.trim() && 'Informe um XML de exemplo no formato do XSD de origem para testar.',
  ].filter(Boolean);
  if (inputProblems.length > 0) {
    state.testError = inputProblems.join(' ');
    render();
    return;
  }

  state.localProblems = [...validateDraft(state.tree.root, state.mappings, state.operators), ...duplicateProblems(state.lookupTables)];
  if (state.localProblems.length > 0) {
    render();
    return;
  }

  state.busy = true;
  render();
  try {
    const expectedXml = state.expectedXml.trim() ? state.expectedXml : null; // em branco = sem comparação
    const result = await api.testDraft({
      schema: state.schema,
      mapping: buildMappingTemplate(state.tree.root, state.mappings),
      sourceSchema: state.sourceSchema,
      sourceXml: state.sampleXml,
      lookupTables: tablesForApi(state.lookupTables),
      expectedXml,
    });
    // Guarda o XML esperado usado neste teste: a comparação mostrada é sempre a
    // do teste, mesmo que o campo seja editado depois (revisão da Fase 6, A10).
    state.testResult = { ...result, testedExpectedXml: expectedXml };
  } catch (e) {
    state.testError = e.message;
    state.testResult = null;
  } finally {
    state.busy = false;
    render();
  }
}

/** Testes estruturais automáticos (Fase 8) — dados gerados pela suíte, não o XML de exemplo. */
async function runQuality() {
  state.qualityError = null;
  if (!state.tree) {
    state.qualityError = 'Carregue o XSD de destino primeiro.';
    render();
    return;
  }

  const problems = validateDraft(state.tree.root, state.mappings, state.operators);
  if (problems.length > 0) {
    state.localProblems = problems;
    render();
    return;
  }

  state.busy = true;
  render();
  try {
    state.qualityResult = await api.checkQuality({
      schema: state.schema,
      mapping: buildMappingTemplate(state.tree.root, state.mappings),
      lookupTables: tablesForApi(state.lookupTables),
    });
    state.qualityStale = false;
  } catch (e) {
    state.qualityError = e.message;
    state.qualityResult = null;
  } finally {
    state.busy = false;
    render();
  }
}

/** Publica a próxima versão: a API só aceita se todos os gates passarem. */
async function runPublish() {
  state.publishError = null;
  state.publishResult = null;
  let mapping = null;
  const problems = [];
  if (state.tree) {
    problems.push(...validateDraft(state.tree.root, state.mappings, state.operators));
    if (problems.length === 0) mapping = buildMappingTemplate(state.tree.root, state.mappings);
  }

  const pendingRemap = state.schemaChanges?.orphans.length ?? 0;
  if (pendingRemap > 0) {
    problems.push(`Há ${pendingRemap} mapeamento(s) do XSD anterior para remapear ou descartar (painel "Mudanças do XSD").`);
  }
  const inactive = state.schemaChanges && state.tree ? inactiveMappings(state.tree.root, state.mappings).length : 0;
  if (inactive > 0) {
    problems.push(`Há ${inactive} mapeamento(s) que o motor ignoraria (bloco que passou a repetir sem "for each"): defina o "for each" ou descarte (painel "Mudanças do XSD").`);
  }

  const { problems: missing, request } = buildPublication({
    templateId: state.current?.templateId,
    schema: state.schema,
    sourceSchema: state.sourceSchema,
    mapping,
    lookupTables: tablesForApi(state.lookupTables),
    sampleXml: state.sampleXml,
    expectedXml: state.expectedXml,
  });
  state.publishProblems = [...problems, ...missing];
  if (!request || state.publishProblems.length > 0) {
    render();
    return;
  }

  state.busy = true;
  render();
  try {
    state.publishResult = await api.publishTemplate(request.templateId, request.body);
    if (state.publishResult?.success) {
      state.current = { templateId: request.templateId, isNew: false, baseVersion: state.publishResult.snapshotVersion };
      save();
      await loadVersions();
    }
  } catch (e) {
    state.publishError = e.message;
  } finally {
    state.busy = false;
    render();
  }
}

/** Campos de origem disponíveis para um nó de destino (respeitando blocos for each). */
function scopeFor(path) {
  const root = sourceRoot();
  if (!state.tree || !root) return { scope: null, label: '(origem)', missingBlock: false, chain: [] };
  const chain = scopeChain(state.tree.root, state.mappings, path);
  if (chain.includes(null)) return { scope: null, label: '—', missingBlock: true, chain };
  const scope = scopeNode(root, chain);
  const label = chain.length === 0 ? `<${state.sourceTree?.rootElementName ?? 'origem'}> (raiz)` : chain.map((c) => `item de ${c}`).join(' › ');
  return { scope, label, missingBlock: false, chain };
}

function mapSource(destinationPath, sourcePath) {
  const node = findDestination(state.tree.root, destinationPath);
  if (!node || !node.mappable) return;
  const { scope } = scopeFor(destinationPath);
  const source = findNode(scope, sourcePath);
  if (!source) {
    state.notice = `"${sourcePath}" não existe no escopo de "${node.name}". Selecione o destino primeiro para ver os campos do escopo dele.`;
    state.selectedPath = destinationPath;
    render();
    return;
  }

  const composite = source.type === 'list' || source.type === 'object';
  if (node.kind === 'Scalar') {
    if (composite) {
      state.notice = `"${sourcePath}" é ${source.type === 'list' ? 'uma lista' : 'um objeto'}; escolha um campo simples.`;
      render();
      return;
    }

    update((s) => {
      s.mappings[destinationPath] = { ...(s.mappings[destinationPath] ?? {}), value: field(sourcePath) };
      s.selectedPath = destinationPath;
      s.notice = null;
    });
  } else if (isRepeatable(node)) {
    if (!composite) {
      state.notice = `Um bloco "for each" precisa de uma lista (ou de um objeto, como 1 item); "${sourcePath}" não é.`;
      render();
      return;
    }

    update((s) => {
      s.mappings[destinationPath] = { forEach: withCollection(s.mappings[destinationPath]?.forEach, sourcePath, source.type === 'object') };
      s.selectedPath = destinationPath;
      s.notice = null;
    });
  }
}

/** Seleciona um nó de destino; `scroll` traz o nó para a vista (ex.: vindo de um diagnóstico). */
function selectPath(path, { scroll = false } = {}) {
  state.selectedPath = path;
  render();
  if (scroll) document.querySelector('.dest-node.selected')?.scrollIntoView({ block: 'center' });
}

// ---------------------------------------------------------------- renderização

const PANELS = [
  ['status-bar', () => renderHeader()],
  ['nav-actions', () => renderNav()],
  ['home-view', () => renderHome()],
  ['source-schema-panel', () => renderSchemaPanel('source')],
  ['schema-panel', () => renderSchemaPanel('destination')],
  ['source-panel', () => renderSourcePanel()],
  ['tables-panel', () => renderTablesPanel()],
  ['changes-panel', () => renderChangesPanel()],
  ['destination-panel', () => renderDestinationPanel()],
  ['rule-panel', () => renderRulePanel()],
  ['test-panel', () => renderTestPanel()],
  ['publish-panel', () => renderPublishPanel()],
];

const HOME_PANELS = new Set(['status-bar', 'nav-actions', 'home-view']);

const FOCUSABLE = 'button, select, input, textarea, a[href], [tabindex]';

// Campo que está disparando o evento "input" agora (a renderização síncrona
// dentro do handler o vê); limpo logo depois.
let typing = null;
document.addEventListener('input', (e) => {
  typing = e.target;
  queueMicrotask(() => { typing = null; });
}, true);
const isTextEntry = (el) => el instanceof HTMLTextAreaElement
  || (el instanceof HTMLInputElement && !['checkbox', 'radio', 'file', 'button', 'submit'].includes(el.type));

/**
 * Re-renderiza os painéis a partir do estado sem perder o lugar do usuário
 * (revisão da Fase 6, A1/A2):
 * - o painel com um campo de texto em edição não é recriado (o estado já foi
 *   atualizado pelo evento "input"; recriar tiraria o cursor do campo);
 * - nos demais, o foco volta ao controle na mesma posição do painel
 *   (ex.: o nó da árvore escolhido pelo teclado, o select alterado);
 * - a rolagem das áreas roláveis é preservada.
 */
function render() {
  const home = state.view === 'home';
  $('home-view').hidden = !home;
  $('editor-view').hidden = home;
  $('editor-menu').hidden = home;

  const scrolls = [...document.querySelectorAll('.scroll')].map((el) => [el.id, el.scrollTop]);
  const active = document.activeElement;
  const activePanel = active?.closest(PANELS.map(([id]) => `#${id}`).join(', '));
  // Só preserva o painel quando esta renderização veio da digitação no próprio
  // campo em foco; mudanças vindas de outro lugar (Enter num formulário, fim de
  // um teste) redesenham normalmente (revisão da Fase 6, R2/R3).
  const keepPanel = activePanel && typing && isTextEntry(active) && typing === active ? activePanel.id : null;
  const focusIndex = activePanel && !keepPanel ? [...activePanel.querySelectorAll(FOCUSABLE)].indexOf(active) : -1;
  const caret = isTextEntry(active) ? [active.selectionStart, active.selectionEnd] : null;

  for (const [id, renderPanel] of PANELS) {
    if (id === keepPanel) continue;
    if (home !== HOME_PANELS.has(id) && id !== 'status-bar' && id !== 'nav-actions') continue;
    renderPanel();
  }

  if (activePanel && !keepPanel && focusIndex >= 0) {
    const candidates = $(activePanel.id)?.querySelectorAll(FOCUSABLE) ?? [];
    const target = candidates[focusIndex] ?? candidates[candidates.length - 1];
    target?.focus({ preventScroll: true });
    // Campo de texto recriado: o cursor volta para onde estava.
    if (caret && target && isTextEntry(target) && caret[0] !== null) {
      try { target.setSelectionRange(caret[0], caret[1]); } catch { /* tipo sem seleção (ex.: number) */ }
    }
  }

  for (const [id, top] of scrolls) {
    const el = $(id);
    if (el) el.scrollTop = top;
  }
}

function renderNav() {
  if (state.view === 'home' || !state.current) {
    replaceChildren($('nav-actions'));
    return;
  }

  const c = state.current;
  replaceChildren($('nav-actions'),
    h('button', { type: 'button', id: 'btn-home', onclick: goHome }, '← Templates'),
    h('span', { class: 'current-template', id: 'current-template' },
      c.isNew ? `${c.templateId} (novo)` : `${c.templateId} · a partir da v${c.baseVersion}`));
}

function renderHeader() {
  const counts = state.view === 'editor' && state.tree ? statusCounts(state.tree.root, state.mappings) : null;
  const pills = [];
  if (counts) {
    if (counts.missing) pills.push(h('span', { class: 'pill missing' }, `${counts.missing} obrigatório(s) sem mapeamento`));
    if (counts.blocked) pills.push(h('span', { class: 'pill missing', title: 'Conteúdo opaco (ex.: xs:any) que o motor não gera' }, `${counts.blocked} obrigatório(s) não mapeável(is)`));
    if (counts.inactive) pills.push(h('span', { class: 'pill missing', title: 'Mapeados dentro de bloco "for each" sem coleção de origem' }, `${counts.inactive} mapeamento(s) ignorado(s)`));
    if (!counts.missing && !counts.blocked) pills.push(h('span', { class: 'pill ok' }, 'todos os obrigatórios mapeados'));
  }

  replaceChildren($('status-bar'),
    state.apiError && h('span', { class: 'banner error' }, state.apiError),
    pills,
    state.notice && h('span', { class: 'pill notice', role: 'status' }, state.notice));
}

function renderHome() {
  replaceChildren($('home-view'), homeView({
    hasCredential: api.hasCredential(),
    templates: state.templates,
    templatesError: state.templatesError,
    homeError: state.homeError,
    busy: state.busy,
    current: state.current,
    newTemplateName: state.newTemplateName,
    onNewTemplateName: (value) => { state.newTemplateName = value; },
    onCreate: newTemplate,
    onOpen: openVersion,
    onContinue: () => { state.view = 'editor'; render(); },
  }));
}

const SCHEMA_PANELS = {
  source: {
    panelId: 'source-schema-panel',
    idPrefix: 'source-',
    noFiles: 'Escolha ao menos um arquivo .xsd de origem.',
    loaded: () => state.sourceTree && { entryPoint: state.sourceSchema?.entryPoint, rootElementName: state.sourceTree.rootElementName },
    labels: { summaryNew: 'Formato do XML de entrada', summaryLoaded: 'Trocar o XSD de origem', files: 'Arquivos XSD de origem', load: 'Carregar XSD de origem' },
  },
  destination: {
    panelId: 'schema-panel',
    idPrefix: '',
    noFiles: 'Escolha ao menos um arquivo .xsd.',
    loaded: () => state.tree && { entryPoint: state.schema?.entryPoint, rootElementName: state.tree.rootElementName },
    labels: { summaryNew: 'Formato do XML de saída', summaryLoaded: 'Trocar o XSD de destino (nova versão do layout)', files: 'Arquivos XSD de destino', load: 'Carregar XSD de destino' },
  },
};

function renderSchemaPanel(side) {
  const panel = SCHEMA_PANELS[side];
  const picker = state.pickers[side];
  replaceChildren($(panel.panelId), schemaPicker({
    idPrefix: panel.idPrefix,
    picker,
    loaded: panel.loaded(),
    labels: panel.labels,
    onChange: (patch) => { state.pickers[side] = { ...state.pickers[side], ...patch }; render(); },
    onRootDraft: (value) => { state.pickers[side].rootDraft = value; },
    // "Trocar o XSD" aberto pelo usuário continua aberto entre redesenhos (escolher arquivo redesenha).
    onToggle: (open) => { state.pickers[side].open = open; },
    onLoad: () => loadXsd(side),
  }));
}

function renderSourcePanel() {
  const { scope, label, missingBlock } = scopeFor(state.selectedPath ?? '');
  replaceChildren($('source-panel'),
    missingBlock
      ? h('p', { class: 'warning' }, 'O destino selecionado está num bloco repetível sem coleção definida.')
      : sourceTree({
        scope,
        scopeLabel: label,
        onPick: (path) => {
          if (!state.selectedPath) {
            state.notice = 'Selecione um campo de destino e depois clique no campo de origem (ou arraste).';
            render();
            return;
          }

          mapSource(state.selectedPath, path);
        },
      }));
}

function renderTablesPanel() {
  replaceChildren($('tables-panel'), tablesPanel({
    tables: state.lookupTables,
    onRows: (name, rowsOf) => update((s) => { s.lookupTables[name] = rowsOf(s.lookupTables[name] ?? []); }),
    onDelete: (name) => update((s) => { delete s.lookupTables[name]; }),
    onCreate: (name) => update((s) => { s.lookupTables[name] = [['', '']]; }),
  }));
}

function renderChangesPanel() {
  const changes = state.schemaChanges;
  if (!changes || !state.tree) {
    replaceChildren($('changes-panel'));
    return;
  }

  const selected = state.selectedPath && findDestination(state.tree.root, state.selectedPath);
  replaceChildren($('changes-panel'), schemaChangesPanel({
    report: changes.report,
    orphans: changes.orphans,
    inactive: inactiveMappings(state.tree.root, state.mappings).map((item) => ({ ...item, summary: summarize(state.mappings[item.path]) })),
    onDiscardMapping: (path) => update((s) => {
      const { [path]: _, ...rest } = s.mappings;
      s.mappings = rest;
    }),
    selectedPath: state.selectedPath,
    selectedAccepts: (orphan) => Boolean(selected) && accepts(selected, orphan.entry) && !state.mappings[state.selectedPath],
    exists: (path) => Boolean(findDestination(state.tree.root, path)),
    error: state.schemaChangesError,
    onSelect: selectPath,
    onMove: (id, targetPath) => {
      const moved = moveOrphan(state.tree.root, state.mappings, changes.orphans, id, targetPath);
      if (moved.error) {
        state.schemaChangesError = moved.error;
        render();
        return;
      }

      update((s) => {
        s.mappings = moved.mappings;
        s.schemaChanges = { ...changes, orphans: moved.orphans };
        s.schemaChangesError = null;
        s.selectedPath = targetPath;
      });
    },
    onDiscard: (id) => update((s) => {
      s.schemaChanges = { ...changes, orphans: discardOrphan(changes.orphans, id) };
      s.schemaChangesError = null;
    }),
    onClose: () => update((s) => {
      s.schemaChanges = null;
      s.schemaChangesError = null;
    }),
  }));
}

function renderDestinationPanel() {
  if (!state.tree) {
    replaceChildren($('destination-panel'), h('p', { class: 'muted' }, 'Carregue o XSD de destino (passo 2) para ver a árvore.'));
    return;
  }

  replaceChildren($('destination-panel'), destinationTree({
    root: state.tree.root,
    mappings: state.mappings,
    changes: changeMarks(state.schemaChanges?.report),
    status: computeStatus(state.tree.root, state.mappings),
    diagnosticsByPath: criticalByPath(state.testResult),
    selectedPath: state.selectedPath,
    onSelect: (path) => {
      state.notice = null;
      selectPath(path);
    },
    onDrop: mapSource,
  }));
}

function renderRulePanel() {
  const path = state.selectedPath;
  const node = state.tree && path ? findDestination(state.tree.root, path) : null;
  const { scope, label, missingBlock, chain } = scopeFor(path ?? '');
  const forEach = state.mappings[path]?.forEach;
  const itemScope = forEach?.collectionPath && scope ? scopeNode(scope, [forEach.collectionPath]) : null;

  // Lente do mapeamento deste nó: lê sempre o estado atual (ver ui/lens.js).
  const lens = {
    get: () => state.mappings[path],
    set: (entry) => update((s) => {
      if (entry) s.mappings[path] = entry;
      else delete s.mappings[path];
    }),
  };

  replaceChildren($('rule-panel'), rulePanel({
    node,
    lens,
    fields: withRootFields(valueFields(scope), chain, sourceRoot()),
    listFields: collectionFields(scope),
    itemFields: withRootFields(valueFields(itemScope), [...chain, forEach?.collectionPath ?? ''], sourceRoot()),
    operators: state.operators,
    lookupTableNames: Object.keys(state.lookupTables),
    scopeLabel: label,
    parentBlockMissing: missingBlock,
    onRemoveBlock: () => update((s) => { s.mappings = removeSubtree(s.mappings, path); }),
  }));
}

function renderTestPanel() {
  let mappingTemplate = null;
  if (state.tree) {
    try {
      mappingTemplate = buildMappingTemplate(state.tree.root, state.mappings);
    } catch {
      mappingTemplate = null; // expressão incompleta — o validador explica ao testar
    }
  }

  const scrollTo = (path) => selectPath(path, { scroll: true });
  replaceChildren($('test-panel'),
    testControls({
      sampleXml: state.sampleXml,
      expectedXml: state.expectedXml,
      busy: state.busy,
      stale: Boolean(state.testResult?.stale),
      onSampleXml: (text) => update((s) => { s.sampleXml = text; }),
      onExpectedXml: (text) => update((s) => { s.expectedXml = text; }),
      onTest: runTest,
      onQuality: runQuality,
    }),
    testPanel({
      result: state.testResult,
      localProblems: state.localProblems,
      error: state.testError,
      mappingTemplate,
      busy: state.busy,
      onSelectPath: scrollTo,
    }),
    h('section', { id: 'quality-panel', 'aria-label': 'Testes estruturais' },
      qualityPanel({
        result: state.qualityResult,
        error: state.qualityError,
        stale: state.qualityStale,
        onSelectPath: scrollTo,
      })));
}

function renderPublishPanel() {
  const c = state.current;
  replaceChildren($('publish-panel'), publishPanel({
    templateId: c?.templateId ?? '',
    isNew: Boolean(c?.isNew),
    baseVersion: c?.baseVersion ?? null,
    problems: state.publishProblems,
    result: state.publishResult,
    error: state.publishError ?? (api.hasCredential() ? null : 'Informe a credencial da API (topo da página) para publicar.'),
    busy: state.busy,
    canPublish: api.hasCredential(),
    versions: state.versions,
    versionsError: state.versionsError,
    onTemplateId: (value) => { state.current = { ...state.current, templateId: value.trim() }; save(); },
    onPublish: runPublish,
    onOpenVersion: (version) => {
      if (window.confirm(`Abrir a versão ${version}? A edição atual, se não publicada, é descartada.`)) openVersion(c.templateId, version);
    },
    onRollback: runRollback,
    onSelectPath: selectPath,
  }));
}

// ---------------------------------------------------------------- início

async function start() {
  $('btn-connect').addEventListener('click', connect);
  $('api-key').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') connect();
  });

  restore();
  render();
  await Promise.all([loadOperators(), loadTemplates()]);
}

// A credencial fica só na sessão do navegador; o campo é limpo depois de usada.
async function connect() {
  api.setCredential($('api-key').value);
  $('api-key').value = '';
  await Promise.all([loadOperators(), loadTemplates()]);
  if (state.view === 'editor') {
    await loadVersions();
    render();
  }
}

async function loadOperators() {
  state.apiError = null;
  try {
    state.operators = await api.listOperators();
  } catch (e) {
    state.apiError = e.message;
  }

  render();
}

start();
