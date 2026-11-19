// Painel de regra do nó de destino selecionado. Lê e grava pela lente do
// mapeamento (lens.js): todo evento usa o valor atual do estado.

import { h, select } from './dom.js';
import { prop } from './lens.js';
import { valueEditor } from './valueEditor.js';
import { conditionEditor } from './conditionEditor.js';
import { field, literal, operator } from '../model/ruleNodes.js';
import { group } from '../model/conditions.js';
import { isRepeatable, withCollection } from '../model/draft.js';
import { OPERATOR } from '../model/operators.js';

const MODE_OPTIONS = [
  { value: 'none', label: 'Sem mapeamento' },
  { value: 'value', label: 'Calcular valor' },
  { value: 'omit', label: 'Omitir o elemento' },
  { value: 'null', label: 'Nulo (xsi:nil)' },
];

const modeOf = (entry) => {
  if (!entry?.value) return 'none';
  if (entry.value.kind === 'operator' && entry.value.name === OPERATOR.omit) return 'omit';
  if (entry.value.kind === 'operator' && entry.value.name === OPERATOR.setNull) return 'null';
  return 'value';
};

/**
 * props: { node, lens (entrada do mapeamento; set(null) remove), onRemoveBlock(),
 *          fields, listFields, itemFields, operators, lookupTableNames, parentBlockMissing }
 */
export function rulePanel(props) {
  const { node } = props;
  if (!node) {
    return h('p', { class: 'muted' }, 'Selecione um campo de destino para configurar a regra.');
  }

  const header = h('header', { class: 'rule-header' },
    h('h3', {}, node.name),
    h('code', { class: 'path' }, node.path));

  if (props.parentBlockMissing) {
    return h('div', {}, header, h('p', { class: 'warning' },
      'Este campo está dentro de um bloco repetível sem "for each" configurado. Configure primeiro a coleção de origem do bloco.'));
  }

  if (!node.mappable) {
    return h('div', {}, header, h('p', { class: 'muted' },
      'Conteúdo opaco (ex.: xs:any ou grupo vazio): o motor não gera este nó. Ele aparece para consulta, mas não pode ser mapeado.'));
  }

  if (node.kind === 'Group') {
    return h('div', {}, header, h('p', { class: 'muted' },
      `Grupo ${node.contentModel ?? ''} sem elemento próprio: mapeie os campos dentro dele.`));
  }

  if (node.kind === 'Scalar') return h('div', {}, header, scalarRule(props));
  if (isRepeatable(node)) return h('div', {}, header, forEachRule(props));

  return h('div', {}, header, h('p', { class: 'muted' },
    node.minOccurs >= 1
      ? 'Elemento obrigatório que agrupa outros campos: é gerado sempre, com os campos mapeados dentro dele.'
      : 'Elemento opcional que agrupa outros campos: é gerado quando algum campo dentro dele é mapeado.'));
}

function scalarRule(props) {
  const { lens, fields, node } = props;
  const entry = lens.get();
  const mode = modeOf(entry);

  const setMode = (newMode) => {
    switch (newMode) {
      case 'none':
        lens.set(null);
        break;
      case 'omit':
        lens.set({ value: operator(OPERATOR.omit) });
        break;
      case 'null':
        lens.set({ value: operator(OPERATOR.setNull) });
        break;
      default:
        lens.set({ ...(lens.get() ?? {}), value: field(fields[0]?.path ?? '') });
    }
  };

  const valueLens = prop(lens, 'value');
  const defaultLens = prop(lens, 'defaultValue');
  const conditionLens = prop(lens, 'condition');

  return h('div', { class: 'rule' },
    h('div', { class: 'field-row' },
      h('label', {}, 'Mapeamento ', select({ onchange: (e) => setMode(e.target.value) },
        // "Nulo" só faz sentido em elemento nillable (xsi:nil); fora disso o motor
        // omite (opcional) ou reprova (obrigatório) — revisão da Fase 6, A9.
        MODE_OPTIONS.filter((o) => o.value !== 'null' || node.nillable || mode === 'null'), mode))),
    mode === 'value' && h('fieldset', {},
      h('legend', {}, 'Valor'),
      valueEditor({ ...props, lens: valueLens, label: 'Valor' })),
    mode === 'value' && h('fieldset', {},
      h('legend', {}, 'Valor padrão (quando o valor calculado for nulo)'),
      entry.defaultValue
        ? [valueEditor({ ...props, lens: defaultLens, label: 'Padrão', depth: 5 }),
          h('button', { type: 'button', class: 'link danger', onclick: () => defaultLens.set(undefined) }, 'remover padrão')]
        : h('button', { type: 'button', class: 'link', onclick: () => defaultLens.set(literal('')) }, '+ definir valor padrão')),
    mode !== 'none' && h('fieldset', {},
      h('legend', {}, 'Aplicar somente se (condição)'),
      entry.condition
        ? [conditionEditor({ ...props, lens: conditionLens, title: 'Condição do mapeamento' }),
          h('button', { type: 'button', class: 'link danger', onclick: () => conditionLens.set(undefined) }, 'remover condição')]
        : h('button', { type: 'button', class: 'link', onclick: () => conditionLens.set(group()) }, '+ adicionar condição')),
    mode !== 'none' && h('button', { type: 'button', class: 'danger', onclick: () => lens.set(null) }, 'Remover mapeamento'));
}

