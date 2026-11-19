// Construtor de condições estilo Azure DevOps (seção 12): linhas "campo /
// operador / valor", agrupáveis com E/OU, negação (NÃO) e grupos aninhados.
// Lê e grava pela lente (lens.js): todo evento usa o valor atual do estado.

import { h, select } from './dom.js';
import { prop, item } from './lens.js';
import { valueEditor, fieldPicker } from './valueEditor.js';
import { clause, group } from '../model/conditions.js';
import { CLAUSE_OPERATORS, OPERATOR, isUnary } from '../model/operators.js';
import { field, literal } from '../model/ruleNodes.js';

const MAX_GROUP_DEPTH = 4;

/** props: { lens (Group), fields, operators, lookupTableNames, title } */
export function conditionEditor(props) {
  return h('div', { class: 'condition-editor', role: 'group', 'aria-label': props.title ?? 'Condição' },
    groupEditor({ ...props, depth: 0 }));
}

function groupEditor(props) {
  const { lens, depth } = props;
  const node = lens.get() ?? group();
  const children = prop(lens, 'children');
  const patch = (values) => lens.set({ ...(lens.get() ?? group()), ...values });

  return h('div', { class: `cond-group depth-${Math.min(depth, 3)}${node.negated ? ' negated' : ''}` },
    h('div', { class: 'cond-group-header' },
      h('label', { class: 'inline' },
        h('input', { type: 'checkbox', checked: node.negated, onchange: (e) => patch({ negated: e.target.checked }) }),
        ' NÃO'),
      select({ 'aria-label': 'Combinar linhas com', onchange: (e) => patch({ combinator: e.target.value }) },
        [{ value: OPERATOR.and, label: 'E — todas' }, { value: OPERATOR.or, label: 'OU — qualquer uma' }], node.combinator),
      h('span', { class: 'muted' }, node.children.length === 0 ? 'sem condição (sempre aplica)' : '')),
    h('ul', { class: 'cond-rows' },
      node.children.map((child, i) => h('li', { class: 'cond-row' },
        i > 0 && h('span', { class: 'combinator' }, node.combinator === OPERATOR.or ? 'OU' : 'E'),
        child.kind === 'group'
          ? groupEditor({ ...props, lens: item(children, i), depth: depth + 1 })
          : clauseEditor({ ...props, lens: item(children, i) }),
        h('button', {
          type: 'button', class: 'link danger', 'aria-label': 'Remover linha',
          onclick: () => children.set((children.get() ?? []).filter((_, j) => j !== i)),
        }, '✕')))),
    h('div', { class: 'cond-actions' },
      h('button', { type: 'button', class: 'link', onclick: () => children.set([...(children.get() ?? []), clause()]) }, '+ condição'),
      depth < MAX_GROUP_DEPTH && h('button', {
        type: 'button', class: 'link',
        onclick: () => children.set([...(children.get() ?? []), group(lens.get()?.combinator === OPERATOR.and ? OPERATOR.or : OPERATOR.and, [clause()])]),
      }, '+ grupo')));
}

function clauseEditor(props) {
  const { lens } = props;
  const node = lens.get();

  const left = fieldPicker({ ...props, lens: prop(lens, 'left'), label: 'Campo da condição' }, node.left, field);
  left.setAttribute('aria-label', 'Campo da condição');

  const operatorSelect = select({
    'aria-label': 'Operador da condição',
    onchange: (e) => {
      const name = e.target.value;
      const currentNode = lens.get();
      lens.set({ ...currentNode, operator: name, right: isUnary(name) ? null : currentNode.right ?? literal('') });
    },
  }, CLAUSE_OPERATORS.map((o) => ({ value: o.name, label: o.label })), node.operator);

  return h('div', { class: 'cond-clause' },
    left,
    operatorSelect,
    !isUnary(node.operator) && valueEditor({
      ...props,
      lens: {
        get: () => lens.get()?.right ?? literal(''),
        set: (v) => lens.set({ ...lens.get(), right: v }),
      },
      label: 'Valor da condição',
      depth: 3,
    }));
}
