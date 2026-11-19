// Troca do XSD de destino num rascunho já mapeado (Fase 14, ADR-0017). Módulo puro.
//
// O relatório vem de POST /v1/design/schema-changes. Aqui ele é aplicado aos
// mapeamentos do editor (mapa por caminho de destino):
//   - raiz renomeada: os caminhos acompanham;
//   - mapeamento cujo campo continua (e aceita o mesmo tipo de mapeamento): fica;
//   - os demais viram "pendentes de remapear" com sugestões, em vez de sumirem
//     em silêncio; a publicação fica bloqueada até cada um ser movido ou descartado.

import { findDestination, isRepeatable, computeStatus } from './draft.js';

export const IMPACT_ORDER = ['Remap', 'Map', 'Review', 'Info'];

export const IMPACT_LABEL = {
  Remap: 'remapear',
  Map: 'mapear',
  Review: 'revisar',
  Info: 'informativo',
};

/** Que tipo de nó aceita o mapeamento: escalar (valor) ou bloco repetível (for each). */
export function accepts(node, entry) {
  if (!node || !node.mappable || node.kind === 'Group') return false;
  if (entry?.forEach) return node.kind !== 'Scalar' && isRepeatable(node);
  return node.kind === 'Scalar';
}

function renamedRoot(path, report) {
  if (!report || report.beforeRootElementName === report.afterRootElementName) return path;
  const before = `/${report.beforeRootElementName}`;
  return path === before || path.startsWith(`${before}/`) ? `/${report.afterRootElementName}${path.slice(before.length)}` : path;
}

/** Sugestões para um caminho antigo: as do relatório e as de blocos movidos (o mesmo caminho dentro do bloco no lugar novo). */
export function suggestionsFor(path, report, newRoot, mappings, entry) {
  const found = [];
  for (const change of report?.changes ?? []) {
    if ((change.path === path || change.beforePath === path) && change.suggestions) found.push(...change.suggestions);
    if (change.kind === 'Moved' && change.beforePath && path.startsWith(`${change.beforePath}/`)) {
      found.push(change.path + path.slice(change.beforePath.length));
    }
  }

  return [...new Set(found)].filter((p) => !mappings[p] && accepts(findDestination(newRoot, p), entry));
}

// Caminho no XSD novo de um caminho antigo dentro de um bloco movido (para reconhecer blocos movidos que deixaram de repetir).
function movedPath(path, report) {
  for (const change of report?.changes ?? []) {
    if (change.kind === 'Moved' && change.beforePath && (path === change.beforePath || path.startsWith(`${change.beforePath}/`))) {
      return change.path + path.slice(change.beforePath.length);
    }
  }

  return path;
}

/**
 * Aplica o relatório ao trocar o XSD: devolve os mapeamentos que continuam
 * válidos e os pendentes de remapear ({ path, entry, reason, suggestions }).
 * oldRoot (opcional): árvore anterior, para achar mapeamentos que estavam
 * ignorados (bloco repetível sem for each) e passariam a ser gerados na
 * árvore nova — esses também viram pendentes para conferir (revisão da Fase 14, aviso 2).
 */
export function applySchemaChanges(newRoot, mappings, report, oldRoot = null) {
  const kept = {};
  const moved = [];
  // Blocos que deixaram de repetir e tinham "for each": os caminhos de origem dos filhos eram relativos ao item.
  const stopped = (report?.changes ?? []).filter((c) => c.kind === 'StoppedRepeating' && c.impact === 'Remap').map((c) => c.path);
  const statusBefore = oldRoot ? computeStatus(oldRoot, mappings ?? {}) : new Map();
  const reasons = new Map();
  for (const [oldPath, entry] of Object.entries(mappings ?? {})) {
    const path = renamedRoot(oldPath, report);
    const block = stopped.find((b) => path.startsWith(`${b}/`) || movedPath(oldPath, report).startsWith(`${b}/`));
    if (accepts(findDestination(newRoot, path), entry) && !block) kept[path] = entry;
    else moved.push([oldPath, path, entry, block]);
  }

  const statusAfter = computeStatus(newRoot, kept);
  for (const [path, entry] of Object.entries(kept)) {
    const oldPath = Object.keys(mappings).find((p) => renamedRoot(p, report) === path) ?? path;
    if (statusBefore.get(oldPath) === 'inactive' && statusAfter.get(path) !== 'inactive') {
      delete kept[path];
      moved.push([oldPath, path, entry, null]);
      reasons.set(oldPath, 'Estava ignorado (bloco repetível sem "for each") e passaria a ser gerado com o XSD novo: confira o campo de origem e confirme (mover para o mesmo campo) ou descarte.');
    }
  }

  const orphans = moved.map(([oldPath, path, entry, block]) => ({
    path: oldPath,
    entry,
    reason: reasons.get(oldPath) ?? (block
      ? `O bloco ${block} deixou de repetir: os campos de origem eram relativos ao item da lista. Confira e confirme (mover para o mesmo campo) ou descarte.`
      : 'O campo não existe mais ou mudou de tipo de nó.'),
    suggestions: (block || reasons.has(oldPath) ? [block && movedPath(oldPath, report) !== oldPath ? movedPath(oldPath, report) : path] : [])
      .concat(suggestionsFor(oldPath, report, newRoot, kept, entry), path !== oldPath ? suggestionsFor(path, report, newRoot, kept, entry) : [])
      .filter((p, i, all) => all.indexOf(p) === i && !kept[p] && accepts(findDestination(newRoot, p), entry)),
  }));
  return { mappings: kept, orphans };
}