function forEachRule(props) {
  const { lens, listFields, itemFields } = props;
  const forEach = lens.get()?.forEach;
  const forEachLens = prop(lens, 'forEach');

  if (!forEach) {
    return h('div', { class: 'rule' },
      h('p', {}, 'Elemento repetível: gere um elemento para cada item de uma coleção de origem (bloco "for each").'),
      h('button', {
        type: 'button', class: 'primary',
        // Lista primeiro; objeto só como objeto único (ADR-0020).
        onclick: () => {
          const first = listFields.find((f) => !f.single) ?? listFields[0];
          lens.set({ forEach: withCollection(null, first?.path ?? '', first?.single) });
        },
      }, 'Criar bloco "for each"'));
  }

  const collectionOptions = [{ value: '', label: listFields.length ? '— coleção —' : '(nenhuma lista neste escopo)' },
    ...listFields.map((f) => ({ value: f.path, label: f.label }))];
  const chooseCollection = (path) =>
    forEachLens.set(withCollection(forEachLens.get(), path, listFields.find((f) => f.path === path)?.single));
  const filterLens = prop(forEachLens, 'filter');

  return h('div', { class: 'rule' },
    h('fieldset', {},
      h('legend', {}, 'Para cada item de'),
      select({ 'aria-label': 'Coleção de origem', onchange: (e) => chooseCollection(e.target.value) }, collectionOptions, forEach.collectionPath),
      forEach.singleObject && h('p', { class: 'hint' }, 'Origem é um objeto único: gera uma ocorrência (se vier uma lista, uma por item).'),
      h('p', { class: 'hint' }, 'Os campos dentro deste bloco são lidos de cada item; os marcados "(raiz)" vêm da raiz da entrada. "$index" é a posição do item (0, 1, 2…); em lista de valores simples, "$value" é o valor.')),
    h('fieldset', {},
      h('legend', {}, 'Filtro (inclui só itens que atendem)'),
      forEach.filter
        ? [conditionEditor({ ...props, fields: itemFields, lens: filterLens, title: 'Filtro do bloco' }),
          h('button', { type: 'button', class: 'link danger', onclick: () => filterLens.set(undefined) }, 'remover filtro')]
        : h('button', { type: 'button', class: 'link', disabled: !forEach.collectionPath, onclick: () => filterLens.set(group()) }, '+ adicionar filtro')),
    h('fieldset', {},
      h('legend', {}, 'Ordenar por (opcional)'),
      select({
        'aria-label': 'Ordenar por',
        onchange: (e) => prop(forEachLens, 'orderBy').set(e.target.value ? field(e.target.value) : undefined),
      }, [{ value: '', label: 'ordem original' }, ...itemFields.filter((f) => !f.scope).map((f) => ({ value: f.path, label: f.label }))], forEach.orderBy?.path ?? '')),
    h('button', { type: 'button', class: 'danger', onclick: () => props.onRemoveBlock() }, 'Remover bloco "for each" e os mapeamentos dentro dele'));
}
