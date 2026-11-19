// Tabelas DE-PARA do template (operador lookup): linhas [de, para] editáveis,
// com aviso de chave repetida. Só h()/textContent.

import { h } from './dom.js';
import { duplicateKeys } from '../model/lookupTables.js';

/**
 * props: { tables ({ nome: [[de, para], ...] }), onRows(name, rowsOf(rows) → rows),
 *          onDelete(name), onCreate(name) }
 */
export function tablesPanel({ tables, onRows, onDelete, onCreate }) {
  const names = Object.keys(tables);
  return [
    names.length === 0 && h('p', { class: 'muted' }, 'Nenhuma tabela. Tabelas DE-PARA traduzem códigos (operador lookup).'),
    names.map((name) => {
      const rows = tables[name];
      const duplicates = duplicateKeys(rows);
      const setCell = (i, column, value) => onRows(name, (r) => r.map((row, j) => (j === i ? row.map((cell, c) => (c === column ? value : cell)) : row)));
      return h('details', { class: 'lookup-table', open: true },
        h('summary', {}, `${name} (${rows.length})`),
        h('table', {},
          h('thead', {}, h('tr', {}, h('th', {}, 'De'), h('th', {}, 'Para'), h('th', {}, ''))),
          h('tbody', {}, rows.map(([key, value], i) => h('tr', {},
            h('td', {}, h('input', {
              value: key, 'aria-label': `De (linha ${i + 1})`, class: duplicates.has(key) ? 'invalid' : null,
              oninput: (e) => setCell(i, 0, e.target.value),
            })),
            h('td', {}, h('input', {
              value, 'aria-label': `Para (linha ${i + 1})`,
              oninput: (e) => setCell(i, 1, e.target.value),
            })),
            h('td', {}, h('button', {
              type: 'button', class: 'link danger', 'aria-label': `Remover linha ${i + 1}`,
              onclick: () => onRows(name, (r) => r.filter((_, j) => j !== i)),
            }, '✕')))))),
        duplicates.size > 0 && h('p', { class: 'warning' }, `Chave repetida: ${[...duplicates].join(', ')}.`),
        h('button', { type: 'button', class: 'link', onclick: () => onRows(name, (r) => [...r, ['', '']]) }, '+ linha'),
        h('button', { type: 'button', class: 'link danger', onclick: () => onDelete(name) }, 'excluir tabela'));
    }),
    h('form', {
      class: 'inline-form',
      onsubmit: (e) => {
        e.preventDefault();
        const name = e.target.elements.namedItem('table-name').value.trim();
        if (name && !(name in tables)) onCreate(name);
      },
    },
    h('input', { name: 'table-name', placeholder: 'nome da tabela', 'aria-label': 'Nome da nova tabela DE-PARA' }),
    h('button', { type: 'submit' }, 'Criar tabela')),
  ];
}
