// Árvores de origem e destino (seção 12: navegar, criar DE-PARA por
// seleção ou arraste, visualizar obrigatórios sem mapeamento e erros).

import { h } from './dom.js';
import { DRAG_MIME } from './valueEditor.js';
import { isRepeatable, summarize } from '../model/draft.js';
import { TYPE_LABEL } from '../model/sourceTree.js';

/**
 * Árvore de origem do escopo atual. props: { scope, scopeLabel, onPick(path, node) }
 * Cada campo é arrastável (para um nó de destino ou um seletor de campo) e
 * clicável (mapeia no nó de destino selecionado).
 */
export function sourceTree({ scope, scopeLabel, onPick }) {
  if (!scope) {
    return h('p', { class: 'muted' }, 'Carregue o XSD de origem (passo 1) para ver os campos de origem.');
  }

  const renderNodes = (nodes) => h('ul', { role: 'group' }, nodes.map((node) => {
    const draggable = node.type !== 'object';
    const item = h('li', { role: 'treeitem', 'aria-expanded': node.children ? 'true' : null },
      h('button', {
        type: 'button',
        class: `source-field type-${node.type}`,
        draggable: draggable ? 'true' : null,
        title: draggable ? 'Arraste para um campo de destino, ou clique para mapear no destino selecionado' : null,
        disabled: !draggable,
        onclick: () => onPick(node.path, node),
        ondragstart: (e) => {
          e.dataTransfer.setData(DRAG_MIME, node.path);
          e.dataTransfer.setData('text/plain', node.path);
          e.dataTransfer.effectAllowed = 'copy';
        },
      },
      h('span', { class: 'name' }, node.name),
      h('span', { class: 'badge' }, TYPE_LABEL[node.type] ?? node.type)),
      node.children && node.type === 'object' && renderNodes(node.children));
    return item;
  }));

  return h('div', { class: 'tree', role: 'tree', 'aria-label': 'Campos de origem' },
    h('p', { class: 'scope' }, 'Escopo: ', h('strong', {}, scopeLabel)),
    renderNodes(scope.children ?? []));
}

const STATUS = {
  mapped: { icon: '✔', label: 'mapeado', css: 'ok' },
  missing: { icon: '⚠', label: 'obrigatório sem mapeamento', css: 'missing' },
  optional: { icon: '○', label: 'opcional', css: 'optional' },
  default: { icon: '◐', label: 'obrigatório com valor padrão do schema', css: 'optional' },
  container: { icon: '▸', label: 'grupo de campos', css: 'container' },
  unsupported: { icon: '⛔', label: 'conteúdo opaco (ex.: xs:any): o motor não gera este nó', css: 'unsupported' },
  blocked: { icon: '⛔', label: 'obrigatório com conteúdo opaco (ex.: xs:any): o motor não gera este nó', css: 'missing' },
  signature: { icon: '✍', label: 'assinatura: quem chama o serviço assina o XML convertido; não entra no DE-PARA', css: 'optional' },
  inactive: { icon: '⊘', label: 'mapeado, mas ignorado: o bloco "for each" acima não tem coleção de origem', css: 'inactive' },
};

const CHANGE_LABEL = { Remap: 'mudou: remapear', Map: 'novo/obrigatório: mapear', Review: 'mudou: revisar', Info: 'mudou' };

/**
 * Árvore de destino. props: { root, mappings, status (Map), diagnosticsByPath (Map),
 *   changes (Map caminho → impacto da troca de XSD, Fase 14), selectedPath, onSelect(path), onDrop(path, sourcePath) }
 */
export function destinationTree({ root, mappings, status, diagnosticsByPath, changes = new Map(), selectedPath, onSelect, onDrop }) {
  const renderNode = (node) => {
    const nodeStatus = STATUS[status.get(node.path)] ?? STATUS.optional;
    const diagnostics = diagnosticsByPath.get(node.path) ?? [];
    const repeatable = node.kind !== 'Scalar' && isRepeatable(node) && node.kind !== 'Group';
    const accepts = node.mappable && (node.kind === 'Scalar' || repeatable);
    const children = node.children ?? [];

    const row = h('button', {
      type: 'button',
      class: `dest-node status-${nodeStatus.css}${node.path === selectedPath && node.kind !== 'Group' ? ' selected' : ''}${diagnostics.length ? ' has-errors' : ''}`,
      'aria-current': node.path === selectedPath ? 'true' : null,
      disabled: node.kind === 'Group',
      onclick: () => onSelect(node.path),
    },
    h('span', { class: 'status-icon', title: nodeStatus.label, 'aria-label': nodeStatus.label }, nodeStatus.icon),
    h('span', { class: 'name' }, node.name),
    badges(node, repeatable),
    changes.has(node.path) && h('span', { class: `change-mark impact-${changes.get(node.path)}`, title: 'Mudança do XSD novo (painel "Mudanças do XSD")' }, CHANGE_LABEL[changes.get(node.path)]),
    h('span', { class: 'summary' }, summarize(mappings[node.path])),
    diagnostics.length > 0 && h('span', {
      class: 'diag-count', title: diagnostics.map((d) => d.message).join('\n'),
    }, `${diagnostics.length} erro${diagnostics.length > 1 ? 's' : ''}`));

    if (accepts) {
      row.addEventListener('dragover', (e) => {
        if (e.dataTransfer.types.includes(DRAG_MIME)) {
          e.preventDefault();
          row.classList.add('drop-hover');
        }
      });
      row.addEventListener('dragleave', () => row.classList.remove('drop-hover'));
      row.addEventListener('drop', (e) => {
        e.preventDefault();
        row.classList.remove('drop-hover');
        const sourcePath = e.dataTransfer.getData(DRAG_MIME);
        if (sourcePath) onDrop(node.path, sourcePath);
      });
    }

    return h('li', { role: 'treeitem', 'aria-expanded': children.length ? 'true' : null },
      row,
      children.length > 0 && h('ul', { role: 'group' }, children.map(renderNode)));
  };

  return h('div', { class: 'tree', role: 'tree', 'aria-label': 'Campos de destino' },
    h('ul', {}, renderNode(root)));
}

function badges(node, repeatable) {
  const list = [];
  if (node.signedByCaller) return h('span', { class: 'badges' }, h('span', { class: 'badge' }, 'assinada por quem chama o serviço'));
  if (node.kind === 'Scalar') list.push(node.scalarType === 'YearMonth' ? 'ano-mês' : ({ String: 'texto', Decimal: 'decimal', Date: 'data', DateTime: 'data e hora' }[node.scalarType] ?? node.scalarType));
  if (node.kind === 'Group') list.push(node.contentModel);
  list.push(node.minOccurs >= 1 ? 'obrigatório' : 'opcional');
  if (repeatable) list.push(node.maxOccurs == null ? 'repete (0..n)' : `repete (até ${node.maxOccurs})`);
  if (node.nillable) list.push('aceita nulo');
  if (node.defaultValue != null) list.push(`padrão ${node.defaultValue}`);
  return h('span', { class: 'badges' }, list.map((b) => h('span', { class: 'badge' }, b)));
}
