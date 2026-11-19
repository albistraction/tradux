// Nomes de operador do catálogo que o editor trata de forma própria — um lugar só.
//
// O catálogo (GET /v1/design/operators) traz nome, categoria, descrição e
// aridade de cada operador, e é dele que saem as transformações oferecidas e a
// validação de argumentos. Ele não traz o que fica aqui: os rótulos curtos da
// linha de condição ("=", "está vazio"), quais operadores viram modo do
// mapeamento (omitir, nulo) ou valor padrão, e os módulos puros (resumos, abrir
// uma versão publicada) precisam desses nomes mesmo sem o catálogo carregado.

export const OPERATOR = Object.freeze({
  and: 'and',
  or: 'or',
  not: 'not',
  setDefault: 'setDefault',
  omit: 'omit',
  setNull: 'setNull',
});

/** Fora da lista de transformações: já são "Constante" e "Campo de origem" no editor de valor. */
export const HIDDEN_VALUE_OPERATORS = new Set(['constant', 'copy']);

/** Operadores de uma linha de condição; unários não têm "valor". */
export const CLAUSE_OPERATORS = [
  { name: 'equals', label: '=' },
  { name: 'notEquals', label: '≠' },
  { name: 'greaterThan', label: '>' },
  { name: 'lessThan', label: '<' },
  { name: 'isEmpty', label: 'está vazio', unary: true },
  { name: 'notEmpty', label: 'está preenchido', unary: true },
];

const CLAUSES = new Map(CLAUSE_OPERATORS.map((o) => [o.name, o]));

export const isClauseOperator = (name) => CLAUSES.has(name);

export const isUnary = (name) => Boolean(CLAUSES.get(name)?.unary);

export const clauseLabel = (name) => CLAUSES.get(name)?.label ?? name;