/**
 * Junta pendentes (troca anterior ainda aberta + nova). O mesmo caminho com
 * mapeamentos diferentes vira dois pendentes (nada é descartado, revisão da Fase 14,
 * aviso 1); cada um recebe um id próprio. Sugestões antigas são refeitas sobre a árvore nova.
 */
export function mergeOrphans(previous, added, root = null, mappings = {}) {
  const seen = new Set();
  const ids = new Set();
  const result = [];
  for (const orphan of [...(previous ?? []), ...added]) {
    const key = `${orphan.path}\u0000${JSON.stringify(orphan.entry)}`;
    if (seen.has(key)) continue;
    seen.add(key);
    let id = orphan.path;
    for (let n = 2; ids.has(id); n++) id = `${orphan.path} (${n})`;
    ids.add(id);
    const suggestions = root
      ? orphan.suggestions.filter((p) => !mappings[p] && accepts(findDestination(root, p), orphan.entry))
      : orphan.suggestions;
    result.push({ ...orphan, id, suggestions });
  }

  return result;
}

/** Identifica um pendente: o id dado por mergeOrphans ou, antes disso, o caminho. */
export const orphanId = (orphan) => orphan.id ?? orphan.path;

/**
 * Mapeamentos que o motor ignoraria na árvore nova: dentro de um bloco
 * repetível sem "for each" (ex.: o bloco passou a repetir). Revisão da Fase 14, C1:
 * enquanto o relatório estiver aberto, eles bloqueiam a publicação.
 */
export function inactiveMappings(root, mappings) {
  const status = computeStatus(root, mappings);
  const result = [];
  for (const path of Object.keys(mappings)) {
    if (status.get(path) !== 'inactive') continue;
    let block = null;
    for (let p = path.slice(0, path.lastIndexOf('/')); p.includes('/'); p = p.slice(0, p.lastIndexOf('/'))) {
      const node = findDestination(root, p);
      if (node && node.kind !== 'Scalar' && isRepeatable(node) && !mappings[p]?.forEach?.collectionPath) block = p;
    }
    result.push({ path, block });
  }

  return result.sort((a, b) => a.path.localeCompare(b.path));
}

/** Move um pendente para um campo do XSD novo. Devolve { mappings, orphans } ou { error }. */
export function moveOrphan(root, mappings, orphans, id, targetPath) {
  const orphan = orphans.find((o) => orphanId(o) === id);
  if (!orphan) return { error: 'Mapeamento pendente não encontrado.' };
  const node = findDestination(root, targetPath);
  if (!accepts(node, orphan.entry)) {
    return { error: orphan.entry?.forEach ? 'Escolha um bloco repetível para o "for each".' : 'Escolha um campo de valor (não um bloco).' };
  }
  if (mappings[targetPath]) return { error: `'${node.name}' já está mapeado: limpe-o antes ou escolha outro campo.` };

  const rest = orphans.filter((o) => o !== orphan);
  return {
    mappings: { ...mappings, [targetPath]: orphan.entry },
    // Outro pendente que sugeria o mesmo campo deixa de sugeri-lo.
    orphans: rest.map((o) => ({ ...o, suggestions: o.suggestions.filter((p) => p !== targetPath) })),
  };
}

export const discardOrphan = (orphans, id) => orphans.filter((o) => orphanId(o) !== id);

/** Marca por caminho do XSD novo (o impacto mais forte), para a árvore de destino. */
export function changeMarks(report) {
  const marks = new Map();
  for (const change of report?.changes ?? []) {
    if (change.kind === 'Removed') continue; // não existe na árvore nova
    const current = marks.get(change.path);
    if (!current || IMPACT_ORDER.indexOf(change.impact) < IMPACT_ORDER.indexOf(current)) marks.set(change.path, change.impact);
  }

  return marks;
}

/** Forma mínima de um relatório guardado na edição em andamento (para não carregar lixo do localStorage). */
export function validSchemaChanges(value) {
  const report = value?.report;
  return Boolean(value && typeof value === 'object' && report && Array.isArray(report.changes)
    && report.summary && ['remap', 'map', 'review', 'info'].every((k) => Number.isInteger(report.summary[k]))
    && typeof report.beforeRootElementName === 'string' && typeof report.afterRootElementName === 'string'
    && report.changes.every((c) => c && typeof c.path === 'string' && typeof c.message === 'string')
    && Array.isArray(value.orphans) && value.orphans.every((o) => o && typeof o.path === 'string' && Array.isArray(o.suggestions)
      && o.suggestions.every((s) => typeof s === 'string')
      && o.entry && typeof o.entry === 'object' && !Array.isArray(o.entry)
      && ((o.entry.value && typeof o.entry.value === 'object') || (o.entry.forEach && typeof o.entry.forEach === 'object'))));
}
