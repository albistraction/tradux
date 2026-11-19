// Edição em andamento guardada no navegador (localStorage), para não perdê-la
// ao recarregar a página — nunca uma versão: as versões são do serviço
// (ADR-0025). A forma guardada é conferida antes de voltar ao estado
// (revisão da Fase 6, A4).

import { TEMPLATE_NAME } from './publish.js';
import { pruneMappings } from './draft.js';
import { validSchemaChanges } from './schemaChanges.js';
import { validRows } from './lookupTables.js';

const STORAGE_KEY = 'tradux-editor-draft-v2';

const STORED_FIELDS = [
  'current', 'schema', 'tree', 'mappings', 'lookupTables', 'expectedXml', 'sourceSchema', 'sourceTree', 'sampleXml', 'schemaChanges',
];

const isObject = (value) => value !== null && typeof value === 'object' && !Array.isArray(value);
const validUpload = (upload) => isObject(upload) && Array.isArray(upload.files) && typeof upload.entryPoint === 'string';

/** Problemas de forma do rascunho guardado (vazio = ok). */
export function draftProblems(draft) {
  if (!isObject(draft)) return ['o rascunho não é um objeto.'];
  const problems = [];

  const validNode = (node) => isObject(node) && typeof node.path === 'string' && typeof node.name === 'string'
    && typeof node.kind === 'string' && Array.isArray(node.children ?? []) && (node.children ?? []).every(validNode);

  const current = draft.current;
  if (!(isObject(current) && typeof current.templateId === 'string' && TEMPLATE_NAME.test(current.templateId))) {
    problems.push('template em edição inválido.');
  }
  if (draft.tree != null && !(draft.tree.root && validNode(draft.tree.root))) problems.push('árvore de destino inválida.');
  if (draft.tree != null && !validUpload(draft.schema)) problems.push('schema de destino ausente ou inválido.');
  if (draft.mappings != null && !isObject(draft.mappings)) problems.push('mapeamentos inválidos.');
  if (draft.lookupTables != null && !(isObject(draft.lookupTables) && Object.values(draft.lookupTables).every(validRows))) {
    problems.push('tabelas DE-PARA inválidas.');
  }
  if (draft.sampleXml != null && typeof draft.sampleXml !== 'string') problems.push('XML de exemplo inválido.');
  if (draft.sourceTree != null && !(draft.sourceTree.root && Array.isArray(draft.sourceTree.root.children) && validUpload(draft.sourceSchema))) {
    problems.push('XSD de origem ausente ou inválido.');
  }
  return problems;
}

/** Campos do editor a partir do rascunho guardado; forma inválida lança, sem tocar em nada. */
export function fromStoredDraft(draft) {
  const problems = draftProblems(draft);
  if (problems.length > 0) throw new Error(problems.join(' '));

  const tree = draft.tree ?? null;
  return {
    current: { templateId: draft.current.templateId, isNew: Boolean(draft.current.isNew), baseVersion: draft.current.baseVersion ?? null },
    schema: draft.schema ?? null,
    tree,
    mappings: tree ? pruneMappings(tree.root, draft.mappings ?? {}) : {},
    lookupTables: draft.lookupTables ?? {},
    expectedXml: draft.expectedXml ?? '',
    selectedPath: tree?.root.path ?? null,
    sourceSchema: draft.sourceTree ? draft.sourceSchema : null,
    sourceTree: draft.sourceTree ?? null,
    sampleXml: draft.sampleXml ?? '',
    schemaChanges: tree && validSchemaChanges(draft.schemaChanges) ? draft.schemaChanges : null,
  };
}

export function saveLocalDraft(state) {
  if (!state.current) return;
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(Object.fromEntries(STORED_FIELDS.map((key) => [key, state[key]]))));
  } catch {
    // Sem espaço ou armazenamento bloqueado: a edição continua em memória.
  }
}

/** Rascunho guardado (campos do editor) ou null; forma inválida lança. */
export function readLocalDraft() {
  const raw = localStorage.getItem(STORAGE_KEY);
  return raw ? fromStoredDraft(JSON.parse(raw)) : null;
}

export function discardLocalDraft() {
  try { localStorage.removeItem(STORAGE_KEY); } catch { /* armazenamento indisponível */ }
}
