// Editor de uma expressão de valor (ValueSpec): campo de origem, constante
// ou transformação (operador do catálogo, com argumentos que são, por sua
// vez, expressões). Nunca aceita código — só escolhas do catálogo.
// Lê e grava pela lente (lens.js): todo evento usa o valor atual do estado.

import { h, select, nextId } from './dom.js';
import { prop, item } from './lens.js';
import { field, literal, operator, parameterAt, parameterName, initialArguments, fieldKey, fieldFromKey } from '../model/ruleNodes.js';
import { HIDDEN_VALUE_OPERATORS } from '../model/operators.js';

const MAX_DEPTH = 5;
export const DRAG_MIME = 'application/x-tradux-source-path';

const KIND_OPTIONS = [
  { value: 'field', label: 'Campo de origem' },
  { value: 'literal', label: 'Constante' },
  { value: 'operator', label: 'Transformação' },
];

/**
 * props: { lens: { get, set } do ValueSpec, fields: [{ path, label }], operators,
 *          lookupTableNames, label, depth?, argumentKind? }
 */
export function valueEditor(props) {
  const { lens, label, depth = 0 } = props;
  const spec = lens.get();
  const kindId = nextId('kind');
  const kind = spec?.kind ?? 'field';
  const kindOptions = depth >= MAX_DEPTH ? KIND_OPTIONS.filter((o) => o.value !== 'operator') : KIND_OPTIONS;

  const changeKind = (newKind) => {
    if (newKind === 'field') lens.set(field(''));
    else if (newKind === 'literal') lens.set(literal(''));
    else lens.set(operator('', []));
  };

  return h('div', { class: `value-editor depth-${Math.min(depth, 3)}` },
    h('div', { class: 'row' },
      h('label', { for: kindId, class: 'sr-label' }, label ?? 'Valor'),
      select({ id: kindId, onchange: (e) => changeKind(e.target.value), 'aria-label': `${label ?? 'Valor'}: tipo` }, kindOptions, kind),
      kind === 'field' && fieldPicker(props, spec),
      kind === 'literal' && literalEditor(props, spec)),
    kind === 'operator' && operatorEditor(props, spec));
}

/**
 * Seletor de campo de origem que também aceita arrastar da árvore de origem.
 * fields: [{ path, label, scope? }] — scope 'root' = campo da raiz dentro de um "for each" (ADR-0020).
 */
export function fieldPicker({ lens, fields, label }, spec, toSpec = field) {
  const current = fieldKey(spec);
  const options = [{ value: '', label: '— escolha um campo —' }, ...fields.map((f) => ({ value: fieldKey(f), label: f.label }))];
  // O campo escolhido pode não estar mais no escopo (ex.: XSD de origem trocado) — continua visível.
  if (current && !fields.some((f) => fieldKey(f) === current)) options.push({ value: current, label: `${current} (fora dos campos de origem)` });

  const picker = select({
    'aria-label': `${label ?? 'Valor'}: campo de origem`,
    class: 'drop-target',
    onchange: (e) => {
      const chosen = fieldFromKey(e.target.value);
      lens.set(toSpec(chosen.path, chosen.scope));
    },
  }, options, current);

  picker.addEventListener('dragover', (e) => { if (e.dataTransfer.types.includes(DRAG_MIME)) e.preventDefault(); });
  picker.addEventListener('drop', (e) => {
    e.preventDefault();
    const path = e.dataTransfer.getData(DRAG_MIME);
    if (path) lens.set(toSpec(path));
  });
  return picker;
}

const literalType = (value) => (value === null ? 'null' : typeof value === 'number' ? 'number' : typeof value === 'boolean' ? 'boolean' : 'text');

function convert(newType, raw) {
  switch (newType) {
    case 'number': {
      const n = Number(String(raw ?? '').replace(',', '.'));
      return literal(Number.isFinite(n) ? n : 0);
    }
    case 'boolean':
      return literal(raw === true || raw === 'true');
    case 'null':
      return literal(null);
    default:
      return literal(raw === null || raw === undefined ? '' : String(raw));
  }
}

