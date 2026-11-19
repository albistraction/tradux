// Query builder de condições (seção 12: "agrupar condições com E, OU e
// negação"; experiência de referência: construtor de consultas do Azure
// DevOps). Módulo puro. A condição vira operadores do catálogo
// (and/or/not + comparação/existência) — nunca código.
//
// Group:  { kind: 'group', combinator: 'and'|'or', negated: bool, children: (Group|Clause)[] }
// Clause: { kind: 'clause', left: ValueSpec, operator, right: ValueSpec|null }

import { field, literal, validate } from './ruleNodes.js';
import { OPERATOR, isClauseOperator, isUnary, clauseLabel } from './operators.js';

export const group = (combinator = OPERATOR.and, children = [], negated = false) => ({ kind: 'group', combinator, negated, children });

export const clause = (leftPath = '', operatorName = 'equals', right = literal('')) => ({
  kind: 'clause',
  left: field(leftPath),
  operator: operatorName,
  right: isUnary(operatorName) ? null : right,
});

/** Grupo sem nenhuma linha (condição "sempre verdadeira"). */
export function isEmptyCondition(node) {
  if (!node) return true;
  if (node.kind === 'clause') return false;
  return node.children.every(isEmptyCondition);
}

/**
 * Converte a condição em ValueSpec de operadores do catálogo. Grupo vazio →
 * null (sem condição). Grupo de um item só não gera and/or supérfluo.
 */
export function toValueSpec(node) {
  if (!node || isEmptyCondition(node)) return null;

  if (node.kind === 'clause') {
    const args = isUnary(node.operator) ? [node.left] : [node.left, node.right];
    return { kind: 'operator', name: node.operator, args };
  }

  const parts = node.children.map(toValueSpec).filter(Boolean);
  const combined = parts.length === 1 ? parts[0] : { kind: 'operator', name: node.combinator, args: parts };
  return node.negated ? { kind: 'operator', name: OPERATOR.not, args: [combined] } : combined;
}

/**
 * Volta de toValueSpec: and/or viram grupo; not(...) vira grupo negado;
 * comparação sobre um campo vira linha. O que o construtor não representa
 * (outro operador) é recusado com explicação.
 */
export function fromValueSpec(spec) {
  const isOperator = (s, ...names) => s.kind === 'operator' && names.includes(s.name);

  const toGroup = (s) => {
    if (isOperator(s, OPERATOR.and, OPERATOR.or)) return group(s.name, s.args.map(toItem));
    if (isOperator(s, OPERATOR.not) && s.args.length === 1) {
      const inner = toGroup(s.args[0]);
      return inner.negated ? group(OPERATOR.and, [inner], true) : { ...inner, negated: true };
    }

    return group(OPERATOR.and, [toItem(s)]);
  };

  const toItem = (s) => {
    if (s.kind === 'operator' && isClauseOperator(s.name) && s.args[0]?.kind === 'field') {
      return { kind: 'clause', left: s.args[0], operator: s.name, right: isUnary(s.name) ? null : (s.args[1] ?? null) };
    }

    if (isOperator(s, OPERATOR.and, OPERATOR.or, OPERATOR.not)) return toGroup(s);
    throw new Error(`Condição com "${s.kind === 'operator' ? s.name : s.kind}" não pode ser aberta no construtor de condições.`);
  };

  return toGroup(spec);
}

/** Problemas da condição (linha sem campo, valor ausente…), com o mesmo validador dos valores. */
export function validateCondition(node, operators, where = 'condição') {
  const spec = toValueSpec(node);
  return spec ? validate(spec, operators, where) : [];
}

const fieldText = (spec) => (spec?.path ? `${spec.scope === 'root' ? '(raiz) ' : ''}${spec.path}` : '?');

/** Resumo legível: "(Numero = "1" E NÃO(Serie está vazio))". */
export function describeCondition(node) {
  if (!node || isEmptyCondition(node)) return '';
  if (node.kind === 'clause') {
    const op = clauseLabel(node.operator);
    const left = fieldText(node.left);
    if (isUnary(node.operator)) return `${left} ${op}`;
    const right = node.right?.kind === 'field' ? fieldText(node.right) : JSON.stringify(node.right?.value ?? null);
    return `${left} ${op} ${right}`;
  }

  const joiner = node.combinator === OPERATOR.or ? ' OU ' : ' E ';
  const inner = node.children.filter((c) => !isEmptyCondition(c)).map(describeCondition).join(joiner);
  const wrapped = node.children.length > 1 ? `(${inner})` : inner;
  return node.negated ? `NÃO ${wrapped}` : wrapped;
}
