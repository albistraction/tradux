// Painel "Mudanças do XSD" (Fase 14, ADR-0017): o que mudou entre o XSD de
// destino anterior e o novo, e o que o operador precisa fazer. Só h()/textContent.

import { h } from './dom.js';
import { summarize } from '../model/draft.js';
import { IMPACT_ORDER, IMPACT_LABEL, orphanId } from '../model/schemaChanges.js';

const HEADINGS = {
  Remap: 'Remapear: mapeamentos que deixaram de valer',
  Map: 'Mapear: campos obrigatórios sem mapeamento',
  Review: 'Revisar: mapeamentos que continuam, mas podem não servir',
  Info: 'Informativo: o motor já acompanha',
};

/**
 * props: { report, orphans, inactive [{ path, block, summary }], selectedPath, selectedAccepts(orphan) → bool, exists(path) → bool,
 *          onMove(orphanId, targetPath), onDiscard(orphanId), onDiscardMapping(path), onSelect(path), onClose(), error }
 */
export function schemaChangesPanel({ report, orphans, inactive = [], selectedPath, selectedAccepts, exists, onMove, onDiscard, onDiscardMapping, onSelect, onClose, error }) {
  const summary = report.summary;
  const pending = orphans.length + inactive.length;

  const pathButton = (path) => (exists(path)
    ? h('button', { type: 'button', class: 'link path', title: 'Selecionar na árvore de destino', onclick: () => onSelect(path) }, path)
    : h('code', { class: 'path' }, path));

  const orphanRow = (orphan) => h('li', { class: 'orphan', 'data-path': orphan.path },
    h('div', {}, h('code', { class: 'path' }, orphan.path), ' ', h('span', { class: 'summary' }, summarize(orphan.entry))),
    orphan.reason && h('div', { class: 'muted' }, orphan.reason),
    h('div', { class: 'orphan-actions' },
      orphan.suggestions.map((target) => h('button', {
        type: 'button', class: 'primary', 'aria-label': `Mover ${orphan.path} para ${target}`, onclick: () => onMove(orphanId(orphan), target),
      }, `Mover para ${target}`)),
      h('button', {
        type: 'button',
        disabled: !selectedPath || !selectedAccepts(orphan),
        title: selectedPath ? `Campo selecionado: ${selectedPath}` : 'Selecione um campo na árvore de destino',
        'aria-label': `Mover ${orphan.path} para o campo selecionado`,
        onclick: () => onMove(orphanId(orphan), selectedPath),
      }, 'Mover para o campo selecionado'),
      h('button', { type: 'button', class: 'danger', 'aria-label': `Descartar ${orphan.path}`, onclick: () => onDiscard(orphanId(orphan)) }, 'Descartar')));

  const changeRow = (change) => h('li', { class: `schema-change impact-${change.impact}` },
    h('span', { class: `pill impact-${change.impact}` }, IMPACT_LABEL[change.impact] ?? change.impact),
    ' ', pathButton(change.path),
    h('div', { class: 'message' }, change.message),
    (change.before || change.after) && h('div', { class: 'muted before-after' }, `${change.before ?? '—'} → ${change.after ?? '—'}`));

  const groups = IMPACT_ORDER.map((impact) => {
    const changes = report.changes.filter((c) => c.impact === impact);
    if (changes.length === 0) return null;
    const list = h('ul', { class: 'schema-changes' }, changes.map(changeRow));
    // Informativas ficam recolhidas: não exigem ação.
    return impact === 'Info'
      ? h('details', {}, h('summary', {}, `${HEADINGS[impact]} (${changes.length})`), list)
      : h('section', {}, h('h3', {}, `${HEADINGS[impact]} (${changes.length})`), list);
  });

  return h('div', { class: 'schema-changes-panel', role: 'region', 'aria-label': 'Mudanças do XSD de destino' },
    h('div', { class: 'schema-changes-head' },
      h('strong', {}, `Mudanças do XSD: <${report.beforeRootElementName}> anterior → <${report.afterRootElementName}> novo`),
      h('span', { class: 'pill impact-Remap', id: 'schema-changes-remap' }, `${summary.remap} remapear`),
      h('span', { class: 'pill impact-Map' }, `${summary.map} mapear`),
      h('span', { class: 'pill impact-Review' }, `${summary.review} revisar`),
      h('span', { class: 'pill impact-Info' }, `${summary.info} informativo(s)`),
      h('button', {
        type: 'button', disabled: pending > 0, onclick: onClose,
        title: pending > 0 ? 'Resolva os mapeamentos pendentes e inativos antes' : 'Fechar o relatório',
      }, 'Fechar relatório')),
    error && h('p', { class: 'banner error', role: 'alert' }, error),
    report.changes.length === 0 && h('p', { class: 'muted' }, 'Nenhuma diferença de estrutura (só comentários/formatação mudaram).'),
    inactive.length > 0 && h('section', { id: 'inactive-mappings' },
      h('h3', {}, `Mapeamentos que o motor ignoraria (${inactive.length})`),
      h('p', { class: 'hint' }, 'Estão dentro de um bloco que agora repete e ainda não tem a lista de origem ("for each"). Selecione o bloco e defina o "for each", ou descarte o mapeamento. A publicação fica bloqueada até resolver.'),
      h('ul', { class: 'orphans' }, inactive.map((item) => h('li', { class: 'orphan', 'data-path': item.path },
        h('div', {}, h('code', { class: 'path' }, item.path), ' ', h('span', { class: 'summary' }, item.summary)),
        h('div', { class: 'orphan-actions' },
          item.block && h('button', { type: 'button', class: 'primary', 'aria-label': `Selecionar o bloco ${item.block}`, onclick: () => onSelect(item.block) },
            `Definir "for each" em ${item.block}`),
          h('button', { type: 'button', class: 'danger', 'aria-label': `Descartar o mapeamento de ${item.path}`, onclick: () => onDiscardMapping(item.path) }, 'Descartar')))))),
    orphans.length > 0 && h('section', { id: 'orphans' },
      h('h3', {}, `Mapeamentos do XSD anterior para remapear (${orphans.length})`),
      h('p', { class: 'hint' }, 'O campo não existe mais (ou mudou de tipo de nó). Mova para a sugestão, selecione outro campo na árvore e use "Mover para o campo selecionado", ou descarte. A publicação fica bloqueada até resolver todos.'),
      h('ul', { class: 'orphans' }, orphans.map(orphanRow))),
    groups);
}