function literalEditor({ lens, label, argumentKind }, spec) {
  const value = spec?.value ?? '';
  const type = literalType(spec?.value ?? '');
  const typeOptions = [
    { value: 'text', label: 'Texto' },
    { value: 'number', label: 'Número' },
    { value: 'boolean', label: 'Sim/Não' },
    { value: 'null', label: 'Nulo' },
  ];

  const input = type === 'boolean'
    ? select({ 'aria-label': `${label ?? 'Valor'}: constante`, onchange: (e) => lens.set(literal(e.target.value === 'true')) },
      [{ value: 'true', label: 'Sim' }, { value: 'false', label: 'Não' }], String(value))
    : type === 'null'
      ? h('span', { class: 'muted' }, 'nulo')
      : h('input', {
        type: type === 'number' ? 'number' : 'text',
        step: type === 'number' ? 'any' : null,
        value: String(value),
        placeholder: argumentKind === 'Integer' ? '0' : 'valor fixo',
        'aria-label': `${label ?? 'Valor'}: constante`,
        // "input": o estado acompanha a digitação (sem re-render ao sair do campo).
        oninput: (e) => lens.set(convert(literalType(lens.get()?.value ?? ''), e.target.value)),
      });

  return [
    select({
      'aria-label': `${label ?? 'Valor'}: tipo da constante`,
      onchange: (e) => lens.set(convert(e.target.value, lens.get()?.value)),
    }, typeOptions, type),
    input,
  ];
}

function operatorEditor(props, spec) {
  const { lens, operators, lookupTableNames = [], label, depth = 0 } = props;
  const valueOperators = operators.filter((o) => o.result === 'Value' && !HIDDEN_VALUE_OPERATORS.has(o.name));
  const current = operators.find((o) => o.name === spec.name);

  const choose = (name) => {
    const op = operators.find((o) => o.name === name);
    lens.set(op ? operator(name, initialArguments(op, lookupTableNames)) : operator('', []));
  };

  const operatorSelect = select(
    { 'aria-label': `${label ?? 'Valor'}: operador`, onchange: (e) => choose(e.target.value) },
    [{ value: '', label: '— escolha a transformação —' },
      ...valueOperators.map((o) => ({ value: o.name, label: `${o.name} — ${o.description}`, group: o.category }))],
    spec.name ?? '');

  if (!current) return h('div', { class: 'operator' }, operatorSelect);

  const argsLens = prop(lens, 'args');
  const args = spec.args ?? [];
  const canAdd = current.repeatsLastParameter && args.length < (current.maxArguments ?? Infinity);
  const canRemove = (i) => args.length > current.minArguments && (current.repeatsLastParameter || i >= current.minArguments);

  return h('div', { class: 'operator' },
    operatorSelect,
    h('ol', { class: 'arguments' },
      args.map((arg, i) => {
        const param = parameterAt(current, i);
        const argLabel = parameterName(current, i);
        const argLens = item(argsLens, i);
        return h('li', { class: 'argument' },
          h('span', { class: 'arg-name', title: param.description ?? '' }, argLabel),
          param.kind === 'LookupTable'
            ? select({ 'aria-label': `${argLabel}: tabela DE-PARA`, onchange: (e) => argLens.set(literal(e.target.value)) },
              [{ value: '', label: lookupTableNames.length ? '— tabela —' : '(crie uma tabela DE-PARA)' },
                ...lookupTableNames.map((n) => ({ value: n, label: n }))],
              arg?.value ?? '')
            : valueEditor({ ...props, lens: argLens, label: argLabel, depth: depth + 1, argumentKind: param.kind }),
          canRemove(i) && h('button', {
            type: 'button', class: 'link danger', 'aria-label': `Remover ${argLabel}`,
            onclick: () => argsLens.set((argsLens.get() ?? []).filter((_, j) => j !== i)),
          }, 'remover'));
      })),
    canAdd && h('button', {
      type: 'button', class: 'link',
      onclick: () => argsLens.set([...(argsLens.get() ?? []), field('')]),
    }, `+ ${parameterAt(current, args.length).name}`));
}
