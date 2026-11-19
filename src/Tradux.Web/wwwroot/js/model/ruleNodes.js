// Expressões de valor do editor (ValueSpec). Módulo puro: sem DOM, testado com
// `node --test`. O usuário nunca escreve código — só compõe campos, constantes
// e operadores do catálogo (seção 12 do requisito de origem). A conversão para
// o JSON do motor fica em mappingTemplate.js.
//
// ValueSpec:
//   { kind: 'field', path, scope? }             campo de origem (relativo ao escopo; scope 'root' = raiz da entrada, ADR-0020)
//   { kind: 'literal', value }                  constante (texto, número, booleano ou null)
//   { kind: 'operator', name, args: ValueSpec[] } operador do catálogo

import { OPERATOR } from './operators.js';

export const field = (path, scope) => (scope === 'root' ? { kind: 'field', path, scope } : { kind: 'field', path });

/** Chave de um campo nos seletores: campos da raiz dentro de um "for each" têm prefixo próprio. */
export const fieldKey = (spec) => (spec?.scope === 'root' ? `root:${spec.path ?? ''}` : spec?.path ?? '');

export const fieldFromKey = (key) => (key.startsWith('root:') ? field(key.slice(5), 'root') : field(key));
export const literal = (value) => ({ kind: 'literal', value });
export const operator = (name, args = []) => ({ kind: 'operator', name, args });

/** Texto curto e legível de uma expressão, para resumos na árvore de destino. */
export function describe(spec) {
  switch (spec?.kind) {
    case 'field':
      return spec.path ? `${spec.scope === 'root' ? '(raiz) ' : ''}${spec.path}` : '(campo não escolhido)';
    case 'literal':
      return typeof spec.value === 'string' ? `"${spec.value}"` : String(spec.value);
    case 'operator':
      if (spec.name === OPERATOR.omit) return 'omitir';
      if (spec.name === OPERATOR.setNull) return 'nulo';
      return `${spec.name}(${(spec.args ?? []).map(describe).join(', ')})`;
    default:
      return '(vazio)';
  }
}

/**
 * Problemas de uma expressão antes de enviar ao motor: campo vazio, operador
 * fora do catálogo, número de argumentos fora da aridade, argumento vazio.
 * `operators` é o catálogo vindo de GET /v1/design/operators.
 */
export function validate(spec, operators, where = 'valor') {
  const byName = new Map(operators.map((o) => [o.name, o]));
  const problems = [];

  const visit = (node, label) => {
    if (!node) {
      problems.push(`${label}: vazio.`);
      return;
    }

    switch (node.kind) {
      case 'field':
        if (!node.path) problems.push(`${label}: escolha um campo de origem.`);
        break;
      case 'literal':
        break;
      case 'operator': {
        const op = byName.get(node.name);
        if (!op) {
          problems.push(`${label}: operador "${node.name}" não existe no catálogo.`);
          break;
        }

        const count = node.args?.length ?? 0;
        const max = op.maxArguments ?? Infinity;
        if (count < op.minArguments || count > max) {
          const expected = op.maxArguments === op.minArguments ? `${op.minArguments}` : `${op.minArguments} a ${op.maxArguments ?? '∞'}`;
          problems.push(`${label}: "${node.name}" espera ${expected} argumento(s), recebeu ${count}.`);
        }

        (node.args ?? []).forEach((arg, i) => visit(arg, `${label} › ${node.name} › ${parameterName(op, i)}`));
        break;
      }
      default:
        problems.push(`${label}: expressão incompleta.`);
    }
  };

  visit(spec, where);
  return problems;
}

/** Nome do i-ésimo parâmetro (o último se repete nos variádicos). */
export function parameterName(op, index) {
  const params = op.parameters ?? [];
  if (params.length === 0) return `arg${index + 1}`;
  if (index < params.length) return params[index].name;
  return op.repeatsLastParameter ? `${params[params.length - 1].name} ${index + 1}` : `arg${index + 1}`;
}

/** Parâmetro (metadados) do i-ésimo argumento. */
export function parameterAt(op, index) {
  const params = op.parameters ?? [];
  if (index < params.length) return params[index];
  return op.repeatsLastParameter && params.length > 0 ? params[params.length - 1] : { name: `arg${index + 1}`, kind: 'Any' };
}

/** Argumentos iniciais para um operador recém-escolhido (tabela de lookup já como literal). */
export function initialArguments(op, lookupTableNames = []) {
  const count = Math.max(op.minArguments, Math.min(op.parameters?.length ?? 0, op.maxArguments ?? Infinity));
  return Array.from({ length: count }, (_, i) =>
    parameterAt(op, i).kind === 'LookupTable' ? literal(lookupTableNames[0] ?? '') : field(''));
}
